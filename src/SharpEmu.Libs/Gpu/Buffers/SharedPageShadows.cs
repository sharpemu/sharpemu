// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.Intrinsics;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Buffers;

// Pages the CPU and shaders both write. On the console both land in one memory. Here the page
// tracker gives each page a single owner, so a shader store through a device address into a page
// the CPU dirtied cannot make it GPU-owned: the next upload copied the whole guest page, stale
// where the shader wrote, over the GPU copy, and the guest never saw the shader's bytes.
//
// A page shaders wrote through device addresses gets a shadow once the CPU touches it (up to the
// capacity): the bytes both copies last agreed on. An upload copies only the bytes the CPU
// changed since (the shader's bytes in the GPU copy stay), and the bytes the GPU
// changed since are merged into guest memory where the CPU left them alone. A byte both changed
// keeps the CPU's value in guest memory, and the next upload carries it to the GPU copy.
internal sealed unsafe class SharedPageShadows(ulong pageBytes, int capacity)
{
    private sealed class Shadow(object owner, byte[] bytes)
    {
        public object Owner = owner;
        public readonly byte[] Bytes = bytes;
        // The submission tick of the last upload staged for the page; the GPU copy lacks
        // its bytes until that tick completes.
        public ulong StagedTick;
    }

    // The owner of a page whose baseline is unproven: no buffer is it, so the next upload into the
    // page copies it whole and rebases the shadow from the staged bytes.
    private static readonly object Unsynchronized = new();

    private readonly Dictionary<ulong, Shadow> _pages = new();
    private readonly object _gate = new();
    // The narrowed copies of one upload, reused under the gate.
    private readonly List<BufferCopy> _filtered = new();
    private int _count;
    private long _mergedBytes;
    private long _skippedUploadBytes;

    public int Count => Volatile.Read(ref _count);

    // Bytes the GPU wrote that reached guest memory through a merge.
    public long MergedBytes => Interlocked.Read(ref _mergedBytes);

    // Bytes uploads left alone because the CPU had not changed them.
    public long SkippedUploadBytes => Interlocked.Read(ref _skippedUploadBytes);

    public bool Contains(ulong page)
    {
        if (Count == 0)
        {
            return false;
        }

        lock (_gate)
        {
            return _pages.ContainsKey(page);
        }
    }

    // The submission tick of the last upload staged for a shadowed page; false when the page has no shadow.
    // A snapshot of the GPU copy taken once that tick completed holds every byte the shadow expects.
    public bool TryGetStagedTick(ulong page, out ulong tick)
    {
        tick = 0;
        if (Count == 0)
        {
            return false;
        }

        lock (_gate)
        {
            if (!_pages.TryGetValue(page, out var shadow))
            {
                return false;
            }

            tick = shadow.StagedTick;
            return true;
        }
    }

    // Starts shadowing a page from bytes both copies hold, which uploads staged up to tick may
    // still change. A shader-written page is adopted when the CPU first touches it: that access
    // downloads the page, so guest memory then matches the GPU copy. A page the CPU dirtied before
    // its first shader write was reported is adopted from the GPU copy instead; it differs where
    // the CPU wrote and where the shader wrote, the next upload copies both, and those shader
    // bytes are lost once. False when the capacity is reached.
    public bool Adopt(ulong page, object owner, ReadOnlySpan<byte> gpuPage, ulong tick = 0)
    {
        RequirePage(gpuPage.Length);
        lock (_gate)
        {
            if (_pages.ContainsKey(page))
            {
                return true;
            }

            if (_pages.Count >= capacity)
            {
                return false;
            }

            _pages.Add(page, new Shadow(owner, gpuPage.ToArray()) { StagedTick = tick });
            Volatile.Write(ref _count, _pages.Count);
            return true;
        }
    }

    // Starts shadowing a page whose GPU copy cannot be read, so the bytes both copies agree on are
    // unknown: guest memory says nothing about bytes the GPU copy never received. The first upload
    // into the page copies all of it and takes the staged bytes as the baseline (a pull of a readable
    // GPU copy baselines it from that copy first, like any owner change). False when the capacity
    // is reached.
    public bool AdoptUnsynchronized(ulong page)
    {
        lock (_gate)
        {
            if (_pages.ContainsKey(page))
            {
                return true;
            }

            if (_pages.Count >= capacity)
            {
                return false;
            }

            _pages.Add(page, new Shadow(Unsynchronized, new byte[pageBytes]));
            Volatile.Write(ref _count, _pages.Count);
            return true;
        }
    }

    // The cache copied the entire old buffer into its replacement. Its shared pages retain
    // the same baseline; treating the replacement as unrelated would upload stale GPU bytes.
    public void NoteOwnerCopy(object source, object target, ulong tick)
    {
        if (Count == 0) return;
        lock (_gate)
        {
            foreach (var shadow in _pages.Values)
            {
                if (!ReferenceEquals(shadow.Owner, source)) continue;
                shadow.Owner = target;
                shadow.StagedTick = Math.Max(shadow.StagedTick, tick);
            }
        }
    }

    // A download wrote GPU bytes into guest memory; on shadowed pages both copies now agree there.
    public void NoteDownloaded(ulong address, ReadOnlySpan<byte> bytes)
    {
        if (Count == 0 || bytes.IsEmpty)
        {
            return;
        }

        var end = address + (ulong)bytes.Length;
        lock (_gate)
        {
            for (var page = address & ~(pageBytes - 1); page < end; page += pageBytes)
            {
                if (!_pages.TryGetValue(page, out var shadow))
                {
                    continue;
                }

                var from = Math.Max(page, address);
                var to = Math.Min(page + pageBytes, end);
                bytes.Slice((int)(from - address), (int)(to - from)).CopyTo(shadow.Bytes.AsSpan((int)(from - page)));
            }
        }
    }

    // Merges the bytes the GPU changed since the shadow into guestPage, where the CPU did not
    // change them, and returns the number of merged bytes (writeGuest receives each merged run as
    // an offset and length). Only once every upload staged for the page has executed: before that
    // the GPU copy still lacks staged CPU bytes, which would read as GPU changes. A page whose
    // owner changed is shadowed again from the new copy. gpuPage must not change during the call.
    public int PullGpuWrites(ulong page, object owner, ReadOnlySpan<byte> gpuPage, Span<byte> guestPage, ulong completedTick,
        Action<int, int> writeGuest)
    {
        RequirePage(gpuPage.Length);
        RequirePage(guestPage.Length);
        lock (_gate)
        {
            if (!_pages.TryGetValue(page, out var shadow))
            {
                return 0;
            }

            if (!ReferenceEquals(shadow.Owner, owner))
            {
                shadow.Owner = owner;
                gpuPage.CopyTo(shadow.Bytes);
                return 0;
            }

            if (shadow.StagedTick > completedTick)
            {
                return 0;
            }

            var bytes = shadow.Bytes.AsSpan();
            var merged = 0;
            var index = 0;
            while ((index += gpuPage[index..].CommonPrefixLength(bytes[index..])) < bytes.Length)
            {
                // A run of GPU changes; within it, the bytes the CPU left alone go to guest memory.
                var runEnd = index + DifferingPrefixLength(gpuPage[index..], bytes[index..]);

                var write = -1;
                for (var at = index; at <= runEnd; at++)
                {
                    var take = at < runEnd && guestPage[at] == bytes[at];
                    if (take)
                    {
                        guestPage[at] = gpuPage[at];
                        write = write < 0 ? at : write;
                    }
                    else if (write >= 0)
                    {
                        writeGuest(write, at - write);
                        merged += at - write;
                        write = -1;
                    }
                }

                gpuPage[index..runEnd].CopyTo(bytes[index..]);
                index = runEnd;
            }

            if (merged != 0)
            {
                Interlocked.Add(ref _mergedBytes, merged);
            }

            return merged;
        }
    }

    // Narrows an upload to the bytes the CPU changed on shadowed pages. copies stage from
    // staged (indexed by SrcOffset) into a buffer at targetAddress (indexed by DstOffset); copies
    // over shadowed pages are split into the runs that differ from the shadow, which takes them.
    // An upload into a new owner copies everything and shadows the page from it.
    public void FilterUpload(List<BufferCopy> copies, object target, ulong targetAddress, ReadOnlySpan<byte> staged, ulong tick)
    {
        if (Count == 0)
        {
            return;
        }

        Span<byte> pageScratch = stackalloc byte[checked((int)pageBytes)];
        lock (_gate)
        {
            List<BufferCopy>? filtered = null;
            for (var index = 0; index < copies.Count; index++)
            {
                var copy = copies[index];
                var begin = targetAddress + copy.DstOffset;
                var end = begin + copy.Size;
                var emitted = begin;
                for (var page = begin & ~(pageBytes - 1); page < end; page += pageBytes)
                {
                    if (!_pages.TryGetValue(page, out var shadow))
                    {
                        continue;
                    }

                    if (filtered is null)
                    {
                        filtered = _filtered;
                        filtered.Clear();
                        for (var earlier = 0; earlier < index; earlier++)
                        {
                            filtered.Add(copies[earlier]);
                        }
                    }

                    var from = Math.Max(page, begin);
                    var to = Math.Min(page + pageBytes, end);
                    if (emitted < from)
                    {
                        filtered.Add(Slice(copy, begin, emitted, from - emitted));
                    }

                    emitted = to;
                    // The staging mapping is write-combining: diff a copy, not the mapping itself.
                    var stagedSlice = staged.Slice(checked((int)(copy.SrcOffset + (from - begin))), checked((int)(to - from)));
                    var source = pageScratch[..stagedSlice.Length];
                    stagedSlice.CopyTo(source);
                    var shadowBytes = shadow.Bytes.AsSpan(checked((int)(from - page)), source.Length);
                    if (!ReferenceEquals(shadow.Owner, target))
                    {
                        shadow.Owner = target;
                        filtered.Add(Slice(copy, begin, from, to - from));
                        shadow.StagedTick = Math.Max(shadow.StagedTick, tick);
                        source.CopyTo(shadowBytes);
                        continue;
                    }

                    var skipped = source.Length;
                    var at = 0;
                    while ((at += source[at..].CommonPrefixLength(shadowBytes[at..])) < source.Length)
                    {
                        var runEnd = at + DifferingPrefixLength(source[at..], shadowBytes[at..]);

                        filtered.Add(Slice(copy, begin, from + (ulong)at, (ulong)(runEnd - at)));
                        // Only a copy that survives filtering can leave staged bytes missing
                        // from the GPU mapping. An eliminated upload has no completion to wait for.
                        shadow.StagedTick = Math.Max(shadow.StagedTick, tick);
                        source[at..runEnd].CopyTo(shadowBytes[at..]);
                        skipped -= runEnd - at;
                        at = runEnd;
                    }

                    Interlocked.Add(ref _skippedUploadBytes, skipped);
                }

                if (filtered is null)
                {
                    continue;
                }

                if (emitted < end)
                {
                    filtered.Add(Slice(copy, begin, emitted, end - emitted));
                }
            }

            if (filtered is not null)
            {
                copies.Clear();
                copies.AddRange(filtered);
                filtered.Clear();
            }
        }
    }

    // The number of leading positions where the spans differ, 32 bytes at a time: CPU-written pages
    // are mostly changed bytes, and a byte loop over them was most of the upload filter's time.
    internal static int DifferingPrefixLength(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        var length = Math.Min(left.Length, right.Length);
        var index = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            ref var leftStart = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(left);
            ref var rightStart = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(right);
            for (; index + 32 <= length; index += 32)
            {
                var equal = Vector256.Equals(
                    Vector256.LoadUnsafe(ref leftStart, (nuint)index),
                    Vector256.LoadUnsafe(ref rightStart, (nuint)index)).ExtractMostSignificantBits();
                if (equal != 0)
                {
                    return index + System.Numerics.BitOperations.TrailingZeroCount(equal);
                }
            }
        }

        while (index < length && left[index] != right[index])
        {
            index++;
        }

        return index;
    }

    private static BufferCopy Slice(BufferCopy copy, ulong begin, ulong address, ulong size) =>
        new(copy.SrcOffset + (address - begin), copy.DstOffset + (address - begin), size);

    private void RequirePage(int length)
    {
        if ((ulong)length != pageBytes)
        {
            throw new ArgumentException("A shadowed page span must cover exactly one page.");
        }
    }
}
