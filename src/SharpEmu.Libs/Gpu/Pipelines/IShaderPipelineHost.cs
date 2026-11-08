// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Pipelines;

// Everything a host pipeline creation reads for one graphics pipeline.
public sealed class GraphicsPipelineDescription
{
    public TessellationPipelineStages? Tessellation { get; init; }
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

public sealed record TessellationPipelineStages(ShaderProgram Control, ShaderProgram Evaluation, uint InputControlPoints);
// Why a dispatch the CPU evaluated still runs on the GPU instead of landing as a store of the stream.
internal enum KernelWriteRefusal
{
    None,
    Unsupported,
    Sparse,
    Thread,
    CrossQueue,
    Publication,
    PendingRange,
    PendingWrite,
    Image,
    Metadata,
    Count,
}

public sealed class ComputePipelineDescription
{
    public required ComputeInputInfo Input { get; init; }
    public required ShaderProgram Program { get; init; }
    public required ShaderProgramInfo Stage { get; init; }
}

public readonly record struct MeshShaderLimits(
    uint MaxInvocations,
    uint OutputVertexCapacity,
    uint OutputPrimitiveCapacity,
    uint MaxSharedMemoryBytes,
    uint MaxGroupCountX,
    uint MaxGroupCountY,
    uint MaxGroupTotalCount,
    uint MaxWorkGroupSizeX,
    uint MaxOutputMemoryBytes,
    uint MaxPayloadAndOutputMemoryBytes,
    uint OutputPerVertexGranularity,
    uint OutputPerPrimitiveGranularity,
    uint MaxOutputComponents);

// The host objects the shader and pipeline caches need: modules, pipelines, limits and guest readers.
internal interface IShaderPipelineHost
{
    uint MaxPushDescriptors { get; }

    bool ComputeWave64Supported { get; }

    // True when a 64-invocation wave64 compute workgroup runs as one 64-lane host subgroup.
    bool ComputeWave64SubgroupNative => false;

    bool ComputeRunsOn32LaneSubgroups(ComputeInputInfo input) => false;

    bool MipStatisticsSubgroupReduction => false;

    // Whether the host reads a linked program's call records at run time, through descriptor set 1.
    bool RuntimeCallRecordsSupported => false;

    // The call marks of programs linked with run-time call records (CallMarks): whether the host binds and reads them, how
    // many mark snapshots it has read, and the snapshot that last held a bit (0 when none did).
    bool CallMarksSupported => false;
    long CallMarkSnapshots => 0;
    long CallMarkedAt(uint bit) => 0;

    bool TableBlocks => false;

    bool GraphicsSubgroupOperationsEnabled { get; }

    // The device supports shaderSharedInt64Atomics, so LDS 64-bit atomics can be
    // emitted as real 64-bit atomics instead of a non-atomic 32-bit pair.
    bool SharedInt64AtomicsEnabled { get; }

    // The device supports shaderImageInt64Atomics, so an atomic on a 64-bit image texel
    // (a 32_32 UINT image) can be compiled; without it such a program is rejected.
    bool ImageInt64AtomicsEnabled => false;
    bool ShaderSignedZeroInfNanPreserveFloat32Supported => false;
    bool ExecGuardElisionEnabled => true;
    ShaderPrewarmList? ShaderPrewarm => null;
    bool PerVertexPixelInputsSupported => true;
    bool ClipDistanceEnabled => false;
    bool ShaderDrawParametersSupported => false;

    bool MeshShadersSupported => false;

    MeshShaderLimits MeshLimits => default;
    uint MeshSubgroupSize => ComputeWave64Supported ? 64u : 32u;

    RenderHostLimits Limits { get; }

    // The device can blend with a second fragment output (dualSrcBlend).
    bool SupportsDualSourceBlend => false;

    // The sample counts a pipeline without attachments can rasterize at.
    SampleCountFlags NoAttachmentSampleCounts { get; }

    bool TryResolveColorOutput(uint dataFormat, uint numberType, uint componentSwap, out Gen5PixelOutputKind outputKind, out Gen5ColorComponentMapping componentMapping);

    // Reads one guest dword the CPU may see; a range the GPU wrote is downloaded first.
    bool TryReadGuestWord(ulong address, out uint word);

    // Reads one guest dword only when no GPU work may still own the range.
    bool TryReadCleanGuestWord(ulong address, out uint word);

    // For an error report: why a clean read of the dword can fail (mapping and GPU ownership).
    string DescribeCleanRead(ulong address) => "";

    // Copies guest bytes the CPU already holds, without synchronizing. False when the GPU
    // may own the range (or, for a clean read, when a clean word read would be refused);
    // the resource cache then re-materializes instead of trusting a stale copy.
    bool TryReadResidentGuestBytes(ulong address, Span<byte> destination, bool clean) => false;

    bool TryCopyImage(in ImageRequest source, in ImageRequest destination) => false;

    uint[] NormalizeImageDescriptor(IShaderFrontend frontend, ReadOnlySpan<uint> words,
        ShaderStage stage, ulong shaderHash, string source) => frontend.NormalizeImageForResolution(words, stage, shaderHash, source, true);
    // A resident read that refuses instead of waiting for pending command-thread work.
    bool TryPeekResidentGuestBytes(ulong address, Span<byte> destination) => false;

    bool CpuWriteNeedsGpuData(ulong address, ulong size) => false;

    // Queues the words as a store at the current stream position when no earlier work may still touch the range.
    KernelWriteRefusal TryPostKernelWrite(ulong address, ReadOnlySpan<uint> words) => KernelWriteRefusal.Unsupported;

    // Creates the host module of one compiled permutation and returns its handle.
    ulong CreateShaderModule(IGuestCompiledShader shader, ShaderStage stage, ulong hash, ulong programId);

    PipelineHandle CreateGraphicsPipeline(GraphicsPipelineDescription description);

    PipelineHandle CreateComputePipeline(ComputePipelineDescription description);

    Task? GetComputePipelineCompletion(ShaderProgram program) => null;

    // Creates the compute pipeline without blocking the command stream on the host
    // shader compiler: false means the compile is still running and the caller should ask
    // again, true hands over the finished pipeline. A backend that compiles cheaply keeps
    // the blocking behaviour.
    bool TryCreateComputePipeline(ComputePipelineDescription description, out PipelineHandle handle)
    {
        handle = CreateComputePipeline(description);
        return true;
    }
}
