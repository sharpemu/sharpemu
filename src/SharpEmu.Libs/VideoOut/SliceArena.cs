// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.IO.Hashing;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Logging;
using HostDiagnostics = SharpEmu.HLE.Diagnostics;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace SharpEmu.Libs.VideoOut;

internal sealed class SliceArenaChunk(GpuBuffer buffer)
{
    public readonly GpuBuffer Buffer = buffer;
    public ulong Offset;
    public long ReturnedAt;
}

internal readonly record struct SliceArenaReservation(SliceArenaChunk Chunk, ulong Offset, ulong Size)
{
    public VkBuffer Buffer => Chunk.Buffer.Handle;

    public Span<byte> Destination => Chunk.Buffer.Mapped.Slice((int)Offset, (int)Size);
}

internal sealed class SliceArenaPool : IDisposable
{
    public const ulong ChunkBytes = 4UL << 20;

    private readonly GpuDeviceInfo _device;
    private readonly SubmissionScheduler _scheduler;
    private readonly ConcurrentBag<SliceArenaChunk> _free = new();
    private readonly List<SliceArenaChunk> _all = new();
    private long _nextTrim;
    private readonly Func<HostMemoryAvailability> _readMemory;
    private long _nextPressureCheck;
    private long _lastGrowth;
    private long _nextSurplusSample;
    private long _surplusWindowEnd;
    private int _minimumIdle;

    internal void TrimSurplus(long now)
    {
        if (now < _nextSurplusSample) return;
        _nextSurplusSample = now + 500;
        var idle = _free.Count;
        if (_surplusWindowEnd == 0 || now - Volatile.Read(ref _lastGrowth) < 5000)
        {
            _surplusWindowEnd = now + 5000;
            _minimumIdle = idle;
            return;
        }
        _minimumIdle = Math.Min(_minimumIdle, idle);
        if (now < _surplusWindowEnd) return;
        // A busy pool can cycle every chunk even with persistent surplus. Keep eight for short bursts.
        var release = Math.Min(8, _minimumIdle - 8);
        _surplusWindowEnd = now + 5000;
        for (var i = 0; i < release && _free.TryTake(out var chunk); i++)
        {
            lock (_all) _all.Remove(chunk);
            chunk.Buffer.Dispose();
        }
        _minimumIdle = _free.Count;
    }

    public SliceArenaPool(GpuDeviceInfo device, SubmissionScheduler scheduler, Func<HostMemoryAvailability>? readMemory = null)
    {
        _device = device;
        _scheduler = scheduler;
        _readMemory = readMemory ?? HostDiagnostics.GcFootprint.ReadMemory;
    }

    public int ChunkCount
    {
        get
        {
            lock (_all)
            {
                return _all.Count;
            }
        }
    }

    public SliceArenaChunk Rent(ulong minimumBytes)
    {
        if (minimumBytes <= ChunkBytes && _free.TryTake(out var chunk))
        {
            return chunk;
        }
        if (minimumBytes <= ChunkBytes)
        {
            // A sweep briefly owns the free chunks it examines. Recheck before growing the pool.
            lock (_all)
                if (_free.TryTake(out chunk)) return chunk;
        }

        var size = Math.Max(ChunkBytes, (minimumBytes + ChunkBytes - 1) / ChunkBytes * ChunkBytes);
        chunk = new SliceArenaChunk(new GpuBuffer(_device, _scheduler, GpuBufferUsage.Upload, 0, GpuBuffer.AddressedFlags, size));
        lock (_all)
        {
            _all.Add(chunk);
            if (size == ChunkBytes) Volatile.Write(ref _lastGrowth, Environment.TickCount64);
        }

        return chunk;
    }

    public void Return(SliceArenaChunk chunk) => Return(chunk, Environment.TickCount64);

    internal void Return(SliceArenaChunk chunk, long returnedAt)
    {
        chunk.Offset = 0;
        if (chunk.Buffer.Size == ChunkBytes)
        {
            chunk.ReturnedAt = returnedAt;
            _free.Add(chunk);
            return;
        }

        lock (_all)
        {
            _all.Remove(chunk);
        }

        chunk.Buffer.Dispose();
    }

    // Only returned chunks are eligible: recorded slices return theirs on GPU completion.
    // Give bursts time to settle before surrendering at most two chunks in a render iteration.
    internal void TrimUnderPressure(long now)
    {
        if (now < _nextPressureCheck) return;
        _nextPressureCheck = now + 1000;
        const int spareChunks = 8;
        if (now - Volatile.Read(ref _lastGrowth) < 5000 || _free.Count <= spareChunks) return;

        var memory = _readMemory();
        var low = Math.Clamp(memory.TotalPhysicalBytes / 16, 512L << 20, 2L << 30);
        if (memory.TotalPhysicalBytes <= 0 || memory.AvailablePhysicalBytes < 0 || memory.AvailablePhysicalBytes > low)
            return;
        for (var i = 0; i < 2 && _free.Count > spareChunks && _free.TryTake(out var chunk); i++)
        {
            lock (_all) _all.Remove(chunk);
            chunk.Buffer.Dispose();
        }
    }

    internal void TrimIdle(long now)
    {
        if (now < _nextTrim) return;
        _nextTrim = now + VulkanHostBufferPool.SpillIdleMilliseconds;
        HashSet<SliceArenaChunk>? expired = null;
        lock (_all)
        {
            var count = _free.Count;
            if (count < 2) return;
            var keep = new List<SliceArenaChunk>(count);
            for (var i = 0; i < count && _free.TryTake(out var chunk); i++)
            {
                // Keep one spare. TryTake transfers ownership, just as it does for a renter.
                if (i == 0 || now - chunk.ReturnedAt < VulkanHostBufferPool.SpillIdleMilliseconds)
                    keep.Add(chunk);
                else
                    (expired ??= []).Add(chunk);
            }
            foreach (var chunk in keep) _free.Add(chunk);
            if (expired is null) return;
            _all.RemoveAll(expired.Contains);
        }
        foreach (var chunk in expired) chunk.Buffer.Dispose();
    }

    public void Dispose()
    {
        lock (_all)
        {
            foreach (var chunk in _all)
            {
                chunk.Buffer.Dispose();
            }

            _all.Clear();
        }

        while (_free.TryTake(out _))
        {
        }
    }
}

// Host-visible bytes one recording thread fills for one slice; the joiner retires the chunks with
// the slice's submission.
internal sealed class SliceArena(SliceArenaPool pool, ulong nonCoherentAtomSize)
{
    private readonly List<SliceArenaChunk> _chunks = new();
    private SliceArenaChunk? _current;

    private (byte[]? Bytes, SliceArenaReservation Upload)[]? _tables;

    public bool TryUploadTable(ReadOnlySpan<byte> data, ulong alignment, out SliceArenaReservation result)
    {
        var cache = data.Length is > 0 and <= 2048;
        var slot = 0;
        if (cache)
        {
            _tables ??= new (byte[]?, SliceArenaReservation)[16];
            slot = (int)(XxHash3.HashToUInt64(data) & 15);
            var (bytes, upload) = _tables[slot];
            if (upload.Size == (ulong)data.Length && upload.Offset % Math.Max(1UL, alignment) == 0 &&
                data.SequenceEqual(bytes.AsSpan(0, data.Length)))
            {
                result = upload;
                return true;
            }
        }
        if (!TryReserve((ulong)data.Length, alignment, out result)) return false;
        data.CopyTo(result.Destination);
        Commit(in result);
        if (cache)
        {
            ref var entry = ref _tables![slot];
            if (entry.Bytes is null || entry.Bytes.Length < data.Length)
                entry.Bytes = GC.AllocateUninitializedArray<byte>(data.Length);
            data.CopyTo(entry.Bytes);
            entry.Upload = result;
        }
        return true;
    }

    public bool TryReserve(ulong size, ulong alignment, out SliceArenaReservation reservation)
    {
        reservation = default;
        if (size == 0 || size > int.MaxValue)
        {
            return false;
        }

        alignment = Math.Max(Math.Max(alignment, 1), nonCoherentAtomSize);
        var reserved = (size + nonCoherentAtomSize - 1) / nonCoherentAtomSize * nonCoherentAtomSize;
        if (_current is { } current)
        {
            var aligned = (current.Offset + alignment - 1) / alignment * alignment;
            if (aligned + reserved <= current.Buffer.Size)
            {
                current.Offset = aligned + reserved;
                reservation = new SliceArenaReservation(current, aligned, size);
                return true;
            }
        }

        var chunk = pool.Rent(reserved + alignment);
        _chunks.Add(chunk);
        _current = chunk;
        chunk.Offset = reserved;
        reservation = new SliceArenaReservation(chunk, 0, size);
        return true;
    }

    public void Commit(in SliceArenaReservation reservation)
    {
        if (!reservation.Chunk.Buffer.IsCoherent)
        {
            var size = (reservation.Size + nonCoherentAtomSize - 1) / nonCoherentAtomSize * nonCoherentAtomSize;
            reservation.Chunk.Buffer.Flush(reservation.Offset, Math.Min(size, reservation.Chunk.Buffer.Size - reservation.Offset));
        }
    }

    public bool TryUpload(ReadOnlySpan<byte> data, ulong alignment, out VkBuffer buffer, out ulong offset)
    {
        if (!TryReserve((ulong)data.Length, alignment, out var reservation))
        {
            buffer = default;
            offset = 0;
            return false;
        }

        data.CopyTo(reservation.Destination);
        Commit(in reservation);
        buffer = reservation.Buffer;
        offset = reservation.Offset;
        return true;
    }

    public List<SliceArenaChunk> TakeChunks()
    {
        var taken = new List<SliceArenaChunk>(_chunks);
        _chunks.Clear();
        if (_tables is not null)
            foreach (ref var entry in _tables.AsSpan()) entry.Upload = default;
        _current = null;
        return taken;
    }
}
