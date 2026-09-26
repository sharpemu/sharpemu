// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Pipelines;

// Everything a host pipeline creation reads for one graphics pipeline.
public sealed class GraphicsPipelineDescription
{
    public required PipelineRenderingState Rendering { get; init; }
    public required PipelineVertexInputState VertexInput { get; init; }
    public required VertexInputInfo VertexInfo { get; init; }
    public required ShaderProgram VertexProgram { get; init; }
    public required ShaderProgramInfo VertexStage { get; init; }
    public PixelInputInfo? PixelInfo { get; init; }
    public ShaderProgram PixelProgram { get; init; }
    public ShaderProgramInfo? PixelStage { get; init; }
    public required PipelineStaticParameters StaticParameters { get; init; }
}

public sealed class ComputePipelineDescription
{
    public required ComputeInputInfo Input { get; init; }
    public required ShaderProgram Program { get; init; }
    public required ShaderProgramInfo Stage { get; init; }
}

// The host objects the shader and pipeline caches need: modules, pipelines, limits and guest readers.
internal interface IShaderPipelineHost
{
    uint MaxPushDescriptors { get; }

    bool ComputeWave64Supported { get; }

    bool GraphicsSubgroupOperationsEnabled { get; }

    // The device supports shaderSharedInt64Atomics, so LDS 64-bit atomics can be
    // emitted as real 64-bit atomics instead of a non-atomic 32-bit pair.
    bool SharedInt64AtomicsEnabled { get; }

    RenderHostLimits Limits { get; }

    // The sample counts a pipeline without attachments can rasterize at.
    SampleCountFlags NoAttachmentSampleCounts { get; }

    bool TryResolveColorOutput(uint dataFormat, uint numberType, uint componentSwap, out Gen5PixelOutputKind outputKind, out Gen5ColorComponentMapping componentMapping);

    // Reads one guest dword the CPU may see; a range the GPU wrote is downloaded first.
    bool TryReadGuestWord(ulong address, out uint word);

    // Reads one guest dword only when no GPU work may still own the range.
    bool TryReadCleanGuestWord(ulong address, out uint word);

    // Copies guest bytes the CPU already holds, without synchronizing. False when the GPU
    // may own the range (or, for a clean read, when a clean word read would be refused);
    // the resource cache then re-materializes instead of trusting a stale copy.
    bool TryReadResidentGuestBytes(ulong address, Span<byte> destination, bool clean) => false;

    // Creates the host module of one compiled permutation and returns its handle.
    ulong CreateShaderModule(IGuestCompiledShader shader, ShaderStage stage, ulong hash, ulong programId);

    PipelineHandle CreateGraphicsPipeline(GraphicsPipelineDescription description);

    PipelineHandle CreateComputePipeline(ComputePipelineDescription description);
}
