// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.Diagnostics;

namespace SharpEmu.Libs.Gpu.Buffers;

internal sealed class StreamCopyShares
{
    private const int Slots = 1024;

    private readonly record struct Entry(ulong Address, ulong Size, long Observation, ulong Tick, long Publications, ulong Offset);

    private readonly Entry[] _entries = new Entry[Slots];

    public static readonly ThreadCounter Copies = new();
    public static readonly ThreadCounter Shared = new();

    public bool TryShare(ulong address, ulong size, long observation, ulong tick, long publications, out ulong offset)
    {
        ref readonly var entry = ref _entries[SlotOf(address, size)];
        if (entry.Size != 0 && entry.Address == address && entry.Size == size && entry.Observation == observation && entry.Tick == tick &&
            entry.Publications == publications)
        {
            offset = entry.Offset;
            Shared.Increment();
            return true;
        }

        offset = 0;
        return false;
    }

    public void Remember(ulong address, ulong size, long observation, ulong tick, long publications, ulong offset)
    {
        _entries[SlotOf(address, size)] = new Entry(address, size, observation, tick, publications, offset);
        Copies.Increment();
    }

    public void Forget(ulong address, ulong size) => _entries[SlotOf(address, size)] = default;

    private static int SlotOf(ulong address, ulong size) =>
        (int)(((address >> 4) ^ (address >> 17) ^ (size * 0x9E3779B1UL)) & (Slots - 1));
}
