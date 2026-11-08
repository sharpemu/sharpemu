// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Collections.Concurrent;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.Scheduling;

namespace SharpEmu.Libs.VideoOut;

internal static unsafe partial class VulkanVideoPresenter
{
    private sealed partial class Presenter
    {
        private ConcurrentDictionary<ulong, PendingGuestPublications.Entry>? _flipPublications;
        private PendingGuestPublications.Entry? _capturePublication;
        private int _partialLabelReadBlocked;
        private SignalBatch? _signalBatch;

        // Consecutive labels and signals of one queue with the same tick publish as one scheduler entry.
        // Each keeps its own publish step, in order, so checks, failures, and wakes stay per entry.
        private sealed class SignalBatch(int queue)
        {
            public readonly int Queue = queue;
            public readonly List<Action> Publications = new();
            public ulong Tick;
            public ulong Sequence;
            public bool Closed;

            public bool TryAdd(Action publication)
            {
                lock (Publications)
                {
                    if (Closed) return false;
                    Publications.Add(publication);
                    return true;
                }
            }

            public void Publish()
            {
                lock (Publications) Closed = true;
                foreach (var publication in Publications) publication();
            }
        }
        public bool HasPendingMemoryPublication(ulong address, ulong size) => _guestPublications.HasLabelOverlap(address, size);

        public bool TryReadDeferredLabel(int queueId, ulong address, ulong size, out bool deferred, out ulong value)
        {
            deferred = false;
            value = 0;
            if (size is not (4 or 8)) return !HasPendingMemoryPublication(address, size);
            Span<byte> bytes = stackalloc byte[(int)size];
            if (!_guestPublications.TryOverlayLabels(queueId, address, bytes, out var covered, out var newestForeign))
            {
                return false;
            }
            if (covered == 0) return true;
            if (covered != (1 << (int)size) - 1)
            {
                // Bytes outside the labels are current; GPU-written bytes need the render thread.
                Span<byte> current = stackalloc byte[(int)size];
                if (OnCommandThread) _commandThread!.WaitForPendingRange(address, size);
                if (!OnCommandThread || _bufferCache.MayHaveGpuDirtyPages(address, size) ||
                    !_commandThread!.ReadOverlay(address, current))
                {
                    Volatile.Write(ref _partialLabelReadBlocked, 1);
                    return false;
                }
                for (var index = 0; index < (int)size; index++)
                    if ((covered & (1 << index)) == 0) bytes[index] = current[index];
            }
            deferred = true;
            value = size == 8 ? BinaryPrimitives.ReadUInt64LittleEndian(bytes) : BinaryPrimitives.ReadUInt32LittleEndian(bytes);
            if (newestForeign is not null)
            {
                KeepOrderAfterForeignLabel(queueId, newestForeign.Ordinal);
                var windows = _crossQueueWindows ??= new();
                if (!windows.TryGetValue(queueId, out var current) || current.Completed || current.Ordinal < newestForeign.Ordinal)
                    windows[queueId] = newestForeign;
            }
            return true;
        }

        // The newest foreign label each queue passed a wait on; its writes follow that label until it publishes.
        // Accessed on the command thread only.
        private Dictionary<int, PendingGuestPublications.Entry>? _crossQueueWindows;

        private bool InCrossQueueOrderWindow(int queueId) =>
            _crossQueueWindows is { } windows && windows.TryGetValue(queueId, out var entry) && !entry.Completed;

        private bool InAnyCrossQueueOrderWindow()
        {
            if (_crossQueueWindows is not { } windows)
            {
                return false;
            }

            foreach (var entry in windows.Values)
            {
                if (!entry.Completed)
                {
                    return true;
                }
            }

            return false;
        }

        public bool MustHoldForCrossQueueOrder(int queueId)
        {
            if (!InCrossQueueOrderWindow(queueId)) return false;
            // The foreign label is deferred, so its publication must wake this suspended queue.
            Volatile.Write(ref _partialLabelReadBlocked, 1);
            return true;
        }

        public bool TryDeferOrderedWrite(int queueId, ulong address, ReadOnlySpan<byte> source)
        {
            if (!InCrossQueueOrderWindow(queueId)) return false;
            var entry = _guestPublications.AddWrite(queueId, address, (ulong)source.Length);
            var payload = source.ToArray();
            try
            {
                if (OnCommandThread) _commandThread!.Post(() => RecordDeferredWrite(entry, payload));
                else RecordDeferredWrite(entry, payload);
            }
            catch (Exception exception)
            {
                FailGuestPublications(exception);
                throw;
            }
            return true;
        }

        // Queued after the ordering entry, so the scheduler publishes it after the foreign label.
        private void RecordDeferredWrite(PendingGuestPublications.Entry entry, byte[] payload)
        {
            try
            {
                var tick = _scheduler.QueueMemoryPublication(() => PublishGuestEntry(entry, () =>
                {
                    if (!_guestMemory.TryWrite(entry.Address, payload))
                        throw SubmissionScheduler.Fatal($"Cannot publish a held-back command write: address=0x{entry.Address:X16}.");
                }), _guestPublications.PredecessorTick(entry));
                _guestPublications.Register(entry, tick);
                if (tick == _scheduler.CurrentTick) EnsureBatchedGuestCommands();
                if (_scheduler.IsTickKnownComplete(tick)) _scheduler.RunCompletedOperations();
            }
            catch (Exception exception)
            {
                FailGuestPublications(exception);
                throw;
            }
        }

        // On hardware a wait on another queue's label passes only after that label lands, so this
        // queue's later signals must not publish first. An ordering entry holds them behind it.
        private void KeepOrderAfterForeignLabel(int queueId, long foreignOrdinal)
        {
            // An older pending entry of this queue can publish before the foreign label, so it is not enough.
            if (_guestPublications.QueueFollows(queueId, foreignOrdinal)) return;
            var entry = _guestPublications.AddOrdering(queueId);
            try
            {
                if (OnCommandThread) _commandThread!.Post(() => RecordOrderingEntry(entry));
                else RecordOrderingEntry(entry);
            }
            catch (Exception exception)
            {
                FailGuestPublications(exception);
                throw;
            }
        }

        // The scheduler publishes in queue order, so this entry completes after every earlier publication.
        private void RecordOrderingEntry(PendingGuestPublications.Entry entry)
        {
            try
            {
                var tick = _scheduler.QueueMemoryPublication(
                    () => PublishDeferredSignal(entry, false, static () => { }), _guestPublications.PredecessorTick(entry));
                _guestPublications.Register(entry, tick);
                if (tick == _scheduler.CurrentTick) EnsureBatchedGuestCommands();
                if (_scheduler.IsTickKnownComplete(tick)) _scheduler.RunCompletedOperations();
            }
            catch (Exception exception)
            {
                FailGuestPublications(exception);
                throw;
            }
        }

        public bool TryDeferEndOfPipe(int queueId, in EndOfPipeWrite write)
        {
            var size = write.Kind switch
            {
                EndOfPipeWriteKind.InterruptOnly or EndOfPipeWriteKind.Flip => 0UL,
                EndOfPipeWriteKind.Write32 or EndOfPipeWriteKind.WriteBack32 or EndOfPipeWriteKind.Interrupt32 or
                    EndOfPipeWriteKind.InterruptWriteBack32 or EndOfPipeWriteKind.FlipWithWrite32 or
                    EndOfPipeWriteKind.FlipWithInterruptWriteBack32 => 4UL,
                EndOfPipeWriteKind.Write64 or EndOfPipeWriteKind.WriteBack64 or EndOfPipeWriteKind.Interrupt64 or
                    EndOfPipeWriteKind.InterruptWriteBack64 or EndOfPipeWriteKind.ClockWrite or
                    EndOfPipeWriteKind.ClockWriteBack => 8UL,
                _ => throw SubmissionScheduler.Fatal("The deferred end-of-pipe kind is not supported."),
            };
            if (size != 0 && write.Destination == 0)
                throw SubmissionScheduler.Fatal("Cannot defer a label at address zero.");
            if (_guestPublications.Count >= 1024)
            {
                if (OnCommandThread) _commandThread!.Run(() => _scheduler.Finish());
                else _scheduler.Finish();
            }
            var entry = _guestPublications.AddAfterPending(queueId, write.Destination, size, write.Value, write.WaitForGpu);
            if (entry is null) return false;
            var captured = write;
            try
            {
                if (OnCommandThread) _commandThread!.Post(() => RecordDeferredSignal(entry, captured));
                else RecordDeferredSignal(entry, captured);
            }
            catch (Exception exception)
            {
                FailGuestPublications(exception);
                throw;
            }
            return true;
        }

        private void RecordDeferredSignal(PendingGuestPublications.Entry entry, EndOfPipeWrite write)
        {
            try
            {
                using var completionScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.CommandEndOfPipe);
                var hasSignal = write.Kind is EndOfPipeWriteKind.InterruptOnly or EndOfPipeWriteKind.Interrupt32 or
                    EndOfPipeWriteKind.Interrupt64 or EndOfPipeWriteKind.InterruptWriteBack32 or
                    EndOfPipeWriteKind.InterruptWriteBack64 or EndOfPipeWriteKind.Flip or
                    EndOfPipeWriteKind.FlipWithWrite32 or EndOfPipeWriteKind.FlipWithInterruptWriteBack32;
                _ = CurrentRecording();
                var dependency = _guestPublications.PredecessorTick(entry);
                if (hasSignal || write.WaitForGpu)
                {
                    EnsureBatchedGuestCommands();
                    dependency = _scheduler.CurrentTick;
                }
                var isFlip = write.FlipRequestId != 0;
                Action publication = () => PublishDeferredSignal(entry, isFlip, () =>
                {
                    try
                    {
                        if (entry.Size != 0)
                        {
                            Span<byte> bytes = stackalloc byte[8];
                            BinaryPrimitives.WriteUInt64LittleEndian(bytes, write.Value);
                            if (!_guestMemory.TryWrite(write.Destination, bytes[..(int)entry.Size]))
                                throw SubmissionScheduler.Fatal("Cannot publish a deferred end-of-pipe label.");
                        }
                        if (hasSignal) _endOfPipe.CompleteDeferred(in write);
                        if (write.FlipRequestId != 0)
                        {
                            if (write.FlipIndex == -1) VideoOutExports.ReleaseNoBufferFlip(-1, write.FlipRequestId);
                            if (_flipPublications?.TryRemove(write.FlipRequestId, out var capture) == true)
                                _guestPublications.Publish(capture, static () => { });
                        }
                    }
                    catch
                    {
                        if (write.FlipRequestId != 0) VideoOutExports.CancelFlip(write.FlipRequestId);
                        throw;
                    }
                });
                ulong tick;
                if (!isFlip && _signalBatch is { } open && open.Queue == entry.Queue &&
                    _scheduler.IsLastQueuedPublication(open.Sequence, dependency, open.Tick) && open.TryAdd(publication))
                    tick = open.Tick;
                else if (isFlip)
                {
                    _signalBatch = null;
                    tick = _scheduler.QueueMemoryPublication(publication, dependency);
                }
                else
                {
                    var batch = new SignalBatch(entry.Queue);
                    batch.Publications.Add(publication);
                    tick = _scheduler.QueueMemoryPublication(batch.Publish, dependency, out batch.Sequence);
                    batch.Tick = tick;
                    _signalBatch = batch;
                }
                _guestPublications.Register(entry, tick);
                if (tick == _scheduler.CurrentTick) EnsureBatchedGuestCommands();
                // A device query per label is costly; the render loop runs later completions.
                if (_scheduler.IsTickKnownComplete(tick)) _scheduler.RunCompletedOperations();
            }
            catch (Exception exception)
            {
                if (write.FlipRequestId != 0) VideoOutExports.CancelFlip(write.FlipRequestId);
                FailGuestPublications(exception);
                throw;
            }
        }

        private void PublishGuestEntry(PendingGuestPublications.Entry entry, Action write)
        {
            try { _guestPublications.Publish(entry, write); }
            catch (Exception exception) { FailGuestPublications(exception); throw; }
            finally { WakePublicationConsumers(); }
        }

        // The command processor already sees deferred label values, so most label publications wake nobody.
        private void PublishDeferredSignal(PendingGuestPublications.Entry entry, bool isFlip, Action write)
        {
            try { _guestPublications.Publish(entry, write); }
            catch (Exception exception) { FailGuestPublications(exception); throw; }
            if (isFlip || Interlocked.Exchange(ref _partialLabelReadBlocked, 0) != 0 || !_guestPublications.HasQueue(entry.Queue))
                WakePublicationConsumers();
        }

        private void FailGuestPublications(Exception exception)
        {
            _guestPublications.Fail(exception);
            if (_flipPublications is { } flips)
                foreach (var request in flips.Keys)
                    if (flips.TryRemove(request, out _)) VideoOutExports.CancelFlip(request);
            WakePublicationConsumers();
        }

        private void WakePublicationConsumers()
        {
            _commandStream?.NotifyPublicationCompleted();
            WakeRenderThread();
        }
    }
}
