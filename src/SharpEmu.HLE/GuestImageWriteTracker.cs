// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace SharpEmu.HLE;

/// <summary>
/// Detects guest CPU writes into memory that backs a host GPU image. On PS5
/// render targets alias unified memory, so games freely mix CPU writes and GPU
/// draws on the same surface (Chowdren titles memset their fog layers every
/// frame). Host GPU images are separate storage, so the video backend needs to
/// know when the guest CPU rewrote a surface to re-upload it. Ranges are
/// write-protected; the first write faults, the fault handler removes that
/// image's page watchers and marks it dirty, and the video backend adds the
/// watchers again after re-uploading. Shared pages stay protected while any
/// clean image still watches them.
/// </summary>
public static unsafe partial class GuestImageWriteTracker
{
    public readonly record struct ReadSnapshot(
        ulong Address,
        long Generation,
        bool Active);

    private const int ProtRead = 0x1;
    private const int ProtWrite = 0x2;
    private const int ProtExec = 0x4;
    private const int ClockMonotonicRaw = 4;
    private const ulong TrackingPageSize = 0x1000UL;
    private const int RangeDisarmed = 0;
    private const int RangeArmed = 1;
    private const int RangeInvalidating = 2;
    private const int RangeArming = 3;

    private sealed class PageState
    {
        public ulong Address;
        public int WriteWatchers;
        public bool Executable;
        public nint NativeWindowsEntry;
    }

    private sealed class TrackedRange
    {
        public ulong Address;
        public ulong ByteCount;
        public ulong Start;
        public ulong End;
        public int Dirty;
        public int Armed;
        /// <summary>
        /// When false the range is watch-only: managed writes still dirty it via
        /// <see cref="NotifyManagedWrite"/>, but pages are never write-protected
        /// so native CPU stores do not fault.
        /// </summary>
        public bool Protect;
        public bool Executable;
        public PageState[] Pages = [];
        public int FirstCpuWriteSeen;
        public int PendingFirstCpuWrite;
        public long WriteGeneration;
        public bool TraceLifetime;
        public long SourceSequence;
        public long FirstCpuWriteTraceSequence;
        public long FirstCpuWriteTimestampNanoseconds;
        public ulong FirstCpuWriteAddress;
        public ulong FirstCpuWritePage;
        public long ProfileArmCount;
        public long ProfileArmBytes;
        public long ProfileFaultCount;
        public long ProfileFaultBytes;
        public bool ManagedWriter;
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
    private static readonly Dictionary<ulong, TrackedRange> _watchRangesByAddress = new();
    private static readonly Dictionary<ulong, PageState> _pagesByAddress = new();

    /// <summary>Immutable snapshot read lock-free from the signal handler and
    /// the managed-write pre-visit; rebuilt on every mutation under the gate
    /// (signal handlers must not take managed locks). Carrying the overall
    /// bounds inside the same object keeps the hot-path intersection test
    /// consistent with the array it guards.</summary>
    private sealed class RangeSnapshot
    {
        public static readonly RangeSnapshot Empty = new([]);

        public readonly TrackedRange[] Ranges;
        public readonly ulong Start;
        public readonly ulong End;

        public RangeSnapshot(TrackedRange[] ranges)
        {
            Ranges = ranges;
            Start = ulong.MaxValue;
            End = 0;
            foreach (var range in ranges)
            {
                Start = Math.Min(Start, range.Start);
                End = Math.Max(End, range.End);
            }
        }
    }

    private static RangeSnapshot _rangeSnapshot = RangeSnapshot.Empty;

    /// <summary>
    /// Immutable exact-range index for managed writes. Watch-only texture
    /// ranges do not enter the fault snapshot because they must not widen the
    /// native write-fault hot path.
    /// </summary>
    private sealed class WatchRangeSnapshot
    {
        public static readonly WatchRangeSnapshot Empty = new([], []);

        public readonly TrackedRange[] Ranges;
        public readonly ulong[] PrefixMaximumEnds;

        public WatchRangeSnapshot(TrackedRange[] ranges, ulong[] prefixMaximumEnds)
        {
            Ranges = ranges;
            PrefixMaximumEnds = prefixMaximumEnds;
        }
    }

    private static WatchRangeSnapshot _watchRangeSnapshot = WatchRangeSnapshot.Empty;

    private static readonly bool _enabled =
        !string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_GUEST_IMAGE_CPU_SYNC"),
            "0",
            StringComparison.Ordinal);
    private static readonly (bool Wildcard, ulong[] Addresses) _lifetimeTraceFilter =
        ParseAddressList(Environment.GetEnvironmentVariable("SHARPEMU_TRACE_GUEST_IMAGE_ADDRS"));
    private static readonly (bool Wildcard, string[] Sources) _lifetimeSourceTraceFilter =
        ParseSourceList(Environment.GetEnvironmentVariable("SHARPEMU_TRACE_GUEST_MEMORY_LIFETIME"));
    private static readonly bool _lifetimeTraceEnabled =
        _lifetimeTraceFilter.Wildcard ||
        _lifetimeTraceFilter.Addresses.Length != 0 ||
        _lifetimeSourceTraceFilter.Wildcard ||
        _lifetimeSourceTraceFilter.Sources.Length != 0;
    private static readonly bool _profileEnabled =
        string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_GUEST_IMAGE_TRACKER"),
            "1",
            StringComparison.Ordinal);
    private static readonly long _lifetimeTraceEpochNanoseconds =
        _enabled && _lifetimeTraceEnabled ? GetMonotonicNanoseconds() : 0;
    private static long _lifetimeTraceSequence;
    private static long _profileLastReportTimestamp = Stopwatch.GetTimestamp();
    private static long _profileSnapshotCount;
    private static long _profileArmCount;
    private static long _profileArmBytes;
    private static long _profileFaultCount;
    private static long _profileFaultBytes;
    private static long _profileDisarmCount;
    private static long _profileDisarmBytes;

    private const uint PageReadonly = 0x02;
    private const uint PageReadWrite = 0x04;

    [DllImport("libc", EntryPoint = "mprotect", SetLastError = true)]
    private static extern int Mprotect(nint address, nuint length, int protection);

    [DllImport("libc", EntryPoint = "clock_gettime", SetLastError = false)]
    private static extern int ClockGetTime(int clockId, Timespec* time);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int VirtualProtect(
        nint lpAddress,
        nuint dwSize,
        uint flNewProtect,
        out uint lpflOldProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint VirtualAlloc(
        nint lpAddress,
        nuint dwSize,
        uint flAllocationType,
        uint flProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int VirtualFree(nint lpAddress, nuint dwSize, uint dwFreeType);

    private const uint MemCommit = 0x1000;
    private const uint MemReserve = 0x2000;
    private const uint MemRelease = 0x8000;

    public static bool Enabled => _enabled;

    /// <summary>
    /// Test/diagnostics helper: whether <paramref name="address"/> is tracked
    /// with write protection armed (watch-only ranges report protect=false).
    /// </summary>
    public static bool TryGetProtectionState(
        ulong address,
        out bool protect,
        out bool armed)
    {
        protect = false;
        armed = false;
        lock (_gate)
        {
            if (_rangesByAddress.TryGetValue(address, out var protectedRange))
            {
                DrainNativeWindowsFaultsForRangeLocked(protectedRange);
                protect = true;
                armed = Volatile.Read(ref protectedRange.Armed) == RangeArmed;
                return true;
            }

            if (!_watchRangesByAddress.TryGetValue(address, out var range))
            {
                return false;
            }

            protect = range.Protect;
            armed = Volatile.Read(ref range.Armed) == RangeArmed;
            return true;
        }
    }

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

        // VirtualProtect only belongs on VirtualAlloc/mmap pages. Warming on
        // CRT heap memory makes neighbouring heap metadata read-only and
        // crashes the process on Windows.
        var scratch = OperatingSystem.IsWindows()
            ? VirtualAlloc(0, 4096, MemCommit | MemReserve, PageReadWrite)
            : (nint)NativeMemory.AllocZeroed(4096);
        if (scratch == 0)
        {
            return;
        }

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
            if (OperatingSystem.IsWindows())
            {
                _ = VirtualFree(scratch, 0, MemRelease);
            }
            else
            {
                NativeMemory.Free((void*)scratch);
            }
        }
    }

    /// <summary>
    /// Registers a range. When <paramref name="protect"/> is true, arms write
    /// protection so native stores fault and mark the range dirty. When false,
    /// the range is watch-only (managed HLE writes still dirty via
    /// <see cref="NotifyManagedWrite"/>) and never <c>VirtualProtect</c>'d.
    /// </summary>
    public static void Track(
        ulong address,
        ulong byteCount,
        long sourceSequence = 0,
        string source = "unspecified",
        bool protect = true)
    {
        if (address == 0 || byteCount == 0 || (protect && !_enabled))
        {
            return;
        }

        if (!protect)
        {
            TrackWatchOnly(address, byteCount, sourceSequence, source);
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
                // a fresh immutable range, carrying the write generation so
                // resizes do not hide guest CPU rewrites from cache owners.
                var writeGeneration = Volatile.Read(ref range.WriteGeneration);
                var keepProtect = range.Protect || protect;
                DisarmLocked(range, "replace-range");
                _rangesByAddress.Remove(address);
                range = new TrackedRange
                {
                    Address = address,
                    ByteCount = byteCount,
                    Start = start,
                    End = start + length,
                    Protect = keepProtect,
                    Executable = range.Executable || IsExecutableMapping(address),
                    WriteGeneration = writeGeneration,
                };
                range.Pages = GetTrackedPagesLocked(
                    range.Start,
                    range.End,
                    range.Executable);
                _rangesByAddress[address] = range;
                RebuildSnapshotLocked();
            }

            if (range is null)
            {
                range = new TrackedRange
                {
                    Address = address,
                    ByteCount = byteCount,
                    Start = start,
                    End = start + length,
                    Protect = protect,
                    Executable = IsExecutableMapping(address),
                    TraceLifetime =
                        ShouldTraceRange(start, start + length) || ShouldTraceSource(source),
                    SourceSequence = sourceSequence,
                    Source = source,
                };
                range.Pages = GetTrackedPagesLocked(
                    range.Start,
                    range.End,
                    range.Executable);
                _rangesByAddress[address] = range;
                RebuildSnapshotLocked();
            }
            else
            {
                FlushPendingFirstCpuWrite(range);
                // Protect is sticky: a later watch-only Track (texture cache)
                // must not disarm an RT that already needs page faults.
                if (protect && !range.Protect)
                {
                    range.Protect = true;
                }
            }

            range.SourceSequence = sourceSequence;
            range.Source = source;
            range.TraceLifetime =
                ShouldTraceRange(range.Start, range.End) || ShouldTraceSource(source);
            if (range.Protect)
            {
                ArmLocked(range, "arm");
            }
        }
    }

    public static void Untrack(ulong address)
    {
        lock (_gate)
        {
            if (_rangesByAddress.TryGetValue(address, out var range))
            {
                DisarmLocked(range, "untrack");
                _rangesByAddress.Remove(address);
                RebuildSnapshotLocked();
            }

            if (_watchRangesByAddress.Remove(address))
            {
                RebuildWatchSnapshotLocked();
            }
        }
    }

    /// <summary>
    /// Removes only native write protection for a guest image. A sampled
    /// texture observer at the same address remains active.
    /// </summary>
    public static void UntrackProtected(ulong address)
    {
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
    /// Removes only the managed-write observer for a sampled texture. A render
    /// target at the same address keeps its native write protection.
    /// </summary>
    public static void UntrackWatchOnly(ulong address)
    {
        lock (_gate)
        {
            if (_watchRangesByAddress.Remove(address))
            {
                RebuildWatchSnapshotLocked();
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
        lock (_gate)
        {
            var dirty = false;
            if (_rangesByAddress.TryGetValue(address, out var range))
            {
                DrainNativeWindowsFaultsForRangeLocked(range);
                FlushPendingFirstCpuWrite(range);
                dirty |= Interlocked.Exchange(ref range.Dirty, 0) != 0;
            }

            if (_watchRangesByAddress.TryGetValue(address, out var watchRange))
            {
                dirty |= Interlocked.Exchange(ref watchRange.Dirty, 0) != 0;
            }

            return dirty;
        }
    }

    /// <summary>
    /// Non-consuming variant of <see cref="ConsumeDirty"/>: reports whether
    /// the range has been written since it was last re-armed, leaving the
    /// flag for the owner that evicts and re-uploads.
    /// </summary>
    public static bool PeekDirty(ulong address)
    {
        lock (_gate)
        {
            if (_rangesByAddress.TryGetValue(address, out var range))
            {
                DrainNativeWindowsFaultsForRangeLocked(range);
                FlushPendingFirstCpuWrite(range);
                if (Volatile.Read(ref range.Dirty) != 0)
                {
                    return true;
                }
            }

            return _watchRangesByAddress.TryGetValue(address, out var watchRange) &&
                Volatile.Read(ref watchRange.Dirty) != 0;
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
            if (_rangesByAddress.TryGetValue(address, out var range) &&
                range.Protect)
            {
                DrainNativeWindowsFaultsForRangeLocked(range);
                ArmLocked(range, "rearm");
            }
        }
    }

    /// <summary>
    /// Returns the monotonic first-write generation for a tracked allocation.
    /// Unlike the consuming dirty flag, this remains changed after another
    /// cache owner consumes and re-arms the range.
    /// </summary>
    public static bool TryGetWriteGeneration(ulong address, out long generation)
    {
        generation = 0;
        lock (_gate)
        {
            var found = false;
            if (_rangesByAddress.TryGetValue(address, out var range))
            {
                DrainNativeWindowsFaultsForRangeLocked(range);
                generation = Volatile.Read(ref range.WriteGeneration);
                found = true;
            }

            if (_watchRangesByAddress.TryGetValue(address, out var watchRange))
            {
                generation = unchecked(generation + Volatile.Read(ref watchRange.WriteGeneration));
                found = true;
            }

            return found;
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
        if (address == 0 || byteCount == 0)
        {
            return;
        }

        var end = address > ulong.MaxValue - byteCount
            ? ulong.MaxValue
            : address + byteCount;

        // Fast rejection for the hot path: this runs on every managed guest
        // write, and almost none of them touch tracked texture pages. The
        // bounds live inside the snapshot so they are always consistent with
        // the ranges the per-page visit below would consult.
        MarkManagedWatchRanges(address, end);

        if (!_enabled)
        {
            return;
        }

        var snapshot = Volatile.Read(ref _rangeSnapshot);
        if (snapshot.Ranges.Length == 0 || end <= snapshot.Start || address >= snapshot.End)
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            // Managed writers cannot resume from a CLR access violation. Use
            // the page index as the exact pre-filter, then claim every watched
            // page while holding the tracker lock once for the complete span.
            // This preserves the pre-visit guarantee without serializing one
            // lock acquisition per page inside the broad tracked envelope.
            lock (_gate)
            {
                var candidate = address;
                while (candidate < end)
                {
                    var pageAddress = candidate & ~(TrackingPageSize - 1);
                    if (_pagesByAddress.TryGetValue(pageAddress, out var page) &&
                        Volatile.Read(ref page.WriteWatchers) != 0 &&
                        TryClaimNativeWindowsPageManagedLocked(pageAddress, out _))
                    {
                        _ = DrainNativeWindowsDirtyPageLocked(pageAddress);
                    }

                    var nextPage = pageAddress + TrackingPageSize;
                    if (nextPage <= candidate)
                    {
                        break;
                    }
                    candidate = nextPage;
                }
            }
            return;
        }

        var posixCandidate = address;
        while (posixCandidate < end)
        {
            _ = TryHandleWriteFault(posixCandidate);
            var nextPage = (posixCandidate & ~0xFFFUL) + 0x1000UL;
            if (nextPage <= posixCandidate)
            {
                break;
            }
            posixCandidate = nextPage;
        }
    }

    /// <summary>
    /// Flushes scalar first-write records captured by the POSIX signal handler.
    /// Call only from ordinary managed execution, never from signal context.
    /// </summary>
    public static void FlushPendingDiagnostics()
    {
        if (!_enabled)
        {
            return;
        }

        if (_lifetimeTraceEnabled)
        {
            lock (_gate)
            {
                DrainAllNativeWindowsFaultsLocked();
                foreach (var range in _rangesByAddress.Values)
                {
                    FlushPendingFirstCpuWrite(range);
                }
            }
        }

        ReportProfileIfDue();
        ReportNativeWindowsFaultsIfDue();
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

        var faultPage = faultAddress & ~(TrackingPageSize - 1);
        if (OperatingSystem.IsWindows())
        {
            lock (_gate)
            {
                if (!TryClaimNativeWindowsPageManagedLocked(
                        faultPage,
                        out _))
                {
                    return false;
                }

                return DrainNativeWindowsDirtyPageLocked(faultPage);
            }
        }

        var ranges = Volatile.Read(ref _rangeSnapshot).Ranges;
        var faultPageEnd = faultPage + TrackingPageSize;
        var handled = false;
        long writableBytes = 0;

        // Invalidate every image whose exact guest byte range contains the
        // write. Removing that image's watchers from all pages lets a
        // sequential CPU update continue without one exception per page.
        for (var index = 0; index < ranges.Length; index++)
        {
            var range = ranges[index];
            var exactEnd = range.Address > ulong.MaxValue - range.ByteCount
                ? ulong.MaxValue
                : range.Address + range.ByteCount;
            if (faultAddress < range.Address || faultAddress >= exactEnd)
            {
                continue;
            }

            var previousState = Interlocked.CompareExchange(
                ref range.Armed,
                RangeInvalidating,
                RangeArmed);
            if (previousState is RangeInvalidating or RangeArming)
            {
                handled = true;
                continue;
            }
            if (previousState != RangeArmed)
            {
                continue;
            }

            if (!RemovePageWatchers(range, out var changedBytes))
            {
                Volatile.Write(ref range.Armed, RangeDisarmed);
                return false;
            }

            writableBytes += changedBytes;
            if (_profileEnabled)
            {
                Interlocked.Increment(ref range.ProfileFaultCount);
                Interlocked.Add(ref range.ProfileFaultBytes, changedBytes);
            }
            MarkFaultedRange(range, faultAddress);
            Volatile.Write(ref range.Armed, RangeDisarmed);
            handled = true;
        }

        // Page protection is coarser than an image's exact byte range. A
        // boundary page can still have a watcher from an image whose bytes do
        // not contain this address. Remove that owner too so retrying the same
        // store cannot fault forever. This is limited to the one shared page;
        // unrelated overlap chains are not invalidated.
        if (IsPageStillWatched(ranges, faultPage))
        {
            for (var index = 0; index < ranges.Length; index++)
            {
                var range = ranges[index];
                if (range.Start >= faultPageEnd || range.End <= faultPage)
                {
                    continue;
                }


                var previousState = Interlocked.CompareExchange(
                    ref range.Armed,
                    RangeInvalidating,
                    RangeArmed);
                if (previousState is RangeInvalidating or RangeArming)
                {
                    handled = true;
                    continue;
                }
                if (previousState != RangeArmed)
                {
                    continue;
                }

                if (!RemovePageWatchers(range, out var changedBytes))
                {
                    Volatile.Write(ref range.Armed, RangeDisarmed);
                    return false;
                }

                writableBytes += changedBytes;
                if (_profileEnabled)
                {
                    Interlocked.Increment(ref range.ProfileFaultCount);
                    Interlocked.Add(ref range.ProfileFaultBytes, changedBytes);
                }
                MarkFaultedRange(range, faultAddress);
                Volatile.Write(ref range.Armed, RangeDisarmed);
                handled = true;
            }
        }

        if (!handled)
        {
            return false;
        }

        if (_profileEnabled)
        {
            Interlocked.Increment(ref _profileFaultCount);
            Interlocked.Add(ref _profileFaultBytes, writableBytes);
        }

        return true;
    }

    private static void ArmLocked(TrackedRange range, string operation)
    {
        DrainNativeWindowsFaultsForRangeLocked(range);
        FlushPendingFirstCpuWrite(range);
        if (Interlocked.CompareExchange(
                ref range.Armed,
                RangeArming,
                RangeDisarmed) != RangeDisarmed)
        {
            return;
        }

        // A new publication/rearm starts a new first-write lifetime.
        Volatile.Write(ref range.FirstCpuWriteSeen, 0);
        var failed = !AddPageWatchers(range, out var protectedBytes);
        if (failed)
        {
            Volatile.Write(ref range.Armed, RangeDisarmed);
        }
        else
        {
            Volatile.Write(ref range.Armed, RangeArmed);
            if (_profileEnabled)
            {
                Interlocked.Increment(ref _profileArmCount);
                Interlocked.Add(ref _profileArmBytes, protectedBytes);
                Interlocked.Increment(ref range.ProfileArmCount);
                Interlocked.Add(ref range.ProfileArmBytes, protectedBytes);
            }
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
        var wasArmed = Interlocked.CompareExchange(
            ref range.Armed,
            RangeDisarmed,
            RangeArmed) == RangeArmed;
        if (wasArmed && RemovePageWatchers(range, out var writableBytes))
        {
            if (_profileEnabled)
            {
                Interlocked.Increment(ref _profileDisarmCount);
                Interlocked.Add(ref _profileDisarmBytes, writableBytes);
            }
        }

        if (range.TraceLifetime)
        {
            TraceLifetime(
                range,
                wasArmed ? operation : $"{operation}-already-disarmed");
        }
    }

    /// <summary>
    /// Registers a range written only through managed HLE memory helpers.
    /// Those helpers notify the tracker before each write, so page protection
    /// would duplicate the same observation and add avoidable fault traffic.
    /// </summary>
    public static void TrackManagedWriter(
        ulong address,
        ulong byteCount,
        long sourceSequence = 0,
        string source = "managed-writer")
    {
        if (address == 0 || byteCount == 0)
        {
            return;
        }

        TrackWatchOnly(
            address,
            byteCount,
            sourceSequence,
            source,
            managedWriter: true);
    }

    private static PageState[] GetTrackedPagesLocked(
        ulong start,
        ulong end,
        bool executable)
    {
        var pageCount = checked((int)((end - start) / TrackingPageSize));
        var pages = new PageState[pageCount];
        for (var index = 0; index < pageCount; index++)
        {
            var pageAddress = start + (ulong)index * TrackingPageSize;
            if (!_pagesByAddress.TryGetValue(pageAddress, out var page))
            {
                page = new PageState
                {
                    Address = pageAddress,
                    Executable = executable,
                };
                _pagesByAddress[pageAddress] = page;
            }
            else if (Volatile.Read(ref page.WriteWatchers) == 0)
            {
                // A guest mapping can be recycled with different execute
                // permissions after its previous image owner retired.
                page.Executable = executable;
            }
            else
            {
                page.Executable |= executable;
            }

            pages[index] = page;
        }

        return pages;
    }

    private static bool AddPageWatchers(TrackedRange range, out long protectedBytes)
    {
        protectedBytes = 0;
        var runStart = 0UL;
        var runEnd = 0UL;
        var runExecutable = false;
        var processedPages = 0;

        for (var index = 0; index < range.Pages.Length; index++)
        {
            var page = range.Pages[index];
            var watcherCount = Interlocked.Increment(ref page.WriteWatchers);
            processedPages++;
            if (watcherCount != 1)
            {
                if (!FlushProtectionRun(
                        ref runStart,
                        ref runEnd,
                        runExecutable,
                        writable: false,
                        ref protectedBytes))
                {
                    RecordNativeWindowsArmRaceLocked(range, processedPages);
                    _ = RemovePageWatchers(range, processedPages, out _);
                    return false;
                }
                continue;
            }

            if (runEnd == page.Address && runExecutable == page.Executable)
            {
                runEnd += TrackingPageSize;
                continue;
            }

            if (!FlushProtectionRun(
                    ref runStart,
                    ref runEnd,
                    runExecutable,
                    writable: false,
                    ref protectedBytes))
            {
                RecordNativeWindowsArmRaceLocked(range, processedPages);
                _ = RemovePageWatchers(range, processedPages, out _);
                return false;
            }

            runStart = page.Address;
            runEnd = page.Address + TrackingPageSize;
            runExecutable = page.Executable;
        }

        if (FlushProtectionRun(
                ref runStart,
                ref runEnd,
                runExecutable,
                writable: false,
                ref protectedBytes))
        {
            return true;
        }

        RecordNativeWindowsArmRaceLocked(range, processedPages);
        _ = RemovePageWatchers(range, processedPages, out _);
        return false;
    }

    private static bool RemovePageWatchers(TrackedRange range, out long writableBytes) =>
        RemovePageWatchers(range, range.Pages.Length, out writableBytes);

    private static bool RemovePageWatchers(
        TrackedRange range,
        int pageCount,
        out long writableBytes)
    {
        writableBytes = 0;
        var runStart = 0UL;
        var runEnd = 0UL;
        var runExecutable = false;

        for (var index = 0; index < pageCount; index++)
        {
            var page = range.Pages[index];
            var watcherCount = Interlocked.Decrement(ref page.WriteWatchers);
            if (watcherCount < 0)
            {
                Interlocked.Increment(ref page.WriteWatchers);
                return false;
            }

            if (watcherCount != 0)
            {
                if (!FlushProtectionRun(
                        ref runStart,
                        ref runEnd,
                        runExecutable,
                        writable: true,
                        ref writableBytes))
                {
                    return false;
                }
                continue;
            }

            if (runEnd == page.Address && runExecutable == page.Executable)
            {
                runEnd += TrackingPageSize;
                continue;
            }

            if (!FlushProtectionRun(
                    ref runStart,
                    ref runEnd,
                    runExecutable,
                    writable: true,
                    ref writableBytes))
            {
                return false;
            }

            runStart = page.Address;
            runEnd = page.Address + TrackingPageSize;
            runExecutable = page.Executable;
        }

        return FlushProtectionRun(
            ref runStart,
            ref runEnd,
            runExecutable,
            writable: true,
            ref writableBytes);
    }

    private static bool FlushProtectionRun(
        ref ulong runStart,
        ref ulong runEnd,
        bool executable,
        bool writable,
        ref long changedBytes)
    {
        if (runEnd <= runStart)
        {
            return true;
        }

        var byteCount = runEnd - runStart;
        if (OperatingSystem.IsWindows())
        {
            if (!writable)
            {
                for (var page = runStart; page < runEnd; page += TrackingPageSize)
                {
                    if (!PublishNativeWindowsPageLocked(page, executable))
                    {
                        for (var published = runStart; published < page; published += TrackingPageSize)
                        {
                            UnpublishNativeWindowsPageLocked(published);
                        }
                        return false;
                    }
                }

                if (!TrySetProtection(runStart, byteCount, writable: false, executable))
                {
                    _ = TrySetProtection(runStart, byteCount, writable: true, executable);
                    for (var page = runStart; page < runEnd; page += TrackingPageSize)
                    {
                        UnpublishNativeWindowsPageLocked(page);
                    }
                    return false;
                }

            }
            else
            {
                if (!TrySetProtection(runStart, byteCount, writable: true, executable))
                {
                    return false;
                }

                for (var page = runStart; page < runEnd; page += TrackingPageSize)
                {
                    UnpublishNativeWindowsPageLocked(page);
                }
            }
        }
        else if (!TrySetProtection(runStart, byteCount, writable, executable))
        {
            return false;
        }

        changedBytes += (long)byteCount;
        runStart = 0;
        runEnd = 0;
        return true;
    }

    private static bool IsPageStillWatched(TrackedRange[] ranges, ulong pageAddress)
    {
        var pageEnd = pageAddress + TrackingPageSize;
        for (var index = 0; index < ranges.Length; index++)
        {
            var range = ranges[index];
            if (Volatile.Read(ref range.Armed) != RangeDisarmed &&
                range.Start < pageEnd && range.End > pageAddress)
            {
                return true;
            }
        }

        return false;
    }

    private static void MarkFaultedRange(TrackedRange range, ulong faultAddress)
    {
        Interlocked.Exchange(ref range.Dirty, 1);
        Interlocked.Increment(ref range.WriteGeneration);
        if (!range.TraceLifetime ||
            Interlocked.CompareExchange(ref range.FirstCpuWriteSeen, 1, 0) != 0)
        {
            return;
        }

        // Signal context: capture preallocated scalar fields only. Formatting
        // and I/O are deferred to a locked safe path.
        range.FirstCpuWriteTraceSequence = Interlocked.Increment(ref _lifetimeTraceSequence);
        range.FirstCpuWriteTimestampNanoseconds = GetMonotonicNanoseconds();
        range.FirstCpuWriteAddress = faultAddress;
        range.FirstCpuWritePage = faultAddress & ~(TrackingPageSize - 1);
        Volatile.Write(ref range.PendingFirstCpuWrite, 1);
        Volatile.Write(ref range.FirstCpuWriteSeen, 2);
    }

    private static void RebuildSnapshotLocked()
    {
        Volatile.Write(ref _rangeSnapshot, new RangeSnapshot(_rangesByAddress.Values.ToArray()));
    }

    /// <summary>
    /// Arms write observation before a caller copies guest image memory and
    /// returns the generation that must still be current when the copy ends.
    /// </summary>
    public static ReadSnapshot BeginReadSnapshot(
        ulong address,
        ulong byteCount,
        long sourceSequence = 0,
        string source = "texture-snapshot")
    {
        if (!_enabled || address == 0 || byteCount == 0)
        {
            return new ReadSnapshot(address, -1, false);
        }

        if (_profileEnabled)
        {
            Interlocked.Increment(ref _profileSnapshotCount);
        }

        if (!HasManagedWriterCoverage(address, byteCount))
        {
            Track(address, byteCount, sourceSequence, source, protect: true);
        }
        return TryGetWriteGeneration(address, out var generation)
            ? new ReadSnapshot(address, generation, true)
            : new ReadSnapshot(address, -1, false);
    }

    /// <summary>
    /// Returns true only when no observed guest write overlapped the snapshot.
    /// </summary>
    public static bool IsReadSnapshotStable(in ReadSnapshot snapshot) =>
        !snapshot.Active ||
        (TryGetWriteGeneration(snapshot.Address, out var generation) &&
            generation == snapshot.Generation);

    private static void TrackWatchOnly(
        ulong address,
        ulong byteCount,
        long sourceSequence,
        string source,
        bool managedWriter = false)
    {
        lock (_gate)
        {
            if (_watchRangesByAddress.TryGetValue(address, out var range))
            {
                // Several views can share one allocation. Keep the largest
                // observed extent so a narrower view cannot hide later writes.
                if (byteCount <= range.ByteCount)
                {
                    range.SourceSequence = sourceSequence;
                    range.Source = source;
                    range.ManagedWriter |= managedWriter;
                    return;
                }

                var generation = Volatile.Read(ref range.WriteGeneration);
                var dirty = Volatile.Read(ref range.Dirty);
                range = new TrackedRange
                {
                    Address = address,
                    ByteCount = byteCount,
                    Start = address,
                    End = SaturatingEnd(address, byteCount),
                    Protect = false,
                    Dirty = dirty,
                    WriteGeneration = generation,
                    ManagedWriter = range.ManagedWriter || managedWriter,
                    SourceSequence = sourceSequence,
                    Source = source,
                };
                _watchRangesByAddress[address] = range;
                RebuildWatchSnapshotLocked();
                return;
            }

            _watchRangesByAddress[address] = new TrackedRange
            {
                Address = address,
                ByteCount = byteCount,
                Start = address,
                End = SaturatingEnd(address, byteCount),
                Protect = false,
                ManagedWriter = managedWriter,
                SourceSequence = sourceSequence,
                Source = source,
            };
            RebuildWatchSnapshotLocked();
        }
    }

    private static void RebuildWatchSnapshotLocked()
    {
        var ranges = _watchRangesByAddress.Values
            .OrderBy(static range => range.Start)
            .ToArray();
        var prefixMaximumEnds = new ulong[ranges.Length];
        var maximumEnd = 0UL;
        for (var index = 0; index < ranges.Length; index++)
        {
            maximumEnd = Math.Max(maximumEnd, ranges[index].End);
            prefixMaximumEnds[index] = maximumEnd;
        }

        Volatile.Write(
            ref _watchRangeSnapshot,
            new WatchRangeSnapshot(ranges, prefixMaximumEnds));
    }

    private static void MarkManagedWatchRanges(ulong address, ulong end)
    {
        var snapshot = Volatile.Read(ref _watchRangeSnapshot);
        var ranges = snapshot.Ranges;
        if (ranges.Length == 0)
        {
            return;
        }

        var low = 0;
        var high = ranges.Length;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (ranges[middle].Start < end)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        for (var index = low - 1;
             index >= 0 && snapshot.PrefixMaximumEnds[index] > address;
             index--)
        {
            var range = ranges[index];
            if (range.End <= address)
            {
                continue;
            }

            if (Interlocked.Exchange(ref range.Dirty, 1) == 0)
            {
                Interlocked.Increment(ref range.WriteGeneration);
            }
        }
    }

    private static ulong SaturatingEnd(ulong address, ulong byteCount) =>
        address > ulong.MaxValue - byteCount ? ulong.MaxValue : address + byteCount;

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

    private static bool TrySetProtection(
        ulong start,
        ulong length,
        bool writable,
        bool executable)
    {
        if (length == 0)
        {
            return true;
        }

        if (OperatingSystem.IsWindows())
        {
            return VirtualProtect(
                (nint)start,
                (nuint)length,
                executable
                    ? writable
                        ? HostMemory.PAGE_EXECUTE_READWRITE
                        : HostMemory.PAGE_EXECUTE_READ
                    : writable
                        ? PageReadWrite
                        : PageReadonly,
                out _) != 0;
        }

        var protection = ProtRead |
            (writable ? ProtWrite : 0) |
            (executable ? ProtExec : 0);
        return Mprotect(
            (nint)start,
            (nuint)length,
            protection) == 0;
    }

    private static bool IsExecutableMapping(ulong address)
    {
        if (address == 0 ||
            HostMemory.Query((void*)address, out var info) == 0)
        {
            return false;
        }

        var protection = info.Protect & 0xFFu;
        return protection is
            HostMemory.PAGE_EXECUTE or
            HostMemory.PAGE_EXECUTE_READ or
            HostMemory.PAGE_EXECUTE_READWRITE;
    }

    private static long GetMonotonicNanoseconds()
    {
        if (OperatingSystem.IsWindows())
        {
            return Stopwatch.GetTimestamp() * 1_000_000_000L / Stopwatch.Frequency;
        }

        Timespec time;
        return ClockGetTime(ClockMonotonicRaw, &time) == 0
            ? unchecked((time.Seconds * 1_000_000_000L) + time.Nanoseconds)
            : 0;
    }

    private static void ReportProfileIfDue()
    {
        if (!_profileEnabled)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var previous = Volatile.Read(ref _profileLastReportTimestamp);
        if (now - previous < Stopwatch.Frequency * 5 ||
            Interlocked.CompareExchange(ref _profileLastReportTimestamp, now, previous) != previous)
        {
            return;
        }

        var snapshots = Interlocked.Exchange(ref _profileSnapshotCount, 0);
        var arms = Interlocked.Exchange(ref _profileArmCount, 0);
        var armBytes = Interlocked.Exchange(ref _profileArmBytes, 0);
        var faults = Interlocked.Exchange(ref _profileFaultCount, 0);
        var faultBytes = Interlocked.Exchange(ref _profileFaultBytes, 0);
        var disarms = Interlocked.Exchange(ref _profileDisarmCount, 0);
        var disarmBytes = Interlocked.Exchange(ref _profileDisarmBytes, 0);
        int protectedRanges;
        List<(ulong Address, ulong ByteCount, string Source, long Arms, long ArmBytes, long Faults, long FaultBytes)> hotRanges;
        lock (_gate)
        {
            protectedRanges = _rangesByAddress.Count;
            hotRanges = new(protectedRanges);
            foreach (var range in _rangesByAddress.Values)
            {
                var rangeArms = Interlocked.Exchange(ref range.ProfileArmCount, 0);
                var rangeArmBytes = Interlocked.Exchange(ref range.ProfileArmBytes, 0);
                var rangeFaults = Interlocked.Exchange(ref range.ProfileFaultCount, 0);
                var rangeFaultBytes = Interlocked.Exchange(ref range.ProfileFaultBytes, 0);
                if (rangeArms == 0 && rangeFaults == 0)
                {
                    continue;
                }

                hotRanges.Add((
                    range.Address,
                    range.ByteCount,
                    range.Source,
                    rangeArms,
                    rangeArmBytes,
                    rangeFaults,
                    rangeFaultBytes));
            }
        }

        Console.Error.WriteLine(
            $"[PERF][GUEST_IMAGE_TRACKER] snapshots={snapshots} " +
            $"arms={arms}/{armBytes}B faults={faults}/{faultBytes}B " +
            $"disarms={disarms}/{disarmBytes}B ranges={protectedRanges}");

        foreach (var range in hotRanges
                     .OrderByDescending(static range => Math.Max(range.ArmBytes, range.FaultBytes))
                     .ThenByDescending(static range => range.Faults)
                     .Take(5))
        {
            Console.Error.WriteLine(
                $"[PERF][GUEST_IMAGE_TRACKER_RANGE] addr=0x{range.Address:X16} " +
                $"size={range.ByteCount}B source='{range.Source}' " +
                $"arms={range.Arms}/{range.ArmBytes}B " +
                $"faults={range.Faults}/{range.FaultBytes}B");
        }
    }

    private static bool HasManagedWriterCoverage(ulong address, ulong byteCount)
    {
        lock (_gate)
        {
            return _watchRangesByAddress.TryGetValue(address, out var range) &&
                range.ManagedWriter &&
                range.ByteCount >= byteCount;
        }
    }
}
