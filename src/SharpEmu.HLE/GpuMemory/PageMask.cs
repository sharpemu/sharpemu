// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Numerics;
using System.Runtime.CompilerServices;

namespace SharpEmu.HLE.GpuMemory;

public struct PageMask
{
    private const int Bits = TrackerLayout.PagesPerBlock;
    private const int WordBits = 64;
    private const int WordCount = Bits / WordBits;

    [InlineArray(WordCount)]
    private struct Words
    {
        private ulong _first;
    }

    private Words _words;

    public bool Get(int index) => (_words[index / WordBits] & (1UL << (index % WordBits))) != 0;

    public void Set(int index) => _words[index / WordBits] |= 1UL << (index % WordBits);

    public void SetRange(int start, int end)
    {
        if (start >= end || end > Bits)
        {
            return;
        }

        var firstWord = start / WordBits;
        var lastWord = (end - 1) / WordBits;
        var startMask = ~0UL << (start % WordBits);
        var endBit = (end - 1) % WordBits;
        var endMask = endBit == WordBits - 1 ? ~0UL : (1UL << (endBit + 1)) - 1;

        if (firstWord == lastWord)
        {
            _words[firstWord] |= startMask & endMask;
            return;
        }

        _words[firstWord] |= startMask;
        for (var word = firstWord + 1; word < lastWord; word++)
        {
            _words[word] = ~0UL;
        }

        _words[lastWord] |= endMask;
    }

    public (int Start, int End) FindFirstSetRange() => FindFirstSetRangeFrom(0);

    public (int Start, int End) FindFirstSetRangeFrom(int start)
    {
        var first = FindNextSetBit(start);
        if (first == Bits)
        {
            return (Bits, Bits);
        }

        var end = first + 1;
        while (end < Bits && Get(end))
        {
            end++;
        }

        return (first, end);
    }

    public (int Start, int End) FindLastSetRange() => FindLastSetRangeBefore(Bits);

    public (int Start, int End) FindLastSetRangeBefore(int end)
    {
        var last = FindPreviousSetBit(end);
        if (last < 0)
        {
            return (0, 0);
        }

        var start = last;
        while (start > 0 && Get(start - 1))
        {
            start--;
        }

        return (start, last + 1);
    }

    private int FindNextSetBit(int start)
    {
        for (var index = start; index < Bits; index += WordBits - index % WordBits)
        {
            var value = _words[index / WordBits] >> (index % WordBits);
            if (value != 0)
            {
                return index + BitOperations.TrailingZeroCount(value);
            }
        }

        return Bits;
    }

    private int FindPreviousSetBit(int end)
    {
        for (var index = end - 1; index >= 0; index -= index % WordBits + 1)
        {
            var value = _words[index / WordBits] << (WordBits - 1 - index % WordBits);
            if (value != 0)
            {
                return index - BitOperations.LeadingZeroCount(value);
            }
        }

        return -1;
    }
}
