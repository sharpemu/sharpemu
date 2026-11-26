// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.IO.Hashing;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Scheduling;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace SharpEmu.Libs.VideoOut;

internal sealed class SliceArenaChunk(GpuBuffer buffer)
{
    public readonly GpuBuffer Buffer = buffer;
    public ulong Offset;
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

    public SliceArenaPool(GpuDeviceInfo device, SubmissionScheduler scheduler)
    {
        _device = device;
        _scheduler = scheduler;
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

        var size = Math.Max(ChunkBytes, (minimumBytes + ChunkBytes - 1) / ChunkBytes * ChunkBytes);
        chunk = new SliceArenaChunk(new GpuBuffer(_device, _scheduler, GpuBufferUsage.Upload, 0, GpuBuffer.AddressedFlags, size));
        lock (_all)
        {
            _all.Add(chunk);
        }

        return chunk;
    }

    public void Return(SliceArenaChunk chunk)
    {
        chunk.Offset = 0;
        if (chunk.Buffer.Size == ChunkBytes)
        {
            _free.Add(chunk);
            return;
        }

        lock (_all)
        {
            _all.Remove(chunk);
        }

        chunk.Buffer.Dispose();
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
