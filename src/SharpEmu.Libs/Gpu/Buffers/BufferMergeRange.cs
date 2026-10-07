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
    public bool IncludeBuffer(ulong bufferBegin, ulong bufferEnd, int streamScore)
    {
        var expandsLeft = bufferBegin < Begin;
        var expandsRight = bufferEnd > End;
        Begin = Math.Min(Begin, bufferBegin);
        End = Math.Max(End, bufferEnd);
        if (HasStreamExpansion || (_streamScore += streamScore) <= 16)
        {
            return false;
        }

        HasStreamExpansion = true;
        const ulong expansionSize = GuestBufferCache.CachingPageSize * 128;
        if (expandsRight)
        {
            End += Math.Min(expansionSize, PageOwnerTable.AddressSpaceSize - End);
        }

        if (!expandsLeft)
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
