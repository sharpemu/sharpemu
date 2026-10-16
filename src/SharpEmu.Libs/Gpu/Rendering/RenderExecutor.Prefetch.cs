// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Images;

namespace SharpEmu.Libs.Gpu.Rendering;

// Program inputs include target mappings, stage activity, and indexed draw mode.
public struct GraphicsProgramInputs : IEquatable<GraphicsProgramInputs>
{
    [System.Runtime.CompilerServices.InlineArray(RenderingState.ColorAttachmentCapacity)]
    public struct ExportMappings
    {
        private ColorComponentMap _element0;
    }

    public ExportMappings Mapping;
    public bool PixelActive;
    public bool DepthBound;
    public bool Indexed;

    public static GraphicsProgramInputs Create()
    {
        var inputs = new GraphicsProgramInputs();
        ((Span<ColorComponentMap>)inputs.Mapping).Fill(ColorComponentMap.Identity);
        return inputs;
    }

    public readonly bool Equals(GraphicsProgramInputs other) =>
        PixelActive == other.PixelActive && DepthBound == other.DepthBound && Indexed == other.Indexed &&
        ((ReadOnlySpan<ColorComponentMap>)Mapping).SequenceEqual(other.Mapping);

    public override readonly bool Equals(object? obj) => obj is GraphicsProgramInputs other && Equals(other);

    public override readonly int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var map in (ReadOnlySpan<ColorComponentMap>)Mapping)
        {
            hash.Add(map.Packed);
        }

        hash.Add(PixelActive);
        hash.Add(DepthBound);
        hash.Add(Indexed);
        return hash.ToHashCode();
    }
}

// The host side of resolving programs on a thread other than the render thread. The resolving
// thread records what it assumed of the host's GPU-ownership state; the render thread checks,
// when it takes the result, that the assumptions still hold.
public interface IConcurrentResolveHost
{
    // On the resolving thread, around one resolution.
    void BeginConcurrentResolve();

    object EndConcurrentResolve();

    // On the render thread.
    bool IsStillCurrent(object assumptions);
}

// Program resolutions computed ahead of their draws. A draw takes one only when it was resolved
// from the same register banks with the same inputs; otherwise it resolves its programs itself.
public interface IProgramPrefetch
{
    bool TryTakeGraphics(RegisterBanks banks, in GraphicsProgramInputs inputs, out GraphicsPrograms programs);
}

public sealed partial class RenderExecutor
{
    public IProgramPrefetch? Prefetch { get; set; }

    // The inputs ResolveShaderPrograms derives from the targets TryResolveDrawTargets binds, from the
    // registers alone: the target requests and the pixel and depth activity, without the images.
    public static GraphicsProgramInputs ProgramInputsOf(RegisterBanks banks, IImageFormatSupport formats, Func<string, Exception> fatal, bool indexed = false)
    {
        var inputs = GraphicsProgramInputs.Create();
        var context = banks.Context;
        Span<ColorComponentMap> mapping = inputs.Mapping;
        for (var slot = 0u; slot < ContextRegisters.ColorTargetCount; slot++)
        {
            if (slot != 0 && (context.RenderTargetMaskForSlot(slot) == 0 || context.ColorTargets[slot].BaseAddress == 0))
            {
                continue;
            }

            if (ColorTargetResolver.Resolve(context, slot, DrawLayerOffset, ignoreTargetMask: false, out var resolvedSlot) is { } resolution)
            {
                mapping[(int)resolvedSlot] = resolution.ExportMapping;
            }
        }

        inputs.DepthBound = DepthTargetResolver.Resolve(context, formats, fatal) is not null;
        inputs.PixelActive = HasActivePixelShader(banks);
        inputs.Indexed = indexed;
        return inputs;
    }

    // The programs of a draw with these banks and inputs, as ResolveShaderPrograms resolves them.
    public GraphicsPrograms ResolveGraphicsPrograms(RegisterBanks banks, in GraphicsProgramInputs inputs)
    {
        var context = banks.Context;
        if (UsesMeshExecution(banks))
        {
            var userConfig = banks.UserConfig;
            if (inputs.Indexed && userConfig.PrimitiveType is
                (uint)GuestPrimitiveType.TriangleList or (uint)GuestPrimitiveType.TriangleStrip)
            {
                userConfig = userConfig.Copy();
                userConfig.PrimitiveType = (uint)GuestPrimitiveType.TriangleList;
            }

            return _pipelines.GetMeshGraphicsPrograms(
                banks.Shader.Vertex, banks.Shader.Pixel, context.ShaderInterface, context,
                userConfig, inputs.Mapping, inputs.PixelActive, inputs.DepthBound);
        }

        return _pipelines.GetGraphicsPrograms(
            banks.Shader.Vertex,
            banks.Shader.Pixel,
            context.ShaderInterface,
            context,
            inputs.Mapping,
            inputs.PixelActive,
            inputs.DepthBound);
    }
}
