// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Images;

namespace SharpEmu.Libs.Gpu.Rendering;

// What a draw's program resolution reads besides the stage and context registers: the export
// mapping of every bound color slot, whether the pixel stage runs and whether a depth target is
// bound. All three follow from the register banks alone.
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

    public static GraphicsProgramInputs Create()
    {
        var inputs = new GraphicsProgramInputs();
        ((Span<ColorComponentMap>)inputs.Mapping).Fill(ColorComponentMap.Identity);
        return inputs;
    }

    public readonly bool Equals(GraphicsProgramInputs other) =>
        PixelActive == other.PixelActive && DepthBound == other.DepthBound &&
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
    public static GraphicsProgramInputs ProgramInputsOf(RegisterBanks banks, IImageFormatSupport formats, Func<string, Exception> fatal)
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
        return inputs;
    }

    // The programs of a draw with these banks and inputs, as ResolveShaderPrograms resolves them.
    public GraphicsPrograms ResolveGraphicsPrograms(RegisterBanks banks, in GraphicsProgramInputs inputs)
    {
        var context = banks.Context;
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
