// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Numerics;
using SharpEmu.Libs.Agc;
using SharpEmu.ShaderCompiler.Metal;

namespace SharpEmu.Libs.Gpu.Metal;

/// <summary>
/// Metal twin of <c>VulkanDetilePass</c>: runs the ExactXor detile equation from
/// <see cref="GnmTiling.GetDetileParams"/> as a Metal compute kernel
/// (<see cref="MslFixedShaders.CreateDetileCompute"/>), writing a linear buffer
/// and blitting it into the sampled texture.
///
/// <see cref="RecordDetile"/> records the compute dispatch + blit onto a caller's
/// command buffer and returns its transient buffers for the caller to release
/// once that command buffer completes — the async, non-blocking shape (Metal
/// hazard-tracks the compute-write → blit-read → sample dependency automatically,
/// so no manual barriers are needed).
///
/// Only ExactXor 4-bytes/element surfaces are handled. NOTE: authored on Windows;
/// the MSL and every Metal call here are <b>Mac-untested</b> — mirrors the
/// verified Vulkan logic and the existing Metal message-send conventions, but
/// must be validated on a real Metal device.
/// </summary>
internal sealed unsafe class MetalDetilePass : IDisposable
{
    private const uint LocalSize = 8;
    private const int PushConstantUints = 8;

    private readonly nint _device;
    private nint _pipelineState;
    private bool _initialized;
    private bool _disposed;

    public MetalDetilePass(nint device)
    {
        _device = device;
    }

    public static bool Supports(in DetileParams parameters) =>
        parameters.Equation == DetileEquation.ExactXor && parameters.BytesPerElement == 4;

    /// <summary>
    /// Records the deswizzle of <paramref name="tiled"/> into
    /// <paramref name="texture"/> (RGBA8, <paramref name="width"/> x
    /// <paramref name="height"/>) onto <paramref name="commandBuffer"/>. Does not
    /// commit; the caller releases <paramref name="transientBuffers"/> when the
    /// command buffer completes. Returns false (empty transients) when unsupported
    /// or the pipeline could not be built.
    /// </summary>
    public bool RecordDetile(
        nint commandBuffer,
        nint texture,
        uint width,
        uint height,
        ReadOnlySpan<byte> tiled,
        in DetileParams parameters,
        out nint[] transientBuffers)
    {
        transientBuffers = [];
        if (_disposed || commandBuffer == 0 || texture == 0 ||
            !Supports(parameters) || width == 0 || height == 0 || tiled.IsEmpty ||
            !EnsurePipeline())
        {
            return false;
        }

        var shift = BitOperations.TrailingZeroCount((uint)parameters.BytesPerElement);
        var xTerm = ToElementTerms(parameters.XByteTerm, shift);
        var yTerm = ToElementTerms(parameters.YByteTerm, shift);

        var newBufferWithBytes = MetalNative.Selector("newBufferWithBytes:length:options:");
        var newBufferWithLength = MetalNative.Selector("newBufferWithLength:options:");

        nint tiledBuffer;
        nint xBuffer;
        nint yBuffer;
        fixed (byte* tiledPointer = tiled)
        {
            tiledBuffer = MetalNative.SendBuffer(
                _device, newBufferWithBytes, (nint)tiledPointer, (nuint)tiled.Length, 0);
        }

        fixed (uint* xPointer = xTerm)
        {
            xBuffer = MetalNative.SendBuffer(
                _device, newBufferWithBytes, (nint)xPointer, (nuint)xTerm.Length * sizeof(uint), 0);
        }

        fixed (uint* yPointer = yTerm)
        {
            yBuffer = MetalNative.SendBuffer(
                _device, newBufferWithBytes, (nint)yPointer, (nuint)yTerm.Length * sizeof(uint), 0);
        }

        var outputBytes = (nuint)width * height * sizeof(uint);
        var outputBuffer = MetalNative.SendNewBuffer(_device, newBufferWithLength, outputBytes, 0);

        Span<uint> push =
        [
            width,
            height,
            (uint)parameters.BlockWidth,
            (uint)parameters.BlockHeight,
            (uint)parameters.BlockElements,
            (uint)parameters.BlocksPerRow,
            (uint)parameters.XMask,
            (uint)parameters.YMask,
        ];
        nint paramsBuffer;
        fixed (uint* pushPointer = push)
        {
            paramsBuffer = MetalNative.SendBuffer(
                _device, newBufferWithBytes, (nint)pushPointer, (nuint)PushConstantUints * sizeof(uint), 0);
        }

        if (tiledBuffer == 0 || xBuffer == 0 || yBuffer == 0 || outputBuffer == 0 || paramsBuffer == 0)
        {
            ReleaseAll(tiledBuffer, xBuffer, yBuffer, outputBuffer, paramsBuffer);
            return false;
        }

        // Compute encoder: one thread per texel.
        var setBuffer = MetalNative.Selector("setBuffer:offset:atIndex:");
        var encoder = MetalNative.Send(commandBuffer, MetalNative.Selector("computeCommandEncoder"));
        MetalNative.Send(encoder, MetalNative.Selector("setComputePipelineState:"), _pipelineState);
        MetalNative.SendSetBuffer(encoder, setBuffer, tiledBuffer, 0, 0);
        MetalNative.SendSetBuffer(encoder, setBuffer, xBuffer, 0, 1);
        MetalNative.SendSetBuffer(encoder, setBuffer, yBuffer, 0, 2);
        MetalNative.SendSetBuffer(encoder, setBuffer, outputBuffer, 0, 3);
        MetalNative.SendSetBuffer(encoder, setBuffer, paramsBuffer, 0, 4);

        var threadgroups = new MtlSize
        {
            Width = (nuint)((width + LocalSize - 1) / LocalSize),
            Height = (nuint)((height + LocalSize - 1) / LocalSize),
            Depth = 1,
        };
        var threadsPerThreadgroup = new MtlSize { Width = LocalSize, Height = LocalSize, Depth = 1 };
        MetalNative.SendDispatch(
            encoder,
            MetalNative.Selector("dispatchThreadgroups:threadsPerThreadgroup:"),
            threadgroups,
            threadsPerThreadgroup);
        MetalNative.SendVoid(encoder, MetalNative.Selector("endEncoding"));

        // Blit the linear output buffer into the sampled texture. Metal tracks
        // the compute-write -> blit-read hazard automatically.
        var blit = MetalNative.Send(commandBuffer, MetalNative.Selector("blitCommandEncoder"));
        MetalNative.SendCopyBufferToTexture(
            blit,
            MetalNative.Selector(
                "copyFromBuffer:sourceOffset:sourceBytesPerRow:sourceBytesPerImage:sourceSize:" +
                "toTexture:destinationSlice:destinationLevel:destinationOrigin:"),
            outputBuffer,
            0,
            (nuint)width * sizeof(uint),
            outputBytes,
            new MtlSize { Width = width, Height = height, Depth = 1 },
            texture,
            0,
            0,
            new MtlOrigin { X = 0, Y = 0, Z = 0 });
        MetalNative.SendVoid(blit, MetalNative.Selector("endEncoding"));

        transientBuffers = [tiledBuffer, xBuffer, yBuffer, outputBuffer, paramsBuffer];
        return true;
    }

    private bool EnsurePipeline()
    {
        if (_initialized)
        {
            return _pipelineState != 0;
        }

        _initialized = true;

        var options = MetalNative.Send(
            MetalNative.Send(MetalNative.Class("MTLCompileOptions"), MetalNative.Selector("alloc")),
            MetalNative.Selector("init"));
        MetalNative.SendVoidBool(options, MetalNative.Selector("setFastMathEnabled:"), false);

        nint libraryError = 0;
        var library = MetalNative.Send(
            _device,
            MetalNative.Selector("newLibraryWithSource:options:error:"),
            MetalNative.NsString(MslFixedShaders.CreateDetileCompute()),
            options,
            ref libraryError);
        if (library == 0)
        {
            Console.Error.WriteLine(
                $"[GPU-DETILE] Metal detile library compile failed: {MetalNative.DescribeError(libraryError)}");
            return false;
        }

        var function = MetalNative.Send(
            library, MetalNative.Selector("newFunctionWithName:"), MetalNative.NsString("detile_cs"));
        if (function == 0)
        {
            Console.Error.WriteLine("[GPU-DETILE] Metal detile function 'detile_cs' not found.");
            return false;
        }

        nint pipelineError = 0;
        _pipelineState = MetalNative.Send(
            _device,
            MetalNative.Selector("newComputePipelineStateWithFunction:error:"),
            function,
            ref pipelineError);
        if (_pipelineState == 0)
        {
            Console.Error.WriteLine(
                $"[GPU-DETILE] Metal detile pipeline failed: {MetalNative.DescribeError(pipelineError)}");
            return false;
        }

        return true;
    }

    private static uint[] ToElementTerms(int[] byteTerms, int shift)
    {
        var terms = new uint[byteTerms.Length];
        for (var index = 0; index < byteTerms.Length; index++)
        {
            terms[index] = (uint)byteTerms[index] >> shift;
        }

        return terms;
    }

    private static void ReleaseAll(params nint[] objects)
    {
        var release = MetalNative.Selector("release");
        foreach (var handle in objects)
        {
            if (handle != 0)
            {
                MetalNative.SendVoid(handle, release);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_pipelineState != 0)
        {
            MetalNative.SendVoid(_pipelineState, MetalNative.Selector("release"));
            _pipelineState = 0;
        }
    }
}
