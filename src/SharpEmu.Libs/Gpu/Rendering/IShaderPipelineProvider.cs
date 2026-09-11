// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Images;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Rendering;

// A compiled shader module the pipeline provider hands back; zero means no program.
public readonly record struct ShaderProgram(ulong Id)
{
    public bool IsValid => Id != 0;
}

public readonly record struct PipelineHandle(ulong Pipeline, ulong Layout, bool UsesPushDescriptors);

// A clear the provider recognised in the program pair; the host clears the targets instead of drawing.
public readonly record struct SolidColorClear(float Red, float Green, float Blue, float Alpha);

// The float32x3 position stream of the vertex program, when it has one.
public readonly record struct VertexPositionStream(ulong Address, uint Stride, uint OffsetBytes);

public sealed class GraphicsPrograms
{
    public ShaderProgram Vertex { get; init; }
    public ShaderProgram Pixel { get; init; }
    public VertexInputInfo VertexInput { get; init; } = new();
    public PixelInputInfo PixelInput { get; init; } = new();

    // False when the provider could not build the programs; the executor skips the draw.
    public bool Available { get; init; } = true;

    public SolidColorClear? SolidClear { get; init; }

    public VertexPositionStream? PositionStream { get; init; }
}

public sealed class ComputeProgram
{
    public ShaderProgram Program { get; init; }
    public ComputeInputInfo Input { get; init; } = new();

    // False when the provider could not build the program; the executor skips the dispatch.
    public bool Available { get; init; } = true;

    // True when the provider ran the kernel itself; the executor records nothing.
    public bool Consumed { get; init; }
}

// The shader and pipeline caches behind the executor.
public interface IShaderPipelineProvider
{
    GraphicsPrograms GetGraphicsPrograms(
        VertexStageRegisters vertex,
        PixelStageRegisters pixel,
        ShaderInterfaceRegisters shaderInterface,
        ContextRegisters context,
        ReadOnlySpan<ColorComponentMap> targetExportMapping,
        bool pixelActive);

    PipelineHandle CreateGraphicsPipeline(
        ReadOnlySpan<ColorTargetState> colors,
        in DepthAttachmentState depth,
        VertexInputInfo vertexInput,
        PixelInputInfo? pixelInput,
        ContextRegisters context,
        in RenderingState rendering,
        PrimitiveTopology topology,
        bool primitiveRestartEnabled,
        ShaderProgram vertexProgram,
        ShaderProgram pixelProgram);

    ComputeProgram GetComputeProgram(ComputeStageRegisters compute, ShaderInterfaceRegisters shaderInterface, uint dispatchInitiator);

    PipelineHandle CreateComputePipeline(ComputeInputInfo input, ShaderProgram program);
}
