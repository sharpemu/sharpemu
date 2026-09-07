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

    // Copies only the bits of [start, end).
    public PageMask(in PageMask source, int start, int end)
    {
        for (var word = 0; word < WordCount; word++)
        {
            _words[word] = source._words[word] & RangeWord(word, start, end);
        }
    }

    public bool Any
    {
        get
        {
            for (var word = 0; word < WordCount; word++)
            {
                if (_words[word] != 0)
                {
                    return true;
                }
            }

            return false;
        }
    }

    public bool None => !Any;

    public bool Get(int index) => (_words[index / WordBits] & (1UL << (index % WordBits))) != 0;

    public void Set(int index) => _words[index / WordBits] |= 1UL << (index % WordBits);

    public void Fill()
    {
        for (var word = 0; word < WordCount; word++)
        {
            _words[word] = ~0UL;
        }
    }

    public void SetRange(int start, int end)
    {
        for (var word = 0; word < WordCount; word++)
        {
            _words[word] |= RangeWord(word, start, end);
        }
    }

    public void UnsetRange(int start, int end)
    {
        for (var word = 0; word < WordCount; word++)
        {
            _words[word] &= ~RangeWord(word, start, end);
        }
    }

    public static PageMask operator ^(in PageMask left, in PageMask right)
    {
        var result = default(PageMask);
        for (var word = 0; word < WordCount; word++)
        {
            result._words[word] = left._words[word] ^ right._words[word];
        }

        return result;
    }

    public static PageMask operator ~(in PageMask value)
    {
        var result = default(PageMask);
        for (var word = 0; word < WordCount; word++)
        {
            result._words[word] = ~value._words[word];
        }

        return result;
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

    public RunEnumerator GetEnumerator() => new(this);

    public struct RunEnumerator(PageMask mask)
    {
        private int _next;

        public (int Start, int End) Current { get; private set; }

        public bool MoveNext()
        {
            var run = mask.FindFirstSetRangeFrom(_next);
            if (run.Start == Bits)
            {
                return false;
            }

            Current = run;
            _next = run.End;
            return true;
        }
    }

    private static ulong RangeWord(int word, int start, int end)
    {
        if (end > Bits)
        {
            return 0;
        }

        var low = Math.Max(start, word * WordBits) - word * WordBits;
        var high = Math.Min(end, (word + 1) * WordBits) - word * WordBits;
        if (low >= high)
        {
            return 0;
        }

        return (high == WordBits ? ~0UL : (1UL << high) - 1) & (~0UL << low);
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
