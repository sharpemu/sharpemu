// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using SharpEmu.HLE.GpuMemory;

namespace SharpEmu.Libs.Gpu.Buffers;

// SHARPEMU_WATCH_BLOCKS=addr:len[,addr:len...] (diagnostic): after every presented frame, logs each
// watched range whose bytes changed in guest memory or in the GPU copy, with both contents, and
// logs every cache event that touches a watched range (device-address write reports, uploads,
// writable bindings, CPU write faults). Ticks date each line against the GPU timeline.
public sealed unsafe partial class GuestBufferCache
{
    private readonly record struct WatchedRange(ulong Address, int Length);

    private static readonly WatchedRange[] WatchedRanges = ParseWatchedRanges(Environment.GetEnvironmentVariable("SHARPEMU_WATCH_BLOCKS"));
    private const int MaxWatchLines = 20000;
    private readonly byte[]?[] _watchedGuest = new byte[WatchedRanges.Length][];
    private readonly byte[]?[] _watchedGpu = new byte[WatchedRanges.Length][];
    private long _watchFrame;
    private static int _watchLines;

    private static WatchedRange[] ParseWatchedRanges(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var ranges = new List<WatchedRange>();
        foreach (var item in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = item.Split(':');
            if (ulong.TryParse(parts[0].Replace("0x", "", StringComparison.OrdinalIgnoreCase), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var address) &&
                address != 0)
            {
                var length = parts.Length > 1 && int.TryParse(parts[1].Replace("0x", "", StringComparison.OrdinalIgnoreCase), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed)
                    ? Math.Clamp(parsed, 4, 256)
                    : 64;
                ranges.Add(new WatchedRange(address, length));
            }
        }

        return [.. ranges];
    }

    private static bool Watches(ulong address, ulong size)
    {
        foreach (var range in WatchedRanges)
        {
            if (address < range.Address + (ulong)range.Length && range.Address < address + size)
            {
                return true;
            }
        }

        return false;
    }

    private void WatchEvent(ulong address, ulong size, string kind)
    {
        if (WatchedRanges.Length == 0 || !Watches(address, size) || Interlocked.Increment(ref _watchLines) > MaxWatchLines)
        {
            return;
        }

        Console.Error.WriteLine(FormattableString.Invariant(
            $"[GPU][WATCH] frame={_watchFrame} event={kind} addr=0x{address:X} size=0x{size:X} submission_tick={_scheduler.CurrentTick} completed_tick={_scheduler.Timeline.CompletedTick}"));
    }

    public void WatchFrame()
    {
        if (WatchedRanges.Length == 0)
        {
            return;
        }

        _watchFrame++;
        for (var index = 0; index < WatchedRanges.Length; index++)
        {
            var range = WatchedRanges[index];
            var guest = new byte[range.Length];
            if (!_backing.TryReadBacking(range.Address, guest))
            {
                guest = null;
            }

            byte[]? gpu = null;
            if (FindOwner(range.Address, (ulong)range.Length) is { MappedPointer: not null, IsCoherent: true } owner)
            {
                gpu = new ReadOnlySpan<byte>(owner.MappedPointer + owner.Offset(range.Address), range.Length).ToArray();
            }

            var guestChanged = !Same(guest, _watchedGuest[index]);
            var gpuChanged = !Same(gpu, _watchedGpu[index]);
            _watchedGuest[index] = guest;
            _watchedGpu[index] = gpu;
            if ((!guestChanged && !gpuChanged) || Interlocked.Increment(ref _watchLines) > MaxWatchLines)
            {
                continue;
            }

            var page = range.Address & ~(TrackerLayout.PageBytes - 1);
            var cpuDirty = _tracker.HasCpuDirtyPages(page, TrackerLayout.PageBytes);
            var gpuDirty = _tracker.HasGpuDirtyPages(page, TrackerLayout.PageBytes);
            var guestHex = guest is null ? "n/a" : Convert.ToHexString(guest);
            var gpuHex = gpu is null ? "n/a" : Convert.ToHexString(gpu);
            Console.Error.WriteLine(FormattableString.Invariant(
                $"[GPU][WATCH] frame={_watchFrame} range=0x{range.Address:X} guest_changed={guestChanged} gpu_changed={gpuChanged} cpu_dirty={cpuDirty} gpu_dirty={gpuDirty} submission_tick={_scheduler.CurrentTick} completed_tick={_scheduler.Timeline.CompletedTick} guest={guestHex} gpu={gpuHex}"));
        }
    }

    private static bool Same(byte[]? left, byte[]? right) =>
        left is null ? right is null : right is not null && left.AsSpan().SequenceEqual(right);
}
