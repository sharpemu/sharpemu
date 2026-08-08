// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

internal static unsafe partial class VulkanVideoPresenter
{
    private static readonly bool _indexDirtyGuestBuffers = !string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_INDEX_DIRTY_GUEST_BUFFERS"),
        "0",
        StringComparison.Ordinal);

    private sealed partial class Presenter
    {
        private readonly DirtyGuestBufferIndex<GuestBufferAllocation>
            _dirtyGuestBufferIndex = new();
        private readonly List<GuestBufferAllocation> _dirtyGuestBufferCandidates = [];

        private IReadOnlyList<GuestBufferAllocation> GetDirtyGuestBufferCandidates(
            string? queueName)
        {
            if (!_indexDirtyGuestBuffers)
            {
                return _guestBufferAllocations;
            }

            _dirtyGuestBufferIndex.CopyCandidates(queueName, _dirtyGuestBufferCandidates);
            return _dirtyGuestBufferCandidates;
        }

        private void IndexDirtyGuestBuffer(
            GuestBufferAllocation allocation,
            string queueName)
        {
            if (_indexDirtyGuestBuffers)
            {
                _dirtyGuestBufferIndex.Mark(allocation, queueName);
            }
        }

        private void RefreshDirtyGuestBufferIndex(
            GuestBufferAllocation allocation,
            string? queueName)
        {
            if (!_indexDirtyGuestBuffers)
            {
                return;
            }

            if (queueName is not null)
            {
                if (!allocation.DirtyRanges.Any(range =>
                        string.Equals(range.QueueName, queueName, StringComparison.Ordinal)))
                {
                    _dirtyGuestBufferIndex.Remove(allocation, queueName);
                }

                return;
            }

            _dirtyGuestBufferIndex.Remove(allocation);
            foreach (var remainingQueueName in allocation.DirtyRanges
                         .Select(static range => range.QueueName)
                         .Distinct(StringComparer.Ordinal))
            {
                _dirtyGuestBufferIndex.Mark(allocation, remainingQueueName);
            }
        }

        private void ForgetDirtyGuestBuffer(GuestBufferAllocation allocation)
        {
            if (_indexDirtyGuestBuffers)
            {
                _dirtyGuestBufferIndex.Remove(allocation);
            }
        }

    }
}
