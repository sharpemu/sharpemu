// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Scheduling;

namespace SharpEmu.Libs.VideoOut;

internal static unsafe partial class VulkanVideoPresenter
{
    private sealed partial class Presenter
    {
        private sealed class FlipPublicationDecision(PendingGuestPublications.Entry entry, EndOfPipeWrite write)
        {
            internal readonly PendingGuestPublications.Entry Entry = entry;
            internal readonly EndOfPipeWrite Write = write;
            internal long Sequence;
            internal volatile bool Immediate;
        }

        // Parser-owned. The range reservation indexes the label for ordinary command memory
        // reads. Label forwarding and queue-order decisions also need its actual disposition.
        private FlipPublicationDecision? _pendingFlipDecision;

        private void SettleFlipDecision(int queueId, ulong address = 0, ulong size = 0)
        {
            if (_pendingFlipDecision is not { } pending || !OnCommandThread) return;
            var entry = pending.Entry;
            if (queueId != entry.Queue && (size == 0 || entry.Size == 0 ||
                (address >= entry.Address ? address - entry.Address >= entry.Size : entry.Address - address >= size))) return;
            WaitForFlipDecision(pending);
        }

        private void WaitForFlipDecision(FlipPublicationDecision pending)
        {
            _commandThread!.WaitPending(pending.Sequence);
            _pendingFlipDecision = null;
        }

        public bool TryPostPublicationFlip(int queueId, in EndOfPipeWrite write, out Func<bool>? immediateLabel)
        {
            immediateLabel = null;
            if (!OnCommandThread) return false;
            if (_pendingFlipDecision is { } previous) WaitForFlipDecision(previous);
            if (!_guestPublications.HasQueue(queueId) || _guestPublications.Count >= 1024) return false;
            var labelled = write.Kind != EndOfPipeWriteKind.Flip;
            // A held cross-queue store uses the interpreter's ordinary ordered-write path.
            if (labelled && (write.Destination == 0 || InCrossQueueOrderWindow(queueId))) return false;
            if (!_translation.TryEndFrameBeforeCapture(write.FlipHandle, write.FlipIndex)) return false;

            // The predecessor may retire before capture. Reserve the signal's place now,
            // but leave it unregistered until earlier render items have run. Later parser
            // entries cannot turn themselves into the flip's predecessor.
            var entry = _guestPublications.AddAfterPending(queueId, write.Destination, labelled ? 4UL : 0,
                write.Value, requireCompletion: true)!;
            var decision = new FlipPublicationDecision(entry, write);
            PostGpuFlip(write.FlipHandle, write.FlipIndex, write.FlipMode, write.FlipArgument, decision);
            decision.Sequence = _lastAsyncFlipSequence;
            _pendingFlipDecision = decision;
            if (labelled) immediateLabel = () => decision.Immediate;
            return true;
        }

        // On the render thread, at the same stream position as PrepareFlip. A predecessor
        // still recorded here gates presentation until the flip signal publishes, even if
        // it retires inside the image copy. Entries for later packets have no tick yet.
        private void CaptureAndPublishFlip(FlipPublicationDecision decision, ulong request)
        {
            var write = decision.Write with { FlipRequestId = request };
            var entry = decision.Entry;
            var gated = _guestPublications.LastRecorded(entry.Queue) is not null;
            _capturePublication = gated ? entry : null;
            try
            {
                PublishAndCaptureGpuFlip(write.FlipHandle, write.FlipIndex, request, write.FlipMode, write.FlipArgument);
            }
            finally { _capturePublication = null; }

            if (gated || write.WaitForGpu)
            {
                if (!(_flipPublications ??= new()).TryAdd(request, entry))
                    throw SubmissionScheduler.Fatal("The flip publication is already registered.");
                RecordDeferredSignal(entry, write);
            }
            else
            {
                // PublishLabel would issue an ordinary store here, after capture and before
                // recording the priority flip/interrupt completion. Keep its memory checks.
                _guestPublications.Publish(entry, () =>
                {
                    if (entry.Size == 0) return;
                    Span<byte> bytes = stackalloc byte[sizeof(uint)];
                    BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)write.Value);
                    _bufferCache.NoteObservation();
                    if (!((ICommandStreamHost)this).Memory.TryWrite(write.Destination, bytes))
                        throw SubmissionScheduler.Fatal($"The command stream cannot write the flip label at 0x{write.Destination:X16}.");
                    decision.Immediate = true;
                    if (ImageClearTrace.Enabled) TraceGuestWrite("command-write-PublishLabel", write.Destination, sizeof(uint));
                });
                RecordEndOfPipe(in write);
                WakePublicationConsumers();
            }
        }
    }
}
