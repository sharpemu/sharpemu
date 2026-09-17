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
        private long _lastCommandStreamProgressTicks;
        private static readonly long _blockedRetryTicks =
            CommandStreamQueue.AllBlockedRetryMilliseconds * System.Diagnostics.Stopwatch.Frequency / 1000L;

        public CommandStreamQueue CommandStream => _commandStream;

        private void CreateCommandStream()
        {
            var (_, guest, _) = RequireGuestMemory("command stream");
            _guestMemory = guest;
            _endOfPipe = new EndOfPipe(_endOfPipeEvents, this);
            _translation = AgcExports.CreateCommandStreamTranslation(guest, this);
            _lastCommandStreamProgressTicks = System.Diagnostics.Stopwatch.GetTimestamp();
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

        // Runs slices until the budget ends or nothing is runnable; blocked heads retry every 100 ms.
        private void RunCommandStreamSlices(long renderWorkDeadline)
        {
            var slices = 0;
            while (slices < _maxGuestWorkPerRender)
            {
                using (RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.Collect))
                {
                    CollectCompletedGuestSubmissions(waitForOldest: false);
                }

                SliceResult result;
                using (RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.CommandStream))
                {
                    result = _commandStream.ProcessOne();
                }

                switch (result)
                {
                    case SliceResult.NoWork:
                        return;
                    case SliceResult.AllBlocked:
                        RetryBlockedCommandStreamIfDue();
                        return;
                    case SliceResult.BlockedWithoutProgress:
                        if (!_commandStream.HasUnblockedPending)
                        {
                            RetryBlockedCommandStreamIfDue();
                            return;
                        }

                        break;
                    default:
                        _lastCommandStreamProgressTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                        break;
                }

                slices++;
                if (System.Diagnostics.Stopwatch.GetTimestamp() >= renderWorkDeadline)
                {
                    return;
                }
            }
        }

        private void RetryBlockedCommandStreamIfDue()
        {
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (now - _lastCommandStreamProgressTicks < _blockedRetryTicks)
            {
                return;
            }

            _commandStream.RetryBlocked();
            _lastCommandStreamProgressTicks = now;
        }

        // Milliseconds until the blocked heads are due for a retry; null when none is blocked.
        private int? BlockedRetryWaitMilliseconds()
        {
            if (!_vulkanReady || !_commandStream.HasPending || _commandStream.HasUnblockedPending)
            {
                return null;
            }

            var remaining = _blockedRetryTicks - (System.Diagnostics.Stopwatch.GetTimestamp() - _lastCommandStreamProgressTicks);
            return remaining <= 0 ? 0 : (int)Math.Max(1, remaining * 1000 / System.Diagnostics.Stopwatch.Frequency);
        }

        ICpuMemory ICommandStreamHost.Memory => _guestMemory;

        public bool TryReadGuest(ulong address, Span<byte> destination)
        {
            using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.CommandMemorySync))
            {
                if (!_bufferCache.TrySynchronizeCpuRead(address, (ulong)destination.Length))
                {
                    return false;
                }
            }

            using var readScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.CommandMemoryRead);
            return _guestMemory.TryRead(address, destination);
        }

        public void RunPendingCommands()
        {
            using var relayScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.QueueRelay);
            _relay.RunPendingCommands();
        }

        public void BeginSubmission(int queueId, ulong submissionId, object? geometrySnapshots)
        {
            using var contextScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.QueueContext);
            _activeGuestQueue = new VulkanGuestQueueIdentity(_commandQueueNames[queueId], submissionId);
            BindSubmissionContext(_activeGuestQueue);
            _ = CurrentRecordingBuffer();
            _translation.BeginSubmission(queueId, submissionId, geometrySnapshots, _commandStream.GetInterpreter(queueId));
        }

        public void Flush()
        {
            using var flushScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.Flush);
            FlushBatchedGuestCommands();
        }

        public void FlushAndWait()
        {
            using var waitScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.CommandGpuWait);
            _ = CurrentRecordingBuffer();
            _scheduler.FlushAndWait();
            CollectCompletedGuestSubmissions(waitForOldest: false);
        }

        public void SynchronizeGpu()
        {
            using var waitScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.CommandGpuWait);
            _ = CurrentRecordingBuffer();
            _scheduler.Finish();
            CollectCompletedGuestSubmissions(waitForOldest: false);
        }

        public void RunGarbageCollector() => RunGuestCacheCollection();

        public void EmitGlobalBarrier()
        {
            EndRendering();
            EndRendering();
            var commandBuffer = BeginBatchedGuestCommands();
            var barrier = new MemoryBarrier
            {
                SType = StructureType.MemoryBarrier,
                SrcAccessMask = AccessFlags.MemoryWriteBit,
                DstAccessMask = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit,
            };
            _vk.CmdPipelineBarrier(
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
            using var transferScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.CommandMemoryTransfer);
            EndRendering();
            _ = BeginBatchedGuestCommands();
            _bufferCache.FillBuffer(address, size, value, isGds);
        }

        public void CopyBuffer(ulong destination, ulong source, ulong size, bool destinationIsGds, bool sourceIsGds)
        {
            using var transferScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.CommandMemoryTransfer);
            EndRendering();
            _ = BeginBatchedGuestCommands();
            _bufferCache.CopyBuffer(destination, source, size, destinationIsGds, sourceIsGds);
        }

        public void ReadGds(Span<uint> destination, uint wordOffset, uint wordCount) =>
            EndOfPipe.ReadGdsWords(_bufferCache.GdsBuffer.Mapped, destination, wordOffset, wordCount);

        public void RecordEndOfPipe(in EndOfPipeWrite write)
        {
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

        public void TriggerInterrupt(int eventId, uint contextId) => _endOfPipeEvents.TriggerInterrupt(eventId, contextId);

        public bool HasFlipSlot()
        {
            lock (_gate)
            {
                return _pendingGuestImagePresentations.Count < MaxPendingGuestFlipVersions;
            }
        }

        public ulong PrepareFlip(int handle, int index, int flipMode, long flipArgument)
        {
            _ = BeginBatchedGuestCommands();
            return _endOfPipe.PrepareVideoOutFlip(_scheduler.Current, handle, index, flipMode, flipArgument);
        }

        public bool IsFlipDone(int handle, int index) => VideoOutExports.IsFlipDone(handle, index);

        public void PrepareCpuFlip(int handle, int index, ulong requestId) =>
            CaptureFlip(handle, index, requestId, flipMode: 0, flipArg: 0);

        public void DrawIndexed(ulong submitId, in DrawIndexedArguments arguments)
        {
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

        public void DispatchDirect(ulong submitId, uint groupsX, uint groupsY, uint groupsZ, uint dispatchInitiator)
        {
            using var translationScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.CommandDispatchTranslation);
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                _translation.Dispatch(submitId, groupsX, groupsY, groupsZ, dispatchInitiator);
            }
            finally
            {
                Interlocked.Add(ref _perfDrawTicks, System.Diagnostics.Stopwatch.GetTimestamp() - started);
            }
        }

        public void OnQueueReset(int queueId) => _translation.QueueReset(queueId);

        public Exception Fatal(string message) => SubmissionScheduler.Fatal(message);

        // The flip packet: translation first, then the request, then the capture in stream order.
        public int SubmitFlipFromGpu(RecordingBuffer buffer, int handle, int index, int flipMode, long flipArg, out ulong requestId)
        {
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

        public void CompleteFlip(ulong requestId) => VideoOutExports.CompleteFlip(requestId);

        // Copies the display surface through the store; presentation shows the copy once its tick retires.
        internal void CaptureFlip(int handle, int index, ulong requestId, int flipMode, long flipArg)
        {
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
            RunGuestCacheCollection();
            EnsureGuestSubmissionCapacity();
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
                    displayBuffer.Address, 0, displayBuffer.PixelFormat, displayBuffer.Width, displayBuffer.Height, displayBuffer.TilingMode, 0, 0, 0, false);
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
                var toTransferDst = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask = 0,
                    DstAccessMask = AccessFlags.TransferWriteBit,
                    OldLayout = ImageLayout.Undefined,
                    NewLayout = ImageLayout.TransferDstOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = snapshot.Image,
                    SubresourceRange = ColorSubresourceRange(),
                };
                _vk.CmdPipelineBarrier(
                    commandBuffer, PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.TransferBit, 0, 0, null, 0, null, 1, &toTransferDst);
                var copy = new ImageCopy
                {
                    SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                    DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                    Extent = new Extent3D(extent.Width, extent.Height, 1),
                };
                _vk.CmdCopyImage(
                    commandBuffer, source.Backing.Handle, ImageLayout.TransferSrcOptimal, snapshot.Image, ImageLayout.TransferDstOptimal, 1, &copy);
                var toShaderRead = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask = AccessFlags.TransferWriteBit,
                    DstAccessMask = AccessFlags.ShaderReadBit | AccessFlags.TransferReadBit,
                    OldLayout = ImageLayout.TransferDstOptimal,
                    NewLayout = ImageLayout.ShaderReadOnlyOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = snapshot.Image,
                    SubresourceRange = ColorSubresourceRange(),
                };
                _vk.CmdPipelineBarrier(
                    commandBuffer, PipelineStageFlags.TransferBit, PipelineStageFlags.AllCommandsBit, 0, 0, null, 0, null, 1, &toShaderRead);

                FlushBatchedGuestCommands();
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
                        RequiredTick: _submitTimeline,
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
