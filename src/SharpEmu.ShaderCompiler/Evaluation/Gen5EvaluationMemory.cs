// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace SharpEmu.ShaderCompiler.Evaluation;

public interface IGen5EvaluationMemory
{
    bool TryRead(ulong address, Span<byte> destination);
}

internal readonly record struct Gen5MemoInput(ulong Address, int Words, ulong[]? Mask, int ValueOffset);

internal sealed class Gen5EvaluationReads
{
    public const long MaxFetchedBytes = 16L * 1024 * 1024;
    private const int PageShift = 12;
    private const int PageBytes = 1 << PageShift;
    private const int PageDwords = PageBytes / sizeof(uint);

    private sealed class Page
    {
        public readonly uint[] Words = new uint[PageDwords];
        public readonly ulong[] Loaded = new ulong[PageDwords / 64];
        public readonly ulong[] Read = new ulong[PageDwords / 64];
        public bool WindowRefused;

        public void Clear()
        {
            Array.Clear(Loaded);
            Array.Clear(Read);
            WindowRefused = false;
        }
    }

    private readonly Dictionary<ulong, Page> _pages = new();
    private readonly Stack<Page> _spare = new();
    private readonly byte[] _scratch = new byte[PageBytes];
    private IGen5EvaluationMemory _memory = null!;
    private Page? _lastPage;
    private ulong _lastPageIndex = ulong.MaxValue;
    private Page? _probePage;
    private ulong _probePageIndex = ulong.MaxValue;

    public long FetchedBytes { get; private set; }

    public void Reset(IGen5EvaluationMemory memory)
    {
        _memory = memory;
        foreach (var page in _pages.Values)
        {
            page.Clear();
            _spare.Push(page);
        }

        _pages.Clear();
        _lastPage = null;
        _lastPageIndex = ulong.MaxValue;
        _probePage = null;
        _probePageIndex = ulong.MaxValue;
        FetchedBytes = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Gen5EvaluationRefusal TryRead(ulong address, ulong windowStart, ulong windowEnd, bool kernelRead, out uint value)
    {
        if (address >> PageShift == _lastPageIndex)
        {
            var page = _lastPage!;
            var slot = (int)((address & (PageBytes - 1)) >> 2);
            var bit = 1ul << (slot & 63);
            if ((page.Loaded[slot >> 6] & bit) != 0)
            {
                if (kernelRead)
                {
                    page.Read[slot >> 6] |= bit;
                }

                value = page.Words[slot];
                return Gen5EvaluationRefusal.None;
            }
        }

        return TryReadSlow(address, windowStart, windowEnd, kernelRead, out value);
    }

    public Gen5EvaluationRefusal TryReadRun(ulong address, Span<uint> words, ulong windowStart, ulong windowEnd)
    {
        var index = 0;
        while (index < words.Length)
        {
            var at = address + (ulong)index * sizeof(uint);
            var page = PageOf(at);
            var slot = (int)((at & (PageBytes - 1)) >> 2);
            var run = Math.Min(words.Length - index, PageDwords - slot);
            for (var offset = 0; offset < run; offset++, slot++)
            {
                var bit = 1ul << (slot & 63);
                if ((page.Loaded[slot >> 6] & bit) == 0)
                {
                    var refusal = Fetch(page, at + (ulong)offset * sizeof(uint), windowStart, windowEnd);
                    if (refusal != Gen5EvaluationRefusal.None)
                    {
                        return refusal;
                    }
                }

                page.Read[slot >> 6] |= bit;
                words[index + offset] = page.Words[slot];
            }

            index += run;
        }

        return Gen5EvaluationRefusal.None;
    }

    public bool AnyRead(ulong address, int words)
    {
        var index = 0;
        while (index < words)
        {
            var at = address + (ulong)index * sizeof(uint);
            var slot = (int)((at & (PageBytes - 1)) >> 2);
            var run = Math.Min(words - index, PageDwords - slot);
            if (_pages.TryGetValue(at >> PageShift, out var page))
            {
                for (var offset = 0; offset < run; offset++, slot++)
                {
                    if ((page.Read[slot >> 6] & (1ul << (slot & 63))) != 0)
                    {
                        return true;
                    }
                }
            }

            index += run;
        }

        return false;
    }

    // Every word the result depends on, page by page: the span from the first to the last word read, which
    // words of it were read (null when all were) and their values in address order.
    public void CollectInputs(List<Gen5MemoInput> inputs, List<uint> values)
    {
        foreach (var (index, page) in _pages)
        {
            var first = -1;
            var last = -1;
            for (var block = 0; block < page.Read.Length; block++)
            {
                var bits = page.Read[block];
                if (bits == 0)
                {
                    continue;
                }

                if (first < 0)
                {
                    first = block * 64 + BitOperations.TrailingZeroCount(bits);
                }

                last = block * 64 + 63 - BitOperations.LeadingZeroCount(bits);
            }

            if (first < 0)
            {
                continue;
            }

            var words = last - first + 1;
            var valueOffset = values.Count;
            for (var slot = first; slot <= last; slot++)
            {
                if ((page.Read[slot >> 6] & (1ul << (slot & 63))) != 0)
                {
                    values.Add(page.Words[slot]);
                }
            }

            ulong[]? mask = null;
            if (values.Count - valueOffset != words)
            {
                mask = new ulong[(words + 63) / 64];
                for (var slot = first; slot <= last; slot++)
                {
                    if ((page.Read[slot >> 6] & (1ul << (slot & 63))) != 0)
                    {
                        mask[(slot - first) >> 6] |= 1ul << ((slot - first) & 63);
                    }
                }
            }

            inputs.Add(new Gen5MemoInput((index << PageShift) + (ulong)first * sizeof(uint), words, mask, valueOffset));
        }
    }

    private Gen5EvaluationRefusal TryReadSlow(ulong address, ulong windowStart, ulong windowEnd, bool kernelRead, out uint value)
    {
        var page = PageOf(address);
        var slot = (int)((address & (PageBytes - 1)) >> 2);
        var bit = 1ul << (slot & 63);
        if ((page.Loaded[slot >> 6] & bit) == 0)
        {
            var refusal = Fetch(page, address, windowStart, windowEnd);
            if (refusal != Gen5EvaluationRefusal.None)
            {
                value = 0;
                return refusal;
            }
        }

        if (kernelRead)
        {
            page.Read[slot >> 6] |= bit;
        }

        value = page.Words[slot];
        return Gen5EvaluationRefusal.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool WasRead(ulong address)
    {
        var index = address >> PageShift;
        Page? page;
        if (index == _lastPageIndex)
        {
            page = _lastPage;
        }
        else if (index == _probePageIndex)
        {
            page = _probePage;
        }
        else
        {
            _pages.TryGetValue(index, out page);
            _probePage = page;
            _probePageIndex = index;
        }

        if (page is null)
        {
            return false;
        }

        var slot = (int)((address & (PageBytes - 1)) >> 2);
        return (page.Read[slot >> 6] & (1ul << (slot & 63))) != 0;
    }

    private Gen5EvaluationRefusal Fetch(Page page, ulong address, ulong windowStart, ulong windowEnd)
    {
        var pageStart = address & ~(ulong)(PageBytes - 1);
        var start = Math.Max(pageStart, windowStart & ~3ul);
        var end = Math.Min(pageStart + PageBytes, windowEnd);
        if (address >= start && address + sizeof(uint) <= end && end - start > sizeof(uint))
        {
            if (!page.WindowRefused)
            {
                if (TryFill(page, pageStart, start, end))
                {
                    return Gen5EvaluationRefusal.None;
                }

                page.WindowRefused = true;
            }

            if (end - address > sizeof(uint) && TryFill(page, pageStart, address, end))
            {
                return Gen5EvaluationRefusal.None;
            }
        }

        return TryFill(page, pageStart, address, address + sizeof(uint)) ? Gen5EvaluationRefusal.None : Gen5EvaluationRefusal.InputOwned;
    }

    private bool TryFill(Page page, ulong pageStart, ulong start, ulong end)
    {
        var length = (int)(end - start);
        if (FetchedBytes + length > MaxFetchedBytes)
        {
            return false;
        }

        var bytes = _scratch.AsSpan(0, length);
        if (!_memory.TryRead(start, bytes))
        {
            return false;
        }

        FetchedBytes += length;
        var first = (int)((start - pageStart) >> 2);
        for (var index = 0; index < length / sizeof(uint); index++)
        {
            var slot = first + index;
            var bit = 1ul << (slot & 63);
            if ((page.Loaded[slot >> 6] & bit) == 0)
            {
                page.Words[slot] = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(index * sizeof(uint))..]);
                page.Loaded[slot >> 6] |= bit;
            }
        }

        return true;
    }

    private Page PageOf(ulong address)
    {
        var index = address >> PageShift;
        if (index == _lastPageIndex)
        {
            return _lastPage!;
        }

        if (!_pages.TryGetValue(index, out var page))
        {
            page = _spare.Count != 0 ? _spare.Pop() : new Page();
            _pages.Add(index, page);
            if (index == _probePageIndex)
            {
                _probePage = page;
            }
        }

        _lastPage = page;
        _lastPageIndex = index;
        return page;
    }
}

internal sealed class Gen5EvaluationWrites
{
    public const int MaxOutputDwords = 1024 * 1024;
    private const int PageShift = 12;
    private const int PageBytes = 1 << PageShift;
    private const int PageDwords = PageBytes / sizeof(uint);

    private sealed class Page
    {
        public readonly uint[] Values = new uint[PageDwords];
        public readonly int[] Writers = new int[PageDwords];
        public readonly ulong[] Written = new ulong[PageDwords / 64];
    }

    private readonly Dictionary<ulong, Page> _pages = new();
    private readonly Stack<Page> _spare = new();
    private int _maxDwords = MaxOutputDwords;
    private Page? _lastPage;
    private ulong _lastPageIndex = ulong.MaxValue;
    private Page? _probePage;
    private ulong _probePageIndex = ulong.MaxValue;

    public int Count { get; private set; }

    public Gen5EvaluationWrites Reset(int maxDwords)
    {
        _maxDwords = maxDwords;
        foreach (var page in _pages.Values)
        {
            Array.Clear(page.Written);
            _spare.Push(page);
        }

        _pages.Clear();
        _lastPage = null;
        _lastPageIndex = ulong.MaxValue;
        _probePage = null;
        _probePageIndex = ulong.MaxValue;
        Count = 0;
        return this;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsWritten(ulong address)
    {
        if (Count == 0)
        {
            return false;
        }

        var index = address >> PageShift;
        Page? page;
        if (index == _lastPageIndex)
        {
            page = _lastPage;
        }
        else if (index == _probePageIndex)
        {
            page = _probePage;
        }
        else
        {
            _pages.TryGetValue(index, out page);
            _probePage = page;
            _probePageIndex = index;
        }

        if (page is null)
        {
            return false;
        }

        var slot = (int)((address & (PageBytes - 1)) >> 2);
        return (page.Written[slot >> 6] & (1ul << (slot & 63))) != 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Gen5EvaluationRefusal Write(ulong address, uint value, int writer)
    {
        var index = address >> PageShift;
        var page = index == _lastPageIndex ? _lastPage! : PageFor(index);
        var slot = (int)((address & (PageBytes - 1)) >> 2);
        var bit = 1ul << (slot & 63);
        if ((page.Written[slot >> 6] & bit) != 0)
        {
            if (page.Writers[slot] != writer && page.Values[slot] != value)
            {
                return Gen5EvaluationRefusal.WriteConflict;
            }

            page.Values[slot] = value;
            page.Writers[slot] = writer;
            return Gen5EvaluationRefusal.None;
        }

        if (Count >= _maxDwords)
        {
            return Gen5EvaluationRefusal.OutputTooLarge;
        }

        page.Written[slot >> 6] |= bit;
        page.Values[slot] = value;
        page.Writers[slot] = writer;
        Count++;
        return Gen5EvaluationRefusal.None;
    }

    // Stores lanes [first, first + lanes) of the data rows back to back from the address, components interleaved.
    public Gen5EvaluationRefusal WriteRun(ulong address, uint[] vectors, int dataRow, int rowStride, int dwords, int first, int lanes, int writerBase)
    {
        var pageIndex = ulong.MaxValue;
        Page page = null!;
        var target = address;
        for (var lane = first; lane < first + lanes; lane++)
        {
            var writer = writerBase | lane;
            for (var component = 0; component < dwords; component++, target += sizeof(uint))
            {
                if (target >> PageShift != pageIndex)
                {
                    pageIndex = target >> PageShift;
                    page = Find(pageIndex);
                }

                var value = vectors[dataRow + component * rowStride + lane];
                var slot = (int)((target & (PageBytes - 1)) >> 2);
                var bit = 1ul << (slot & 63);
                if ((page.Written[slot >> 6] & bit) != 0)
                {
                    if (page.Writers[slot] != writer && page.Values[slot] != value)
                    {
                        return Gen5EvaluationRefusal.WriteConflict;
                    }
                }
                else
                {
                    if (Count >= _maxDwords)
                    {
                        return Gen5EvaluationRefusal.OutputTooLarge;
                    }

                    page.Written[slot >> 6] |= bit;
                    Count++;
                }

                page.Values[slot] = value;
                page.Writers[slot] = writer;
            }
        }

        return Gen5EvaluationRefusal.None;

        Page Find(ulong pageNumber) => pageNumber == _lastPageIndex ? _lastPage! : PageFor(pageNumber);
    }

    private Page PageFor(ulong index)
    {
        if (!_pages.TryGetValue(index, out var page))
        {
            page = _spare.Count != 0 ? _spare.Pop() : new Page();
            _pages.Add(index, page);
            if (index == _probePageIndex)
            {
                _probePage = page;
            }
        }

        _lastPage = page;
        _lastPageIndex = index;
        return page;
    }

    public bool SameAs(Gen5EvaluationWrites other)
    {
        if (Count != other.Count || _pages.Count != other._pages.Count)
        {
            return false;
        }

        foreach (var (index, page) in _pages)
        {
            if (!other._pages.TryGetValue(index, out var otherPage))
            {
                return false;
            }

            for (var word = 0; word < page.Written.Length; word++)
            {
                var written = page.Written[word];
                if (written != otherPage.Written[word])
                {
                    return false;
                }

                while (written != 0)
                {
                    var slot = word * 64 + BitOperations.TrailingZeroCount(written);
                    written &= written - 1;
                    if (page.Values[slot] != otherPage.Values[slot])
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }

    public Gen5EvaluationRefusal TryBuild(Gen5EvaluationReads reads, out ulong address, out uint[] words, out int count)
    {
        address = 0;
        words = [];
        count = 0;
        if (Count == 0)
        {
            return Gen5EvaluationRefusal.NoWrites;
        }

        var first = ulong.MaxValue;
        var last = 0ul;
        foreach (var (index, page) in _pages)
        {
            for (var word = 0; word < page.Written.Length; word++)
            {
                var written = page.Written[word];
                if (written == 0)
                {
                    continue;
                }

                var low = (index << PageShift) + (ulong)(word * 64 + BitOperations.TrailingZeroCount(written)) * sizeof(uint);
                var high = (index << PageShift) + (ulong)(word * 64 + 63 - BitOperations.LeadingZeroCount(written)) * sizeof(uint);
                first = Math.Min(first, low);
                last = Math.Max(last, high);
            }
        }

        var span = (last - first) / sizeof(uint) + 1;
        if (span > (ulong)_maxDwords)
        {
            return Gen5EvaluationRefusal.OutputTooLarge;
        }

        if (span - (ulong)Count > (ulong)Count)
        {
            return Gen5EvaluationRefusal.WriteRegions;
        }

        count = (int)span;
        words = count > 16 * 1024 ? System.Buffers.ArrayPool<uint>.Shared.Rent(count) : new uint[count];
        var position = 0;
        while (position < count)
        {
            var target = first + (ulong)position * sizeof(uint);
            var slot = (int)((target & (PageBytes - 1)) >> 2);
            var run = Math.Min(count - position, PageDwords - slot);
            _pages.TryGetValue(target >> PageShift, out var page);
            for (var offset = 0; offset < run; offset++, slot++)
            {
                if (page is not null && (page.Written[slot >> 6] & (1ul << (slot & 63))) != 0)
                {
                    words[position + offset] = page.Values[slot];
                    continue;
                }

                var refusal = reads.TryRead(target + (ulong)offset * sizeof(uint), first, last + sizeof(uint), kernelRead: true, out words[position + offset]);
                if (refusal != Gen5EvaluationRefusal.None)
                {
                    Return(words, count);
                    words = [];
                    count = 0;
                    return refusal;
                }
            }

            position += run;
        }

        address = first;
        return Gen5EvaluationRefusal.None;
    }

    public static bool IsPooled(int count) => count > 16 * 1024;

    private static void Return(uint[] words, int count)
    {
        if (IsPooled(count))
        {
            System.Buffers.ArrayPool<uint>.Shared.Return(words);
        }
    }
}
