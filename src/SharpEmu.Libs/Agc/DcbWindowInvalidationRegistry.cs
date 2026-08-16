// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;

namespace SharpEmu.Libs.Agc;

/// <summary>
/// Invalidates parser-side command-buffer snapshots when another thread writes
/// into their guest ranges.
/// </summary>
internal static class DcbWindowInvalidationRegistry
{
    internal sealed class Lease
    {
        internal required long Id { get; init; }

        internal required ulong Start { get; init; }

        internal required ulong End { get; init; }

        internal int Invalidated;
    }

    private static readonly ConcurrentDictionary<long, Lease> _leases = new();
    private static long _nextId;

    internal static Lease Register(ulong start, ulong length)
    {
        var lease = new Lease
        {
            Id = Interlocked.Increment(ref _nextId),
            Start = start,
            End = SaturatingAdd(start, length),
        };
        _leases[lease.Id] = lease;
        return lease;
    }

    internal static bool IsValid(Lease? lease) =>
        lease is not null && Volatile.Read(ref lease.Invalidated) == 0;

    internal static void Unregister(Lease? lease)
    {
        if (lease is not null)
        {
            _leases.TryRemove(lease.Id, out _);
        }
    }

    internal static int Invalidate(ulong start, ulong length)
    {
        if (length == 0)
        {
            return 0;
        }

        var end = SaturatingAdd(start, length);
        var invalidated = 0;
        foreach (var lease in _leases.Values)
        {
            if (start >= lease.End || end <= lease.Start ||
                Interlocked.Exchange(ref lease.Invalidated, 1) != 0)
            {
                continue;
            }

            invalidated++;
        }

        return invalidated;
    }

    internal static void ClearForTests()
    {
        _leases.Clear();
        Interlocked.Exchange(ref _nextId, 0);
    }

    private static ulong SaturatingAdd(ulong value, ulong addend) =>
        ulong.MaxValue - value < addend ? ulong.MaxValue : value + addend;
}
