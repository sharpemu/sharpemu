// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.IO.Hashing;
using System.Runtime.InteropServices;
using SharpEmu.HLE.Diagnostics;
using SharpEmu.Libs.Gpu.Scheduling;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace SharpEmu.Libs.Gpu.Buffers;

public sealed class ContentUploadArena : IDisposable
{
    private const ulong ArenaBytes = 32UL * 1024 * 1024;
    private const int MaxContentBytes = 64 * 1024;

    private sealed class Arena(GpuBuffer buffer)
    {
        public readonly GpuBuffer Buffer = buffer;
        public readonly byte[] Mirror = GC.AllocateUninitializedArray<byte>((int)ArenaBytes);
        public readonly Dictionary<ulong, (ulong Offset, int Size)> Entries = new();
        public readonly Dictionary<uint[], (ulong Offset, int Size)> IdentityEntries = new(ReferenceEqualityComparer.Instance);
        public ulong Offset;
        public ulong LastUseTick;
    }

    private readonly SubmissionScheduler _scheduler;
    private readonly Arena[] _arenas;
    private int _active;

    private static readonly ThreadCounter _hits = new();
    private static readonly ThreadCounter _identityHits = new();
    private static readonly ThreadCounter _stores = new();
    private static readonly ThreadCounter _recycles = new();
    private static readonly ThreadCounter _refused = new();
    private static readonly ThreadCounter _collisions = new();

    public ContentUploadArena(GpuDeviceInfo device, SubmissionScheduler scheduler)
    {
        _scheduler = scheduler;
        _arenas =
        [
            new Arena(new GpuBuffer(device, scheduler, GpuMemoryPlacement.StreamRing, 0, GpuBuffer.AddressedFlags, ArenaBytes)),
            new Arena(new GpuBuffer(device, scheduler, GpuMemoryPlacement.StreamRing, 0, GpuBuffer.AddressedFlags, ArenaBytes)),
        ];
    }

    public bool TryUpload(uint[] words, ulong alignment, out VkBuffer buffer, out ulong offset)
    {
        buffer = default;
        offset = 0;
        if (words.Length == 0 || words.Length * sizeof(uint) > MaxContentBytes || alignment == 0)
        {
            return false;
        }

        var tick = _scheduler.CurrentTick;
        for (var index = 0; index < _arenas.Length; index++)
        {
            var arena = _arenas[(_active + index) % _arenas.Length];
            if (!arena.IdentityEntries.TryGetValue(words, out var entry))
            {
                continue;
            }

            arena.LastUseTick = tick;
            _identityHits.Increment();
            buffer = arena.Buffer.Handle;
            offset = entry.Offset;
            return true;
        }

        if (!TryStore(MemoryMarshal.AsBytes<uint>(words), alignment, tick, out var stored, out buffer, out offset))
        {
            return false;
        }

        stored.IdentityEntries[words] = (offset, words.Length * sizeof(uint));
        return true;
    }

    public bool TryUpload(ReadOnlySpan<byte> data, ulong alignment, out VkBuffer buffer, out ulong offset)
    {
        buffer = default;
        offset = 0;
        if (data.Length == 0 || data.Length > MaxContentBytes || alignment == 0)
        {
            return false;
        }

        var hash = XxHash3.HashToUInt64(data);
        var tick = _scheduler.CurrentTick;
        for (var index = 0; index < _arenas.Length; index++)
        {
            var arena = _arenas[(_active + index) % _arenas.Length];
            if (!arena.Entries.TryGetValue(hash, out var entry))
            {
                continue;
            }

            if (entry.Size != data.Length || !data.SequenceEqual(arena.Mirror.AsSpan((int)entry.Offset, entry.Size)))
            {
                _collisions.Increment();
                break;
            }

            arena.LastUseTick = tick;
            _hits.Increment();
            buffer = arena.Buffer.Handle;
            offset = entry.Offset;
            return true;
        }

        if (!TryStore(data, alignment, tick, out var stored, out buffer, out offset))
        {
            return false;
        }

        stored.Entries[hash] = (offset, data.Length);
        return true;
    }

    public bool TryUploadAddress(ReadOnlySpan<byte> data, ulong alignment, out ulong address)
    {
        address = 0;
        if (!TryUpload(data, alignment, out var buffer, out var offset))
        {
            return false;
        }

        foreach (var arena in _arenas)
        {
            if (arena.Buffer.Handle.Handle == buffer.Handle)
            {
                address = arena.Buffer.DeviceAddress + offset;
                return true;
            }
        }

        return false;
    }

    private bool TryStore(ReadOnlySpan<byte> data, ulong alignment, ulong tick, out Arena stored, out VkBuffer buffer, out ulong offset)
    {
        var active = _arenas[_active];
        var aligned = (active.Offset + alignment - 1) / alignment * alignment;
        if (aligned + (ulong)data.Length > ArenaBytes)
        {
            var other = _arenas[1 - _active];
            if (other.LastUseTick > _scheduler.CompletedTick)
            {
                _refused.Increment();
                stored = active;
                buffer = default;
                offset = 0;
                return false;
            }

            other.Entries.Clear();
            other.IdentityEntries.Clear();
            other.Offset = 0;
            _active = 1 - _active;
            active = other;
            aligned = 0;
            _recycles.Increment();
        }

        active.Buffer.Write(aligned, data);
        data.CopyTo(active.Mirror.AsSpan((int)aligned));
        active.Offset = aligned + (ulong)data.Length;
        active.LastUseTick = tick;
        _stores.Increment();
        stored = active;
        buffer = active.Buffer.Handle;
        offset = aligned;
        return true;
    }

    public static string TakeReport() =>
        FormattableString.Invariant($"[PERF][CONTENT_UPLOADS] hits={_hits.Take()} identity_hits={_identityHits.Take()} stores={_stores.Take()} recycles={_recycles.Take()} ") +
        FormattableString.Invariant($"refused={_refused.Take()} collisions={_collisions.Take()}");

    public void Dispose()
    {
        foreach (var arena in _arenas)
        {
            arena.Buffer.Dispose();
        }
    }
}
