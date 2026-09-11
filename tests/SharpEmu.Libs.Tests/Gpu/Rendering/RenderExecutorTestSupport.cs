// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using Xunit;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Tests.Gpu.Rendering;

internal sealed class RenderExecutorFatalException(string message) : Exception(message);

internal sealed class RecordedBindings(ShaderStageResources stage, int id) : IPreparedBindings
{
    public ShaderStageResources Stage => stage;

    public int Id => id;
}

// Accepts every image format the depth builder asks about.
internal sealed class AcceptingFormatSupport : IImageFormatSupport
{
    public bool TryGetImageFormatProperties(Format format, ImageType type, ImageTiling tiling, ImageUsageFlags usage, ImageCreateFlags flags, out ImageFormatProperties properties)
    {
        properties = new ImageFormatProperties { SampleCounts = SampleCountFlags.Count1Bit | SampleCountFlags.Count2Bit | SampleCountFlags.Count4Bit | SampleCountFlags.Count8Bit };
        return true;
    }
}

// Records every host call in order and fakes the buffer and image caches.
internal sealed class RecordingRenderHost : IRenderHost
{
    private sealed class Preparation(RecordingRenderHost owner) : IResourcePreparation
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            owner.PreparationDepth--;
            owner.Calls.Add("preparation_end");
        }
    }

    public const ulong MemoryBase = 0x1_0000_0000;
    public const int MemorySize = 0x100_0000;
    public const ulong PageSize = 0x1000;
    public const ulong NullHandle = 1;
    public const ulong TransientHandle = 0x50;

    private sealed class Allocation
    {
        public ulong Handle;
        public ulong Start;
        public ulong End;
        public bool Deleted;
    }

    private readonly List<Allocation> _allocations = new();
    private readonly HashSet<ulong> _dirtyPages = new();
    private readonly Dictionary<ulong, ResourceSlotIdentifier> _images = new();
    private ulong _nextHandle = 0x100;
    private uint _nextImage = 1;
    private int _nextBindings = 1;
    private ulong _transientOffset;

    public FakeCpuMemory GuestMemory { get; } = new(MemoryBase, MemorySize);

    public List<string> Calls { get; } = new();

    public List<RenderingState> BegunRenderings { get; } = new();

    public List<DynamicDrawState> DynamicStates { get; } = new();

    public int GuestReads { get; private set; }

    public bool Recording { get; set; } = true;

    public RenderHostLimits Limits { get; set; } = new(16384, 8192, 16384, 16384);

    public IImageFormatSupport FormatSupport { get; } = new AcceptingFormatSupport();

    public bool IsRecording => Recording;

    public ImageLayout ColorLayout { get; set; } = ImageLayout.ColorAttachmentOptimal;

    public Dictionary<ulong, uint> ImageSamples { get; } = new();

    public Dictionary<ulong, ClearColorValue> ColorMetadataClears { get; } = new();

    public bool DepthMetadataClear { get; set; }

    public HashSet<ulong> MetadataAddresses { get; } = new();

    public HashSet<ulong> ClearableMetadata { get; } = new();

    public HashSet<ulong> RegisteredDcc { get; } = new();

    public HashSet<ulong> ClearableImages { get; } = new();

    public Func<ulong, ulong, ulong>? ClampOverride { get; set; }

    public int PreparationDepth { get; private set; }

    public int PreparationsOpened { get; private set; }

    // The next scheduler-touching operation needs a ring wrap; it is refused while a preparation is open.
    public bool PendingRingWrap { get; set; }

    private void TryWrapRing(string operation)
    {
        if (!PendingRingWrap)
        {
            return;
        }

        PendingRingWrap = false;
        if (PreparationDepth != 0)
        {
            Calls.Add($"wrap_refused {operation}");
            return;
        }

        LastTransient = null;
        Calls.Add($"wrap {operation}");
    }

    public IResourcePreparation BeginPreparation()
    {
        PreparationDepth++;
        PreparationsOpened++;
        Calls.Add("preparation_begin");
        return new Preparation(this);
    }

    public BufferBinding NullBuffer => new(NullHandle, 0);

    public void RunPendingOperations() => Calls.Add("pending");

    public void SetDebugInformation(RecordedOperation operation, ulong submitId, uint argument0, uint argument1, uint argument2, uint argument3, ulong argument4) =>
        Calls.Add($"debug {operation} {submitId} {argument0:X} {argument1:X} {argument2:X} {argument3:X} {argument4:X}");

    public bool TryReadGuest(ulong address, Span<byte> destination)
    {
        GuestReads++;
        return GuestMemory.TryRead(address, destination);
    }

    public ulong ClampMappedSize(ulong address, ulong size) => ClampOverride?.Invoke(address, size) ?? size;

    public ResourceSlotIdentifier FindImage(ref ImageRequest request, bool exactFormat)
    {
        var address = request.Description.Data.Address;
        if (!_images.TryGetValue(address, out var image))
        {
            image = new ResourceSlotIdentifier(_nextImage++, 1);
            _images[address] = image;
        }

        Calls.Add($"find_image {address:X} {request.Role} exact={exactFormat} -> {image.Index}");
        return image;
    }

    public ResourceSlotIdentifier ImageOf(ulong address) => _images[address];

    public void BindRenderTarget(ResourceSlotIdentifier image) => Calls.Add($"bind_target {image.Index}");

    public void ResetBindings() => Calls.Add("reset_bindings");

    public ColorAttachmentAcquisition AcquireColorAttachment(in ColorTargetState target)
    {
        var address = target.Resolution.BaseAddress;
        TryWrapRing("acquire_color");
        Calls.Add($"acquire_color {target.Slot} {address:X} image={target.Image.Index}");
        var metadataClear = ColorMetadataClears.TryGetValue(address, out var clearValue);
        return new ColorAttachmentAcquisition(
            target.Image,
            new ImageView(0x1000 + target.Image.Index),
            ColorLayout,
            ImageSamples.TryGetValue(address, out var samples) ? samples : target.Resolution.Samples,
            metadataClear,
            clearValue);
    }

    public DepthAttachmentAcquisition AcquireDepthAttachment(in DepthAttachmentState depth)
    {
        var target = depth.Target.Target;
        Calls.Add($"acquire_depth {target.DepthAddress:X} image={depth.Image.Index} clear={depth.Target.State.DepthClearEnabled}");
        return new DepthAttachmentAcquisition(
            new ImageView(0x2000 + depth.Image.Index),
            ImageSamples.TryGetValue(target.DepthAddress, out var samples) ? samples : target.Samples,
            DepthMetadataClear);
    }

    public void TransitionDepthAttachment(in DepthAttachmentState depth, ImageLayout layout, ImageAspectFlags writeAspects) =>
        Calls.Add($"transition_depth {depth.Image.Index} {layout} {writeAspects} loadClear={depth.LoadClear}");

    // Marks the pages the guest wrote so the next obtain re-uploads them.
    public void WriteGuest(ulong address, ReadOnlySpan<byte> data)
    {
        Assert.True(GuestMemory.TryWrite(address, data));
        for (var page = address & ~(PageSize - 1); page < address + (ulong)data.Length; page += PageSize)
        {
            _dirtyPages.Add(page);
        }

        Calls.Add($"cpu_write {address:X} {data.Length:X}");
    }

    public BufferBinding ObtainBuffer(ulong address, ulong size, bool isWritten)
    {
        TryWrapRing("obtain");
        var end = address + size;
        var owner = _allocations.Find(a => !a.Deleted && a.Start <= address && end <= a.End);
        if (owner is null)
        {
            var start = address;
            var stop = end;
            foreach (var overlapped in _allocations.Where(a => !a.Deleted && a.Start < end && address < a.End).ToList())
            {
                start = Math.Min(start, overlapped.Start);
                stop = Math.Max(stop, overlapped.End);
            }

            owner = new Allocation { Handle = _nextHandle++, Start = start, End = stop };
            foreach (var overlapped in _allocations.Where(a => !a.Deleted && a.Start < end && address < a.End).ToList())
            {
                Calls.Add($"merge {overlapped.Handle:X}->{owner.Handle:X}");
                overlapped.Deleted = true;
            }

            _allocations.Add(owner);
        }

        for (var page = address & ~(PageSize - 1); page < end; page += PageSize)
        {
            if (_dirtyPages.Remove(page))
            {
                Calls.Add($"upload {owner.Handle:X} {page:X}");
            }
        }

        Calls.Add($"obtain {address:X} {size:X} written={isWritten} -> {owner.Handle:X}:{address - owner.Start:X}");
        return new BufferBinding(owner.Handle, address - owner.Start);
    }

    public BufferBinding UploadTransient(ReadOnlySpan<byte> data, uint alignment)
    {
        var offset = _transientOffset;
        _transientOffset += ((ulong)data.Length + alignment - 1) & ~(alignment - 1ul);
        Calls.Add($"upload_transient {data.Length:X} align={alignment} -> {TransientHandle:X}:{offset:X}");
        LastTransient = data.ToArray();
        return new BufferBinding(TransientHandle, offset);
    }

    public byte[]? LastTransient { get; private set; }

    public void BindVertexBuffers(ReadOnlySpan<BufferBinding> bindings) =>
        Calls.Add($"bind_vertex {string.Join(",", bindings.ToArray().Select(b => $"{b.Handle:X}:{b.Offset:X}"))}");

    public void BindIndexBuffer(BufferBinding binding, IndexType type) => Calls.Add($"bind_index {binding.Handle:X}:{binding.Offset:X} {type}");

    public bool FailPrepareBindings { get; set; }

    public IPreparedBindings PrepareBindings(ShaderStageResources stage)
    {
        Assert.NotEqual(0, PreparationDepth);
        if (FailPrepareBindings)
        {
            throw Fatal($"The bindings cannot be prepared: stage={stage.Program?.Stage}.");
        }

        var id = _nextBindings++;
        Calls.Add($"prepare_bindings {stage.Program?.Stage} -> {id}");
        return new RecordedBindings(stage, id);
    }

    public void PrepareDeviceAddresses() => Calls.Add("prepare_device_addresses");

    public void BindResources(IPreparedBindings prepared) => Calls.Add($"bind_resources {((RecordedBindings)prepared).Id}");

    public void CommitBindings(PipelineBindPoint bindPoint, in PipelineHandle pipeline, ReadOnlySpan<IPreparedBindings> stages) =>
        Calls.Add($"commit {bindPoint} {pipeline.Pipeline:X} [{string.Join(",", stages.ToArray().Select(s => ((RecordedBindings)s).Id))}]");

    public void SetDynamicState(in DynamicDrawState state)
    {
        DynamicStates.Add(state);
        Calls.Add("dynamic_state");
    }

    public void BeginRendering(in RenderingState state)
    {
        BegunRenderings.Add(state);
        Calls.Add($"begin_rendering {state.Width}x{state.Height}x{state.Layers} colors={state.ColorAttachmentCount} samples={state.Samples}");
    }

    public void EndRendering() => Calls.Add("end_rendering");

    public void BindPipeline(PipelineBindPoint bindPoint, in PipelineHandle pipeline) => Calls.Add($"bind_pipeline {bindPoint} {pipeline.Pipeline:X}");

    public void Draw(uint vertexCount, uint instanceCount, uint firstVertex, uint firstInstance) =>
        Calls.Add($"draw {vertexCount} {instanceCount} {firstVertex} {firstInstance}");

    public void DrawIndexed(uint indexCount, uint instanceCount, uint firstIndex, int vertexOffset, uint firstInstance) =>
        Calls.Add($"draw_indexed {indexCount} {instanceCount} {firstIndex} {vertexOffset} {firstInstance}");

    public void Dispatch(uint groupsX, uint groupsY, uint groupsZ) => Calls.Add($"dispatch {groupsX} {groupsY} {groupsZ}");

    public void ShaderWriteBarrier(PipelineStageFlags sourceStages) => Calls.Add($"write_barrier {sourceStages}");

    public void ShaderWriteHazardBarrier() => Calls.Add("write_hazard_barrier");

    public void ShaderAccessBarrier() => Calls.Add("access_barrier");

    public bool RetainTargetlessDraws { get; set; }

    public List<(RegisterBanks Banks, GraphicsPrograms Programs, TargetlessDrawArguments Arguments)> RetainedDraws { get; } = new();

    public void ClearColorTargets(ReadOnlySpan<ColorTargetState> targets, SolidColorClear clear) =>
        Calls.Add($"clear_targets {string.Join(",", targets.ToArray().Select(t => t.Slot))} {clear.Red},{clear.Green},{clear.Blue},{clear.Alpha}");


    public bool TryRetainTargetlessDraw(RegisterBanks banks, GraphicsPrograms programs, in TargetlessDrawArguments arguments)
    {
        Calls.Add($"retain_targetless {arguments.SubmitId}");
        if (!RetainTargetlessDraws)
        {
            return false;
        }

        RetainedDraws.Add((banks, programs, arguments));
        return true;
    }

    public void MarkGpuWritten(ResourceSlotIdentifier image) => Calls.Add($"mark_written {image.Index}");

    public void ResolveImage(ResourceSlotIdentifier source, uint sourceMip, uint sourceLayer, ResourceSlotIdentifier destination, uint destinationMip, uint destinationLayer) =>
        Calls.Add($"resolve {source.Index}:{sourceMip}:{sourceLayer} -> {destination.Index}:{destinationMip}:{destinationLayer}");

    public bool IsMetadata(ulong address)
    {
        Calls.Add($"is_metadata {address:X}");
        return MetadataAddresses.Contains(address);
    }

    public bool ClearMetadata(ulong address)
    {
        Calls.Add($"clear_metadata {address:X}");
        return ClearableMetadata.Contains(address);
    }

    public bool TryClearImageFromBuffer(ulong address, ulong size, uint packedClear)
    {
        Calls.Add($"clear_image {address:X} {size:X} {packedClear:X8}");
        return ClearableImages.Contains(address);
    }

    public bool TryAbsorbDccFill(ulong address, ulong size, uint fillValue)
    {
        Calls.Add($"absorb_dcc {address:X} {size:X} {fillValue:X8}");
        return RegisteredDcc.Contains(address);
    }

    public Exception Fatal(string message) => new RenderExecutorFatalException(message);
}

// Hands back the configured programs and records every pipeline request.
internal sealed class FakePipelineProvider : IShaderPipelineProvider
{
    public GraphicsPrograms Graphics { get; set; } = RenderExecutorFixtures.Programs();

    public ComputeProgram Compute { get; set; } = RenderExecutorFixtures.ComputeProgram();

    public List<string> Calls { get; } = new();

    public List<RenderingState> PipelineRenderings { get; } = new();

    public List<(PrimitiveTopology Topology, bool Restart, bool PixelActive)> PipelineRequests { get; } = new();

    public bool LastDispatchThreadDimensions { get; private set; }

    public List<ColorComponentMap[]> ExportMappings { get; } = new();

    public GraphicsPrograms GetGraphicsPrograms(
        VertexStageRegisters vertex,
        PixelStageRegisters pixel,
        ShaderInterfaceRegisters shaderInterface,
        ContextRegisters context,
        ReadOnlySpan<ColorComponentMap> targetExportMapping,
        bool pixelActive)
    {
        Calls.Add($"get_graphics_programs pixelActive={pixelActive}");
        ExportMappings.Add(targetExportMapping.ToArray());
        return Graphics;
    }

    public PipelineHandle CreateGraphicsPipeline(
        ReadOnlySpan<ColorTargetState> colors,
        in DepthAttachmentState depth,
        VertexInputInfo vertexInput,
        PixelInputInfo? pixelInput,
        ContextRegisters context,
        in RenderingState rendering,
        PrimitiveTopology topology,
        bool primitiveRestartEnabled,
        ShaderProgram vertexProgram,
        ShaderProgram pixelProgram)
    {
        Calls.Add($"create_graphics_pipeline colors={colors.Length} depth={depth.HasTarget} topology={topology} restart={primitiveRestartEnabled}");
        PipelineRenderings.Add(rendering);
        PipelineRequests.Add((topology, primitiveRestartEnabled, pixelInput is not null));
        return new PipelineHandle(0xA1, 0xB1, false);
    }

    public ComputeProgram GetComputeProgram(ComputeStageRegisters compute, ShaderInterfaceRegisters shaderInterface, uint dispatchInitiator)
    {
        var dispatchThreadDimensions = (dispatchInitiator & (1u << 5)) != 0;
        Calls.Add($"get_compute_program threadDimensions={dispatchThreadDimensions}");
        LastDispatchThreadDimensions = dispatchThreadDimensions;
        return Compute;
    }

    public PipelineHandle CreateComputePipeline(ComputeInputInfo input, ShaderProgram program)
    {
        Calls.Add("create_compute_pipeline");
        return new PipelineHandle(0xC1, 0xD1, false);
    }
}

// Register banks and programs for a draw that renders into one 64x64 color target.
internal static class RenderExecutorFixtures
{
    public const ulong ColorBase = RecordingRenderHost.MemoryBase;
    public const ulong SecondColorBase = RecordingRenderHost.MemoryBase + 0x10_0000;
    public const ulong DepthBase = RecordingRenderHost.MemoryBase + 0x20_0000;
    public const ulong StencilBase = RecordingRenderHost.MemoryBase + 0x30_0000;
    public const ulong VertexBase = RecordingRenderHost.MemoryBase + 0x40_0000;
    public const ulong IndexBase = RecordingRenderHost.MemoryBase + 0x50_0000;
    public const ulong VertexShader = 0x2_0000_0000;
    public const ulong PixelShader = 0x2_0001_0000;
    public const ulong ComputeShader = 0x2_0002_0000;
    public const uint PrimitiveTriangleList = 4;
    public const uint PrimitiveTriangleStrip = 6;

    private static Exception Fatal(string message) => new RenderExecutorFatalException(message);

    public static RegisterBanks Banks(uint primitiveType = PrimitiveTriangleList, bool withDepth = false, bool withPixel = true)
    {
        var banks = new RegisterBanks(Fatal);
        var context = banks.Context;
        context.ColorTargets[0] = RegisterWords.Color(ColorBase, 64, 64);
        context.RenderTargetMask = 0xF;
        context.ShaderInterface.ColorShaderMask = 0xF;
        context.ScreenViewport.Viewports[0] = new ViewportRegisters { XScale = 32, XOffset = 32, YScale = -32, YOffset = 32, ZScale = 0.5f, ZOffset = 0.5f, MaxDepth = 1 };
        if (withDepth)
        {
            context.DepthTarget = RegisterWords.Depth(DepthBase, 64, 64);
        }

        banks.Shader.Vertex.ExportAddress = VertexShader;
        banks.Shader.Pixel.Address = withPixel ? PixelShader : 0;
        banks.Shader.Compute.Address = ComputeShader;
        banks.Shader.Compute.ThreadsX = 64;
        banks.Shader.Compute.ThreadsY = 1;
        banks.Shader.Compute.ThreadsZ = 1;
        banks.UserConfig.PrimitiveType = primitiveType;
        return banks;
    }

    public static ShaderProgramInfo Program(ShaderStageKind stage, BufferResourceInfo[]? buffers = null, ImageResourceInfo[]? images = null, uint userDataBase = 0, bool usesDeviceAddresses = false, bool exclusiveOr = false, int vertexOffsetScalar = ShaderProgramInfo.NoScalarRegister, int instanceOffsetScalar = ShaderProgramInfo.NoScalarRegister, uint parameterExportMask = 1) =>
        new()
        {
            Stage = stage,
            Hash = 0x1234_5678_9ABC_DEF0,
            UserDataBase = userDataBase,
            ParameterExportMask = parameterExportMask,
            Buffers = buffers ?? [],
            Images = images ?? [],
            UsesDeviceAddresses = usesDeviceAddresses,
            HasBitwiseExclusiveOr = exclusiveOr,
            VertexOffsetScalarRegister = vertexOffsetScalar,
            InstanceOffsetScalarRegister = instanceOffsetScalar,
        };

    public static ShaderStageResources Stage(ShaderProgramInfo program, uint[][]? buffers = null, uint[]? userData = null) =>
        new(program, new ResourceSnapshot { Buffers = buffers ?? [], UserData = userData ?? [] });

    public static GraphicsPrograms Programs(VertexInputBuffer[]? vertexBuffers = null, bool fetchEmbedded = false, ShaderStageResources? vertexStage = null, ShaderStageResources? pixelStage = null, uint pixelInputs = 1, VertexPositionStream? positionStream = null, SolidColorClear? solidClear = null) =>
        new()
        {
            Vertex = new ShaderProgram(0x11),
            Pixel = new ShaderProgram(0x22),
            VertexInput = new VertexInputInfo
            {
                Buffers = vertexBuffers ?? [],
                FetchEmbedded = fetchEmbedded,
                Stage = vertexStage ?? Stage(Program(ShaderStageKind.Vertex)),
            },
            PixelInput = new PixelInputInfo { InputCount = pixelInputs, Stage = pixelStage ?? Stage(Program(ShaderStageKind.Pixel)) },
            PositionStream = positionStream,
            SolidClear = solidClear,
        };

    public static uint[] BufferDescriptor(ulong address, uint stride, uint records, uint format = BufferDescriptorWords.Format32x4UInt, bool swizzle = false, uint indexStride = 0, bool addThreadId = false) =>
    [
        (uint)address,
        (uint)((address >> 32) & 0xFFFF) | (stride << 16) | (swizzle ? 1u << 31 : 0),
        records,
        4 | (format << 12) | (indexStride << 21) | (addThreadId ? 1u << 23 : 0),
    ];

    public static ComputeProgram ComputeProgram(ShaderStageResources? stage = null, bool threadDimensions = false, uint threadsX = 64, uint threadsY = 1, uint threadsZ = 1) =>
        new()
        {
            Program = new ShaderProgram(0x33),
            Input = new ComputeInputInfo
            {
                ThreadsX = threadsX,
                ThreadsY = threadsY,
                ThreadsZ = threadsZ,
                DispatchThreadDimensions = threadDimensions,
                GroupIdX = true,
                ThreadIdCount = 1,
                WaveSize = 64,
                Stage = stage ?? Stage(Program(ShaderStageKind.Compute)),
            },
        };

    public static DrawIndexedArguments Indexed(uint count, uint instances = 1, uint indexType = 0, int baseVertex = 0, uint firstInstance = 0, DrawOffsetSource source = DrawOffsetSource.Packet, ulong indexAddress = IndexBase) =>
        new(0, 0, count, indexAddress, indexType, instances, baseVertex, firstInstance, source);

    public static DrawAutoArguments Auto(uint count, uint instances = 1, uint firstVertex = 0, uint firstInstance = 0, DrawOffsetSource source = DrawOffsetSource.Packet) =>
        new(0, 0, count, instances, firstVertex, firstInstance, source);
}
