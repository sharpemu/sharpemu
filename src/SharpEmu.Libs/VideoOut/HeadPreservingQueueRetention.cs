// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

internal static class HeadPreservingQueueRetention
{
    /// <summary>
    /// Bounds a FIFO while preserving its oldest item and the newest tail.
    /// Intermediate items are removed oldest-first so an unconsumed head
    /// cannot be starved by a producer that continuously outruns its consumer.
    /// </summary>
    public static int CoalesceIntermediateItems<T>(
        LinkedList<T> pending,
        int maximumCount,
        Action<T>? onCoalesced = null)
    {
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCount, 1);

        var coalescedCount = 0;
        while (pending.Count > maximumCount)
        {
            var oldestIntermediate = pending.First!.Next;
            System.Diagnostics.Debug.Assert(oldestIntermediate is not null);
            var coalescedItem = oldestIntermediate!.Value;
            pending.Remove(oldestIntermediate!);
            onCoalesced?.Invoke(coalescedItem);
            coalescedCount++;
        }

        return coalescedCount;
    }
}

internal static class GuestPresentationScheduling
{
    /// <summary>
    /// An ordered flip has captured an immutable guest-image generation and
    /// enqueued its presentation. Yielding the guest-work drain at this point
    /// gives that FIFO head a presentation opportunity before newer flips can
    /// coalesce intermediate generations behind it.
    /// </summary>
    public static bool ShouldYieldToPresenter(object completedWork)
    {
        ArgumentNullException.ThrowIfNull(completedWork);
        return completedWork is VulkanOrderedGuestFlip;
    }
}
