// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Buffers;

public sealed class WriteTickMap
{
    private const int ChunkShift = 22;
    private const int GranuleShift = 12;
    private const ulong ChunkBytes = 1UL << ChunkShift;
    private const int GranulesPerChunk = 1 << (ChunkShift - GranuleShift);

    private sealed class Chunk
    {
        public ulong UniformTick;
        public ulong MaxTick;
        public ulong[]? Granules;
    }

    private Chunk?[] _chunks = [];
    private int _count;
    private ulong _floor;

    public int Count => _count;

    public ulong Floor => _floor;

    public void Assign(ulong begin, ulong size, ulong tick)
    {
        if (size == 0)
        {
            return;
        }

        var end = begin + size;
        var lastChunk = (end - 1) >> ChunkShift;
        if (lastChunk >= (ulong)_chunks.Length)
        {
            Array.Resize(ref _chunks, (int)Math.Max(lastChunk + 1, (ulong)_chunks.Length * 2));
        }

        for (var index = begin >> ChunkShift; index <= lastChunk; index++)
        {
            var chunk = _chunks[index];
            if (chunk == null)
            {
                chunk = new Chunk();
                _chunks[index] = chunk;
                _count++;
            }

            var chunkBegin = index << ChunkShift;
            var from = Math.Max(begin, chunkBegin) - chunkBegin;
            var to = Math.Min(end, chunkBegin + ChunkBytes) - chunkBegin;
            if (from == 0 && to == ChunkBytes)
            {
                chunk.UniformTick = Math.Max(chunk.UniformTick, tick);
                chunk.MaxTick = Math.Max(chunk.MaxTick, tick);
            }
            else if (tick > chunk.UniformTick)
            {
                AssignGranules(chunk, (int)(from >> GranuleShift), (int)((to - 1) >> GranuleShift) + 1, tick);
            }
        }
    }

    public ulong MaxTick(ulong begin, ulong size)
    {
        var tick = _floor;
        if (size == 0)
        {
            return tick;
        }

        var end = begin + size;
        var lastChunk = (end - 1) >> ChunkShift;
        for (var index = begin >> ChunkShift; index <= lastChunk && index < (ulong)_chunks.Length; index++)
        {
            var chunk = _chunks[index];
            if (chunk == null || chunk.MaxTick <= tick)
            {
                continue;
            }

            var chunkBegin = index << ChunkShift;
            var from = Math.Max(begin, chunkBegin) - chunkBegin;
            var to = Math.Min(end, chunkBegin + ChunkBytes) - chunkBegin;
            if ((from == 0 && to == ChunkBytes) || chunk.Granules is not { } granules)
            {
                tick = Math.Max(tick, from == 0 && to == ChunkBytes ? chunk.MaxTick : chunk.UniformTick);
                continue;
            }

            tick = Math.Max(tick, chunk.UniformTick);
            var last = (int)((to - 1) >> GranuleShift);
            for (var granule = (int)(from >> GranuleShift); granule <= last && tick < chunk.MaxTick; granule++)
            {
                tick = Math.Max(tick, granules[granule]);
            }
        }

        return tick;
    }

    public void Prune(ulong below)
    {
        for (var index = 0; index < _chunks.Length; index++)
        {
            if (_chunks[index] is { } chunk && chunk.MaxTick < below)
            {
                _floor = Math.Max(_floor, chunk.MaxTick);
                _chunks[index] = null;
                _count--;
            }
        }
    }

    private static void AssignGranules(Chunk chunk, int first, int end, ulong tick)
    {
        var granules = (chunk.Granules ??= new ulong[GranulesPerChunk]).AsSpan(first, end - first);
        if (tick >= chunk.MaxTick)
        {
            granules.Fill(tick);
            chunk.MaxTick = tick;
            return;
        }

        foreach (ref var granule in granules)
        {
            granule = Math.Max(granule, tick);
        }
    }
}
