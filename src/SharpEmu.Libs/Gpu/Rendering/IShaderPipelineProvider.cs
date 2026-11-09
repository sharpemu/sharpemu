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
    public TessellationDrawPrograms? Tessellation { get; init; }
    public GraphicsPrograms()
        : this(new VertexInputInfo(), new PixelInputInfo())
    {
    }

    public GraphicsPrograms(VertexInputInfo vertexInput, PixelInputInfo pixelInput)
    {
        VertexInput = vertexInput;
        PixelInput = pixelInput;
    }

    public ShaderProgram Vertex { get; init; }
    public ShaderProgram Pixel { get; init; }
    public VertexInputInfo VertexInput { get; init; }
    public MeshDrawConfiguration? MeshInput { get; init; }
    public PixelInputInfo PixelInput { get; init; }

    // False when the provider could not build the programs; the executor skips the draw.
    public bool Available { get; init; } = true;

    public SolidColorClear? SolidClear { get; init; }

    public VertexPositionStream? PositionStream { get; init; }

    // Blending is off for a fill the provider recognised as an overwrite.
    public bool DisableBlending { get; init; }

    // The pixel program was compiled without the color targets it never writes,
    // so the executor drops them too and attachments stay aligned with its outputs.
    public bool DropUnwrittenColorTargets { get; init; }
}

public sealed record TessellationDrawPrograms(SharpEmu.ShaderCompiler.Gen5TessellationInfo Configuration,
    SharpEmu.ShaderCompiler.Gen5TessellationHullInfo HullConfiguration, ComputeProgram Hull);
// Learns when a known write of one producer is read.
internal interface IKnownWriteDemand
{
    void NoteRead();
}

public sealed class KnownGpuWrite(ulong address, uint[] buffer, int count, bool pooled = false)
{
    private readonly object _gate = new();
    private bool _released;
    private int _read;

    internal IKnownWriteDemand? Demand { get; init; }

    public ulong Address { get; } = address;

    internal void NoteRead()
    {
        if (Demand is { } demand && Interlocked.Exchange(ref _read, 1) == 0)
        {
            demand.NoteRead();
        }
    }

    public int ByteCount => count * sizeof(uint);

    public ReadOnlySpan<uint> Words => buffer.AsSpan(0, count);

    public bool TryCopyWords(int offsetBytes, Span<byte> destination)
    {
        lock (_gate)
        {
            if (_released)
            {
                return false;
            }

            System.Runtime.InteropServices.MemoryMarshal.AsBytes(buffer.AsSpan(0, count)).Slice(offsetBytes, destination.Length).CopyTo(destination);
            return true;
        }
    }

    public void Release()
    {
        lock (_gate)
        {
            if (pooled && !_released)
            {
                _released = true;
                System.Buffers.ArrayPool<uint>.Shared.Return(buffer);
            }
        }
    }
}

public sealed record FrontendImageCopy(ImageRequest Source, ImageRequest Destination, Func<ComputeProgram> Fallback);

public sealed class ComputeProgram
{
    public FrontendImageCopy? ImageCopy { get; init; }
    public KnownGpuWrite? KnownWrite { get; init; }

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
    bool MeshShadersSupported => false;

    void DumpRejectedGraphics(RegisterBanks banks, bool autoDraw) { }

    GraphicsPrograms GetGraphicsPrograms(
        RegisterBanks banks,
        ReadOnlySpan<ColorComponentMap> targetExportMapping,
        bool pixelActive,
        bool depthBound);

    GraphicsPrograms GetMeshGraphicsPrograms(
        RegisterBanks banks,
        UserConfigRegisters userConfig,
        ReadOnlySpan<ColorComponentMap> targetExportMapping,
        bool pixelActive,
        bool depthBound) => new() { Available = false };
    GraphicsPrograms GetGraphicsPrograms(
        RegisterBanks banks,
        ReadOnlySpan<ColorComponentMap> targetExportMapping,
        bool pixelActive,
        bool depthBound,
        int startInstanceUserData) =>
        GetGraphicsPrograms(banks, targetExportMapping, pixelActive, depthBound);

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

    ComputeProgram GetComputeProgram(RegisterBanks banks, uint dispatchInitiator, uint dimensionX, uint dimensionY, uint dimensionZ) =>
        GetComputeProgram(banks.Shader.Compute, banks.Context.ShaderInterface, dispatchInitiator, dimensionX, dimensionY, dimensionZ);

    // An indirect dispatch reads its dimensions on the GPU, so the provider cannot know its result.
    ComputeProgram GetComputeProgram(RegisterBanks banks, uint dispatchInitiator, uint dimensionX, uint dimensionY, uint dimensionZ, bool indirect) =>
        GetComputeProgram(banks, dispatchInitiator, dimensionX, dimensionY, dimensionZ);

    PipelineHandle CreateComputePipeline(ComputeInputInfo input, ShaderProgram program);

    Task? GetComputePipelineCompletion(ShaderProgram program) => null;

    // False means the host is still compiling this pipeline; asking again returns it once
    // the compile finishes, without starting a second one.
    bool TryCreateComputePipeline(ComputeInputInfo input, ShaderProgram program, out PipelineHandle handle)
    {
        handle = CreateComputePipeline(input, program);
        return true;
    }
}
