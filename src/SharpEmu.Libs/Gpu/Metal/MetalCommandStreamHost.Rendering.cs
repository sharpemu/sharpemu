// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using SharpEmu.ShaderCompiler;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.ShaderCompiler.Metal;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Metal;

// The render host over the Metal seam: the executor's resolved state becomes one copied draw record per draw.
internal sealed partial class MetalCommandStreamHost : IRenderHost, AgcExports.IHostPipelineFactory
{
    private const uint MaxDimension = 16384;
    private const ulong MappedPageSize = 4096;
    private const uint SingleRectangleVertexCount = 4;

    // Every image format the target builders ask about is accepted; the Metal presenter picks its own formats.
    private sealed class AcceptingFormatSupport : IImageFormatSupport
    {
        public bool TryGetImageFormatProperties(Format format, ImageType type, ImageTiling tiling, ImageUsageFlags usage, ImageCreateFlags flags, out ImageFormatProperties properties)
        {
            properties = new ImageFormatProperties
            {
                SampleCounts = SampleCountFlags.Count1Bit | SampleCountFlags.Count2Bit | SampleCountFlags.Count4Bit | SampleCountFlags.Count8Bit,
                MaxExtent = new Extent3D(MaxDimension, MaxDimension, 1),
                MaxMipLevels = 15,
                MaxArrayLayers = 2048,
            };
            return true;
        }
    }

    private sealed class PreparedStage(ShaderStageResources stage, AgcExports.CompiledStageProgram program) : IPreparedBindings
    {
        public ShaderStageResources Stage => stage;

        public AgcExports.CompiledStageProgram Program => program;
    }

    private sealed class Preparation(MetalCommandStreamHost owner) : IResourcePreparation
    {
        public void Dispose()
        {
            if (ReferenceEquals(owner._preparation, this))
            {
                owner._preparation = null;
            }
        }
    }

    // A guest range the executor obtained, or bytes it uploaded; the draw record copies from it.
    private sealed record BoundBuffer(ulong Address, ulong Size, byte[]? Bytes);

    private sealed record GraphicsPipelineRecord(
        AgcExports.CompiledStageProgram Vertex,
        AgcExports.CompiledStageProgram? Pixel,
        VertexInputInfo VertexInput,
        uint AttributeCount,
        PrimitiveTopology Topology);

    private sealed record ComputePipelineRecord(AgcExports.CompiledStageProgram Program, ComputeInputInfo Input);

    private static readonly IImageFormatSupport _formatSupport = new AcceptingFormatSupport();
    private readonly Dictionary<ulong, ResourceSlotIdentifier> _imageIdentifiers = new();
    private readonly Dictionary<ResourceSlotIdentifier, GuestRenderTarget> _guestTargets = new();
    private readonly Dictionary<ulong, BoundBuffer> _buffers = new();
    private readonly Dictionary<ulong, object> _pipelines = new();
    private readonly List<ColorTargetState> _colorTargets = new();
    private readonly List<AgcExports.CompiledStageProgram> _committedPrograms = new();
    private ulong _nextBufferHandle = 1;
    private ulong _nextPipelineHandle = 1;
    private uint _nextImageIndex = 1;
    private Preparation? _preparation;
    private DepthAttachmentState _depth;
    private bool _hasDepth;
    private DynamicDrawState _dynamicState;
    private BufferBinding[] _vertexBindings = [];
    private BufferBinding? _indexBinding;
    private IndexType _indexType;
    private object? _boundPipeline;

    private IGuestGpuBackend Backend => _backend;

    private ContextRegisters RequireContext() =>
        Translation.CurrentContextRegisters ?? throw Fatal("The command stream has no current context registers.");


    public bool TryResolveColorOutput(
        uint dataFormat,
        uint numberType,
        uint componentSwap,
        out Gen5PixelOutputKind outputKind,
        out Gen5ColorComponentMapping componentMapping)
    {
        if (MetalGuestFormats.TryDecodeRenderTargetFormat(
                dataFormat,
                numberType,
                componentSwap,
                out var format))
        {
            outputKind = format.OutputKind;
            componentMapping = format.ExportMapping;
            return true;
        }

        outputKind = default;
        componentMapping = default;
        return false;
    }
    private bool CanReadGuest(ulong address)
    {
        Span<byte> probe = stackalloc byte[1];
        return Memory.TryRead(address, probe);
    }

    RenderHostLimits IRenderHost.Limits => new(MaxDimension, MaxDimension, MaxDimension, MaxDimension);

    IImageFormatSupport IRenderHost.FormatSupport => _formatSupport;

    // The ordered queue of the presenter takes every record; nothing waits on a command buffer.
    bool IRenderHost.IsRecording => true;

    void IRenderHost.RunPendingOperations() => RunPendingCommands();

    void IRenderHost.SetDebugInformation(RecordedOperation operation, ulong submitId, uint argument0, uint argument1, uint argument2, uint argument3, ulong argument4)
    {
        _ = (operation, submitId, argument0, argument1, argument2, argument3, argument4);
    }

    ulong IRenderHost.ClampMappedSize(ulong address, ulong size)
    {
        if (address == 0 || size == 0 || size > ulong.MaxValue - address || !CanReadGuest(address))
        {
            throw Fatal($"The buffer range starts in unmapped memory: address=0x{address:X16} size=0x{size:X16}.");
        }

        var end = address + size;
        var page = (address & ~(MappedPageSize - 1)) + MappedPageSize;
        while (page < end && CanReadGuest(page))
        {
            page += MappedPageSize;
        }

        return Math.Min(size, page - address);
    }

    ResourceSlotIdentifier IRenderHost.FindImage(ref ImageRequest request, bool exactFormat)
    {
        _ = exactFormat;
        var address = request.Description.Data.Address;
        if (!_imageIdentifiers.TryGetValue(address, out var identifier))
        {
            identifier = new ResourceSlotIdentifier(_nextImageIndex++, 1);
            _imageIdentifiers.Add(address, identifier);
        }

        return identifier;
    }

    void IRenderHost.BindRenderTarget(ResourceSlotIdentifier image)
    {
        _ = image;
    }

    // The records of one draw or dispatch live until the executor resets; the submitted copy owns its bytes.
    void IRenderHost.ResetBindings()
    {
        _colorTargets.Clear();
        _committedPrograms.Clear();
        _buffers.Clear();
        _pipelines.Clear();
        _hasDepth = false;
        _vertexBindings = [];
        _indexBinding = null;
        _boundPipeline = null;
    }

    // The view handle carries the image identifier; the presenter keys its images by guest address.
    ColorAttachmentAcquisition IRenderHost.AcquireColorAttachment(in ColorTargetState target)
    {
        var context = RequireContext();
        _guestTargets[target.Image] = GuestTargetOf(in target, context.ColorTargets[target.Slot], context.RenderTargetMaskForSlot(target.Slot));
        _colorTargets.Add(target);
        return new ColorAttachmentAcquisition(target.Image, new ImageView(target.Image.Index), ImageLayout.General, target.Resolution.Samples, false, default);
    }

    DepthAttachmentAcquisition IRenderHost.AcquireDepthAttachment(in DepthAttachmentState depth) =>
        new(new ImageView(depth.Image.Index), depth.Target.Target.Samples, false);

    void IRenderHost.TransitionDepthAttachment(in DepthAttachmentState depth, ImageLayout layout, ImageAspectFlags writeAspects)
    {
        _ = (layout, writeAspects);
        _depth = depth;
        _hasDepth = true;
    }

    BufferBinding IRenderHost.NullBuffer => new(0, 0);

    BufferBinding IRenderHost.ObtainBuffer(ulong address, ulong size, bool isWritten)
    {
        _ = isWritten;
        var handle = _nextBufferHandle++;
        _buffers[handle] = new BoundBuffer(address, size, null);
        return new BufferBinding(handle, 0);
    }

    BufferBinding IRenderHost.UploadTransient(ReadOnlySpan<byte> data, uint alignment)
    {
        _ = alignment;
        var handle = _nextBufferHandle++;
        _buffers[handle] = new BoundBuffer(0, (ulong)data.Length, data.ToArray());
        return new BufferBinding(handle, 0);
    }

    void IRenderHost.BindVertexBuffers(ReadOnlySpan<BufferBinding> bindings) => _vertexBindings = bindings.ToArray();

    void IRenderHost.BindIndexBuffer(BufferBinding binding, IndexType type)
    {
        _indexBinding = binding;
        _indexType = type;
    }

    IResourcePreparation IRenderHost.BeginPreparation()
    {
        if (_preparation is not null)
        {
            throw Fatal("A resource preparation is already open.");
        }

        _preparation = new Preparation(this);
        return _preparation;
    }

    private static AgcExports.CompiledStageProgram RequireCompiledProgram(ShaderStageResources stage) =>
        stage.Program as AgcExports.CompiledStageProgram
        ?? throw SubmissionScheduler.Fatal($"The stage program is not a compiled program: stage={stage.Program?.Stage} hash=0x{stage.Program?.Hash ?? 0:X16}.");

    IPreparedBindings IRenderHost.PrepareBindings(ShaderStageResources stage) => new PreparedStage(stage, RequireCompiledProgram(stage));

    void IRenderHost.PrepareDeviceAddresses() =>
        throw Fatal("A program uses device addresses, which the shader pipeline provider does not produce yet.");

    void IRenderHost.BindResources(IPreparedBindings prepared)
    {
        _ = prepared;
    }

    void IRenderHost.CommitBindings(PipelineBindPoint bindPoint, in PipelineHandle pipeline, ReadOnlySpan<IPreparedBindings> stages)
    {
        _ = (bindPoint, pipeline);
        _committedPrograms.Clear();
        foreach (var stage in stages)
        {
            _committedPrograms.Add(((PreparedStage)stage).Program);
        }
    }

    void IRenderHost.SetDynamicState(in DynamicDrawState state) => _dynamicState = state;

    void IRenderHost.BeginRendering(in RenderingState state)
    {
        _ = state;
    }

    void IRenderHost.EndRendering()
    {
    }

    void IRenderHost.BindPipeline(PipelineBindPoint bindPoint, in PipelineHandle pipeline)
    {
        _ = bindPoint;
        _boundPipeline = _pipelines.TryGetValue(pipeline.Pipeline, out var record)
            ? record
            : throw Fatal($"The pipeline handle is unknown: pipeline={pipeline.Pipeline} layout={pipeline.Layout}.");
    }

    private GraphicsPipelineRecord RequireGraphicsPipeline() =>
        _boundPipeline as GraphicsPipelineRecord ?? throw Fatal("No graphics pipeline is bound for the draw.");

    // The guest primitive code the presenter maps itself; a patch list is the rectangle list.
    private static uint PrimitiveTypeOf(PrimitiveTopology topology) => topology switch
    {
        PrimitiveTopology.PointList => 1,
        PrimitiveTopology.LineList => 2,
        PrimitiveTopology.LineStrip => 3,
        PrimitiveTopology.TriangleFan => 5,
        PrimitiveTopology.TriangleStrip => 6,
        PrimitiveTopology.PatchList => 7,
        _ => 4,
    };

    private BoundBuffer RequireBuffer(in BufferBinding binding) =>
        _buffers.TryGetValue(binding.Handle, out var buffer) ? buffer : throw Fatal($"The buffer handle is unknown: handle={binding.Handle} offset=0x{binding.Offset:X}.");

    private byte[] ReadBound(BoundBuffer buffer, ulong offset, ulong size)
    {
        if (buffer.Bytes is { } bytes)
        {
            return bytes.AsSpan((int)offset, (int)Math.Min(size, (ulong)bytes.Length - offset)).ToArray();
        }

        var data = new byte[checked((int)size)];
        if (!Memory.TryRead(buffer.Address + offset, data))
        {
            throw Fatal($"The bound buffer is unreadable: address=0x{buffer.Address + offset:X16} size=0x{size:X}.");
        }

        return data;
    }

    private IReadOnlyList<GuestVertexBuffer> CopyVertexBuffers(GraphicsPipelineRecord pipeline)
    {
        var buffers = pipeline.VertexInput.Buffers;
        var attributes = pipeline.Vertex.VertexAttributes;
        var bytes = new byte[buffers.Length][];
        for (var index = 0; index < buffers.Length; index++)
        {
            bytes[index] = index < _vertexBindings.Length && _vertexBindings[index].Handle != 0 && buffers[index].Size != 0
                ? ReadBound(RequireBuffer(in _vertexBindings[index]), _vertexBindings[index].Offset, buffers[index].Size)
                : [];
        }

        var result = new GuestVertexBuffer[attributes.Length];
        for (var index = 0; index < attributes.Length; index++)
        {
            var attribute = attributes[index];
            var data = bytes[attribute.BufferIndex];
            result[index] = new GuestVertexBuffer(
                attribute.Location,
                attribute.ComponentCount,
                attribute.DataFormat,
                attribute.NumberFormat,
                buffers[attribute.BufferIndex].Address,
                buffers[attribute.BufferIndex].Stride,
                attribute.OffsetBytes,
                data,
                data.Length,
                Pooled: false,
                attribute.PerInstance);
        }

        return result;
    }

    private GuestIndexBuffer? CopyIndexBuffer(uint indexCount)
    {
        if (_indexBinding is not { } binding)
        {
            return null;
        }

        var buffer = RequireBuffer(in binding);
        var indexSize = _indexType == IndexType.Uint32 ? 4u : 2u;
        var bytes = ReadBound(buffer, binding.Offset, Math.Min(buffer.Size - binding.Offset, indexCount * (ulong)indexSize));
        return new GuestIndexBuffer(bytes, bytes.Length, _indexType == IndexType.Uint32, Pooled: false) { GuestAddress = buffer.Bytes is null ? buffer.Address + binding.Offset : 0 };
    }

    private static GuestRenderTarget GuestTargetOf(in ColorTargetState target, in ColorTargetWords words, uint writeMask) =>
        new(
            target.Resolution.BaseAddress,
            target.Resolution.Extent.Width,
            target.Resolution.Extent.Height,
            (uint)words.Layout,
            (uint)words.NumberType,
            MipLevels: 1,
            ComponentSwap: (uint)words.Order,
            TileMode: (uint)words.TileMode,
            Registers: words,
            WriteMask: writeMask);

    private GuestDepthTarget? GuestDepthOf(ContextRegisters context)
    {
        if (!_hasDepth)
        {
            return null;
        }

        var resolution = _depth.Target.Target;
        var state = _depth.Target.State;
        var words = context.DepthTarget;
        return new GuestDepthTarget(
            words.ZReadBase,
            words.ZWriteBase,
            resolution.Width,
            resolution.Height,
            (uint)words.DepthFormat,
            (words.ZInfo >> 4) & 0x1Fu,
            state.DepthClearValue,
            ReadOnly: words.DepthWriteDisabled || words.ZWriteBase == 0,
            HtileAddress: resolution.HtileAddress,
            HtileBaseLayer: words.SliceStart,
            HtileAcceleration: resolution.HasHtile && words.HtileAcceleration,
            HasStencil: resolution.HasStencil,
            StencilReadAddress: words.StencilReadBase,
            StencilWriteAddress: words.StencilWriteBase,
            StencilReadOnly: words.StencilWriteDisabled || words.StencilWriteBase == 0,
            Registers: words);
    }

    private static GuestStencilFaceState StencilFaceOf(in StencilOperations operations, in StencilMasks masks, byte operationValue) =>
        new((uint)operations.FailOperation, (uint)operations.PassOperation, (uint)operations.DepthFailOperation, (uint)operations.Compare, masks.CompareMask, masks.WriteMask, masks.Reference, operationValue);

    private GuestRenderState RenderStateOf(ContextRegisters context, IReadOnlyList<ColorTargetState> targets, bool disableBlending)
    {
        var blends = new GuestBlendState[targets.Count];
        for (var index = 0; index < targets.Count; index++)
        {
            var slot = targets[index].Slot;
            var blend = context.BlendControls[slot];
            blends[index] = new GuestBlendState(
                blend.Enable && !disableBlending,
                blend.ColorSourceFactor,
                blend.ColorDestinationFactor,
                blend.ColorFunction,
                blend.AlphaSourceFactor,
                blend.AlphaDestinationFactor,
                blend.AlphaFunction,
                blend.SeparateAlpha,
                targets[index].Resolution.ExportMapping.ApplyMask(context.RenderTargetMaskForSlot(slot)));
        }

        var state = _dynamicState;
        var scissor = state.Scissor;
        var depthState = _hasDepth ? _depth.Target.State : default;
        return new GuestRenderState(
            blends,
            new GuestRect(scissor.Left, scissor.Top, (uint)Math.Max(scissor.Right - scissor.Left, 0), (uint)Math.Max(scissor.Bottom - scissor.Top, 0)),
            new GuestViewport(state.ViewportX, state.ViewportY, state.ViewportWidth, state.ViewportHeight, state.ViewportMinDepth, state.ViewportMaxDepth),
            new GuestRasterState(
                context.RasterMode.CullFront,
                context.RasterMode.CullBack,
                context.RasterMode.FrontFaceClockwise,
                context.RasterMode.PolygonMode != 0,
                state.DepthBiasEnabled,
                state.DepthBiasConstantFactor,
                state.DepthBiasClamp,
                state.DepthBiasSlopeFactor,
                context.PolygonOffset.NegativeDepthBits,
                context.PolygonOffset.DepthIsFloat),
            new GuestDepthState(
                state.DepthTestEnabled,
                state.DepthWriteEnabled,
                (uint)state.DepthCompare,
                _hasDepth && _depth.LoadClear,
                state.StencilTestEnabled,
                _hasDepth && depthState.StencilClearEnabled,
                depthState.StencilClearValue,
                StencilFaceOf(depthState.FrontOperations, state.FrontStencil, context.StencilMask.OperationValue),
                StencilFaceOf(depthState.BackOperations, state.BackStencil, context.StencilMask.OperationValueBack)),
            new GuestBlendConstant(state.BlendRed, state.BlendGreen, state.BlendBlue, state.BlendAlpha));
    }

    private static readonly GuestMemoryBuffer _emptyGlobalBuffer = new(0, [], 0, sizeof(uint), Pooled: false);

    // The flat slots the programs were compiled against: buffers by base and scalar index, images by base.
    private (IReadOnlyList<GuestDrawTexture> Textures, IReadOnlyList<GuestMemoryBuffer> Globals) CollectResources(IReadOnlyList<AgcExports.CompiledStageProgram> programs)
    {
        var totalGlobals = 0;
        var totalTextures = 0;
        foreach (var program in programs)
        {
            totalGlobals = Math.Max(totalGlobals, program.TotalGlobalBuffers);
            totalTextures += program.Textures.Count;
        }

        var globals = new GuestMemoryBuffer?[totalGlobals];
        var textures = new GuestDrawTexture?[totalTextures];
        foreach (var program in programs)
        {
            for (var index = 0; index < program.GlobalBuffers.Count; index++)
            {
                PlaceSlot(globals, program.GlobalBufferBase + index, program.GlobalBuffers[index], program);
            }

            if (program.ScalarBuffer is { } scalars)
            {
                PlaceSlot(globals, program.ScalarBufferIndex, scalars, program);
            }

            for (var index = 0; index < program.Textures.Count; index++)
            {
                PlaceSlot(textures, program.ImageBindingBase + index, program.Textures[index], program);
            }
        }

        var boundGlobals = new GuestMemoryBuffer[totalGlobals];
        for (var slot = 0; slot < totalGlobals; slot++)
        {
            boundGlobals[slot] = globals[slot] ?? _emptyGlobalBuffer;
        }

        var boundTextures = new GuestDrawTexture[totalTextures];
        for (var slot = 0; slot < totalTextures; slot++)
        {
            boundTextures[slot] = textures[slot] ?? throw Fatal($"The image slot has no binding: slot={slot} images={totalTextures}.");
        }

        return (boundTextures, boundGlobals);
    }

    private Exception SlotFatal(int slot, int count, AgcExports.CompiledStageProgram program) =>
        Fatal($"The binding slot is outside the layout or bound twice: slot={slot} slots={count} stage={program.Stage} hash=0x{program.Hash:X16}.");

    private void PlaceSlot<T>(T?[] slots, int slot, T value, AgcExports.CompiledStageProgram program)
        where T : class
    {
        if (slot < 0 || slot >= slots.Length || slots[slot] is not null)
        {
            throw SlotFatal(slot, slots.Length, program);
        }

        slots[slot] = value;
    }

    private void SubmitDraw(uint vertexCount, uint instanceCount, int baseVertex, bool indexed)
    {
        var pipeline = RequireGraphicsPipeline();
        var context = RequireContext();
        var (textures, globals) = CollectResources(_committedPrograms);
        var vertexBuffers = CopyVertexBuffers(pipeline);
        var indexBuffer = indexed ? CopyIndexBuffer(vertexCount) : null;
        var primitiveType = PrimitiveTypeOf(pipeline.Topology);
        var count = pipeline.Topology == PrimitiveTopology.PatchList && !indexed && vertexCount is 1 or 3 or 4 ? SingleRectangleVertexCount : vertexCount;
        var depthTarget = GuestDepthOf(context);
        var vertexShader = pipeline.Vertex.Shader;
        var pixelShader = pipeline.Pixel?.Shader ?? Backend.GetDepthOnlyFragmentShader();
        var renderState = RenderStateOf(context, _colorTargets, pipeline.Pixel?.DisableBlending ?? false);
        if (_colorTargets.Count == 0)
        {
            if (depthTarget is null)
            {
                return;
            }

            _snapshots.SubmitDepthOnlyTranslatedDraw(
                pixelShader, textures, globals, pipeline.AttributeCount, depthTarget, vertexShader, count, instanceCount, primitiveType,
                indexBuffer, vertexBuffers, renderState with { Blends = [GuestBlendState.Default with { WriteMask = 0 }] }, pipeline.Pixel?.Address ?? 0, baseVertex);
            return;
        }

        var targets = new GuestRenderTarget[_colorTargets.Count];
        for (var index = 0; index < targets.Length; index++)
        {
            targets[index] = _guestTargets[_colorTargets[index].Image];
        }

        _snapshots.SubmitOffscreenTranslatedDraw(
            pixelShader, textures, globals, pipeline.AttributeCount, targets, vertexShader, count, instanceCount, primitiveType,
            indexBuffer, vertexBuffers, renderState, depthTarget, pipeline.Pixel?.Address ?? 0, baseVertex);
    }

    void IRenderHost.Draw(uint vertexCount, uint instanceCount, uint firstVertex, uint firstInstance)
    {
        _ = firstInstance;
        SubmitDraw(vertexCount, instanceCount, (int)firstVertex, indexed: false);
    }

    void IRenderHost.DrawIndexed(uint indexCount, uint instanceCount, uint firstIndex, int vertexOffset, uint firstInstance)
    {
        _ = (firstIndex, firstInstance);
        SubmitDraw(indexCount, instanceCount, vertexOffset, indexed: true);
    }

    void IRenderHost.Dispatch(uint groupsX, uint groupsY, uint groupsZ)
    {
        var pipeline = _boundPipeline as ComputePipelineRecord ?? throw Fatal("No compute pipeline is bound for the dispatch.");
        var (textures, globals) = CollectResources([pipeline.Program]);
        var input = pipeline.Input;
        var writesGlobalMemory = false;
        foreach (var buffer in pipeline.Program.Buffers)
        {
            writesGlobalMemory |= buffer.Written;
        }

        _snapshots.SubmitComputeDispatch(
            pipeline.Program.Address, pipeline.Program.Shader, textures, globals, groupsX, groupsY, groupsZ, 0, 0, 0,
            input.ThreadsX, input.ThreadsY, input.ThreadsZ, isIndirect: false, writesGlobalMemory,
            input.DispatchThreadDimensions ? input.DispatchThreadsX : uint.MaxValue,
            input.DispatchThreadDimensions ? input.DispatchThreadsY : uint.MaxValue,
            input.DispatchThreadDimensions ? input.DispatchThreadsZ : uint.MaxValue);
    }

    // The seam submits whole records; the presenter orders them itself.
    void IRenderHost.ShaderWriteBarrier(PipelineStageFlags sourceStages)
    {
        _ = sourceStages;
    }

    void IRenderHost.ShaderWriteHazardBarrier()
    {
    }

    void IRenderHost.ShaderAccessBarrier()
    {
    }

    // A fullscreen solid draw into every target; the presenter has no clear command on this seam.
    void IRenderHost.ClearColorTargets(ReadOnlySpan<ColorTargetState> targets, SolidColorClear clear)
    {
        var context = RequireContext();
        var guestTargets = new GuestRenderTarget[targets.Length];
        var blends = new GuestBlendState[targets.Length];
        for (var index = 0; index < targets.Length; index++)
        {
            guestTargets[index] = GuestTargetOf(in targets[index], context.ColorTargets[targets[index].Slot], 0xF);
            blends[index] = GuestBlendState.Default;
        }

        var pixel = new MetalCompiledGuestShader(new Gen5MslShader(
            MslFixedShaders.CreateSolidFragment(clear.Red, clear.Green, clear.Blue, clear.Alpha), "solid_clear_fs", Gen5MslStage.Pixel, [], [], AttributeCount: 0, []));
        var vertex = new MetalCompiledGuestShader(new Gen5MslShader(
            MslFixedShaders.CreateFullscreenVertex(0), "solid_clear_vs", Gen5MslStage.Vertex, [], [], AttributeCount: 0, []));
        var extent = targets[0].Resolution.Extent;
        var renderState = new GuestRenderState(
            blends,
            new GuestRect(0, 0, extent.Width, extent.Height),
            new GuestViewport(0, 0, extent.Width, extent.Height, 0, 1),
            GuestRasterState.Default,
            GuestDepthState.Default);
        _snapshots.SubmitOffscreenTranslatedDraw(pixel, [], [], 0, guestTargets, vertex, 3, 1, 4, null, null, renderState);
    }

    bool IRenderHost.TryRetainTargetlessDraw(RegisterBanks banks, GraphicsPrograms programs, in TargetlessDrawArguments arguments)
    {
        _ = programs;
        Translation.RetainTargetlessDraw(banks, in arguments);
        return true;
    }

    void IRenderHost.MarkGpuWritten(ResourceSlotIdentifier image)
    {
        _ = image;
    }

    void IRenderHost.ResolveImage(ResourceSlotIdentifier source, uint sourceMip, uint sourceLayer, ResourceSlotIdentifier destination, uint destinationMip, uint destinationLayer)
    {
        _ = (sourceMip, sourceLayer, destinationMip, destinationLayer);
        if (!_guestTargets.TryGetValue(source, out var from) || !_guestTargets.TryGetValue(destination, out var to))
        {
            throw Fatal($"The resolve names an image that was never bound as a color target: source={source.Index} destination={destination.Index}.");
        }

        _ = _snapshots.TrySubmitGuestImageBlit(from, to);
    }

    bool IRenderHost.IsMetadata(ulong address)
    {
        _ = address;
        return false;
    }

    bool IRenderHost.ClearMetadata(ulong address)
    {
        _ = address;
        return false;
    }

    // A full fill of a snapshot image replaces its pixels; anything else runs as a dispatch.
    bool IRenderHost.TryClearImageFromBuffer(ulong address, ulong size, uint packedClear)
    {
        if (!_snapshots.TryGetGuestImageExtent(address, out _, out _, out var imageBytes) || imageBytes == 0 || size < imageBytes)
        {
            return false;
        }

        _snapshots.SubmitGuestImageFill(address, packedClear);
        return true;
    }

    bool IRenderHost.TryAbsorbDccFill(ulong address, ulong size, uint fillValue)
    {
        _ = (address, size, fillValue);
        return false;
    }

    PipelineHandle AgcExports.IHostPipelineFactory.CreateGraphicsPipeline(
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
        _ = (colors.Length, depth.HasTarget, context, rendering.Samples, primitiveRestartEnabled, vertexProgram, pixelProgram);
        var record = new GraphicsPipelineRecord(
            RequireCompiledProgram(vertexInput.Stage),
            pixelInput is null ? null : RequireCompiledProgram(pixelInput.Stage),
            vertexInput,
            pixelInput?.InputCount ?? 0,
            topology);
        var handle = _nextPipelineHandle++;
        _pipelines[handle] = record;
        return new PipelineHandle(handle, handle, UsesPushDescriptors: false);
    }

    PipelineHandle AgcExports.IHostPipelineFactory.CreateComputePipeline(ComputeInputInfo input, ShaderProgram program)
    {
        _ = program;
        var handle = _nextPipelineHandle++;
        _pipelines[handle] = new ComputePipelineRecord(RequireCompiledProgram(input.Stage), input);
        return new PipelineHandle(handle, handle, UsesPushDescriptors: false);
    }
}
