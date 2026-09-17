// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
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

    internal sealed record RetainedTargetlessDraw(RegisterBanks Banks, TargetlessDrawArguments Arguments);

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
        (uint Offset, uint Value)[] shaderRegisters,
        SubmittedVertexData? vertexData = null,
        ulong exportShaderAddress = 0)
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
                [packetAddress] = new SubmittedVertexSnapshot(exportShaderAddress, vertexData, capture),
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
        private readonly ShaderPipelineCache? _pipelines;
        private readonly RenderExecutor? _executor;

        internal CommandStreamTranslation(ICpuMemory memory, ICommandStreamHost host)
        {
            _host = host;
            _context = new CpuContext(memory, Generation.Gen5);
            _gpuState = _submittedGpuStates.GetValue(CanonicalMemory(memory), static _ => new SubmittedGpuState());
            if (host is IRenderHost renderHost && host is IShaderPipelineHost pipelineHost)
            {
                _pipelines = new ShaderPipelineCache(_context, pipelineHost, GuestGpu.Current, CreateShaderHeaderRegistry(_context));
                _executor = new RenderExecutor(renderHost, _pipelines);
            }
        }

        // The context bank of the queue whose slice runs now; the Metal host rebuilds its records from it.
        internal ContextRegisters? CurrentContextRegisters => _current?.TypedRegisters?.Context;

        internal SubmittedVertexData? FindSubmittedVertexData(VertexInputInfo input)
        {
            var snapshot = _current?.CurrentVertexSnapshot;
            if (snapshot?.Data is not { } data || snapshot.ExportShaderAddress != input.Stage.ShaderBase || !data.Matches(input))
            {
                DcbSubmissionProfile.RecordVertexSnapshot(snapshot is not null, matched: false);
                return null;
            }

            DcbSubmissionProfile.RecordVertexSnapshot(available: true, matched: true);
            return data;
        }

        private RegisterBanks RequireTypedRegisters(SubmittedDcbState state) =>
            state.TypedRegisters ?? throw _host.Fatal($"The queue state has no typed register banks: queue={state.QueueName} submission={state.ActiveSubmissionId}.");

        // The words of every bound color target, so a flip can replay a retained draw into the display buffer.
        private static void RecordKnownColorTargets(SubmittedDcbState state, RegisterBanks banks)
        {
            var context = banks.Context;
            for (var slot = 0u; slot < ContextRegisters.ColorTargetCount; slot++)
            {
                var words = context.ColorTargets[slot];
                if (words.BaseAddress != 0 && context.RenderTargetMaskForSlot(slot) != 0)
                {
                    state.KnownColorTargets[words.BaseAddress] = words;
                }
            }
        }

        private bool TryRunExecutorDraw(ulong submitId, SubmittedDcbState state, bool indexed, in DrawIndexedArguments indexedArguments, in DrawAutoArguments autoArguments)
        {
            if (_executor is not { } executor)
            {
                return false;
            }

            var banks = RequireTypedRegisters(state);
            RecordKnownColorTargets(state, banks);
            state.FrameDrawCount++;
            state.SawIndexedDraw |= indexed;
            var drawStarted = DcbParseProfile.Begin();
            try
            {
                if (indexed)
                {
                    executor.DrawIndexed(submitId, banks, in indexedArguments);
                }
                else
                {
                    executor.DrawAuto(submitId, banks, in autoArguments);
                }
            }
            finally
            {
                DcbParseProfile.RecordDraw(drawStarted);
                state.CurrentIndexSnapshot = null;
                state.CurrentVertexSnapshot = null;
            }

            return true;
        }

        // Keeps the banks the draw was issued under; the flip replays it into the display buffer.
        internal void RetainTargetlessDraw(RegisterBanks banks, in TargetlessDrawArguments arguments)
        {
            var state = RequireCurrent();
            state.RetainedTargetlessDraw = new RetainedTargetlessDraw(banks.Clone(), arguments);
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
            RecordIndexedDrawState(in arguments);
            if (!TryRunExecutorDraw(submitId, RequireCurrent(), indexed: true, in arguments, default))
                throw _host.Fatal("The command stream has no render executor.");
        }

        internal void RecordIndexedDrawState(in DrawIndexedArguments arguments)
        {
            var state = RequireCurrent();
            state.IndexBufferAddress = arguments.IndexAddress;
            state.IndexBufferCount = arguments.IndexCount;
            state.DrawIndexOffset = 0;
            state.IndexSize = arguments.IndexTypeAndSize & 0x3u;
            state.InstanceCount = Math.Max(arguments.InstanceCount, 1);
            SelectSnapshots(state, arguments.PacketAddress, indexed: true, arguments.IndexAddress, arguments.IndexCount);
        }

        public void DrawAuto(ulong submitId, in DrawAutoArguments arguments)
        {
            RecordAutoDrawState(in arguments);
            if (!TryRunExecutorDraw(submitId, RequireCurrent(), indexed: false, default, in arguments))
                throw _host.Fatal("The command stream has no render executor.");
        }

        internal void RecordAutoDrawState(in DrawAutoArguments arguments)
        {
            var state = RequireCurrent();
            state.InstanceCount = Math.Max(arguments.InstanceCount, 1);
            SelectSnapshots(state, arguments.PacketAddress, indexed: false, 0, arguments.VertexCount);
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


        public void Dispatch(ulong submitId, uint endX, uint endY, uint endZ, uint dispatchInitiator)
        {
            var state = RequireCurrent();
            if (_executor is { } executor)
            {
                var banks = RequireTypedRegisters(state);
                state.FrameDispatchCount++;
                var executorStarted = DcbParseProfile.Begin();
                try
                {
                    executor.Dispatch(submitId, banks, endX, endY, endZ, dispatchInitiator);
                }
                finally
                {
                    DcbParseProfile.RecordDispatch(executorStarted);
                }

                return;
            }

            throw _host.Fatal("The command stream has no render executor.");
        }

            // The retained draw runs into the display buffer with the target words it was last bound with.
        private void ReplayRetainedTargetlessDraw(RenderExecutor executor, SubmittedDcbState state, int handle, int displayBufferIndex)
        {
            if (state.RetainedTargetlessDraw is not { } retained)
            {
                return;
            }

            state.RetainedTargetlessDraw = null;
            if (!VideoOutExports.TryGetDisplayBufferInfo(handle, displayBufferIndex, out var displayBuffer) ||
                !state.KnownColorTargets.TryGetValue(displayBuffer.Address, out var words))
            {
                return;
            }

            var banks = retained.Banks;
            banks.Context.ColorTargets[0] = words;
            banks.Context.RenderTargetMask = 0xF;
            var arguments = retained.Arguments;
            if (arguments.Indexed)
            {
                executor.DrawIndexed(arguments.SubmitId, banks, arguments.Indexed_);
            }
            else
            {
                executor.DrawAuto(arguments.SubmitId, banks, arguments.Auto);
            }

            TraceAgcShader(
                $"agc.deferred_composite dst=0x{displayBuffer.Address:X16} export=0x{banks.Shader.Vertex.ExportAddress:X16} " +
                $"pixel=0x{banks.Shader.Pixel.Address:X16} size={displayBuffer.Width}x{displayBuffer.Height}");
        }

        // The interpreter cleared its banks; the translation state drops what it derived from them.
        public void QueueReset(int queueId)
        {
            var state = GetQueueState(queueId);
            state.GuestDrawKind = GuestDrawKind.None;
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
            if (_executor is { } executor)
            {
                ReplayRetainedTargetlessDraw(executor, state, handle, displayBufferIndex);
            }


            state.SawIndexedDraw = false;
            state.GuestDrawKind = GuestDrawKind.None;

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

        protected CommandStreamTranslation Translation => _translation ?? throw Fatal("The command stream translation is not attached.");

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
        // Command-state tests record packet state; render tests use the executor's recording host.
        private sealed class CommandStateHost(ICpuMemory memory) : TranslatingCommandStreamHost(memory)
        {
            public override void DrawIndexed(ulong submitId, in DrawIndexedArguments arguments) =>
                Translation.RecordIndexedDrawState(in arguments);

            public override void DrawAuto(ulong submitId, in DrawAutoArguments arguments) =>
                Translation.RecordAutoDrawState(in arguments);

            public override void DispatchDirect(ulong submitId, uint groupsX, uint groupsY, uint groupsZ, uint dispatchInitiator)
            {
                Dispatches.Add((submitId, groupsX, groupsY, groupsZ, dispatchInitiator));
            }

            internal List<(ulong Submission, uint GroupsX, uint GroupsY, uint GroupsZ, uint Initiator)> Dispatches { get; } = [];
        }
        private static readonly ConditionalWeakTable<object, HeadlessCommandStream> _streams = new();
        private readonly object _gate = new();

        private HeadlessCommandStream(ICpuMemory memory)
        {
            Host = new CommandStateHost(memory);
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

}
