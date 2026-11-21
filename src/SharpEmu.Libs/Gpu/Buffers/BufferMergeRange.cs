// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Buffers;

// Tracks allocation bounds without moving buffers or changing their owners.
internal struct BufferMergeRange(ulong begin, ulong end)
{
    private int _streamScore;

    public ulong Begin { get; private set; } = begin;
    public ulong End { get; private set; } = end;
    public bool HasStreamExpansion { get; private set; }

    // A left expansion requires the caller to include earlier overlapping buffers.
    //
    // A buffer whose merges keep scoring is a stream: the guest walks through memory and each new range lands just
    // past the buffer, so every step would recreate (and copy) the whole merged buffer. Past the score threshold the
    // range grows ahead of the walk, in the direction the requested range sticks out of the buffer, by half the
    // merged size (at least 128 pages, at most 1024), so the recreations become geometric instead of one per page.
    // A request that sticks out on both sides, or on neither, is not a walk and only unions the bounds.
    public bool IncludeBuffer(ulong bufferBegin, ulong bufferEnd, int streamScore)
    {
        var growsRight = End > bufferEnd && Begin >= bufferBegin;
        var growsLeft = Begin < bufferBegin && End <= bufferEnd;
        Begin = Math.Min(Begin, bufferBegin);
        End = Math.Max(End, bufferEnd);
        if (HasStreamExpansion || (_streamScore += streamScore) <= 16)
        {
            return false;
        }

        HasStreamExpansion = true;
        const ulong minimumExpansion = GuestBufferCache.CachingPageSize * 128;
        const ulong maximumExpansion = GuestBufferCache.CachingPageSize * 1024;
        var expansionSize = Math.Clamp((End - Begin) / 2 & ~(GuestBufferCache.CachingPageSize - 1), minimumExpansion, maximumExpansion);
        if (growsRight)
        {
            End += Math.Min(expansionSize, PageOwnerTable.AddressSpaceSize - End);
        }

        if (!growsLeft)
        {
            return false;
        }

        const ulong minimumBegin = GuestBufferCache.CachingPageSize * 2;
        if (Begin > minimumBegin)
        {
            Begin -= Math.Min(expansionSize, Begin - minimumBegin);
        }

        return true;
    }

    public void IncludeEarlierBuffer(ulong bufferBegin) => Begin = Math.Min(Begin, bufferBegin);
}
