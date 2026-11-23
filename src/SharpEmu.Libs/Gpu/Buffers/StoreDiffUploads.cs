// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers;
using System.Runtime.InteropServices;
using SharpEmu.HLE;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Buffers;

// A shader that stores through a run-time device address changes the cache buffer alone, and the host learns of it
// only when the fault scan reports the page. If the guest CPU writes the same 4 KiB page in between, a full-page
// upload puts the stale guest bytes over the shader's. For a reported page the upload therefore sends only the bytes
// the CPU changed since the last upload (a shadow of what guest memory held then), and the shader's bytes stay.
internal sealed class StoreDiffUploads(ICpuMemory guest)
{
    // SHARPEMU_STORE_DIFF_UPLOAD=0 keeps the full-page uploads, for A/B.
    internal static readonly bool Enabled = Environment.GetEnvironmentVariable("SHARPEMU_STORE_DIFF_UPLOAD") != "0";

    internal const int PageBytes = 4096;
    internal const int MaxShadows = 32768;

    // Changed runs closer than this merge into one copy, which bounds the copy count of a page.
    internal const int MergeGapBytes = 64;

    private const ulong PageMask = PageBytes - 1;

    // Reports come from the thread that records, uploads from the render and command threads, and downloads from
    // either plus readback completions: one lock covers the set and the shadows.
    private readonly object _lock = new();
    private readonly HashSet<ulong> _tracked = [];
    private readonly Dictionary<ulong, byte[]> _shadows = [];
    private readonly byte[] _snapshot = new byte[PageBytes];
    private readonly List<(int Offset, int Length)> _runs = [];
    private readonly List<BufferCopy> _result = [];
    private ulong _trackedMin = ulong.MaxValue;
    private ulong _trackedMax;
    private int _trackedCount;

    private long _diffPages;
    private long _diffBytes;
    private long _diffEmpty;
    private long _shadowCapped;

    public long DiffPages => Volatile.Read(ref _diffPages);
    public long DiffBytes => Volatile.Read(ref _diffBytes);
    public long DiffEmpty => Volatile.Read(ref _diffEmpty);
    public long ShadowCapped => Volatile.Read(ref _shadowCapped);
    public int TrackedPages => Volatile.Read(ref _trackedCount);

    public int ShadowPages
    {
        get
        {
            lock (_lock)
                return _shadows.Count;
        }
    }

    // Marks every tracker page of the range as one a shader stored to.
    public void Track(ulong address, ulong size)
    {
        if (!Enabled || size == 0)
            return;

        lock (_lock)
        {
            for (var page = address & ~PageMask; page < address + size; page += PageBytes)
            {
                if (_tracked.Add(page))
                {
                    _trackedMin = Math.Min(_trackedMin, page);
                    _trackedMax = Math.Max(_trackedMax, page);
                }
            }

            Volatile.Write(ref _trackedCount, _tracked.Count);
        }
    }

    // Rewrites the copies of one upload: a tracked page that has a shadow keeps only the runs the CPU changed, a
    // tracked page without one stays whole and gets its shadow. Source offsets are repacked and totalSize follows.
    // The tracker has already cleared the pages' dirty state, so only the choice of bytes changes here.
    public void Apply(ulong bufferAddress, List<BufferCopy> copies, ref ulong totalSize)
    {
        if (!Enabled || Volatile.Read(ref _trackedCount) == 0 || copies.Count == 0)
            return;

        lock (_lock)
        {
            if (!TouchesTrackedPage(bufferAddress, copies))
                return;

            _result.Clear();
            ulong packed = 0;
            foreach (var copy in copies)
            {
                var address = bufferAddress + copy.DstOffset;
                var end = address + copy.Size;
                var passStart = 0UL;
                var passEnd = 0UL;
                for (var cursor = address; cursor < end;)
                {
                    var page = cursor & ~PageMask;
                    var pieceEnd = Math.Min(end, page + PageBytes);
                    if (!_tracked.Contains(page) || !DiffPage(page, cursor, pieceEnd))
                    {
                        // Untracked, or tracked but uploaded whole (no shadow yet, partial piece, cap): as before.
                        Extend(ref passStart, ref passEnd, cursor, pieceEnd);
                    }
                    else
                    {
                        FlushPass(bufferAddress, ref passStart, ref passEnd, ref packed);
                        foreach (var (offset, length) in _runs)
                        {
                            _result.Add(new BufferCopy(packed, page + (ulong)offset - bufferAddress, (ulong)length));
                            packed += (ulong)length;
                        }
                    }

                    cursor = pieceEnd;
                }

                FlushPass(bufferAddress, ref passStart, ref passEnd, ref packed);
            }

            copies.Clear();
            copies.AddRange(_result);
            totalSize = packed;
        }
    }

    private bool TouchesTrackedPage(ulong bufferAddress, List<BufferCopy> copies)
    {
        foreach (var copy in copies)
        {
            var address = bufferAddress + copy.DstOffset;
            var end = address + copy.Size;
            if (end <= _trackedMin || address > _trackedMax + PageBytes)
                continue;

            for (var page = address & ~PageMask; page < end; page += PageBytes)
            {
                if (_tracked.Contains(page))
                    return true;
            }
        }

        return false;
    }

    // Pieces that upload whole join into one copy; a diffed page flushes the pending one first, so they stay adjacent.
    private static void Extend(ref ulong start, ref ulong end, ulong pieceStart, ulong pieceEnd)
    {
        if (end == start)
            start = pieceStart;
        end = pieceEnd;
    }

    private void FlushPass(ulong bufferAddress, ref ulong start, ref ulong end, ref ulong packed)
    {
        if (end == start)
            return;

        _result.Add(new BufferCopy(packed, start - bufferAddress, end - start));
        packed += end - start;
        start = end = 0;
    }

    // True when the page's runs are in _runs and only they should upload; false when the whole piece uploads (partial
    // piece, unreadable page, no shadow yet, cap reached).
    private bool DiffPage(ulong page, ulong pieceStart, ulong pieceEnd)
    {
        if (pieceStart != page || pieceEnd != page + PageBytes)
        {
            // A piece of a page: its shadow no longer says what the buffer holds.
            DropShadow(page);
            return false;
        }

        // Snapshot from guest memory, never from staging, which may be write-combined. The staged bytes are read
        // after this and so are as new; a CPU write in between marks the page dirty again for the next diff.
        if (!guest.TryRead(page, _snapshot))
        {
            DropShadow(page);
            return false;
        }

        if (!_shadows.TryGetValue(page, out var shadow))
        {
            if (_shadows.Count >= MaxShadows)
            {
                Interlocked.Increment(ref _shadowCapped);
                return false;
            }

            shadow = ArrayPool<byte>.Shared.Rent(PageBytes);
            _snapshot.CopyTo(shadow, 0);
            _shadows.Add(page, shadow);
            return false;
        }

        ComputeRuns(shadow.AsSpan(0, PageBytes), _snapshot, _runs);
        _snapshot.CopyTo(shadow, 0);
        Interlocked.Increment(ref _diffPages);
        if (_runs.Count == 0)
            Interlocked.Increment(ref _diffEmpty);
        foreach (var (_, length) in _runs)
            Interlocked.Add(ref _diffBytes, length);
        return true;
    }

    private void DropShadow(ulong page)
    {
        if (_shadows.Remove(page, out var shadow))
            ArrayPool<byte>.Shared.Return(shadow);
    }

    // Guest memory just received downloaded bytes: they are what the CPU sees now, not a CPU change to upload.
    public void NoteDownloaded(ulong address, ReadOnlySpan<byte> bytes)
    {
        if (!Enabled || Volatile.Read(ref _trackedCount) == 0 || bytes.IsEmpty)
            return;

        var end = address + (ulong)bytes.Length;
        lock (_lock)
        {
            if (_shadows.Count == 0)
                return;

            for (var page = address & ~PageMask; page < end; page += PageBytes)
            {
                if (!_shadows.TryGetValue(page, out var shadow))
                    continue;

                var from = Math.Max(address, page);
                var to = Math.Min(end, page + PageBytes);
                bytes.Slice((int)(from - address), (int)(to - from)).CopyTo(shadow.AsSpan((int)(from - page)));
            }
        }
    }

    // The 8-byte-aligned runs where current differs from shadow; runs whose gap is under MergeGapBytes join.
    internal static void ComputeRuns(ReadOnlySpan<byte> shadow, ReadOnlySpan<byte> current, List<(int Offset, int Length)> runs)
    {
        runs.Clear();
        if (shadow.SequenceEqual(current))
            return;

        const int Chunk = 64;
        var runStart = -1;
        var runEnd = 0;
        for (var chunk = 0; chunk < current.Length; chunk += Chunk)
        {
            if (shadow.Slice(chunk, Chunk).SequenceEqual(current.Slice(chunk, Chunk)))
                continue;

            for (var offset = chunk; offset < chunk + Chunk; offset += 8)
            {
                if (MemoryMarshal.Read<ulong>(shadow[offset..]) == MemoryMarshal.Read<ulong>(current[offset..]))
                    continue;

                if (runStart >= 0 && offset - runEnd < MergeGapBytes)
                {
                    runEnd = offset + 8;
                    continue;
                }

                if (runStart >= 0)
                    runs.Add((runStart, runEnd - runStart));
                runStart = offset;
                runEnd = offset + 8;
            }
        }

        if (runStart >= 0)
            runs.Add((runStart, runEnd - runStart));
    }
}
