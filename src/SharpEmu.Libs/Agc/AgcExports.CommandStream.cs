// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.VideoOut;
using SharpEmu.ShaderCompiler;

namespace SharpEmu.Libs.Agc;

// This partial connects the command interpreter's host to draw and dispatch translation.
public static partial class AgcExports
{
    private static readonly bool _traceFramePackets = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_TRACE_FRAME_PACKETS"),
        "1",
        StringComparison.Ordinal);
    private static readonly HashSet<uint> _tracedSubmittedDrawOpcodes = new();

    // The snapshots the submit-time prepass captured for one submission.
    private sealed record SubmittedGeometrySnapshots(
        Dictionary<ulong, SubmittedIndexSnapshot>? IndexSnapshots,
        Dictionary<ulong, SubmittedVertexSnapshot>? VertexSnapshots);

    // The host that runs the current slice on this thread; the compute fast paths write through it.
    [ThreadStatic]
    private static ICommandStreamHost? _activeCommandStreamHost;

    internal enum GeometrySnapshotDecision
    {
        None,
        Accepted,
        Rejected,
    }

    // What the last draw decided about its retained geometry; tests read it.
    internal static GeometrySnapshotDecision LastGeometrySnapshotDecisionForTests { get; private set; }

    internal static IReadOnlyDictionary<uint, uint> GetGeometryCaptureRegistersForTests(ICpuMemory memory) =>
        new Dictionary<uint, uint>(_submittedGpuStates.GetValue(CanonicalMemory(memory), static _ => new SubmittedGpuState()).GeometryCapture.ShRegisters);

    // Builds the snapshots of one draw as the prepass would, for tests of the execution-time check.
    internal static object CreateGeometrySnapshotsForTests(
        ulong packetAddress,
        uint indexSize,
        ulong indexAddress,
        uint indexCount,
        uint instanceCount,
        (uint Offset, uint Value)[] shaderRegisters)
    {
        var capture = new GeometryCaptureFingerprint(
            indexSize,
            indexAddress,
            indexCount,
            0,
            instanceCount,
            RecordShaderRegisters(shaderRegisters.ToDictionary(static entry => entry.Offset, static entry => entry.Value)));
        return new SubmittedGeometrySnapshots(
            null,
            new Dictionary<ulong, SubmittedVertexSnapshot>
            {
                [packetAddress] = new SubmittedVertexSnapshot(0, [], capture),
            });
    }

    // A snapshot is used only when the state it was captured under is the state the draw runs with.
    private static bool IsCaptureStateCurrent(
        GeometryCaptureFingerprint capture,
        SubmittedDcbState state,
        bool indexed,
        ulong indexAddress,
        uint count,
        uint instanceCount)
    {
        if (capture.IndexCount != count)
        {
            DcbSubmissionProfile.RecordCaptureRejection("count", capture.IndexCount, count);
            return false;
        }

        if (capture.InstanceCount != instanceCount)
        {
            DcbSubmissionProfile.RecordCaptureRejection("instances", capture.InstanceCount, instanceCount);
            return false;
        }

        if (indexed &&
            (capture.IndexSize != state.IndexSize || capture.IndexAddress != indexAddress || capture.DrawIndexOffset != state.DrawIndexOffset))
        {
            if (capture.IndexSize != state.IndexSize)
            {
                DcbSubmissionProfile.RecordCaptureRejection("index_size", capture.IndexSize, state.IndexSize);
            }
            else if (capture.IndexAddress != indexAddress)
            {
                DcbSubmissionProfile.RecordCaptureRejection("index_address", capture.IndexAddress, indexAddress);
            }
            else
            {
                DcbSubmissionProfile.RecordCaptureRejection("index_offset", capture.DrawIndexOffset, state.DrawIndexOffset);
            }
            return false;
        }

        var live = RecordShaderRegisters(state.ShRegisters);
        if (live.Length != capture.ShaderRegisters.Length)
        {
            DcbSubmissionProfile.RecordCaptureRejection("register_count", (ulong)capture.ShaderRegisters.Length, (ulong)live.Length);
            return false;
        }

        for (var index = 0; index < live.Length; index++)
        {
            if (live[index].Key != capture.ShaderRegisters[index].Key || live[index].Value != capture.ShaderRegisters[index].Value)
            {
                if (live[index].Key != capture.ShaderRegisters[index].Key)
                {
                    DcbSubmissionProfile.RecordCaptureRejection("register_offset", capture.ShaderRegisters[index].Key, live[index].Key);
                }
                else
                {
                    DcbSubmissionProfile.RecordCaptureRejection($"register_{live[index].Key:X}", capture.ShaderRegisters[index].Value, live[index].Value);
                }

                return false;
            }
        }

        return true;
    }

    internal static CommandStreamTranslation CreateCommandStreamTranslation(ICpuMemory memory, ICommandStreamHost host) =>
        new(memory, host);

    // Translates the draws, dispatches, resets and flips of one interpreter host thread.
    internal sealed class CommandStreamTranslation
    {
        private readonly ICommandStreamHost _host;
        private readonly CpuContext _context;
        private readonly SubmittedGpuState _gpuState;
        private SubmittedDcbState? _current;
        private int _currentQueueId;

        internal CommandStreamTranslation(ICpuMemory memory, ICommandStreamHost host)
        {
            _host = host;
            _context = new CpuContext(memory, Generation.Gen5);
            _gpuState = _submittedGpuStates.GetValue(CanonicalMemory(memory), static _ => new SubmittedGpuState());
        }

        public void BeginSubmission(int queueId, ulong submissionId, object? geometrySnapshots, GpuCommandInterpreter interpreter)
        {
            _activeCommandStreamHost = _host;
            var state = GetQueueState(queueId);
            state.AttachInterpreter(interpreter);
            state.ActiveSubmissionId = submissionId;
            var snapshots = geometrySnapshots as SubmittedGeometrySnapshots;
            state.ActiveIndexSnapshots = snapshots?.IndexSnapshots;
            state.ActiveVertexSnapshots = snapshots?.VertexSnapshots;
            _current = state;
            _currentQueueId = queueId;
        }

        private SubmittedDcbState GetQueueState(int queueId)
        {
            if (queueId == 0)
            {
                _gpuState.Graphics.QueueName = "dcb.graphics";
                return _gpuState.Graphics;
            }

            var owner = (uint)(GpuCommandInterpreter.ComputeQueueBase + queueId - 1);
            if (!_gpuState.ComputeQueues.TryGetValue(owner, out var state))
            {
                state = new SubmittedDcbState
                {
                    QueueName = $"acb.compute[{owner}]",
                    CompletionEventId = owner,
                };
                _gpuState.ComputeQueues.Add(owner, state);
            }

            return state;
        }

        private SubmittedDcbState RequireCurrent() =>
            _current ?? throw _host.Fatal("A draw arrived before its submission began.");

        public void DrawIndexed(ulong submitId, in DrawIndexedArguments arguments)
        {
            _ = submitId;
            var state = RequireCurrent();
            state.IndexBufferAddress = arguments.IndexAddress;
            state.IndexBufferCount = arguments.IndexCount;
            state.DrawIndexOffset = 0;
            state.IndexSize = arguments.IndexTypeAndSize & 0x3u;
            state.InstanceCount = Math.Max(arguments.InstanceCount, 1);
            SelectSnapshots(state, arguments.PacketAddress, indexed: true, arguments.IndexAddress, arguments.IndexCount);
            RunDraw(state, arguments.Opcode, arguments.IndexCount, indexed: true);
        }

        public void DrawAuto(ulong submitId, in DrawAutoArguments arguments)
        {
            _ = submitId;
            var state = RequireCurrent();
            state.InstanceCount = Math.Max(arguments.InstanceCount, 1);
            SelectSnapshots(state, arguments.PacketAddress, indexed: false, 0, arguments.VertexCount);
            RunDraw(state, arguments.Opcode, arguments.VertexCount, indexed: false);
        }

        // Candidates are found by packet address; each is validated against the execution-time state.
        private static void SelectSnapshots(SubmittedDcbState state, ulong packetAddress, bool indexed, ulong indexAddress, uint count)
        {
            using var validationScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.GeometrySnapshotValidation);
            var decision = GeometrySnapshotDecision.None;
            state.CurrentVertexSnapshot = null;
            if (state.ActiveVertexSnapshots is not null &&
                state.ActiveVertexSnapshots.TryGetValue(packetAddress, out var vertexSnapshot))
            {
                if (IsCaptureStateCurrent(vertexSnapshot.Capture, state, indexed, indexAddress, count, state.InstanceCount))
                {
                    state.CurrentVertexSnapshot = vertexSnapshot;
                    decision = GeometrySnapshotDecision.Accepted;
                }
                else
                {
                    decision = GeometrySnapshotDecision.Rejected;
                }
            }

            DcbSubmissionProfile.RecordCaptureSelection(decision);
            state.CurrentIndexSnapshot = null;
            if (indexed &&
                state.ActiveIndexSnapshots is not null &&
                state.ActiveIndexSnapshots.TryGetValue(packetAddress, out var indexSnapshot))
            {
                if (indexSnapshot.SourceAddress == indexAddress &&
                    indexSnapshot.IndexCount == count &&
                    IsCaptureStateCurrent(indexSnapshot.Capture, state, indexed, indexAddress, count, state.InstanceCount))
                {
                    state.CurrentIndexSnapshot = indexSnapshot;
                    if (decision != GeometrySnapshotDecision.Rejected)
                    {
                        decision = GeometrySnapshotDecision.Accepted;
                    }
                }
                else
                {
                    decision = GeometrySnapshotDecision.Rejected;
                }
            }

            LastGeometrySnapshotDecisionForTests = decision;
        }

        private void RunDraw(SubmittedDcbState state, uint opcode, uint count, bool indexed)
        {
            if (count == 0)
            {
                return;
            }

            state.FrameDrawCount++;
            if (_traceAgcShader)
            {
                lock (_submitTraceGate)
                {
                    if (_tracedSubmittedDrawOpcodes.Add(opcode))
                    {
                        TraceAgcShader($"agc.draw_packet op=0x{opcode:X2} count={count}");
                    }
                }
            }

            state.SawIndexedDraw |= indexed;
            var drawStarted = DcbParseProfile.Begin();
            try
            {
                TryTranslateGuestDraw(_context, _gpuState, state, count, indexed);
            }
            finally
            {
                DcbParseProfile.RecordDraw(drawStarted);
                state.CurrentIndexSnapshot = null;
                state.CurrentVertexSnapshot = null;
            }
        }

        public void Dispatch(ulong submitId, uint endX, uint endY, uint endZ, uint dispatchInitiator)
        {
            _ = submitId;
            var state = RequireCurrent();
            if (!TryDecodeComputeDispatch(state, endX, endY, endZ, dispatchInitiator, out var dispatch))
            {
                return;
            }

            state.FrameDispatchCount++;
            var dispatchStarted = DcbParseProfile.Begin();
            ObserveComputeDispatch(_context, _gpuState, state, dispatch);
            DcbParseProfile.RecordDispatch(dispatchStarted);
        }

        // The interpreter cleared its banks; the translation state drops what it derived from them.
        public void QueueReset(int queueId)
        {
            var state = GetQueueState(queueId);
            state.PresenterTexture = null;
            state.GuestDrawKind = GuestDrawKind.None;
            state.TranslatedDraw = null;
            state.RenderTargetWriters.Clear();
            state.IndirectArgsAddress = 0;
            state.SawIndexedDraw = false;
            state.IndexBufferAddress = 0;
            state.IndexBufferCount = 0;
            state.IndexSize = 0;
            state.InstanceCount = 1;
            state.DrawIndexOffset = 0;
            TraceAgc($"agc.queue_reset queue={state.QueueName} submission={state.ActiveSubmissionId}");
        }

        // Runs before the display buffer is captured: the deferred composite lands in it first.
        public void PrepareFlip(int handle, int displayBufferIndex)
        {
            var state = _gpuState.Graphics;
            TraceFramePacketSummary(state);
            SyncCpuWrittenGuestImages(_context);
            if (state.PendingTargetlessDraw is { } pendingComposite &&
                VideoOutExports.TryGetDisplayBufferInfo(handle, displayBufferIndex, out var pendingDisplayBuffer) &&
                state.KnownRenderTargets.TryGetValue(pendingDisplayBuffer.Address, out var pendingDisplayTarget))
            {
                var textures = CreateGuestDrawTextures(_context, pendingComposite.Textures, out _);
                var globalMemoryBuffers = CreateTranslatedDrawGlobalBuffers(pendingComposite);
                var vertexBuffers = CreateGuestVertexBuffers(pendingComposite.VertexInputs);
                ProvideRenderTargetInitialData(_context, pendingDisplayTarget);
                GuestGpu.Current.SubmitOffscreenTranslatedDraw(
                    pendingComposite.PixelShader,
                    textures,
                    globalMemoryBuffers,
                    pendingComposite.AttributeCount,
                    [CreateGuestRenderTarget(pendingDisplayTarget)],
                    pendingComposite.VertexShader,
                    pendingComposite.VertexCount,
                    pendingComposite.InstanceCount,
                    pendingComposite.PrimitiveType,
                    pendingComposite.IndexBuffer,
                    vertexBuffers,
                    pendingComposite.RenderState,
                    pendingComposite.DepthTarget,
                    pendingComposite.PixelShaderAddress,
                    pendingComposite.BaseVertex);
                TraceAgcShader(
                    $"agc.deferred_composite ps=0x{pendingComposite.PixelShaderAddress:X16} " +
                    $"src=0x{pendingComposite.Textures.FirstOrDefault()?.Descriptor.Address ?? 0:X16} " +
                    $"dst=0x{pendingDisplayTarget.Address:X16} " +
                    $"size={pendingDisplayTarget.Width}x{pendingDisplayTarget.Height}");
                state.PendingTargetlessDraw = null;
                state.TranslatedDraw = null;
            }

            ResetVideoDrawChainAtFlip();
            state.SawIndexedDraw = false;
            state.GuestDrawKind = GuestDrawKind.None;
            if (state.PendingTargetlessDraw is { } unusedPendingDraw)
            {
                ReturnPooledDrawArrays(unusedPendingDraw, globals: true, vertex: true, index: true);
                state.PendingTargetlessDraw = null;
            }

            state.TranslatedDraw = null;
        }
    }

    private static void TraceFramePacketSummary(SubmittedDcbState state)
    {
        if (!_traceFramePackets)
        {
            return;
        }

        var flip = ++state.FlipCount;
        if (flip <= 8 || flip % 60 == 0 || state.FrameDrawCount == 0)
        {
            Console.Error.WriteLine(
                $"[FRAMEPKT] flip={flip} submission={state.ActiveSubmissionId} " +
                $"draws={state.FrameDrawCount} dispatches={state.FrameDispatchCount}");
        }

        state.FrameDrawCount = 0;
        state.FrameDispatchCount = 0;
    }

    // A host without a GPU: managed transfers, immediate completions, video-out flip requests.
    internal class TranslatingCommandStreamHost : ManagedCommandStreamHost
    {
        private CommandStreamTranslation? _translation;
        private CommandStreamQueue? _queue;

        public TranslatingCommandStreamHost(ICpuMemory memory)
            : base(memory, new Gpu.Scheduling.EndOfPipeEvents(), VideoOutExports.HeadlessFlipTarget)
        {
        }

        public CommandStreamQueue Queue => _queue ?? throw Fatal("The command stream queue is not attached.");

        // The queue and the host reference each other; the queue is created after the host.
        public void AttachQueue(CommandStreamQueue queue)
        {
            _queue = queue;
            _translation = CreateCommandStreamTranslation(Memory, this);
        }

        private CommandStreamTranslation Translation => _translation ?? throw Fatal("The command stream translation is not attached.");

        public override void BeginSubmission(int queueId, ulong submissionId, object? geometrySnapshots) =>
            Translation.BeginSubmission(queueId, submissionId, geometrySnapshots, Queue.GetInterpreter(queueId));

        public override void DrawIndexed(ulong submitId, in DrawIndexedArguments arguments) =>
            Translation.DrawIndexed(submitId, in arguments);

        public override void DrawAuto(ulong submitId, in DrawAutoArguments arguments) =>
            Translation.DrawAuto(submitId, in arguments);

        public override void DispatchDirect(ulong submitId, uint groupsX, uint groupsY, uint groupsZ, uint dispatchInitiator) =>
            Translation.Dispatch(submitId, groupsX, groupsY, groupsZ, dispatchInitiator);

        public override void OnQueueReset(int queueId) => Translation.QueueReset(queueId);

        public override ulong PrepareFlip(int handle, int index, int flipMode, long flipArgument)
        {
            Translation.PrepareFlip(handle, index);
            return base.PrepareFlip(handle, index, flipMode, flipArgument);
        }
    }

    // The inline command stream of a backend without a presenter thread (test and startup path).
    internal sealed class HeadlessCommandStream
    {
        private static readonly ConditionalWeakTable<object, HeadlessCommandStream> _streams = new();
        private readonly object _gate = new();

        private HeadlessCommandStream(ICpuMemory memory)
        {
            Host = new TranslatingCommandStreamHost(memory);
            Queue = new CommandStreamQueue(Host);
            Host.AttachQueue(Queue);
        }

        public TranslatingCommandStreamHost Host { get; }

        public CommandStreamQueue Queue { get; }

        public static HeadlessCommandStream For(ICpuMemory memory) =>
            _streams.GetValue(CanonicalMemory(memory), _ => new HeadlessCommandStream(memory));

        // Runs on the submitting thread until nothing runnable is left; blocked heads wait for the next submit.
        public void Submit(uint queue, ulong address, uint dwordCount, ulong submissionId, object? geometrySnapshots)
        {
            lock (_gate)
            {
                if (queue == 0)
                {
                    Queue.EnqueueGraphics(address, dwordCount, submissionId, geometrySnapshots);
                }
                else
                {
                    Queue.EnqueueCompute(queue, address, dwordCount, submissionId, geometrySnapshots);
                }

                RunRunnable();
            }
        }

        public IdleOutcome Done()
        {
            lock (_gate)
            {
                RunRunnable();
                return Queue.Done();
            }
        }

        private void RunRunnable()
        {
            for (;;)
            {
                switch (Queue.ProcessOne())
                {
                    case SliceResult.Progressed:
                    case SliceResult.Completed:
                        continue;
                    case SliceResult.BlockedWithoutProgress:
                        if (Queue.HasUnblockedPending)
                        {
                            continue;
                        }

                        return;
                    default:
                        return;
                }
            }
        }

        // Retries the blocked heads once; tests use it after they wrote a label.
        public void RetryBlocked()
        {
            lock (_gate)
            {
                Queue.RetryBlocked();
                RunRunnable();
            }
        }
    }

    // Test support: the inline stream of one memory, so a test can retry or inspect blocked heads.
    internal static HeadlessCommandStream GetHeadlessCommandStreamForTests(ICpuMemory memory) =>
        HeadlessCommandStream.For(memory);

    // Test-only view of the index state the last graphics draw translated with.
    internal static bool TryGetGraphicsIndexStateForTests(
        CpuContext ctx,
        out ulong address,
        out uint count,
        out uint offset)
    {
        address = 0;
        count = 0;
        offset = 0;
        if (!_submittedGpuStates.TryGetValue(CanonicalMemory(ctx.Memory), out var gpuState))
        {
            return false;
        }

        address = gpuState.Graphics.IndexBufferAddress;
        count = gpuState.Graphics.IndexBufferCount;
        offset = gpuState.Graphics.DrawIndexOffset;
        return true;
    }

    // Test-only view of the interpreter's index type after the inline stream ran.
    internal static bool TryGetGraphicsIndexSizeForTests(CpuContext ctx, out uint indexSize)
    {
        indexSize = HeadlessCommandStream.For(ctx.Memory).Queue.GetInterpreter(0).IndexTypeAndSize;
        return true;
    }

    // Splits the dispatch decode that depends on registers from the packet reads.
    private static bool TryDecodeComputeDispatch(
        SubmittedDcbState state,
        uint dispatchEndX,
        uint dispatchEndY,
        uint dispatchEndZ,
        uint initiator,
        out ComputeDispatch dispatch)
    {
        dispatch = default;
        if ((initiator & 1) == 0 || dispatchEndX == 0 || dispatchEndY == 0 || dispatchEndZ == 0)
        {
            return false;
        }

        return TryDecodeComputeDispatchCore(
            state,
            dimensionsAddress: 0,
            initiator,
            "interpreter",
            dispatchEndX,
            dispatchEndY,
            dispatchEndZ,
            out dispatch);
    }
}
