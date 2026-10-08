// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.ShaderCompiler;
using SharpEmu.Libs.Gpu.Vulkan;
using Silk.NET.Vulkan;

internal static unsafe partial class VulkanVideoPresenter
{
    // The presenter whose render thread runs the command stream; null while no window runs.
    private static Presenter? _activePresenter;
    private static Exception? _presenterStartupFailure;
    internal static Func<ICpuMemory, AgcExports.HeadlessCommandStream>? TestCommandStreamFactory { get; set; }

    public static void SubmitCommandStream(ICpuMemory memory, uint queue, ulong address, uint dwordCount, ulong submissionId, object? geometrySnapshots)
    {
        SubmissionFlowProfile.RecordGuest(SubmissionFlowProfile.EventKind.PresenterEntered,
            queue, submissionId, address, dwordCount);
        lock (_gate)
        {
            if (_closed || HostSessionControl.IsShutdownRequested || Volatile.Read(ref _presenterCloseRequested))
            {
                if (_presenterStartupFailure is { } failure)
                    throw new InvalidOperationException("The graphics presenter could not start.", failure);
                throw new OperationCanceledException("The graphics session is shutting down.");
            }
        }
        if (TestCommandStreamFactory?.Invoke(memory) is { } testStream)
        {
            testStream.Submit(queue, address, dwordCount, submissionId, geometrySnapshots);
            return;
        }

        EnsureStarted(1280, 720);
        WaitForShaderPrewarm("first GPU submission");
        // Queue publication precedes device initialization; only the render thread consumes it.
        lock (_gate)
        {
            while (_activePresenter is null && !_closed && !HostSessionControl.IsShutdownRequested &&
                   !Volatile.Read(ref _presenterCloseRequested))
            {
                System.Threading.Monitor.Wait(_gate);
            }

            if (_presenterStartupFailure is { } failure)
            {
                throw new InvalidOperationException("The graphics presenter could not start.", failure);
            }
            if (_closed || HostSessionControl.IsShutdownRequested || Volatile.Read(ref _presenterCloseRequested))
            {
                throw new OperationCanceledException("The graphics session is shutting down.");
            }
            _activePresenter!.EnqueueCommandStream(queue, address, dwordCount, submissionId, geometrySnapshots);
            Presenter.WakeRenderThread();
        }
    }

    public static IdleOutcome SubmitDone(ICpuMemory memory) =>
        Volatile.Read(ref _presenterStartupFailure) is not null ? IdleOutcome.Failed :
        HostSessionControl.IsShutdownRequested || Volatile.Read(ref _closed) || Volatile.Read(ref _presenterCloseRequested) ? IdleOutcome.Cancelled :
        TryGetActivePresenter(out var presenter)
            ? presenter.CommandStream.Done()
            : TestCommandStreamFactory?.Invoke(memory) is { } testStream
                ? testStream.Done()
                : IdleOutcome.Completed;

    // The blocked heads of the stream this memory submits to; null when no stream exists for it.
    internal static BlockedSnapshot? SnapshotBlockedCommandStream(ICpuMemory? memory)
    {
        if (TryGetActivePresenter(out var presenter))
        {
            return presenter.CommandStream.SnapshotBlocked();
        }

        return memory is not null && TestCommandStreamFactory?.Invoke(memory) is { } testStream
            ? testStream.Queue.SnapshotBlocked()
            : null;
    }

    private static bool TryGetActivePresenter([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Presenter? presenter)
    {
        lock (_gate)
        {
            presenter = _closed ? null : _activePresenter;
            return presenter is not null;
        }
    }

    // A draw or dispatch reaches the presenter only from the render thread; none is queued.
    private static bool TryGetRenderThreadPresenter([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Presenter? presenter)
    {
        presenter = Volatile.Read(ref _activePresenter);
        if (presenter is null || !presenter.IsVulkanReady)
        {
            presenter = null;
            return false;
        }

        if (!presenter.Relay.IsGpuQueueThread)
        {
            throw SubmissionScheduler.Fatal("A guest draw was submitted from a thread other than the render thread.");
        }

        return true;
    }

    // A video-out export flip: queued on the graphics queue, so it captures after the draws before it.
    public static bool TrySubmitGuestImage(int videoOutHandle, int displayBufferIndex, ulong address, uint width, uint height, uint pitchInPixel, ulong flipRequestId)
    {
        _ = width;
        _ = height;
        _ = pitchInPixel;
        WaitForShaderPrewarm("first flip");
        if (!IsKnownDisplayBuffer(address) || !TryGetActivePresenter(out var presenter))
        {
            return false;
        }

        if (!presenter.CommandStream.TryEnqueueFlipPreparation(videoOutHandle, displayBufferIndex, flipRequestId))
        {
            return false;
        }

        Presenter.WakeRenderThread();
        return true;
    }

    private static readonly string[] _commandQueueNames = CreateCommandQueueNames();

    private static string[] CreateCommandQueueNames()
    {
        var names = new string[CommandStreamQueue.QueueCount];
        names[0] = "dcb.graphics";
        for (var queueId = 1; queueId < names.Length; queueId++)
        {
            names[queueId] = $"acb.compute[{GpuCommandInterpreter.ComputeQueueBase + queueId - 1}]";
        }

        return names;
    }

    private sealed partial class Presenter : ICommandStreamHost, IFlipSubmitter
    {
        // This partial hosts the command interpreter on the render thread.

        private readonly CommandStreamQueue _commandStream;
        private readonly EndOfPipeEvents _endOfPipeEvents = new();
        private EndOfPipe _endOfPipe = null!;
        private AgcExports.CommandStreamTranslation _translation = null!;
        private ICpuMemory _guestMemory = null!;

        public CommandStreamQueue CommandStream => _commandStream;

        private void CreateCommandStream()
        {
            var (_, guest, _) = RequireGuestMemory("command stream");
            _guestMemory = guest;
            _endOfPipe = new EndOfPipe(_endOfPipeEvents, this);
            _translation = AgcExports.CreateCommandStreamTranslation(guest, this);
        }

        public void EnqueueCommandStream(uint queue, ulong address, uint dwordCount, ulong submissionId, object? geometrySnapshots)
        {
            RenderPhaseProfile.RecordSubmissionArrival();
            if (queue == 0)
            {
                _commandStream.EnqueueGraphics(address, dwordCount, submissionId, geometrySnapshots);
            }
            else
            {
                _commandStream.EnqueueCompute(queue, address, dwordCount, submissionId, geometrySnapshots);
            }
        }

        // The command stream thread interprets the submissions; this thread runs what it hands over
        // until the budget ends or nothing is queued. Null until the first render tick.
        private CommandThread? _commandThread;
        private Action? _betweenCommandItems;

        private bool OnCommandThread => _commandThread is { IsProducerThread: true };

        // Items run in batches; completions are collected between them so blocked waits retry.
        private const int CommandItemBatch = 256;

        private void RunCommandStreamSlices(long renderWorkDeadline)
        {
            if (_commandThread is null)
            {
                _commandThread = new CommandThread(_commandStream, _guestMemory, RunDeferredWork, _translation.Prefetch);
                _betweenCommandItems = RunRelayBetweenCommandItems;
                _translation.Sink = _commandThread;
                _commandThread.Start();
            }

            var thread = _commandThread;
            for (;;)
            {
                using (RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.Collect))
                {
                    CollectCompletedGuestSubmissions(waitForOldest: false);
                }

                int ran;
                using (RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.CommandStream))
                {
                    var aliasAccess = _guestBacking?.TryEnterBackingAliasAccess() == true;
                    _backingAliasAccess = aliasAccess;
                    try
                    {
                        ran = thread.RunBatch(renderWorkDeadline, CommandItemBatch, _betweenCommandItems!);
                    }
                    finally
                    {
                        _backingAliasAccess = false;
                        if (aliasAccess)
                        {
                            _guestBacking!.ExitBackingAliasAccess();
                        }
                    }
                }

                if (ran == 0 || System.Diagnostics.Stopwatch.GetTimestamp() >= renderWorkDeadline)
                {
                    return;
                }
            }
        }

        private void RunDeferredWork(AgcExports.DeferredWork work)
        {
            using var translationScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.CommandDrawTranslation);
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                _translation.RunDeferred(work);
            }
            finally
            {
                Interlocked.Add(ref _perfDrawTicks, System.Diagnostics.Stopwatch.GetTimestamp() - started);
            }
        }

        // Commands other threads post to the GPU worker run between items, with the resolution
        // worker held: they change the caches it reads.
        private void RunRelayBetweenCommandItems()
        {
            if (_relay.HasPendingCommands)
            {
                RunRelayCommandsWithPrefetchHeld();
            }
        }

        private void RunRelayCommandsWithPrefetchHeld()
        {
            var prefetch = _commandThread is not null ? _translation.Prefetch : null;
            prefetch?.Pause();
            try
            {
                using var relayScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.QueueRelay);
                _relay.RunPendingCommands();
            }
            finally
            {
                prefetch?.Resume();
            }
        }

        // The interpreter's own stores (labels, atomics, semaphores) follow every deferred draw.
        private DrainingWriteMemory? _commandMemory;

        ICpuMemory ICommandStreamHost.Memory => _commandMemory ??= new DrainingWriteMemory(_guestMemory, DrainDraws, this);

        // On the command stream thread a store is queued behind the work before it and reads see
        // the queued stores; elsewhere the deferred draws run first.
        private sealed class DrainingWriteMemory(ICpuMemory inner, Action drain, Presenter presenter) : ICpuMemory, ICpuMemoryWrapper
        {
            public ICpuMemory Inner => inner;

            public bool TryRead(ulong virtualAddress, Span<byte> destination) =>
                presenter.OnCommandThread
                    ? presenter._commandThread!.ReadOverlay(virtualAddress, destination)
                    : inner.TryRead(virtualAddress, destination);

            public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
            {
                if (presenter.OnCommandThread)
                {
                    presenter._commandThread!.PostWrite(virtualAddress, source);
                    return true;
                }

                drain();
                return inner.TryWrite(virtualAddress, source);
            }

            public bool TryCompare(ulong virtualAddress, ReadOnlySpan<byte> expected, out bool equal)
            {
                if (presenter.OnCommandThread)
                {
                    var bytes = expected.ToArray();
                    var compared = false;
                    var result = presenter._commandThread!.Run(() => TryCompare(virtualAddress, bytes, out compared));
                    equal = compared;
                    return result;
                }

                drain();
                return inner.TryCompare(virtualAddress, expected, out equal);
            }

            public bool TryCopy(ulong destinationAddress, ulong sourceAddress, ulong length)
            {
                if (presenter.OnCommandThread)
                {
                    return presenter._commandThread!.Run(() => TryCopy(destinationAddress, sourceAddress, length));
                }

                drain();
                return inner.TryCopy(destinationAddress, sourceAddress, length);
            }

            public bool TryScanCString(ulong address, byte needle, bool findLast, ulong maxLength, out ulong match) =>
                inner.TryScanCString(address, needle, findLast, maxLength, out match);

            public bool CanRead(ulong address, ulong size) => inner.CanRead(address, size);

            public string DescribeReadRange(ulong address, ulong size) => inner.DescribeReadRange(address, size);
        }

        // A read the render thread runs after everything queued before it, for the command thread.
        private bool ReadOnRenderThread(ulong address, Span<byte> destination, bool operand)
        {
            var buffer = new byte[destination.Length];
            var read = _commandThread!.Run(() => operand ? TryReadGuestOperand(address, buffer) : TryReadGuest(address, buffer));
            buffer.CopyTo(destination);
            return read;
        }

        // Command memory and polled labels: GPU-written pages go through the render thread, the
        // rest is read here with the stream's queued stores applied.
        private bool ReadOnCommandThread(ulong address, Span<byte> destination)
        {
            _commandThread!.WaitForPendingRange(address, (ulong)destination.Length);
            return _bufferCache.MayHaveGpuDirtyPages(address, (ulong)destination.Length)
                ? ReadOnRenderThread(address, destination, operand: false)
                : _commandThread!.ReadOverlay(address, destination);
        }

        public bool TryReadGuestWaitOperand(ulong address, Span<byte> destination) =>
            OnCommandThread ? ReadOnCommandThread(address, destination) : TryReadGuestOperand(address, destination);

        public bool TryReadGuestOperand(ulong address, Span<byte> destination)
        {
            if (OnCommandThread)
            {
                return ReadOnRenderThread(address, destination, operand: true);
            }

            DrainDraws();
            return TryReadGuest(address, destination);
        }

        public bool TryReadGuest(ulong address, Span<byte> destination)
        {
            if (OnCommandThread)
            {
                return ReadOnCommandThread(address, destination);
            }

            using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.CommandMemorySync))
            {
                // GPU-written command memory is downloaded first: deferred draws are recorded before it.
                if (_translation is { HasPendingDraws: true } && _bufferCache.MayHaveGpuDirtyPages(address, (ulong)destination.Length))
                {
                    DrainDraws();
                }

                if (!_bufferCache.TrySynchronizeCpuRead(address, (ulong)destination.Length,
                    SharpEmu.HLE.GuestMemory.GuestMemoryProfile.ReadbackSource.CommandMemoryRead))
                {
                    return false;
                }
            }

            using var readScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.CommandMemoryRead);
            return _guestMemory.TryRead(address, destination);
        }

        public void RunPendingCommands()
        {
            // With the stream on its own thread the render thread runs them between its items.
            if (OnCommandThread)
            {
                return;
            }

            if (_relay.HasPendingCommands)
            {
                DrainDraws();
            }

            using var relayScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.QueueRelay);
            _relay.RunPendingCommands();
        }

        public void BeginSubmission(int queueId, ulong submissionId, object? geometrySnapshots)
        {
            if (OnCommandThread)
            {
                // The translation state belongs to this thread; the queue context to the render thread.
                _translation.BeginSubmission(queueId, submissionId, geometrySnapshots, _commandStream.GetInterpreter(queueId));
                _commandThread!.Post(() => BeginSubmissionContext(queueId, submissionId));
                return;
            }

            DrainDraws();
            BeginSubmissionContext(queueId, submissionId);
            _translation.BeginSubmission(queueId, submissionId, geometrySnapshots, _commandStream.GetInterpreter(queueId));
        }

        private void BeginSubmissionContext(int queueId, ulong submissionId)
        {
            using var contextScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.QueueContext);
            _activeGuestQueue = new VulkanGuestQueueIdentity(_commandQueueNames[queueId], submissionId);
            BindSubmissionContext(_activeGuestQueue);
            _ = CurrentRecordingBuffer();
        }

        public void Flush()
        {
            if (OnCommandThread)
            {
                _commandThread!.Post(Flush);
                return;
            }

            DrainDraws();
            using var flushScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.Flush);
            FlushBatchedGuestCommands();
        }

        public void FlushAndWait()
        {
            if (OnCommandThread)
            {
                _commandThread!.Run(FlushAndWait);
                return;
            }

            DrainDraws();
            using var waitScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.CommandGpuWait);
            _ = CurrentRecordingBuffer();
            _scheduler.FlushAndWait();
            CollectCompletedGuestSubmissions(waitForOldest: false);
        }

        public void SynchronizeGpu()
        {
            if (OnCommandThread)
            {
                _commandThread!.Run(SynchronizeGpu);
                return;
            }

            DrainDraws();
            using var waitScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.CommandGpuWait);
            _ = CurrentRecordingBuffer();
            _scheduler.Finish();
            CollectCompletedGuestSubmissions(waitForOldest: false);
        }

        public void RunGarbageCollector()
        {
            if (OnCommandThread)
            {
                _commandThread!.Post(RunGarbageCollector);
                return;
            }

            DrainDraws();
            RunGuestCacheCollection(endsFrame: false);
        }

        public void EmitGlobalBarrier()
        {
            if (OnCommandThread)
            {
                _commandThread!.Post(EmitGlobalBarrier);
                return;
            }

            DrainDraws();
            // Inside a rendering scope whose draws only wrote attachments, the barrier matters only
            // to what runs after the scope: everything before the scope is ordered by the barrier
            // BeginRendering records, and a draw that stores to memory ends a deferred scope first.
            if (DeferGlobalBarriers && _renderingActive && !_renderingWritesMemory)
            {
                _globalBarrierAfterRendering = true;
                return;
            }

            EndRendering();
            RecordGlobalBarrier(BeginBatchedGuestCommands());
        }

        private void RecordGlobalBarrier(CommandBuffer commandBuffer)
        {
            var barrier = new MemoryBarrier2
            {
                SType = StructureType.MemoryBarrier2,
                SrcAccessMask = AccessFlags2.MemoryWriteBit,
                DstAccessMask = AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit,
            };
            VulkanSynchronization.PipelineBarrier(_vk,
                commandBuffer,
                PipelineStageFlags.AllCommandsBit,
                PipelineStageFlags.AllCommandsBit,
                0,
                1,
                &barrier,
                0,
                null,
                0,
                null);
        }

        public void FillBuffer(ulong address, ulong size, uint value, bool isGds)
        {
            if (OnCommandThread)
            {
                _commandThread!.PostRange(() => FillBuffer(address, size, value, isGds), address, isGds ? 0 : size);
                return;
            }

            DrainDraws();
            using var transferScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.CommandMemoryTransfer);
            // Transfers the buffer cache completes in guest memory leave the rendering scope open;
            // its GPU paths end the scope before they record.
            _ = BeginBatchedGuestCommands();
            _bufferCache.FillBuffer(address, size, value, isGds);
        }

        public void CopyBuffer(ulong destination, ulong source, ulong size, bool destinationIsGds, bool sourceIsGds)
        {
            if (OnCommandThread)
            {
                _commandThread!.PostRange(() => CopyBuffer(destination, source, size, destinationIsGds, sourceIsGds), destination, destinationIsGds ? 0 : size);
                return;
            }

            DrainDraws();
            using var transferScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.CommandMemoryTransfer);
            // Transfers the buffer cache completes in guest memory leave the rendering scope open;
            // its GPU paths end the scope before they record.
            _ = BeginBatchedGuestCommands();
            _bufferCache.CopyBuffer(destination, source, size, destinationIsGds, sourceIsGds);
        }

        public void ReadGds(Span<uint> destination, uint wordOffset, uint wordCount)
        {
            if (OnCommandThread)
            {
                var words = new uint[destination.Length];
                _commandThread!.Run(() => ReadGds(words, wordOffset, wordCount));
                words.CopyTo(destination);
                return;
            }

            DrainDraws();
            EndOfPipe.ReadGdsWords(_bufferCache.GdsBuffer.Mapped, destination, wordOffset, wordCount);
        }

        public void RecordEndOfPipe(in EndOfPipeWrite write)
        {
            if (OnCommandThread)
            {
                var queued = write;
                _commandThread!.Post(() => RecordEndOfPipe(in queued));
                return;
            }

            DrainDraws();
            using var completionScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.CommandEndOfPipe);
            _ = BeginBatchedGuestCommands();
            var buffer = _scheduler.Current;
            switch (write.Kind)
            {
                case EndOfPipeWriteKind.Write32:
                    _endOfPipe.RecordWrite32(write.SubmitId, buffer, write.Destination, (uint)write.Value);
                    break;
                case EndOfPipeWriteKind.Write64:
                    _endOfPipe.RecordWrite64(write.SubmitId, buffer, write.Destination, write.Value);
                    break;
                case EndOfPipeWriteKind.WriteBack32:
                    _endOfPipe.RecordWrite32WithWriteBack(write.SubmitId, buffer, write.Destination, (uint)write.Value);
                    break;
                case EndOfPipeWriteKind.WriteBack64:
                    _endOfPipe.RecordWrite64WithWriteBack(write.SubmitId, buffer, write.Destination, write.Value);
                    break;
                case EndOfPipeWriteKind.InterruptOnly:
                    _endOfPipe.QueueInterruptOnCompletion(buffer, write.EventId, write.ContextId);
                    break;
                case EndOfPipeWriteKind.Interrupt32:
                    _endOfPipe.RecordWrite32WithInterrupt(write.SubmitId, buffer, write.Destination, (uint)write.Value, write.EventId, write.ContextId);
                    break;
                case EndOfPipeWriteKind.Interrupt64:
                    _endOfPipe.RecordWrite64WithInterrupt(write.SubmitId, buffer, write.Destination, write.Value, write.EventId, write.ContextId);
                    break;
                case EndOfPipeWriteKind.InterruptWriteBack32:
                    _endOfPipe.RecordWrite32WithInterruptAndWriteBack(write.SubmitId, buffer, write.Destination, (uint)write.Value, write.EventId, write.ContextId);
                    break;
                case EndOfPipeWriteKind.InterruptWriteBack64:
                    _endOfPipe.RecordWrite64WithInterruptAndWriteBack(write.SubmitId, buffer, write.Destination, write.Value, write.EventId, write.ContextId);
                    break;
                case EndOfPipeWriteKind.GdsWrite32:
                    _endOfPipe.RecordGdsWrite32(write.SubmitId, buffer, write.Destination, write.GdsWordOffset, write.GdsWordCount);
                    break;
                case EndOfPipeWriteKind.ClockWrite:
                    _endOfPipe.RecordClockWrite(write.SubmitId, buffer, write.Destination);
                    break;
                case EndOfPipeWriteKind.ClockWriteBack:
                    _endOfPipe.RecordClockWriteWithWriteBack(write.SubmitId, buffer, write.Destination);
                    break;
                case EndOfPipeWriteKind.Flip:
                    _endOfPipe.RecordFlipCompletion(write.SubmitId, buffer, write.FlipHandle, write.FlipIndex, write.FlipMode, write.FlipArgument, write.FlipRequestId);
                    break;
                case EndOfPipeWriteKind.FlipWithWrite32:
                    _endOfPipe.RecordWrite32WithFlip(write.SubmitId, buffer, write.Destination, (uint)write.Value, write.FlipHandle, write.FlipIndex, write.FlipMode, write.FlipArgument, write.FlipRequestId);
                    break;
                case EndOfPipeWriteKind.FlipWithInterruptWriteBack32:
                    _endOfPipe.RecordWrite32WithInterruptWriteBackAndFlip(write.SubmitId, buffer, write.Destination, (uint)write.Value, write.FlipHandle, write.FlipIndex, write.FlipMode, write.FlipArgument, write.FlipRequestId, write.EventId);
                    break;
                default:
                    throw SubmissionScheduler.Fatal($"The end-of-pipe write kind is unknown: kind={write.Kind}.");
            }
        }

        public void TriggerInterrupt(int eventId, uint contextId)
        {
            if (OnCommandThread)
            {
                _commandThread!.Post(() => TriggerInterrupt(eventId, contextId));
                return;
            }

            DrainDraws();
            _endOfPipeEvents.TriggerInterrupt(eventId, contextId);
        }

        public bool HasFlipSlot()
        {
            if (OnCommandThread)
            {
                return _commandThread!.Run(HasFlipSlot);
            }

            DrainDraws();
            lock (_gate)
            {
                return _pendingGuestImagePresentations.Count < MaxPendingGuestFlipVersions;
            }
        }

        public ulong PrepareFlip(int handle, int index, int flipMode, long flipArgument)
        {
            if (OnCommandThread)
            {
                return _commandThread!.Run(() => PrepareFlip(handle, index, flipMode, flipArgument));
            }

            DrainDraws();
            _ = BeginBatchedGuestCommands();
            return _endOfPipe.PrepareVideoOutFlip(_scheduler.Current, handle, index, flipMode, flipArgument);
        }

        public bool IsFlipDone(int handle, int index)
        {
            if (OnCommandThread)
            {
                return _commandThread!.Run(() => IsFlipDone(handle, index));
            }

            DrainDraws();
            return VideoOutExports.IsFlipDone(handle, index);
        }

        public void PrepareCpuFlip(int handle, int index, ulong requestId)
        {
            if (OnCommandThread)
            {
                _commandThread!.Run(() => PrepareCpuFlip(handle, index, requestId));
                return;
            }

            DrainDraws();
            CaptureFlip(handle, index, requestId, flipMode: 0, flipArg: 0);
        }

        public void DrawIndexed(ulong submitId, in DrawIndexedArguments arguments)
        {
            if (OnCommandThread)
            {
                _translation.DrawIndexed(submitId, in arguments);
                return;
            }

            using var translationScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.CommandDrawTranslation);
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                _translation.DrawIndexed(submitId, in arguments);
            }
            finally
            {
                Interlocked.Add(ref _perfDrawTicks, System.Diagnostics.Stopwatch.GetTimestamp() - started);
            }
        }

        public void DrawAuto(ulong submitId, in DrawAutoArguments arguments)
        {
            if (OnCommandThread)
            {
                _translation.DrawAuto(submitId, in arguments);
                return;
            }

            using var translationScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.CommandDrawTranslation);
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                _translation.DrawAuto(submitId, in arguments);
            }
            finally
            {
                Interlocked.Add(ref _perfDrawTicks, System.Diagnostics.Stopwatch.GetTimestamp() - started);
            }
        }

        // RenderExecutor records indirect workgroup dispatches with vkCmdDispatchIndirect.
        // SHARPEMU_CPU_INDIRECT_DISPATCH=1 reads the counts back on the CPU as before.
        public bool ResolvesIndirectDispatchOnGpu => !_cpuIndirectDispatch;

        // RenderExecutor records indexed indirect draws with vkCmdDrawIndexedIndirect.
        // SHARPEMU_CPU_INDIRECT_DRAW=1 reads the arguments back on the CPU as before.
        public bool ResolvesIndirectDrawOnGpu => !_cpuIndirectDraw;

        private static readonly bool _cpuIndirectDraw = string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_CPU_INDIRECT_DRAW"), "1", StringComparison.Ordinal);

        private static readonly bool _cpuIndirectDispatch = string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_CPU_INDIRECT_DISPATCH"), "1", StringComparison.Ordinal);

        public void DispatchDirect(ulong submitId, uint groupsX, uint groupsY, uint groupsZ, uint dispatchInitiator, ulong indirectArgumentsAddress = 0)
        {
            if (OnCommandThread)
            {
                _translation.Dispatch(submitId, groupsX, groupsY, groupsZ, dispatchInitiator, indirectArgumentsAddress);
                return;
            }

            DrainDraws();
            using var translationScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.CommandDispatchTranslation);
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                _translation.Dispatch(submitId, groupsX, groupsY, groupsZ, dispatchInitiator, indirectArgumentsAddress);
            }
            finally
            {
                Interlocked.Add(ref _perfDrawTicks, System.Diagnostics.Stopwatch.GetTimestamp() - started);
            }
        }

        public void OnQueueReset(int queueId)
        {
            if (OnCommandThread)
            {
                _translation.QueueReset(queueId);
                return;
            }

            DrainDraws();
            _translation.QueueReset(queueId);
        }

        // Deferred draws run before anything else the stream asks of the host: everything but a
        // draw may record, read or write what they use. Their time counts as draw time. Completion
        // callbacks reach some of the same entry points from the GPU priority worker; they are not
        // ordered with the stream and leave the draws to the render thread.
        private void DrainDraws()
        {
            // Hosts built without a command stream have no translation.
            if (_translation is not { HasPendingDraws: true } || !_relay.IsGpuQueueThread)
            {
                return;
            }

            using var translationScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.CommandDrawTranslation);
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                _translation.DrainPendingDraws();
            }
            finally
            {
                Interlocked.Add(ref _perfDrawTicks, System.Diagnostics.Stopwatch.GetTimestamp() - started);
            }
        }

        public Exception Fatal(string message) => SubmissionScheduler.Fatal(message);

        // The flip packet: translation first, then the request, then the capture in stream order.
        public int SubmitFlipFromGpu(RecordingBuffer buffer, int handle, int index, int flipMode, long flipArg, out ulong requestId)
        {
            DrainDraws();
            _ = buffer;
            _translation.PrepareFlip(handle, index);
            var result = VideoOutExports.TryReserveFlipRequest(handle, index, flipMode, flipArg, gpuQueued: true, out requestId);
            if (result != 0)
            {
                return result;
            }

            try
            {
                CaptureFlip(handle, index, requestId, flipMode, flipArg);
            }
            catch
            {
                VideoOutExports.CancelFlip(requestId);
                throw;
            }
            return 0;
        }

        // The interpreter must suspend before capture so this worker can present queued frames.
        public void WaitForSubmitSlot() => throw SubmissionScheduler.Fatal("The GPU worker cannot wait for a flip slot. Suspend the command stream before capture.");

        public void CompleteFlip(ulong requestId)
        {
            DrainDraws();
            VideoOutExports.CompleteFlip(requestId);
        }

        // Copies the display surface through the store; presentation shows the copy once its tick retires.
        internal void CaptureFlip(int handle, int index, ulong requestId, int flipMode, long flipArg)
        {
            DrainDraws();
            // A no-buffer flip has no image to capture; its completion still retires in order.
            if (VideoOutExports.ReleaseNoBufferFlip(index, requestId))
            {
                return;
            }
            if (!VideoOutExports.TryGetDisplayBufferInfo(handle, index, out var displayBuffer))
            {
                throw SubmissionScheduler.Fatal($"The flip names a display buffer that is not registered: handle={handle} index={index} request={requestId}.");
            }

            if (_deviceLost)
            {
                VideoOutExports.DiscardFlip(requestId);
                return;
            }

            FlushBatchedGuestCommands();
            RunGuestCacheCollection(endsFrame: true);
            EnsureGuestSubmissionCapacity(collectNow: true);
            long version;
            lock (_gate)
            {
                version = ++_guestFlipVersionSequence;
            }

            var submitted = false;
            GuestImageResource? snapshot = null;
            try
            {
                var surface = new DisplaySurfaceWords(
                    displayBuffer.Address, 0, displayBuffer.PixelFormat, displayBuffer.Width, displayBuffer.Height, displayBuffer.TilingMode, displayBuffer.Option, 0, 0, false);
                var request = ImageRequestBuilders.DisplaySurface(surface);
                _ = BeginBatchedGuestCommands();
                var imageIdentifier = _imageCache.FindImage(ref request);
                var source = _imageCache.GetImage(imageIdentifier);
                source.Uses.VideoOut = true;
                _imageCache.RefreshImage(imageIdentifier);
                // The refresh can end the tick; the copy records into the buffer that is current now.
                var commandBuffer = BeginBatchedGuestCommands();
                var extent = source.Backing.Extent;
                snapshot = CreateGuestFlipSnapshot(GetPresentationSnapshotFormat(source.Backing.Format),
                    extent.Width, extent.Height, displayBuffer.Address, version);
                source.Transition(ImageLayout.TransferSrcOptimal, AccessFlags.TransferReadBit, null, commandBuffer);
                var toTransferDst = new ImageMemoryBarrier2
                {
                    SType = StructureType.ImageMemoryBarrier2,
                    SrcAccessMask = 0,
                    DstAccessMask = AccessFlags2.TransferWriteBit,
                    OldLayout = ImageLayout.Undefined,
                    NewLayout = ImageLayout.TransferDstOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = snapshot.Image,
                    SubresourceRange = ColorSubresourceRange(),
                };
                VulkanSynchronization.PipelineBarrier(_vk,
                    commandBuffer, PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.TransferBit, 0, 0, null, 0, null, 1, &toTransferDst);
                var copy = new ImageCopy
                {
                    SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                    DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                    Extent = new Extent3D(extent.Width, extent.Height, 1),
                };
                _vk.CmdCopyImage(
                    commandBuffer, source.Backing.Handle, ImageLayout.TransferSrcOptimal, snapshot.Image, ImageLayout.TransferDstOptimal, 1, &copy);
                var toShaderRead = new ImageMemoryBarrier2
                {
                    SType = StructureType.ImageMemoryBarrier2,
                    SrcAccessMask = AccessFlags2.TransferWriteBit,
                    DstAccessMask = AccessFlags2.ShaderReadBit | AccessFlags2.TransferReadBit,
                    OldLayout = ImageLayout.TransferDstOptimal,
                    NewLayout = ImageLayout.ShaderReadOnlyOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = snapshot.Image,
                    SubresourceRange = ColorSubresourceRange(),
                };
                VulkanSynchronization.PipelineBarrier(_vk,
                    commandBuffer, PipelineStageFlags.TransferBit, PipelineStageFlags.AllCommandsBit, 0, 0, null, 0, null, 1, &toShaderRead);

                // The tick of this flush, not the shared field: the presenter thread also
                // writes _submitTimeline, so reading it here can name an older tick that is
                // already complete and let the blit run before the copy above.
                var captureTick = FlushBatchedGuestCommands();
                submitted = true;
                _guestImageVersions.Add(version, snapshot);

                lock (_gate)
                {
                    var sequence = (_latestPresentation?.Sequence ?? 0) + 1;
                    var presentation = new Presentation(
                        null,
                        displayBuffer.Width,
                        displayBuffer.Height,
                        sequence,
                        GuestDrawKind.None,
                        IsSplash: false,
                        GuestImageAddress: displayBuffer.Address,
                        GuestImageVersion: version,
                        IsHdr: VideoOutExports.IsHdrPixelFormat(displayBuffer.PixelFormat),
                        RequiredTick: captureTick,
                        FlipRequestId: requestId);
                    _latestPresentation = presentation;
                    _pendingGuestImagePresentations.Enqueue(presentation);
                    while (_pendingGuestImagePresentations.Count > MaxPendingGuestFlipVersions)
                    {
                        RetirePresentation(_pendingGuestImagePresentations.Dequeue());
                    }
                }

                CollectAbandonedGuestImageVersions();
                TraceVulkanShader(
                    $"vk.flip_capture version={version} " +
                    $"queue={_activeGuestQueue.Name} submission={_activeGuestQueue.SubmissionId} " +
                    $"request={requestId} addr=0x{displayBuffer.Address:X16} " +
                    $"size={extent.Width}x{extent.Height}");
                RenderDocCapture.OnGuestFlipBoundary(version);
                VideoOutExports.TraceGpuFlip(_guestMemory, handle, index, flipMode, flipArg, displayBuffer.Address, VideoOutExports.GetFlipEventCount(requestId));
            }
            finally
            {
                if (!submitted)
                {
                    VideoOutExports.DiscardFlip(requestId);
                    if (snapshot is not null)
                    {
                        DestroyGuestImage(snapshot);
                    }
                }
            }
        }

        // The presenter drops a frame it will never show: its flip no longer blocks the stream.
        private static void RetirePresentation(in Presentation presentation)
        {
            if (presentation.FlipRequestId != 0)
            {
                VideoOutExports.DiscardFlip(presentation.FlipRequestId);
            }
        }

        // The presenter is done with the frame; only a successful present counts as presented.
        private void CompletePresentation(in Presentation presentation, bool presented)
        {
            _presentedSequence = presentation.Sequence;
            if (presentation.FlipRequestId == 0)
            {
                return;
            }

            if (presented)
            {
                VideoOutExports.MarkFlipPresented(presentation.FlipRequestId);
            }
            else
            {
                VideoOutExports.DiscardFlip(presentation.FlipRequestId);
            }

            _commandStream.RetryBlocked();
        }

        // Keep GPU completion separate from refresh eligibility.
        private bool IsPresentationReadyLocked(in Presentation presentation) =>
            (presentation.RequiredTick == 0 || (_scheduler is not null && _scheduler.Timeline.CompletedTick >= presentation.RequiredTick)) &&
            VideoOutExports.CanPresentFlip(presentation.FlipRequestId, System.Diagnostics.Stopwatch.GetTimestamp());

        private bool TryTakePresentation(out Presentation presentation)
        {
            lock (_gate)
            {
                // Remove cancelled frames without releasing their in-flight image resources.
                while (_pendingGuestImagePresentations.Count > 0 &&
                       (_pendingGuestImagePresentations.Peek().Sequence <= _presentedSequence ||
                        !VideoOutExports.IsFlipPresentationPending(_pendingGuestImagePresentations.Peek().FlipRequestId)))
                {
                    var retired = _pendingGuestImagePresentations.Dequeue();
                    RetirePresentation(retired);
                    if (_latestPresentation is { } last && last.Sequence == retired.Sequence)
                    {
                        _latestPresentation = null;
                    }
                    _commandStream.RetryBlocked();
                }

                if (_pendingGuestImagePresentations.Count > 0)
                {
                    var pending = _pendingGuestImagePresentations.Peek();
                    if (IsPresentationReadyLocked(in pending))
                    {
                        presentation = _pendingGuestImagePresentations.Dequeue();
                        TryReplaceWithHostMovieFrame(ref presentation);
                        return true;
                    }

                    presentation = default;
                    return false;
                }

                while (_pendingVideoPresentations.Count > 0 &&
                       _pendingVideoPresentations.Peek().Sequence <= _presentedSequence)
                {
                    _pendingVideoPresentations.Dequeue();
                }

                if (_pendingVideoPresentations.Count > 0)
                {
                    presentation = _pendingVideoPresentations.Dequeue();
                    return true;
                }

                if (_latestPresentation is not { } latest ||
                    latest.Sequence == _presentedSequence ||
                    !IsPresentationReadyLocked(in latest))
                {
                    if (_latestPresentation is { } rejected &&
                        rejected.GuestImageAddress != 0 &&
                        rejected.Sequence != _presentedSequence &&
                        _tracedGuestImagePresentRejections.Add(rejected.Sequence))
                    {
                        Console.Error.WriteLine(
                            $"[LOADER][WARN] vk.guest_present_rejected addr=0x{rejected.GuestImageAddress:X16} " +
                            $"seq={rejected.Sequence} presentedSeq={_presentedSequence} " +
                            $"required_tick={rejected.RequiredTick} completed_tick={_scheduler.Timeline.CompletedTick}");
                    }

                    presentation = default;
                    return false;
                }

                presentation = latest;
                TryReplaceWithHostMovieFrame(ref presentation);
                return true;
            }
        }

        // True when a guest frame can be shown now; the wait loop wakes for it.
        private bool HasReadyPresentationLocked()
        {
            if (_pendingGuestImagePresentations.Count > 0)
            {
                var pending = _pendingGuestImagePresentations.Peek();
                return pending.Sequence <= _presentedSequence ||
                    !VideoOutExports.IsFlipPresentationPending(pending.FlipRequestId) ||
                    IsPresentationReadyLocked(in pending);
            }

            return _pendingVideoPresentations.Count > 0 ||
                (_latestPresentation is { } latest && latest.Sequence != _presentedSequence && IsPresentationReadyLocked(in latest));
        }
    }
}
