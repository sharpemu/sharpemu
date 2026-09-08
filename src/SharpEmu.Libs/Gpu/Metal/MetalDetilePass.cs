// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Numerics;
using SharpEmu.Libs.Agc;
using SharpEmu.ShaderCompiler.Metal;

namespace SharpEmu.Libs.Gpu.Metal;

/// <summary>Converts four-byte ExactXor surfaces into sampled textures.
/// Keep temporary buffers until the recorded commands complete.</summary>
internal sealed unsafe class MetalDetilePass : IDisposable
{
    private const uint LocalSize = 8;
    private const int PushConstantUints = 11;

    private readonly nint _device;
    private nint _pipelineState;
    private bool _initialized;
    private bool _disposed;

    public MetalDetilePass(nint device)
    {
        _device = device;
    }

    public static bool Supports(in DetileParams parameters) =>
        (parameters.Equation == DetileEquation.ExactXor ||
         parameters.Equation == DetileEquation.BlockTable) &&
        parameters.BytesPerElement is 4 or 8 or 16;

    /// <summary>
    /// Records the deswizzle of <paramref name="tiled"/> into
    /// <paramref name="texture"/> (<paramref name="texelWidth"/> x
    /// <paramref name="texelHeight"/> texels x <paramref name="layers"/> slices)
    /// onto <paramref name="commandBuffer"/>. The kernel iterates the element grid
    /// from <paramref name="parameters"/> (for block-compressed formats a 4x4 block
    /// is one element). Does not commit; the caller releases
    /// <paramref name="transientBuffers"/> when the command buffer completes.
    /// Returns false (empty transients) when unsupported or the pipeline could not
    /// be built.
    /// </summary>
    public bool RecordDetile(
        nint commandBuffer,
        nint texture,
        uint texelWidth,
        uint texelHeight,
        uint layers,
        ReadOnlySpan<byte> tiled,
        in DetileParams parameters,
        out nint[] transientBuffers)
    {
        transientBuffers = [];
        var bytesPerElement = (uint)parameters.BytesPerElement;
        if (_disposed || commandBuffer == 0 || texture == 0 ||
            !Supports(parameters) || texelWidth == 0 || texelHeight == 0 || layers == 0 || tiled.IsEmpty ||
            tiled.Length % (int)(layers * bytesPerElement) != 0 ||
            !EnsurePipeline())
        {
            return false;
        }

        var elementsWide = (uint)parameters.ElementsWide;
        var elementsHigh = (uint)parameters.ElementsHigh;
        var uintsPerElement = bytesPerElement / sizeof(uint);

        // Array slices are packed contiguously in the tiled buffer; each slice's
        // element stride is the whole buffer split evenly by layer.
        var srcSliceElements = (uint)((ulong)tiled.Length / bytesPerElement / layers);

        // Binding 1 carries the within-block offset table. ExactXor: element-shifted
        // X/Y byte terms. BlockTable: GetDetileParams' block table (already element
        // offsets) in binding 1, a placeholder in binding 2. The two equations index
        // different-sized buffers, so the kernel branches and reads only one.
        uint[] xTerm;
        uint[] yTerm;
        uint equationValue;
        if (parameters.Equation == DetileEquation.BlockTable)
        {
            xTerm = new uint[parameters.BlockTable.Length];
            for (var index = 0; index < xTerm.Length; index++)
            {
                xTerm[index] = (uint)parameters.BlockTable[index];
            }

            yTerm = [0];
            equationValue = 1;
        }
        else
        {
            var shift = BitOperations.TrailingZeroCount((uint)parameters.BytesPerElement);
            xTerm = ToElementTerms(parameters.XByteTerm, shift);
            yTerm = ToElementTerms(parameters.YByteTerm, shift);
            equationValue = 0;
        }

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

        var outputBytes = (nuint)elementsWide * elementsHigh * bytesPerElement * layers;
        var outputBuffer = MetalNative.SendNewBuffer(_device, newBufferWithLength, outputBytes, 0);

        Span<uint> push =
        [
            elementsWide,
            elementsHigh,
            (uint)parameters.BlockWidth,
            (uint)parameters.BlockHeight,
            (uint)parameters.BlockElements,
            (uint)parameters.BlocksPerRow,
            (uint)parameters.XMask,
            (uint)parameters.YMask,
            srcSliceElements,
            equationValue,
            uintsPerElement,
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

        // X is widened by uintsPerElement (each thread copies one word); one
        // grid-Z layer per array slice.
        var threadgroups = new MtlSize
        {
            Width = (nuint)((elementsWide * uintsPerElement + LocalSize - 1) / LocalSize),
            Height = (nuint)((elementsHigh + LocalSize - 1) / LocalSize),
            Depth = layers,
        };
        var threadsPerThreadgroup = new MtlSize { Width = LocalSize, Height = LocalSize, Depth = 1 };
        MetalNative.SendDispatch(
            encoder,
            MetalNative.Selector("dispatchThreadgroups:threadsPerThreadgroup:"),
            threadgroups,
            threadsPerThreadgroup);
        MetalNative.SendVoid(encoder, MetalNative.Selector("endEncoding"));

        // Blit the layer-major linear output buffer into the sampled texture, one
        // slice per array layer (Metal copyFromBuffer targets a single slice). The
        // buffer is element/block-packed (row stride = elementsWide*bpp); the copy
        // region is in texels. Metal tracks the compute-write -> blit-read hazard.
        var blit = MetalNative.Send(commandBuffer, MetalNative.Selector("blitCommandEncoder"));
        var copySelector = MetalNative.Selector(
            "copyFromBuffer:sourceOffset:sourceBytesPerRow:sourceBytesPerImage:sourceSize:" +
            "toTexture:destinationSlice:destinationLevel:destinationOrigin:");
        var sliceBytes = (nuint)elementsWide * elementsHigh * bytesPerElement;
        var rowBytes = (nuint)elementsWide * bytesPerElement;
        for (uint layer = 0; layer < layers; layer++)
        {
            MetalNative.SendCopyBufferToTexture(
                blit,
                copySelector,
                outputBuffer,
                (nuint)layer * sliceBytes,
                rowBytes,
                sliceBytes,
                new MtlSize { Width = texelWidth, Height = texelHeight, Depth = 1 },
                texture,
                layer,
                0,
                new MtlOrigin { X = 0, Y = 0, Z = 0 });
        }

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
