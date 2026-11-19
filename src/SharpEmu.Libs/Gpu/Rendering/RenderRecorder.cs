// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.Diagnostics;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.VideoOut;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Rendering;

internal sealed partial class RenderRecorder : IResolvedCommandSink
{
    private const uint TransientIndexAlignment = 16;

    private readonly IRenderHost _host;
    private readonly IShaderPipelineProvider _pipelines;
    private readonly bool _strictDrawResources;
    private readonly RecordPool _pool;
    private readonly PendingGpuWrites? _pendingWrites;
    private readonly bool _returnsRecords;
    private readonly HashSet<(ulong ShaderHash, ImageType ImageType, ImageViewType ViewType)> _reportedDrawImageTypeMismatches = [];
    private readonly BufferBinding[] _vertexBindings = new BufferBinding[VertexInputInfo.MaxBuffers];
    private readonly VertexBufferRange[] _vertexRanges = new VertexBufferRange[VertexInputInfo.MaxBuffers];
    private readonly KeptPipeline _pipeline = new();
    private readonly InPlaceDraw _inPlace = new();
    private long _sequence;
    private static int _meshDrawsOverLimits;
    private static readonly ThreadCounter _inPlaceDraws = new();
    private static readonly ThreadCounter _inPlaceRefused = new();

    private static readonly ThreadCounter _drawReplays = new();
    private static readonly ThreadCounter _drawReplayMisses = new();
    private static readonly bool DrawReplayEnabled = Environment.GetEnvironmentVariable("SHARPEMU_DRAW_REPLAY") != "0";
    private readonly Dictionary<ShaderProgramInfo, bool> _replayablePrograms = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ShaderProgramInfo, bool> _tableReplayablePrograms = new(ReferenceEqualityComparer.Instance);
    private bool _replayValid;
    private long _replaySequence;
    private ShaderStageResources _replayVertex;
    private ShaderStageResources _replayPixel;
    private VertexInputBuffer[] _replayVertexBuffers = [];
    private VertexAttributeResource[] _replayVertexAttributes = [];
    private bool _replayPixelActive;

    private static readonly string[] ReplayMissNames =
        ["sequence", "pixel_active", "vertex_program", "vertex_images", "vertex_samplers", "vertex_buffers", "vertex_table", "vertex_extras",
         "vertex_ranges", "vertex_heaps", "vertex_mips",
         "pixel_program", "pixel_images", "pixel_samplers", "pixel_buffers", "pixel_table", "pixel_extras", "pixel_ranges", "pixel_heaps", "pixel_mips",
         "vertex_input", "targets", "host"];
    private static readonly ThreadCounter[] _replayMisses = Array.ConvertAll(ReplayMissNames, _ => new ThreadCounter());

    private static bool ReplayMiss(int reason)
    {
        _replayMisses[reason].Increment();
        _drawReplayMisses.Increment();
        return false;
    }

    private static string TakeReplayMisses()
    {
        var parts = new List<string>();
        for (var index = 0; index < ReplayMissNames.Length; index++)
        {
            var count = _replayMisses[index].Take();
            if (count != 0)
            {
                parts.Add(FormattableString.Invariant($"{ReplayMissNames[index]}:{count}"));
            }
        }

        return parts.Count == 0 ? "none" : string.Join(",", parts);
    }

    private static readonly string[] SealRefusalNames = ["kind", "writes", "trace", "tessellation", "targets", "vertex", "pixel", "sequence"];
    private static readonly ThreadCounter[] _sealRefusals = Array.ConvertAll(SealRefusalNames, _ => new ThreadCounter());
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _unreplayablePrograms = new();

    private static string TakeSealRefusals()
    {
        var parts = new List<string>();
        for (var index = 0; index < SealRefusalNames.Length; index++)
        {
            var count = _sealRefusals[index].Take();
            if (count != 0)
            {
                parts.Add(FormattableString.Invariant($"{SealRefusalNames[index]}:{count}"));
            }
        }

        return parts.Count == 0 ? "none" : string.Join(",", parts);
    }

    private static string DescribeUnreplayablePrograms() =>
        _unreplayablePrograms.IsEmpty ? "none" : string.Join(",", _unreplayablePrograms.Select(pair => FormattableString.Invariant($"{pair.Key}:{pair.Value}")));

    public static string TakeInPlaceReport() => FormattableString.Invariant(
        $"in_place={_inPlaceDraws.Take()} in_place_refused={_inPlaceRefused.Take()} draw_replays={_drawReplays.Take()} draw_replay_misses={_drawReplayMisses.Take()} ") +
        "replay_seal_refused=" + TakeSealRefusals() + " replay_miss=" + TakeReplayMisses() + " unreplayable_programs=" + DescribeUnreplayablePrograms() +
        " " + TakeDispatchReplayReport();

    private sealed class InPlaceDraw
    {
        public bool Valid;
        public DrawScopeToken Token;
        public ulong ContextStamp;
        public ulong DynamicStamp;
        public uint TargetVersion;
        public ulong VertexProgram;
        public ulong PixelProgram;
        public bool PixelActive;
        public uint FoundCount;
        public bool DepthResolved;
        public readonly ResourceSlotIdentifier[] Found = new ResourceSlotIdentifier[RenderingState.ColorAttachmentCapacity];
        public ResourceSlotIdentifier FoundDepth;
        public readonly ColorTargetResolution[] FoundResolutions = new ColorTargetResolution[RenderingState.ColorAttachmentCapacity];
        public readonly uint[] FoundSlots = new uint[RenderingState.ColorAttachmentCapacity];
        public bool FoundDepthTarget;
        public DepthTargetState FoundDepthState;
        public uint AcquiredCount;
        public readonly ImageView[] Views = new ImageView[RenderingState.ColorAttachmentCapacity];
        public readonly uint[] Slots = new uint[RenderingState.ColorAttachmentCapacity];
        public ImageView DepthView;
        public RenderingState Rendering;
    }

    private static readonly bool InPlaceByTargets = Environment.GetEnvironmentVariable("SHARPEMU_INPLACE_BY_TARGETS") != "0";

    private bool RepeatsPreviousTargets(ResolvedDraw record, in DrawState state, DrawScopeToken token)
    {
        var kept = _inPlace;
        if (!kept.Valid || !token.IsActive || token != kept.Token || ImageClearTrace.Enabled || state.PixelActive != kept.PixelActive ||
            state.ColorCount != kept.FoundCount || state.DepthResolved != kept.DepthResolved)
        {
            return false;
        }

        if (!InPlaceByTargets)
        {
            return record.ContextStamp == kept.ContextStamp && record.DynamicStamp == kept.DynamicStamp && record.TargetVersion == kept.TargetVersion &&
                state.Programs.Vertex.Id == kept.VertexProgram && state.Programs.Pixel.Id == kept.PixelProgram;
        }

        for (var index = 0; index < state.ColorCount; index++)
        {
            if (state.Colors[index].Slot != kept.FoundSlots[index] || !SameColorTarget(in state.Colors[index].Resolution, in kept.FoundResolutions[index]))
            {
                return false;
            }
        }

        return state.Depth.HasTarget == kept.FoundDepthTarget && (!state.Depth.HasTarget || SameDepthAttachment(in state.Depth.Target, in kept.FoundDepthState));
    }

    private static bool SameColorTarget(in ColorTargetResolution current, in ColorTargetResolution kept) =>
        current.BaseAddress == kept.BaseAddress && current.BackingSize == kept.BackingSize &&
        current.Extent.Width == kept.Extent.Width && current.Extent.Height == kept.Extent.Height &&
        current.BaseMipLevel == kept.BaseMipLevel && current.BaseArrayLayer == kept.BaseArrayLayer && current.Samples == kept.Samples &&
        current.ExportMapping.Packed == kept.ExportMapping.Packed && current.MetadataClearSupported == kept.MetadataClearSupported &&
        current.MetadataFixedClearSupported == kept.MetadataFixedClearSupported &&
        current.ColorClearValue.Uint32_0 == kept.ColorClearValue.Uint32_0 && current.ColorClearValue.Uint32_1 == kept.ColorClearValue.Uint32_1 &&
        current.ColorClearValue.Uint32_2 == kept.ColorClearValue.Uint32_2 && current.ColorClearValue.Uint32_3 == kept.ColorClearValue.Uint32_3 &&
        GuestImageCache.SameRequest(current.Request, kept.Request);

    private static bool SameDepthTarget(in DepthTargetResolution current, in DepthTargetResolution kept) =>
        current.Format == kept.Format && current.Width == kept.Width && current.Height == kept.Height && current.Samples == kept.Samples &&
        current.HasStencil == kept.HasStencil && current.HasHtile == kept.HasHtile && current.DepthAddress == kept.DepthAddress &&
        current.DepthSize == kept.DepthSize && current.StencilAddress == kept.StencilAddress && current.StencilSize == kept.StencilSize &&
        current.HtileAddress == kept.HtileAddress && current.HtileSize == kept.HtileSize && current.DepthClearEnabled == kept.DepthClearEnabled &&
        current.StencilClearEnabled == kept.StencilClearEnabled && current.DepthWriteDisabled == kept.DepthWriteDisabled &&
        current.StencilWriteDisabled == kept.StencilWriteDisabled && GuestImageCache.SameRequest(current.Request, kept.Request);

    private static bool SameDepthAttachment(in DepthTargetState current, in DepthTargetState kept)
    {
        var format = current.Target.Format;
        return SameDepthTarget(current.Target, kept.Target) &&
            current.State.AttachmentWriteAspects(format) == kept.State.AttachmentWriteAspects(format) &&
            current.State.DepthClearEnabled == kept.State.DepthClearEnabled &&
            current.State.DepthClearValue.Equals(kept.State.DepthClearValue) &&
            current.State.StencilClearEnabled == kept.State.StencilClearEnabled &&
            current.State.StencilClearValue == kept.State.StencilClearValue;
    }

    private void RestoreFoundTargets(ref DrawState state)
    {
        var kept = _inPlace;
        for (var index = 0; index < state.ColorCount; index++)
        {
            state.Colors[index].Image = kept.Found[index];
            _host.BindRenderTarget(kept.Found[index]);
        }

        if (state.DepthResolved)
        {
            state.Depth.Image = kept.FoundDepth;
            _host.BindRenderTarget(kept.FoundDepth);
        }
    }

    private bool TryRestoreAttachments(ref DrawState state)
    {
        var kept = _inPlace;
        if (state.ColorCount != kept.AcquiredCount || _host.DrawScope != kept.Token)
        {
            return false;
        }

        for (var index = 0; index < state.ColorCount; index++)
        {
            if (state.Colors[index].Slot != kept.Slots[index])
            {
                return false;
            }
        }

        for (var index = 0; index < state.ColorCount; index++)
        {
            state.Colors[index].View = kept.Views[index];
        }

        if (state.Depth.HasTarget)
        {
            state.Depth.View = kept.DepthView;
            state.Depth.MetadataClear = false;
            _host.KeepDepthAttachment(in state.Depth);
        }

        state.Rendering = kept.Rendering;
        return true;
    }

    private void NoteFoundTargets(ref DrawState state)
    {
        var kept = _inPlace;
        kept.FoundCount = state.ColorCount;
        for (var index = 0; index < state.ColorCount; index++)
        {
            kept.Found[index] = state.Colors[index].Image;
            kept.FoundResolutions[index] = state.Colors[index].Resolution;
            kept.FoundSlots[index] = state.Colors[index].Slot;
        }

        kept.FoundDepthTarget = state.Depth.HasTarget;
        kept.FoundDepthState = state.Depth.Target;

        kept.DepthResolved = state.DepthResolved;
        kept.FoundDepth = state.DepthResolved ? state.Depth.Image : default;
    }

    private void KeepTargets(ResolvedDraw record, ref DrawState state)
    {
        var kept = _inPlace;
        kept.Token = _host.DrawScope;
        kept.Valid = kept.Token.IsActive;
        kept.ContextStamp = record.ContextStamp;
        kept.DynamicStamp = record.DynamicStamp;
        kept.TargetVersion = record.TargetVersion;
        kept.VertexProgram = state.Programs.Vertex.Id;
        kept.PixelProgram = state.Programs.Pixel.Id;
        kept.PixelActive = state.PixelActive;
        kept.AcquiredCount = state.ColorCount;
        for (var index = 0; index < state.ColorCount; index++)
        {
            kept.Views[index] = state.Colors[index].View;
            kept.Slots[index] = state.Colors[index].Slot;
        }

        kept.DepthView = state.Depth.View;
        kept.Rendering = state.Rendering;
    }

    private void BeginDrawRendering(ref DrawState state, bool reusedAttachments)
    {
        // Binding resources can end the scope after attachment reuse. Its metadata load clears
        // already ran, so reopening the scope must preserve the pixels drawn since then.
        if (reusedAttachments && _host.DrawScope.Scope != _inPlace.Token.Scope)
        {
            for (var index = 0; index < state.Rendering.ColorAttachmentCount; index++)
            {
                ref var color = ref state.Rendering.ColorAttachments[index];
                color = color with { IsClear = false };
            }

            ref var depth = ref state.Rendering.DepthStencilAttachment;
            depth = depth with { DepthClear = state.Depth.Target.State.DepthClearEnabled };
        }

        _host.BeginRendering(in state.Rendering);
    }

    private struct VertexBufferRange
    {
        public ulong BaseAddress;
        public ulong RequestedEnd;
        public ulong AcquiredEnd;
        public BufferBinding Binding;

        public readonly ulong RequestedSize => RequestedEnd - BaseAddress;
    }

    private readonly record struct PreparedIndexBuffer(BufferBinding Binding, ulong Size, IndexType Type);

    public RenderRecorder(IRenderHost host, IShaderPipelineProvider pipelines, bool strictDrawResources, PendingGpuWrites? pendingWrites = null, RecordPool? pool = null,
        bool returnsRecords = true)
    {
        _host = host;
        _returnsRecords = returnsRecords;
        _pipelines = pipelines;
        _strictDrawResources = strictDrawResources;
        _pendingWrites = pendingWrites;
        _pool = pool ?? new RecordPool();
    }

    public long NextSequence() => ++_sequence;

    public void Record(ResolvedDraw record)
    {
        try
        {
            RecordDraw(record);
        }
        finally
        {
            _pendingWrites?.Release(record.Sequence);
            if (_returnsRecords)
            {
                _pool.Return(record);
            }
        }
    }

    private void RecordDraw(ResolvedDraw record)
    {
        if (record.Announce)
        {
            if (!_host.IsRecording)
            {
                throw _host.Fatal(record.Debug.Operation == RecordedOperation.DrawIndex
                    ? "An indexed draw has no recording command buffer."
                    : "An automatic draw has no recording command buffer.");
            }

            _host.RunPendingOperations();
            SetDebugInformation(in record.Debug);
        }

        var state = record.State;
        if (record.Kind != ResolvedDrawKind.Draw)
        {
            _inPlace.Valid = false;
        }

        switch (record.Kind)
        {
            case ResolvedDrawKind.Skipped:
                if (record.TargetsResolved)
                {
                    FindTargets(ref state);
                }

                break;
            case ResolvedDrawKind.MultisampleResolve:
                ResolveMultisample(record);
                break;
            case ResolvedDrawKind.DepthStencilCopy:
                CopyDepthStencil(record);
                break;
            case ResolvedDrawKind.Tessellation:
                FindTargets(ref state);
                RecordTessellation(record, ref state);
                _inPlace.Valid = false;
                break;
            case ResolvedDrawKind.ClearColorTargets:
                FindTargets(ref state);
                _host.ClearColorTargets(DrawState.BoundColors(ref state), record.Clear);
                break;
            case ResolvedDrawKind.Draw:
            {
                var inPlace = RepeatsPreviousTargets(record, in state, _host.DrawScope);
                if (inPlace)
                {
                    RestoreFoundTargets(ref state);
                }
                else
                {
                    _inPlace.Valid = false;
                    FindTargets(ref state);
                    NoteFoundTargets(ref state);
                }

                RecordDrawCommands(record, ref state, inPlace);
                break;
            }
        }

        if (record.ResetBindings)
        {
            _host.ResetBindings();
        }
    }

    private void SetDebugInformation(in DebugInformation debug) =>
        _host.SetDebugInformation(debug.Operation, debug.SubmitId, debug.Argument0, debug.Argument1, debug.Argument2, debug.Argument3, debug.Argument4);

    private void FindTargets(ref DrawState state)
    {
        for (var index = 0; index < state.ColorCount; index++)
        {
            var request = state.Colors[index].Resolution.Request;
            var image = _host.FindImage(ref request, exactFormat: false);
            _host.BindRenderTarget(image);
            state.Colors[index].Image = image;
        }

        if (state.DepthResolved)
        {
            var request = state.Depth.Target.Target.Request;
            var image = _host.FindImage(ref request, exactFormat: false);
            _host.BindRenderTarget(image);
            state.Depth.Image = image;
        }
    }

    private ColorTargetState FindExactTarget(ColorTargetState target)
    {
        var request = target.Resolution.Request;
        var image = _host.FindImage(ref request, exactFormat: true);
        _host.BindRenderTarget(image);
        target.Image = image;
        return target;
    }

    private void ResolveMultisample(ResolvedDraw record)
    {
        var from = FindExactTarget(record.ResolveSource);
        var to = FindExactTarget(record.ResolveDestination);
        if (!from.Image.IsValid || !to.Image.IsValid)
        {
            return;
        }

        if (from.Resolution.BaseAddress == to.Resolution.BaseAddress &&
            from.Resolution.BaseMipLevel == to.Resolution.BaseMipLevel &&
            from.Resolution.BaseArrayLayer == to.Resolution.BaseArrayLayer)
        {
            return;
        }

        _host.MarkGpuWritten(to.Image);
        _host.ResolveImage(from.Image, from.Resolution.BaseMipLevel, from.Resolution.BaseArrayLayer, to.Image, to.Resolution.BaseMipLevel, to.Resolution.BaseArrayLayer);
    }

    private void CopyDepthStencil(ResolvedDraw record)
    {
        var readRequest = record.CopyRead;
        var writeRequest = record.CopyWrite;
        var source = _host.FindImage(ref readRequest, exactFormat: true);
        var destination = _host.FindImage(ref writeRequest, exactFormat: true);
        _host.BindRenderTarget(source);
        _host.BindRenderTarget(destination);
        if (source == destination || record.CopyReadFormat != record.CopyWriteFormat)
        {
            throw _host.Fatal(
                $"A depth/stencil copy must use distinct images with the same format: source={source.Index} destination={destination.Index} readFormat={(int)record.CopyReadFormat} writeFormat={(int)record.CopyWriteFormat}.");
        }

        var range = new SubresourceRange(
            readRequest.View.BaseLevel,
            readRequest.View.LevelCount,
            readRequest.View.BaseLayer,
            readRequest.View.LayerCount);
        _host.MarkGpuWritten(destination);
        _host.CopyDepthStencilImage(source, destination, in range, in record.CopyExtent, record.CopyAspects);
    }

    private void SetDrawDebugPhase(ulong submitId, in DrawCall draw, uint phase) =>
        _host.SetDebugInformation(draw.Operation, submitId, phase, draw.Count, 0, draw.InstanceCount, draw.FirstInstance);

    private IPreparedBindings PrepareBindings(in ShaderStageResources stage)
    {
        var bindings = _host.PrepareBindings(stage);
        NoteSampledTextures(stage);
        return bindings;
    }

    private static void NoteSampledTextures(in ShaderStageResources stage)
    {
        if (stage.Program is not { } program)
        {
            return;
        }

        var descriptors = stage.Resources.Images;
        var statistics = stage.Resources.MipStatistics;
        for (var index = 0; index < program.Images.Length && index < descriptors.Length; index++)
        {
            if (program.Images[index].Class == ImageResourceClass.Sampled)
            {
                MipStatistics.Shared.NoteSampled(
                    new TextureDescriptorWords(descriptors[index]),
                    statistics is not null && index < statistics.Length ? statistics[index] : ShaderCompiler.Resources.ResourceSnapshot.NoMipStatistics);
            }
        }
    }

    private void RecordDrawCommands(ResolvedDraw record, ref DrawState state, bool inPlace)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.DrawResourcePreparation);
        var submitId = record.SubmitId;
        ref readonly var draw = ref record.Draw;
        var context = record.Context;
        var vertexInput = state.Programs.VertexInput;
        var pixelInput = state.Programs.PixelInput;
        var meshInput = state.Programs.MeshInput;
        var topology = record.Topology;
        ref readonly var emission = ref record.Emission;
        ref readonly var indexSource = ref record.Index;
        using var preparation = _host.BeginPreparation();
        using var indirectMesh = meshInput is not null && emission.IndirectArgumentsAddress != 0
            ? _host.PrepareIndirectMeshDraw(emission.Indexed
                ? new IndirectMeshDrawRequest(emission.IndirectArgumentsAddress,
                    indexSource.Address, draw.Count, indexSource.Type == IndexType.Uint16 ? 2u : 4u,
                    topology == PrimitiveTopology.PointList, meshInput.Geometry.InputPrimitiveCountPerWorkgroup, meshInput.Execution)
                : IndirectMeshDrawRequest.ForAutomaticDraw(emission.IndirectArgumentsAddress,
                    topology == PrimitiveTopology.PointList, meshInput.Geometry.InputPrimitiveCountPerWorkgroup, meshInput.Execution)
                    with { TaskLaunched = meshInput.Geometry.TaskLaunched })
            : null;
        // Only an automatic indirect draw resolves a task-launched program; anything else is a broken join.
        if (meshInput is { Geometry.TaskLaunched: true } && (emission.IndirectArgumentsAddress == 0 || emission.Indexed))
        {
            throw _host.Fatal("A task-launched mesh program reached a draw that is not automatic and indirect.");
        }

        if (meshInput is null && indirectMesh is null && TryReplayDraw(record, ref state, inPlace, vertexInput, pixelInput, in indexSource, in emission, in draw))
        {
            return;
        }

        _replayValid = false;
        var meshVertexCount = draw.Count;
        if (MeshDrawTrace.Active)
            MeshDrawTrace.Write("programs", $"mesh={meshInput is not null} vertexHash=0x{vertexInput.Stage.Program?.Hash:X16} pixelHash=0x{pixelInput.Stage.Program?.Hash:X16} pixelActive={state.PixelActive}");
        if (meshInput is not null)
        {
            if (topology is not (PrimitiveTopology.PointList or PrimitiveTopology.LineList or PrimitiveTopology.TriangleList or PrimitiveTopology.TriangleStrip))
            {
                throw _host.Fatal("The mesh draw input topology is not supported.");
            }

            ulong indexAddress = 0;
            if (emission.Indexed && indirectMesh is null)
            {
                var byteCount = checked((int)indexSource.Size);
                var elementSize = indexSource.Type == IndexType.Uint16 ? 2u : 4u;
                var triangleStrip = topology == PrimitiveTopology.TriangleStrip;
                var lineList = topology == PrimitiveTopology.LineList;
                var pointList = lineList || topology == PrimitiveTopology.PointList;
                var capacity = pointList ? byteCount / (int)elementSize
                    : MeshIndexAssembly.GetMaximumCount(byteCount, elementSize, triangleStrip);
                if (capacity == 0)
                {
                    IndexedDrawTrace.Write(record.IndexedTrace, "no-primitives", submitId, record.PacketAddress);
                    return;
                }

                byte[]? rentedBytes = null;
                uint[]? rentedIndices = null;
                try
                {
                    var bytes = indexSource.HostData is { } hostData ? hostData.AsSpan(0, byteCount)
                        : (rentedBytes = System.Buffers.ArrayPool<byte>.Shared.Rent(byteCount)).AsSpan(0, byteCount);
                    if (indexSource.HostData is null && !_host.TryReadGuest(indexSource.Address, bytes))
                        throw _host.Fatal("The mesh index buffer cannot be read.");
                    rentedIndices = System.Buffers.ArrayPool<uint>.Shared.Rent(capacity);
                    var written = pointList
                        ? MeshIndexAssembly.ExpandPoints(bytes, rentedIndices.AsSpan(0, capacity), elementSize,
                            emission.VertexOffset, record.PrimitiveRestart, elementSize == 2 ? ushort.MaxValue : uint.MaxValue)
                        : MeshIndexAssembly.ExpandTriangles(bytes, rentedIndices.AsSpan(0, capacity),
                        elementSize, emission.VertexOffset, triangleStrip, record.PrimitiveRestart,
                        elementSize == 2 ? ushort.MaxValue : uint.MaxValue, context.RasterMode.ProvokingVertexLast);
                    if (lineList) written &= ~1;
                    meshVertexCount = checked((uint)written);
                    if (written == 0)
                    {
                        IndexedDrawTrace.Write(record.IndexedTrace, "no-primitives", submitId, record.PacketAddress);
                        return;
                    }

                    indexAddress = _host.UploadMeshVertexIndices(rentedIndices.AsSpan(0, written));
                }
                finally
                {
                    if (rentedIndices is not null) System.Buffers.ArrayPool<uint>.Shared.Return(rentedIndices);
                    if (rentedBytes is not null) System.Buffers.ArrayPool<byte>.Shared.Return(rentedBytes);
                }
            }

            // The programs may be kept for other draws: the mesh parameters of this draw go on a copy.
            var meshStage = vertexInput.Stage with
            {
                MeshDraw = indirectMesh is null
                    ? new MeshDrawParameters(meshVertexCount, emission.Indexed ? 0 : emission.FirstVertex,
                        emission.FirstInstance, emission.Indexed ? 4u : 0u, indexAddress)
                    : new MeshDrawParameters(0, 0, 0, uint.MaxValue, indirectMesh.ParametersAddress),
            };
            vertexInput = vertexInput.WithoutStage();
            vertexInput.Stage = meshStage;
        }

        IPreparedBindings vertexBindings;
        IPreparedBindings? pixelBindings;
        try
        {
            vertexBindings = PrepareBindings(vertexInput.Stage);
            pixelBindings = state.PixelActive ? PrepareBindings(pixelInput.Stage) : null;
        }
        catch (DrawImageTypeMismatchException rejection)
        {
            if (MeshDrawTrace.Active) MeshDrawTrace.Write("resource-rejected", rejection.Message);
            IndexedDrawTrace.Write(record.IndexedTrace, "resource-rejected", submitId, record.PacketAddress);
            if (_strictDrawResources)
            {
                throw _host.Fatal(rejection.Message);
            }

            if (_reportedDrawImageTypeMismatches.Add(rejection.WarningKey))
            {
                Console.Error.WriteLine($"[GPU][WARN][DRAW_SKIPPED] {rejection.Message} " +
                    "The draw was not executed. Images and FPS can be incorrect. Set SHARPEMU_STRICT_COMPUTE=1 to stop on this failure.");
            }

            return;
        }

        var vertexProgram = vertexInput.Stage.Program ?? throw _host.Fatal("The vertex stage has no program.");
        var pixelProgram = pixelBindings is null ? null : pixelInput.Stage.Program ?? throw _host.Fatal("The pixel stage has no program.");
        if (vertexProgram.UsesDeviceAddresses || (pixelProgram?.UsesDeviceAddresses ?? false))
        {
            _host.PrepareDeviceAddresses();
        }

        var owners = _host.BufferOwnerGeneration;
        _host.BindResources(vertexBindings);
        if (pixelBindings is not null)
        {
            _host.BindResources(pixelBindings);
        }

        var vertexBuffers = meshInput is null ? AcquireVertexBuffers(vertexInput) : default;
        var indexBuffer = meshInput is null ? AcquireIndexBuffer(in indexSource) : default;
        var indirectArguments = meshInput is null && emission.IndirectArgumentsAddress != 0
            ? _host.ObtainBuffer(emission.IndirectArgumentsAddress,
                emission.Indexed ? RenderExecutor.IndexedIndirectArgumentsSize : RenderExecutor.AutoIndirectArgumentsSize, isWritten: false)
            : default;
        RenderExecutor.DropUnwrittenColorTargets(context, ref state, pixelProgram);
        var reusedAttachments = inPlace && TryRestoreAttachments(ref state);
        if (reusedAttachments)
        {
            _inPlaceDraws.Increment();
        }
        else
        {
            if (inPlace)
            {
                _inPlaceRefused.Increment();
            }

            state.Rendering = AcquireAttachments(ref state);
        }

        // An owner bound above can be merged by a later stage or acquisition; bind and acquire the buffers again
        // until none changes, so the draw records no retired owner.
        for (var attempt = 0; _host.BufferOwnerGeneration != owners; attempt++)
        {
            if (attempt == 8)
            {
                throw _host.Fatal("Buffer owners kept changing while a draw was prepared.");
            }

            owners = _host.BufferOwnerGeneration;
            _host.RebindBuffers(vertexBindings);
            if (pixelBindings is not null)
            {
                _host.RebindBuffers(pixelBindings);
            }

            if (meshInput is null)
            {
                vertexBuffers = AcquireVertexBuffers(vertexInput);
                indexBuffer = AcquireIndexBuffer(in indexSource);
                if (emission.IndirectArgumentsAddress != 0)
                {
                    indirectArguments = _host.ObtainBuffer(emission.IndirectArgumentsAddress,
                        emission.Indexed ? RenderExecutor.IndexedIndirectArgumentsSize : RenderExecutor.AutoIndirectArgumentsSize, isWritten: false);
                }
            }
        }

        var pipeline = ResolvePipeline(record, ref state, vertexInput, state.PixelActive ? pixelInput : null);
        if (record.SetBindDebug)
        {
            SetDrawDebugPhase(submitId, in draw, 0x100);
        }

        if (record.SetAutoDebug)
        {
            SetDrawDebugPhase(submitId, in draw, 0x200);
        }

        if (meshInput is null)
        {
            _host.BindVertexBuffers(vertexBuffers, vertexInput);
        }

        if (pixelBindings is not null && record.SetAutoDebug)
        {
            SetDrawDebugPhase(submitId, in draw, 0x300);
        }

        Span<IPreparedBindings> stages = pixelBindings is null ? [vertexBindings] : [vertexBindings, pixelBindings];
        _host.CommitBindings(PipelineBindPoint.Graphics, in pipeline, stages);
        if (indexBuffer.Size != 0)
        {
            _host.BindIndexBuffer(indexBuffer.Binding, indexBuffer.Type);
        }

        _host.SetDynamicState(in record.Dynamic);
        if (record.SetAutoDebug)
        {
            SetDrawDebugPhase(submitId, in draw, 0x400);
        }

        if (record.WritesMemory)
        {
            _host.PrepareMemoryWritingDraw();
        }

        var imageSnapshot = _host.BeginImageSnapshot(pixelBindings);
        indirectMesh?.Prepare();
        BeginDrawRendering(ref state, reusedAttachments);
        if (MeshDrawTrace.Active) MeshDrawTrace.Write("rendering", $"width={state.Rendering.Width} height={state.Rendering.Height} layers={state.Rendering.Layers} colors={state.ColorCount}");
        _host.BindPipeline(PipelineBindPoint.Graphics, in pipeline);
        if (record.SetAutoDebug)
        {
            SetDrawDebugPhase(submitId, in draw, 0x500);
        }

        if (record.IndexedTrace is { } trace)
        {
            _host.InsertDrawTraceMarker(IndexedDrawTrace.CreateLabel(trace, submitId, record.PacketAddress));
        }

        if (indirectMesh is not null)
        {
            indirectMesh.Draw();
            if (MeshDrawTrace.Active) MeshDrawTrace.Recorded();
        }
        else if (meshInput is { } mesh)
        {
            DrawMeshTasks(record, mesh, meshVertexCount);
        }
        else if (emission.IndirectArgumentsAddress != 0 && emission.Indexed)
        {
            _host.DrawIndexedIndirect(indirectArguments);
        }
        else if (emission.IndirectArgumentsAddress != 0)
        {
            _host.DrawIndirect(indirectArguments);
        }
        else if (record.Kind == ResolvedDrawKind.Tessellation)
        {
            _host.Draw(draw.Count, 1, 0, 0);
        }
        else
        {
            EmitDraw(record.PrimitiveType, vertexInput, in draw, in emission);
        }

        _host.EndImageSnapshot(imageSnapshot, DrawState.BoundColors(ref state));
        IndexedDrawTrace.Write(record.IndexedTrace, "emitted", submitId, record.PacketAddress);
        if (record.SetAutoDebug)
        {
            SetDrawDebugPhase(submitId, in draw, 0x600);
        }

        if (record.WriteStages != PipelineStageFlags.None)
        {
            if (MeshDrawTrace.Active) MeshDrawTrace.Write("write-barrier", $"sourceStages={record.WriteStages}");
            _inPlace.Valid = false;
            _host.EndRendering(Vulkan.ScopeBreak.StoringDraw);
            _host.ShaderWriteBarrier(record.WriteStages);
        }
        else
        {
            KeepTargets(record, ref state);
            if (meshInput is null && indirectMesh is null && imageSnapshot == 0)
            {
                SealReplay(record, in state, vertexInput, pixelInput);
            }
        }

        if (record.SetAutoDebug)
        {
            SetDrawDebugPhase(submitId, in draw, 0x700);
        }
    }

    private bool IsReplayable(ShaderProgramInfo? program)
    {
        if (program is null)
        {
            return false;
        }

        if (_replayablePrograms.TryGetValue(program, out var known))
        {
            return known;
        }

        var reason = UnreplayableReason(program);
        var replayable = reason is null;
        if (!replayable)
        {
            _unreplayablePrograms.AddOrUpdate(reason!, 1, static (_, count) => count + 1);
        }

        _replayablePrograms[program] = replayable;
        return replayable;
    }

    // A compute program whose one replay refusal is its flattened table seals a replay basis too: a dispatch whose table
    // alone differs replays with the table uploaded again and only that descriptor pushed.
    private bool IsDispatchReplayable(ShaderProgramInfo? program)
    {
        if (IsReplayable(program))
        {
            return true;
        }

        if (program is null)
        {
            return false;
        }

        if (_tableReplayablePrograms.TryGetValue(program, out var known))
        {
            return known;
        }

        var replayable = program.Bindings?.Find(ShaderCompiler.Resources.DescriptorBindingKind.FlattenedResourceTable) is not null &&
            UnreplayableReason(program, ignoreFlattenedTable: true) is null;
        _tableReplayablePrograms[program] = replayable;
        return replayable;
    }

    // Why a program cannot seal a replay basis, or null.
    private static string? UnreplayableReason(ShaderProgramInfo program, bool ignoreFlattenedTable = false)
    {
        var layout = program.Bindings;
        return layout is null ? "no_layout"
            : layout.UsesBindlessHeap ? "bindless"
            : program.UsesDeviceAddresses ? "device_addresses"
            : program.CallRecordCallees.Count != 0 || layout.CallTargetCount != 0 || layout.CallArgumentCount != 0 ? "calls"
            : layout.UsesTessellationData ? "tessellation"
            : program.Frontend is { RequiresOrderedImagePreparation: true } ? "frontend"
            : layout.Find(ShaderCompiler.Resources.DescriptorBindingKind.ShaderData) is not null ? "shader_data"
            : !ignoreFlattenedTable && layout.Find(ShaderCompiler.Resources.DescriptorBindingKind.FlattenedResourceTable) is not null ? "flattened_table"
            : layout.Find(ShaderCompiler.Resources.DescriptorBindingKind.CallRecords) is not null ||
              layout.Find(ShaderCompiler.Resources.DescriptorBindingKind.CallMarks) is not null ? "call_records"
            : layout.Find(ShaderCompiler.Resources.DescriptorBindingKind.GlobalDataShare) is not null ? "gds"
            : layout.Find(ShaderCompiler.Resources.DescriptorBindingKind.DeviceAddressFallback) is not null ? "device_address_fallback"
            : null;
    }

    // StageInputMismatch's checks after the flattened table, for a stage whose table differs.
    private static int StageInputMismatchAfterTable(in ShaderStageResources current, in ShaderStageResources kept)
    {
        var resources = current.Resources;
        var keptResources = kept.Resources;
        if (!ReferenceEquals(resources.DeviceAddressRanges, keptResources.DeviceAddressRanges) &&
            !resources.DeviceAddressRanges.AsSpan().SequenceEqual(keptResources.DeviceAddressRanges))
        {
            return 6;
        }

        if (resources.Heaps.Length != 0 || keptResources.Heaps.Length != 0)
        {
            return 7;
        }

        return ReferenceEquals(resources.MipStatistics, keptResources.MipStatistics) ||
            (resources.MipStatistics ?? []).AsSpan().SequenceEqual(keptResources.MipStatistics ?? [])
            ? -1
            : 8;
    }

    private static bool SameWordSets(uint[][] current, uint[][] kept)
    {
        if (ReferenceEquals(current, kept))
        {
            return true;
        }

        if (current.Length != kept.Length)
        {
            return false;
        }

        for (var index = 0; index < current.Length; index++)
        {
            if (!ReferenceEquals(current[index], kept[index]) && !current[index].AsSpan().SequenceEqual(kept[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static int StageInputMismatch(in ShaderStageResources current, in ShaderStageResources kept, bool compareThreadLimits = true)
    {
        if (!ReferenceEquals(current.Program, kept.Program) || current.ShaderBase != kept.ShaderBase)
        {
            return 0;
        }

        if ((compareThreadLimits && current.ThreadLimits != kept.ThreadLimits) || current.MeshDraw != kept.MeshDraw || current.TessellationData is { Length: > 0 } ||
            kept.TessellationData is { Length: > 0 } ||
            !(current.CallArguments ?? []).AsSpan().SequenceEqual(kept.CallArguments ?? []))
        {
            return 5;
        }

        var resources = current.Resources;
        var keptResources = kept.Resources;
        if (ReferenceEquals(resources, keptResources))
        {
            return -1;
        }

        if (!SameWordSets(resources.Images, keptResources.Images))
        {
            return 1;
        }

        if (!SameWordSets(resources.Samplers, keptResources.Samplers))
        {
            return 2;
        }

        if (!SameWordSets(resources.Buffers, keptResources.Buffers))
        {
            return 3;
        }

        if (!resources.FlattenedResourceTable.AsSpan().SequenceEqual(keptResources.FlattenedResourceTable))
        {
            return 4;
        }

        if (!ReferenceEquals(resources.DeviceAddressRanges, keptResources.DeviceAddressRanges) &&
            !resources.DeviceAddressRanges.AsSpan().SequenceEqual(keptResources.DeviceAddressRanges))
        {
            return 6;
        }

        if (resources.Heaps.Length != 0 || keptResources.Heaps.Length != 0)
        {
            return 7;
        }

        return ReferenceEquals(resources.MipStatistics, keptResources.MipStatistics) ||
            (resources.MipStatistics ?? []).AsSpan().SequenceEqual(keptResources.MipStatistics ?? [])
            ? -1
            : 8;
    }

    private bool SameVertexInput(VertexInputInfo vertexInput) =>
        vertexInput.Tessellation is null &&
        (ReferenceEquals(vertexInput.Buffers, _replayVertexBuffers) || vertexInput.Buffers.AsSpan().SequenceEqual(_replayVertexBuffers)) &&
        (ReferenceEquals(vertexInput.Attributes, _replayVertexAttributes) || vertexInput.Attributes.AsSpan().SequenceEqual(_replayVertexAttributes));

    private void SealReplay(ResolvedDraw record, in DrawState state, VertexInputInfo vertexInput, PixelInputInfo pixelInput)
    {
        _replayValid = false;
        if (!DrawReplayEnabled)
        {
            return;
        }

        var refusal = record.Kind != ResolvedDrawKind.Draw ? 0
            : record.WritesMemory ? 1
            : record.IndexedTrace is not null ? 2
            : vertexInput.Tessellation is not null ? 3
            : !_inPlace.Valid ? 4
            : !IsReplayable(vertexInput.Stage.Program) ? 5
            : state.PixelActive && !IsReplayable(pixelInput.Stage.Program) ? 6
            : -1;
        var sequence = _host.CurrentItemSequence;
        if (refusal < 0 && sequence == 0)
        {
            refusal = 7;
        }

        if (refusal >= 0)
        {
            _sealRefusals[refusal].Increment();
            return;
        }

        _replayVertex = vertexInput.Stage;
        _replayPixel = state.PixelActive ? pixelInput.Stage : default;
        _replayVertexBuffers = vertexInput.Buffers;
        _replayVertexAttributes = vertexInput.Attributes;
        _replayPixelActive = state.PixelActive;
        _replaySequence = sequence;
        _replayValid = true;
        _host.SealDrawReplay();
    }

    private bool TryReplayDraw(ResolvedDraw record, ref DrawState state, bool inPlace, VertexInputInfo vertexInput, PixelInputInfo pixelInput,
        in IndexSource indexSource, in DrawEmission emission, in DrawCall draw)
    {
        if (!DrawReplayEnabled || !_replayValid || !inPlace || record.Kind != ResolvedDrawKind.Draw || record.WritesMemory ||
            record.WriteStages != PipelineStageFlags.None || record.IndexedTrace is not null || MeshDrawTrace.Active)
        {
            return false;
        }

        var sequence = _host.CurrentItemSequence;
        if (sequence == 0 || sequence != _replaySequence + 1)
        {
            return ReplayMiss(0);
        }

        if (state.PixelActive != _replayPixelActive)
        {
            return ReplayMiss(1);
        }

        if (StageInputMismatch(vertexInput.Stage, _replayVertex) is var vertexMismatch and >= 0)
        {
            return ReplayMiss(2 + vertexMismatch);
        }

        if (state.PixelActive && StageInputMismatch(pixelInput.Stage, _replayPixel) is var pixelMismatch and >= 0)
        {
            return ReplayMiss(11 + pixelMismatch);
        }

        if (!SameVertexInput(vertexInput))
        {
            return ReplayMiss(20);
        }

        RenderExecutor.DropUnwrittenColorTargets(record.Context, ref state, state.PixelActive ? pixelInput.Stage.Program : null);
        if (!TryRestoreAttachments(ref state))
        {
            return ReplayMiss(21);
        }

        var indexBuffer = AcquireIndexBuffer(in indexSource);
        var indirectArguments = emission.IndirectArgumentsAddress != 0
            ? _host.ObtainBuffer(emission.IndirectArgumentsAddress,
                emission.Indexed ? RenderExecutor.IndexedIndirectArgumentsSize : RenderExecutor.AutoIndirectArgumentsSize, isWritten: false)
            : default;
        var pipeline = ResolvePipeline(record, ref state, vertexInput, state.PixelActive ? pixelInput : null);
        if (!_host.TryReplayDraw(in pipeline, vertexInput.Stage.Resources.UserData,
                state.PixelActive ? pixelInput.Stage.Resources.UserData : ReadOnlySpan<uint>.Empty))
        {
            return ReplayMiss(22);
        }

        NoteSampledTextures(vertexInput.Stage);
        if (state.PixelActive)
        {
            NoteSampledTextures(pixelInput.Stage);
        }

        var submitId = record.SubmitId;
        if (record.SetBindDebug)
        {
            SetDrawDebugPhase(submitId, in draw, 0x100);
        }

        if (record.SetAutoDebug)
        {
            SetDrawDebugPhase(submitId, in draw, 0x400);
        }

        if (indexBuffer.Size != 0)
        {
            _host.BindIndexBuffer(indexBuffer.Binding, indexBuffer.Type);
        }

        _host.SetDynamicState(in record.Dynamic);
        BeginDrawRendering(ref state, reusedAttachments: true);
        _host.BindPipeline(PipelineBindPoint.Graphics, in pipeline);
        if (record.SetAutoDebug)
        {
            SetDrawDebugPhase(submitId, in draw, 0x500);
        }

        if (emission.IndirectArgumentsAddress != 0 && emission.Indexed)
        {
            _host.DrawIndexedIndirect(indirectArguments);
        }
        else if (emission.IndirectArgumentsAddress != 0)
        {
            _host.DrawIndirect(indirectArguments);
        }
        else
        {
            EmitDraw(record.PrimitiveType, vertexInput, in draw, in emission);
        }

        if (record.SetAutoDebug)
        {
            SetDrawDebugPhase(submitId, in draw, 0x600);
        }

        KeepTargets(record, ref state);
        if (record.SetAutoDebug)
        {
            SetDrawDebugPhase(submitId, in draw, 0x700);
        }

        _replaySequence = sequence;
        _host.SealDrawReplay();
        _drawReplays.Increment();
        return true;
    }

    private void DrawMeshTasks(ResolvedDraw record, MeshDrawConfiguration mesh, uint meshVertexCount)
    {
        ref readonly var draw = ref record.Draw;
        ref readonly var emission = ref record.Emission;
        var primitiveCount = mesh.Geometry.InputPointList ? meshVertexCount
            : mesh.Geometry.InputLineList ? meshVertexCount / 2
            : mesh.Geometry.InputTriangleStrip ? (meshVertexCount >= 3 ? meshVertexCount - 2 : 0) : meshVertexCount / 3;
        var groupCount = (uint)(((ulong)primitiveCount + mesh.Geometry.InputPrimitiveCountPerWorkgroup - 1) / mesh.Geometry.InputPrimitiveCountPerWorkgroup);
        var totalGroupCount = (ulong)groupCount * draw.InstanceCount;
        if (groupCount > mesh.Execution.MaxGroupCountX || draw.InstanceCount > mesh.Execution.MaxGroupCountY ||
            totalGroupCount > mesh.Execution.MaxGroupTotalCount)
        {
            // Left out, as the GPU preparation leaves out an indirect draw over the limits.
            var left = Interlocked.Increment(ref _meshDrawsOverLimits);
            if ((left & (left - 1)) == 0)
            {
                Console.Error.WriteLine($"[GPU][WARN] A mesh draw over the device group limits was left out ({left} so far): " +
                    $"groups={groupCount}/{mesh.Execution.MaxGroupCountX} instances={draw.InstanceCount}/{mesh.Execution.MaxGroupCountY} " +
                    $"total={totalGroupCount}/{mesh.Execution.MaxGroupTotalCount} vertices={draw.Count} " +
                    $"hash=0x{(record.State.Programs.VertexInput.Stage.Program?.Hash ?? 0ul):X16}.");
            }

            return;
        }

        if (RenderTrace.Enabled)
        {
            RenderTrace.Write($"MeshDraw vertices={draw.Count} instances={draw.InstanceCount} groups={groupCount} " +
                $"threads={mesh.Geometry.ThreadsPerGroup} ldsDwords={mesh.Geometry.LocalDataShareDwords}");
        }

        _host.DrawMeshTasks(groupCount, draw.InstanceCount, 1);
        if (MeshDrawTrace.Active)
        {
            MeshDrawTrace.Write("command-recorded", $"groups={groupCount}/{draw.InstanceCount}/1 threads={mesh.Geometry.ThreadsPerGroup} wave={mesh.Geometry.WaveSize} lds={mesh.Geometry.LocalDataShareDwords} firstVertex={emission.FirstVertex} firstInstance={emission.FirstInstance}");
            MeshDrawTrace.Recorded();
        }
    }

    private ReadOnlySpan<BufferBinding> AcquireVertexBuffers(VertexInputInfo vertexInput)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.DrawVertexBufferAcquisition);
        var buffers = vertexInput.Buffers;
        if (buffers.Length > VertexInputInfo.MaxBuffers)
        {
            throw _host.Fatal($"The vertex input has too many buffers: count={buffers.Length} max={VertexInputInfo.MaxBuffers}.");
        }

        Span<VertexBufferRange> ranges = stackalloc VertexBufferRange[VertexInputInfo.MaxBuffers];
        Span<ulong> sizes = stackalloc ulong[buffers.Length];
        for (var slot = 0; slot < buffers.Length; slot++)
        {
            sizes[slot] = buffers[slot].Size;
        }

        foreach (var attribute in vertexInput.Attributes)
        {
            var slot = attribute.BufferIndex;
            var descriptor = attribute.Descriptor;
            if (buffers[slot].Stride != 0 || descriptor.RecordCount == 0 || descriptor.OutOfBounds != 2)
            {
                continue;
            }

            // This bounds mode tests only whether the record count is nonzero.
            // Constant attributes still need the full format extent.
            sizes[slot] = Math.Max(sizes[slot], (ulong)attribute.OffsetBytes + VertexAttributeFormats.StorageByteSize(descriptor));
        }

        var rangeCount = 0;
        for (var slot = 0; slot < buffers.Length; slot++)
        {
            ref readonly var vertex = ref buffers[slot];
            var size = sizes[slot];
            if (size == 0)
            {
                continue;
            }

            if (vertex.Address == 0 || size > ulong.MaxValue - vertex.Address)
            {
                throw _host.Fatal($"The vertex buffer range is invalid: address=0x{vertex.Address:X16} size=0x{size:X16}.");
            }

            ranges[rangeCount++] = new VertexBufferRange { BaseAddress = vertex.Address, RequestedEnd = vertex.Address + size };
        }

        ranges[..rangeCount].Sort(static (left, right) => left.BaseAddress.CompareTo(right.BaseAddress));
        Span<VertexBufferRange> merged = _vertexRanges;
        var mergedCount = 0;
        for (var i = 0; i < rangeCount; i++)
        {
            ref readonly var range = ref ranges[i];
            if (mergedCount != 0 && merged[mergedCount - 1].RequestedEnd >= range.BaseAddress)
            {
                merged[mergedCount - 1].RequestedEnd = Math.Max(merged[mergedCount - 1].RequestedEnd, range.RequestedEnd);
                continue;
            }

            merged[mergedCount++] = new VertexBufferRange { BaseAddress = range.BaseAddress, RequestedEnd = range.RequestedEnd };
        }

        for (var i = 0; i < mergedCount; i++)
        {
            ref var range = ref merged[i];
            var size = _host.ClampMappedSize(range.BaseAddress, range.RequestedSize);
            range.AcquiredEnd = range.BaseAddress + size;
            range.Binding = _host.ObtainBuffer(range.BaseAddress, size, isWritten: false);
        }

        var prepared = _vertexBindings.AsSpan(0, buffers.Length);
        BufferBinding? nullBuffer = null;
        Span<ulong> acquiredBytes = stackalloc ulong[buffers.Length];
        acquiredBytes.Clear();
        for (var slot = 0; slot < buffers.Length; slot++)
        {
            ref readonly var vertex = ref buffers[slot];
            if (sizes[slot] == 0)
            {
                nullBuffer ??= _host.NullBuffer;
                prepared[slot] = nullBuffer.Value;
                continue;
            }

            var found = -1;
            for (var i = 0; i < mergedCount; i++)
            {
                if (vertex.Address >= merged[i].BaseAddress && vertex.Address < merged[i].AcquiredEnd)
                {
                    found = i;
                    break;
                }
            }

            if (found < 0)
            {
                throw _host.Fatal($"The vertex buffer address is outside the acquired range: address=0x{vertex.Address:X16}.");
            }

            ref readonly var owner = ref merged[found];
            prepared[slot] = new BufferBinding(owner.Binding.Handle, owner.Binding.Offset + vertex.Address - owner.BaseAddress);
            acquiredBytes[slot] = owner.AcquiredEnd - vertex.Address;
        }

        foreach (var attribute in vertexInput.Attributes)
        {
            var slot = attribute.BufferIndex;
            _host.TraceVertexBuffer(vertexInput.Stage.Program?.Hash ?? 0, attribute, buffers[slot],
                prepared[slot], sizes[slot], acquiredBytes[slot]);
        }

        return prepared;
    }

    private PreparedIndexBuffer AcquireIndexBuffer(in IndexSource source)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.DrawIndexBufferAcquisition);
        if (!source.Enabled)
        {
            return default;
        }

        if (source.Size == 0)
        {
            throw _host.Fatal($"The index buffer is empty: address=0x{source.Address:X16} type={(int)source.Type}.");
        }

        var binding = source.HostData is { } hostData
            ? _host.UploadTransient(hostData, TransientIndexAlignment)
            : _host.ObtainBuffer(source.Address, source.Size, isWritten: false);
        return new PreparedIndexBuffer(binding, source.Size, source.Type);
    }

    private static bool IsSupportedSampleCount(uint samples) => samples is 1 or 2 or 4 or 8;

    private RenderingState AcquireAttachments(ref DrawState state)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.DrawAttachmentPreparation);
        var rendering = new RenderingState
        {
            Width = uint.MaxValue,
            Height = uint.MaxValue,
            Layers = uint.MaxValue,
            ColorAttachmentCount = state.ColorCount,
        };
        var attachmentSamples = 0u;
        for (var i = 0; i < state.ColorCount; i++)
        {
            ref var target = ref state.Colors[i];
            if (!target.Image.IsValid)
            {
                throw _host.Fatal($"A color target has no image: slot={target.Slot} address=0x{target.Resolution.BaseAddress:X16}.");
            }

            var acquired = _host.AcquireColorAttachment(in target);
            target.Image = acquired.Image;
            target.View = acquired.View;
            var samples = target.Resolution.Samples;
            if (acquired.Samples != samples || acquired.View.Handle == 0)
            {
                throw _host.Fatal($"The color attachment does not match its target: slot={target.Slot} imageSamples={acquired.Samples} targetSamples={samples} view=0x{acquired.View.Handle:X}.");
            }

            if (attachmentSamples == 0)
            {
                attachmentSamples = samples;
            }
            else if (attachmentSamples != samples)
            {
                throw _host.Fatal($"Mixed color attachment sample counts are not supported: first={attachmentSamples} next={samples}.");
            }

            var view = target.Resolution.Request.View;
            rendering.Width = Math.Min(rendering.Width, target.Resolution.Extent.Width);
            rendering.Height = Math.Min(rendering.Height, target.Resolution.Extent.Height);
            rendering.Layers = Math.Min(rendering.Layers, view.LayerCount);
            var clear = acquired.MetadataClear ? acquired.MetadataClearValue : target.Resolution.ColorClearValue;
            rendering.ColorAttachments[i] = new RenderingAttachment(
                acquired.View,
                view.Format,
                clear.Uint32_0,
                clear.Uint32_1,
                clear.Uint32_2,
                clear.Uint32_3,
                acquired.MetadataClear,
                HasDepth: false,
                DepthClear: false,
                HasStencil: false,
                StencilClear: false);
        }

        if (state.Depth.HasTarget)
        {
            ref var depth = ref state.Depth;
            var target = depth.Target.Target;
            var acquired = _host.AcquireDepthAttachment(in depth);
            depth.View = acquired.View;
            depth.MetadataClear = acquired.MetadataClear;
            if (acquired.View.Handle == 0 || acquired.Samples != target.Samples)
            {
                throw _host.Fatal($"The depth attachment does not match its target: imageSamples={acquired.Samples} targetSamples={target.Samples} view=0x{acquired.View.Handle:X}.");
            }

            if (attachmentSamples == 0)
            {
                attachmentSamples = target.Samples;
            }
            else if (attachmentSamples != target.Samples)
            {
                throw _host.Fatal($"Mixed color and depth sample counts are not supported: color={attachmentSamples} depth={target.Samples}.");
            }

            var loadState = depth.LoadState;
            _host.TransitionDepthAttachment(in depth, loadState.AttachmentWriteAspects(target.Format));
            var view = target.Request.View;
            rendering.Width = Math.Min(rendering.Width, target.Width);
            rendering.Height = Math.Min(rendering.Height, target.Height);
            rendering.Layers = Math.Min(rendering.Layers, view.LayerCount);
            var aspects = ViewFormatRules.DepthAspects(target.Format);
            rendering.DepthStencilAttachment = new RenderingAttachment(
                acquired.View,
                target.Format,
                BitConverter.SingleToUInt32Bits(depth.Target.State.DepthClearValue),
                depth.Target.State.StencilClearValue,
                0,
                0,
                IsClear: false,
                HasDepth: (aspects & ImageAspectFlags.DepthBit) != 0,
                DepthClear: depth.LoadClear,
                HasStencil: (aspects & ImageAspectFlags.StencilBit) != 0,
                StencilClear: depth.Target.State.StencilClearEnabled);
        }

        if (state.ColorCount == 0 && !state.Depth.HasTarget)
        {
            rendering.Width = _host.Limits.MaxFramebufferWidth;
            rendering.Height = _host.Limits.MaxFramebufferHeight;
        }
        else if (!IsSupportedSampleCount(attachmentSamples))
        {
            throw _host.Fatal($"The render state has no valid attachments: samples={attachmentSamples} colors={state.ColorCount} depth={state.Depth.HasTarget}.");
        }

        if (rendering.Layers == uint.MaxValue)
        {
            rendering.Layers = 1;
        }

        if (rendering.Width == 0 || rendering.Height == 0 || rendering.Layers == 0 || rendering.Width == uint.MaxValue || rendering.Height == uint.MaxValue)
        {
            throw _host.Fatal($"The rendering area is invalid: width={rendering.Width} height={rendering.Height} layers={rendering.Layers}.");
        }

        rendering.Samples = attachmentSamples == 0 ? 1 : attachmentSamples;
        return rendering;
    }

    private PipelineHandle ResolvePipeline(ResolvedDraw record, ref DrawState state, VertexInputInfo vertexInput, PixelInputInfo? pixelInput)
    {
        var boundSlots = 0u;
        for (var index = 0; index < state.ColorCount; index++)
        {
            boundSlots |= 1u << (int)state.Colors[index].Slot;
        }

        var key = new PipelineKey(
            record.ContextStamp,
            record.DynamicStamp,
            record.TargetVersion,
            state.ColorCount,
            boundSlots,
            state.Depth.HasTarget,
            vertexInput.Attributes,
            vertexInput.Buffers,
            pixelInput?.InterpolatorSettings,
            state.Programs.Vertex.Id,
            state.Programs.Pixel.Id,
            (int)record.Topology,
            record.PrimitiveRestart,
            state.Programs.DisableBlending);
        var kept = _pipeline;
        if (kept.Valid && kept.Key == key && kept.Rendering.Equals(state.Rendering))
        {
            return kept.Handle;
        }

        var pipeline = _pipelines.CreateGraphicsPipeline(
            DrawState.BoundColors(ref state),
            in state.Depth,
            vertexInput,
            pixelInput,
            record.Context,
            in state.Rendering,
            record.Topology,
            record.PrimitiveRestart,
            state.Programs.DisableBlending,
            state.Programs.Vertex,
            state.Programs.Pixel);
        kept.Key = key;
        kept.Rendering = state.Rendering;
        kept.Handle = pipeline;
        kept.Valid = true;
        return pipeline;
    }

    private void EmitDraw(GuestPrimitiveType primitiveType, VertexInputInfo vertexInput, in DrawCall draw, in DrawEmission emission)
    {
        switch (primitiveType)
        {
            case GuestPrimitiveType.PointList:
            case GuestPrimitiveType.LineList:
            case GuestPrimitiveType.LineStrip:
            case GuestPrimitiveType.TriangleList:
            case GuestPrimitiveType.TriangleFan:
            case GuestPrimitiveType.TriangleStrip:
            case GuestPrimitiveType.Polygon:
            case GuestPrimitiveType.RectangleList:
                if (emission.Indexed)
                {
                    _host.DrawIndexed(draw.Count, draw.InstanceCount, 0, emission.VertexOffset, emission.FirstInstance);
                }
                else
                {
                    _host.Draw(draw.Count, draw.InstanceCount, emission.FirstVertex, emission.FirstInstance);
                }

                break;
            case GuestPrimitiveType.RectangleListLegacy:
                if (emission.Indexed)
                {
                    throw _host.Fatal($"The primitive type is unknown for an indexed draw: primitiveType={(uint)primitiveType}.");
                }

                if (_host.SupportsNativeRectangles)
                {
                    // Keep guest vertex IDs unchanged; the host fills each three-vertex rectangle.
                    _host.Draw(draw.Count, draw.InstanceCount, emission.FirstVertex, emission.FirstInstance);
                    break;
                }

                if (draw.Count != 3 || vertexInput.Buffers.Length != 0)
                {
                    throw _host.Fatal($"A legacy rectangle list needs three vertices and no vertex buffers: count={draw.Count} buffers={vertexInput.Buffers.Length}.");
                }

                _host.Draw(4, draw.InstanceCount, emission.FirstVertex, emission.FirstInstance);
                break;
            case GuestPrimitiveType.QuadListLegacy:
                if ((draw.Count & 0x3) != 0)
                {
                    throw _host.Fatal($"A legacy quad list count is not a multiple of four: count={draw.Count}.");
                }

                for (var i = 0u; i < draw.Count; i += 4)
                {
                    if (emission.Indexed)
                    {
                        _host.DrawIndexed(4, draw.InstanceCount, i, emission.VertexOffset, emission.FirstInstance);
                    }
                    else
                    {
                        _host.Draw(4, draw.InstanceCount, i + emission.FirstVertex, emission.FirstInstance);
                    }
                }

                break;
            default:
                throw _host.Fatal($"The primitive type is unknown: primitiveType={(uint)primitiveType}.");
        }
    }
}
