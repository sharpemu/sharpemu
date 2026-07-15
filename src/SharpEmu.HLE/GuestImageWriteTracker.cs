// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using System.Runtime.InteropServices;

namespace SharpEmu.HLE;

/// <summary>
/// Detects guest CPU writes into memory that backs a host GPU image. On PS5
/// render targets alias unified memory, so games freely mix CPU writes and GPU
/// draws on the same surface (Chowdren titles memset their fog layers every
/// frame). Host GPU images are separate storage, so the video backend needs to
/// know when the guest CPU rewrote a surface to re-upload it. Ranges are
/// write-protected; the first write faults, the fault handler restores write
/// access and marks the range dirty, and the video backend consumes the dirty
/// flag once per flip and re-arms protection after re-uploading.
/// </summary>
public static unsafe class GuestImageWriteTracker
{
    private const int ProtRead = 0x1;
    private const int ProtWrite = 0x2;
    private const int ClockMonotonicRaw = 4;

    private sealed class TrackedRange
    {
        public ulong Address;
        public ulong ByteCount;
        public ulong Start;
        public ulong End;
        public int Dirty;
        public int Armed;
        public int FirstCpuWriteSeen;
        public int PendingFirstCpuWrite;
        public bool TraceLifetime;
        public long SourceSequence;
        public long FirstCpuWriteTraceSequence;
        public long FirstCpuWriteTimestampNanoseconds;
        public ulong FirstCpuWriteAddress;
        public ulong FirstCpuWritePage;
        public string Source = "unspecified";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Timespec
    {
        public long Seconds;
        public long Nanoseconds;
    }

    private static readonly object _gate = new();
    private static readonly Dictionary<ulong, TrackedRange> _rangesByAddress = new();

    // Snapshot array read lock-free from the signal handler; rebuilt on every
    // mutation under the gate. Signal handlers must not take managed locks.
    private static TrackedRange[] _rangeSnapshot = [];

    private static readonly bool _enabled = !OperatingSystem.IsWindows() &&
        Environment.GetEnvironmentVariable("SHARPEMU_GUEST_IMAGE_CPU_SYNC") != "0";
    private static readonly (bool Wildcard, ulong[] Addresses) _lifetimeTraceFilter =
        ParseAddressList(Environment.GetEnvironmentVariable("SHARPEMU_TRACE_GUEST_IMAGE_ADDRS"));
    private static readonly (bool Wildcard, string[] Sources) _lifetimeSourceTraceFilter =
        ParseSourceList(Environment.GetEnvironmentVariable("SHARPEMU_TRACE_GUEST_MEMORY_LIFETIME"));
    private static readonly bool _lifetimeTraceEnabled =
        _lifetimeTraceFilter.Wildcard ||
        _lifetimeTraceFilter.Addresses.Length != 0 ||
        _lifetimeSourceTraceFilter.Wildcard ||
        _lifetimeSourceTraceFilter.Sources.Length != 0;
    private static readonly long _lifetimeTraceEpochNanoseconds =
        _enabled && _lifetimeTraceEnabled ? GetMonotonicNanoseconds() : 0;
    private static long _lifetimeTraceSequence;

    [DllImport("libc", EntryPoint = "mprotect", SetLastError = true)]
    private static extern int Mprotect(nint address, nuint length, int protection);

    [DllImport("libc", EntryPoint = "clock_gettime", SetLastError = false)]
    private static extern int ClockGetTime(int clockId, Timespec* time);

    public static bool Enabled => _enabled;

    /// <summary>
    /// Exercises the fault-handling path once outside signal context so every
    /// branch is JIT-compiled (and, under Rosetta 2, translated) before a real
    /// fault arrives — a cold signal path is silently never entered there.
    /// </summary>
    public static void WarmUp()
    {
        if (!_enabled)
        {
            return;
        }

        var scratch = NativeMemory.AllocZeroed(4096);
        try
        {
            // Warm the timestamp P/Invoke used by the signal-safe scalar
            // capture path before a real protected-page write reaches it.
            _ = GetMonotonicNanoseconds();
            var address = (ulong)scratch;
            Track(address, 4096);
            _ = TryHandleWriteFault(address);
            _ = ConsumeDirty(address);
            Untrack(address);
        }
        finally
        {
            NativeMemory.Free(scratch);
        }
    }

    /// <summary>Registers a range and arms write protection on it.</summary>
    public static void Track(
        ulong address,
        ulong byteCount,
        long sourceSequence = 0,
        string source = "unspecified")
    {
        if (!_enabled || address == 0 || byteCount == 0)
        {
            return;
        }

        var (start, length) = PageAlign(address, byteCount);
        lock (_gate)
        {
            _rangesByAddress.TryGetValue(address, out var range);
            if (range is not null &&
                (range.Start != start ||
                 range.End != start + length ||
                 range.ByteCount != byteCount))
            {
                // Never resize an object that is still reachable from the
                // signal handler's lock-free snapshot. Retire it and publish
                // a fresh immutable range.
                DisarmLocked(range, "replace-range");
                _rangesByAddress.Remove(address);
                range = null;
            }

            if (range is null)
            {
                range = new TrackedRange
                {
                    Address = address,
                    ByteCount = byteCount,
                    Start = start,
                    End = start + length,
                    TraceLifetime =
                        ShouldTraceRange(start, start + length) || ShouldTraceSource(source),
                    SourceSequence = sourceSequence,
                    Source = source,
                };
                _rangesByAddress[address] = range;
                RebuildSnapshotLocked();
            }
            else
            {
                FlushPendingFirstCpuWrite(range);
            }

            range.SourceSequence = sourceSequence;
            range.Source = source;
            range.TraceLifetime =
                ShouldTraceRange(range.Start, range.End) || ShouldTraceSource(source);
            ArmLocked(range, "arm");
        }
    }

    public static void Untrack(ulong address)
    {
        if (!_enabled)
        {
            return;
        }

        lock (_gate)
        {
            if (_rangesByAddress.TryGetValue(address, out var range))
            {
                DisarmLocked(range, "untrack");
                _rangesByAddress.Remove(address);
                RebuildSnapshotLocked();
            }
        }
    }

    /// <summary>
    /// Returns true when the guest CPU wrote the range since the last call,
    /// clearing the flag. The caller re-arms via <see cref="Rearm"/> after it
    /// finished reading the guest bytes.
    /// </summary>
    public static bool ConsumeDirty(ulong address)
    {
        if (!_enabled)
        {
            return false;
        }

        lock (_gate)
        {
            if (!_rangesByAddress.TryGetValue(address, out var range))
            {
                return false;
            }

            FlushPendingFirstCpuWrite(range);
            return Interlocked.Exchange(ref range.Dirty, 0) != 0;
        }
    }

    /// <summary>
    /// Non-consuming variant of <see cref="ConsumeDirty"/>: reports whether
    /// the range has been written since it was last re-armed, leaving the
    /// flag for the owner that evicts and re-uploads.
    /// </summary>
    public static bool PeekDirty(ulong address)
    {
        if (!_enabled)
        {
            return false;
        }

        lock (_gate)
        {
            if (!_rangesByAddress.TryGetValue(address, out var range))
            {
                return false;
            }

            FlushPendingFirstCpuWrite(range);
            return Volatile.Read(ref range.Dirty) != 0;
        }
    }

    public static void Rearm(ulong address)
    {
        if (!_enabled)
        {
            return;
        }

        lock (_gate)
        {
            if (_rangesByAddress.TryGetValue(address, out var range))
            {
                ArmLocked(range, "rearm");
            }
        }
    }

    /// <summary>
    /// Prepares pages touched by a managed HLE memory write. Native guest
    /// stores fault and enter <see cref="TryHandleWriteFault"/> through the
    /// POSIX signal bridge, but a managed Buffer.MemoryCopy into a protected
    /// page is surfaced by the runtime as a fatal AccessViolation instead of
    /// a resumable guest fault. Visit every page in the write span up front so
    /// all overlapping texture owners are dirtied and made writable.
    /// </summary>
    public static void NotifyManagedWrite(ulong address, ulong byteCount)
    {
        if (!_enabled || address == 0 || byteCount == 0)
        {
            return;
        }

        var end = address > ulong.MaxValue - byteCount
            ? ulong.MaxValue
            : address + byteCount;
        var candidate = address;
        while (candidate < end)
        {
            _ = TryHandleWriteFault(candidate);
            var nextPage = (candidate & ~0xFFFUL) + 0x1000UL;
            if (nextPage <= candidate)
            {
                break;
            }
            candidate = nextPage;
        }
    }

    /// <summary>
    /// Flushes scalar first-write records captured by the POSIX signal handler.
    /// Call only from ordinary managed execution, never from signal context.
    /// </summary>
    public static void FlushPendingDiagnostics()
    {
        if (!_enabled || !_lifetimeTraceEnabled)
        {
            return;
        }

        lock (_gate)
        {
            foreach (var range in _rangesByAddress.Values)
            {
                FlushPendingFirstCpuWrite(range);
            }
        }
    }

    /// <summary>
    /// Signal-handler entry: if the fault address lies in a tracked, armed
    /// range, restore write access, mark the range dirty, and return true so
    /// the faulting write can be retried. Must not allocate or lock.
    /// </summary>
    public static bool TryHandleWriteFault(ulong faultAddress)
    {
        if (!_enabled || faultAddress == 0)
        {
            return false;
        }

        var ranges = Volatile.Read(ref _rangeSnapshot);
        var writableStart = ulong.MaxValue;
        var writableEnd = 0UL;
        for (var index = 0; index < ranges.Length; index++)
        {
            var range = ranges[index];
            if (faultAddress < range.Start || faultAddress >= range.End)
            {
                continue;
            }

            writableStart = Math.Min(writableStart, range.Start);
            writableEnd = Math.Max(writableEnd, range.End);
        }

        if (writableStart == ulong.MaxValue)
        {
            return false;
        }

        // Ranges are page-aligned and may overlap (font atlases and other
        // suballocations commonly share pages). Unprotecting one range also
        // makes every overlapping tracked page writable. Expand to the full
        // transitive overlap and dirty/disarm every owner, otherwise only the
        // first dictionary entry observes the write and the others retain a
        // stale cached texture indefinitely.
        var expanded = true;
        while (expanded)
        {
            expanded = false;
            for (var index = 0; index < ranges.Length; index++)
            {
                var range = ranges[index];
                if (range.Start >= writableEnd || range.End <= writableStart)
                {
                    continue;
                }

                var start = Math.Min(writableStart, range.Start);
                var end = Math.Max(writableEnd, range.End);
                if (start != writableStart || end != writableEnd)
                {
                    writableStart = start;
                    writableEnd = end;
                    expanded = true;
                }
            }
        }

        var needsUnprotect = false;
        for (var index = 0; index < ranges.Length; index++)
        {
            var range = ranges[index];
            if (range.Start < writableEnd && range.End > writableStart &&
                Volatile.Read(ref range.Armed) != 0)
            {
                needsUnprotect = true;
                break;
            }
        }

        if (needsUnprotect &&
            Mprotect(
                (nint)writableStart,
                (nuint)(writableEnd - writableStart),
                ProtRead | ProtWrite) != 0)
        {
            return false;
        }

        for (var index = 0; index < ranges.Length; index++)
        {
            var range = ranges[index];
            if (range.Start >= writableEnd || range.End <= writableStart)
            {
                continue;
            }

            var wasArmed = Interlocked.Exchange(ref range.Armed, 0) != 0;
            if (wasArmed &&
                range.TraceLifetime &&
                Interlocked.CompareExchange(ref range.FirstCpuWriteSeen, 1, 0) == 0)
            {
                // Signal context: capture preallocated scalar fields only.
                // Formatting and I/O are deferred to a locked safe path.
                range.FirstCpuWriteTraceSequence =
                    Interlocked.Increment(ref _lifetimeTraceSequence);
                range.FirstCpuWriteTimestampNanoseconds = GetMonotonicNanoseconds();
                range.FirstCpuWriteAddress = faultAddress;
                range.FirstCpuWritePage = faultAddress & ~0xFFFUL;
                Volatile.Write(ref range.PendingFirstCpuWrite, 1);
                Volatile.Write(ref range.FirstCpuWriteSeen, 2);
            }

            Volatile.Write(ref range.Dirty, 1);
        }

        return true;
    }

    private static void ArmLocked(TrackedRange range, string operation)
    {
        FlushPendingFirstCpuWrite(range);
        if (Interlocked.Exchange(ref range.Armed, 1) == 1)
        {
            return;
        }

        // A new publication/rearm starts a new first-write lifetime.
        Volatile.Write(ref range.FirstCpuWriteSeen, 0);
        var failed = Mprotect(
            (nint)range.Start,
            (nuint)(range.End - range.Start),
            ProtRead) != 0;
        if (failed)
        {
            Volatile.Write(ref range.Armed, 0);
        }

        if (range.TraceLifetime)
        {
            TraceLifetime(
                range,
                failed ? $"{operation}-failed-errno-{Marshal.GetLastPInvokeError()}" : operation);
        }
    }

    private static void DisarmLocked(TrackedRange range, string operation)
    {
        FlushPendingFirstCpuWrite(range);
        var wasArmed = Interlocked.Exchange(ref range.Armed, 0) == 1;
        if (wasArmed)
        {
            _ = Mprotect(
                (nint)range.Start,
                (nuint)(range.End - range.Start),
                ProtRead | ProtWrite);
        }

        if (range.TraceLifetime)
        {
            TraceLifetime(range, wasArmed ? operation : $"{operation}-already-disarmed");
        }
    }

    private static void RebuildSnapshotLocked()
    {
        _rangeSnapshot = _rangesByAddress.Values.ToArray();
    }

    private static (ulong Start, ulong Length) PageAlign(ulong address, ulong byteCount)
    {
        const ulong pageMask = 0xFFFUL;
        var start = address & ~pageMask;
        var end = (address + byteCount + pageMask) & ~pageMask;
        return (start, end - start);
    }

    private static bool ShouldTraceRange(ulong start, ulong end)
    {
        if (_lifetimeTraceFilter.Wildcard)
        {
            return true;
        }

        var addresses = _lifetimeTraceFilter.Addresses;
        for (var index = 0; index < addresses.Length; index++)
        {
            if (addresses[index] >= start && addresses[index] < end)
            {
                return true;
            }
        }

        return false;
    }

    private static (bool Wildcard, ulong[] Addresses) ParseAddressList(string? addresses)
    {
        if (string.IsNullOrWhiteSpace(addresses))
        {
            return (false, []);
        }

        var parsedAddresses = new List<ulong>();
        foreach (var token in addresses.Split(
                     [',', ';', ' ', '\t'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token == "*")
            {
                return (true, []);
            }

            var span = token.AsSpan();
            if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                span = span[2..];
            }

            if (ulong.TryParse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed))
            {
                parsedAddresses.Add(parsed);
            }
        }

        return (false, parsedAddresses.ToArray());
    }

    private static bool ShouldTraceSource(string source)
    {
        if (_lifetimeSourceTraceFilter.Wildcard)
        {
            return true;
        }

        return Array.IndexOf(_lifetimeSourceTraceFilter.Sources, source) >= 0;
    }

    private static (bool Wildcard, string[] Sources) ParseSourceList(string? sources)
    {
        if (string.IsNullOrWhiteSpace(sources))
        {
            return (false, []);
        }

        var parsedSources = new List<string>();
        foreach (var token in sources.Split(
                     [',', ';'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token == "*")
            {
                return (true, []);
            }

            parsedSources.Add(token);
        }

        return (false, parsedSources.ToArray());
    }

    private static void FlushPendingFirstCpuWrite(TrackedRange range)
    {
        var spin = new SpinWait();
        while (Volatile.Read(ref range.FirstCpuWriteSeen) == 1)
        {
            spin.SpinOnce();
        }

        if (!range.TraceLifetime || Interlocked.Exchange(ref range.PendingFirstCpuWrite, 0) == 0)
        {
            return;
        }

        TraceLifetime(
            range,
            "first-cpu-write-disarm",
            range.FirstCpuWriteAddress,
            range.FirstCpuWritePage,
            range.FirstCpuWriteTraceSequence,
            range.FirstCpuWriteTimestampNanoseconds);
    }

    private static void TraceLifetime(
        TrackedRange range,
        string operation,
        ulong faultAddress = 0,
        ulong faultPage = 0,
        long traceSequence = 0,
        long timestampNanoseconds = 0)
    {
        if (traceSequence == 0)
        {
            traceSequence = Interlocked.Increment(ref _lifetimeTraceSequence);
        }

        if (timestampNanoseconds == 0)
        {
            timestampNanoseconds = GetMonotonicNanoseconds();
        }

        var elapsedMilliseconds =
            (timestampNanoseconds - _lifetimeTraceEpochNanoseconds) / 1_000_000.0;
        Console.Error.WriteLine(
            $"[WT][LIFETIME] seq={traceSequence} t_ms={elapsedMilliseconds:F3} " +
            $"event={operation} source_seq={range.SourceSequence} source='{range.Source}' " +
            $"requested=0x{range.Address:X16}+0x{range.ByteCount:X} " +
            $"range=0x{range.Start:X16}..0x{range.End:X16} " +
            $"fault=0x{faultAddress:X16} page=0x{faultPage:X16}");
    }

    private static long GetMonotonicNanoseconds()
    {
        Timespec time;
        return ClockGetTime(ClockMonotonicRaw, &time) == 0
            ? unchecked((time.Seconds * 1_000_000_000L) + time.Nanoseconds)
            : 0;
    }
}
