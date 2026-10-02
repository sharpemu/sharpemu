// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Buffers;

// Device-address ranges of the live buffers and of the most recently destroyed ones, so a
// device fault report can name the buffer a faulting address belongs (or belonged) to.
internal static class GpuBufferAddressBook
{
    private readonly record struct Entry(ulong DeviceAddress, ulong Size, GpuBufferUsage Usage, ulong CpuAddress, long Serial, ulong CreatedTick, ulong DestroyedTick);

    // A Yōtei transition retires more than 256 buffers before an old GPU address
    // faults. Keep a bounded history large enough to identify those older ranges.
    private const int DestroyedCapacity = 16384;

    private static readonly object Gate = new();
    private static readonly Dictionary<GpuBuffer, Entry> Live = new(ReferenceEqualityComparer.Instance);
    private static readonly Entry[] Destroyed = new Entry[DestroyedCapacity];
    private static int _destroyedNext;
    private static long _serial;

    public static void Added(GpuBuffer buffer, ulong deviceAddress, ulong tick)
    {
        lock (Gate)
        {
            Live[buffer] = new Entry(deviceAddress, buffer.Size, buffer.Usage, buffer.CpuAddress, ++_serial, tick, 0);
        }
    }

    public static void Removed(GpuBuffer buffer, ulong tick)
    {
        lock (Gate)
        {
            if (Live.Remove(buffer, out var entry))
            {
                Destroyed[_destroyedNext] = entry with { DestroyedTick = tick };
                _destroyedNext = (_destroyedNext + 1) % DestroyedCapacity;
            }
        }
    }

    // The live and destroyed buffers containing the address, else the live ones nearest to it.
    public static string Describe(ulong address)
    {
        lock (Gate)
        {
            var text = new System.Text.StringBuilder();
            foreach (var entry in Live.Values.Where(entry => Contains(entry, address)))
            {
                text.Append($" live:{Format(entry, address)}");
            }

            foreach (var entry in Destroyed.Where(entry => entry.Size != 0 && Contains(entry, address)))
            {
                text.Append($" destroyed:{Format(entry, address)}");
            }

            if (text.Length == 0)
            {
                var below = Live.Values.Where(entry => entry.DeviceAddress <= address)
                    .OrderByDescending(entry => entry.DeviceAddress).Take(1);
                var above = Live.Values.Where(entry => entry.DeviceAddress > address)
                    .OrderBy(entry => entry.DeviceAddress).Take(1);
                foreach (var entry in below.Concat(above))
                {
                    text.Append($" nearest:{Format(entry, address)}");
                }

                var lowest = Live.Values.Select(entry => entry.DeviceAddress).DefaultIfEmpty().Min();
                text.Append($" lowest_live=0x{lowest:X} live_count={Live.Count}");
            }

            return text.ToString();
        }
    }

    private static bool Contains(Entry entry, ulong address) =>
        address >= entry.DeviceAddress && address - entry.DeviceAddress < entry.Size;

    private static string Format(Entry entry, ulong address) =>
        $"[{entry.Usage} device=0x{entry.DeviceAddress:X}+0x{entry.Size:X} cpu=0x{entry.CpuAddress:X} " +
        $"offset={(long)(address - entry.DeviceAddress)} serial={entry.Serial} created_tick={entry.CreatedTick} destroyed_tick={entry.DestroyedTick}]";
}
