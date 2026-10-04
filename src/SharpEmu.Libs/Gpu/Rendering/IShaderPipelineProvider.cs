// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Images;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Rendering;

// A compiled shader module the pipeline provider hands back; zero means no program.
public readonly record struct ShaderProgram(ulong Id, ulong Module = 0)
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

    // Blending is off for a fill the provider recognised as an overwrite.
    public bool DisableBlending { get; init; }
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
    // Geometry front/back objects are registered as one program by AGC.  The
    // executor uses this capability check before admitting the legacy ES+GS
    // register path; providers without a registry keep rejecting that path.
    bool IsFusedGraphicsProgram(ulong codeAddress) => false;

    // Returns the registered continuation so the executor can validate a
    // non-zero GS address instead of rejecting every split-address encoding.
    bool TryGetFusedGraphicsProgram(ulong codeAddress, out ulong continuationAddress)
    {
        continuationAddress = 0;
        return IsFusedGraphicsProgram(codeAddress);
    }

    // True only when the provider can lower a fused ES+GS program to a native mesh stage.
    bool SupportsFusedGeometry => false;

    GraphicsPrograms GetGraphicsPrograms(
        VertexStageRegisters vertex,
        PixelStageRegisters pixel,
        ShaderInterfaceRegisters shaderInterface,
        ContextRegisters context,
        UserConfigRegisters userConfig,
        ReadOnlySpan<ColorComponentMap> targetExportMapping,
        bool pixelActive,
        bool depthBound);

    // The executor can discover stale colour-register slots only after decoding the pixel
    // program. Providers that specialize pixel outputs by host attachment location use this
    // overload when the surviving guest slots are known. The default keeps lightweight and
    // external providers source-compatible; the production cache implements the mask.
    GraphicsPrograms GetGraphicsPrograms(
        VertexStageRegisters vertex,
        PixelStageRegisters pixel,
        ShaderInterfaceRegisters shaderInterface,
        ContextRegisters context,
        UserConfigRegisters userConfig,
        ReadOnlySpan<ColorComponentMap> targetExportMapping,
        uint boundColorSlots,
        bool pixelActive,
        bool depthBound) =>
        GetGraphicsPrograms(
            vertex,
            pixel,
            shaderInterface,
            context,
            userConfig,
            targetExportMapping,
            pixelActive,
            depthBound);

    PipelineHandle CreateGraphicsPipeline(
        ReadOnlySpan<ColorTargetState> colors,
        in DepthAttachmentState depth,
        VertexInputInfo vertexInput,
        PixelInputInfo? pixelInput,
        ContextRegisters context,
        in RenderingState rendering,
        PrimitiveTopology topology,
        bool primitiveRestartEnabled,
        bool disableBlending,
        ShaderProgram vertexProgram,
        ShaderProgram pixelProgram);

    // The dispatch dimensions are groups, or threads when the initiator counts threads.
    ComputeProgram GetComputeProgram(
        ComputeStageRegisters compute,
        ShaderInterfaceRegisters shaderInterface,
        uint dispatchInitiator,
        uint dimensionX,
        uint dimensionY,
        uint dimensionZ);

    PipelineHandle CreateComputePipeline(ComputeInputInfo input, ShaderProgram program);

    // False means the host is still compiling this pipeline; asking again returns it once
    // the compile finishes, without starting a second one.
    bool TryCreateComputePipeline(ComputeInputInfo input, ShaderProgram program, out PipelineHandle handle)
    {
        handle = CreateComputePipeline(input, program);
        return true;
    }
}
