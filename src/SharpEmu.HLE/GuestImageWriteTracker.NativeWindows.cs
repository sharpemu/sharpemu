// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SharpEmu.HLE;

public static unsafe partial class GuestImageWriteTracker
{
    // The native VEH cannot inspect managed dictionaries. This sparse table
    // covers the 128 TiB Windows user virtual address space without reserving one
    // entry for every possible page. Level-two tables are allocated before a
    // page is protected and remain valid for the process lifetime.
    public const int NativeWindowsPageStateUntracked = 0;
    public const int NativeWindowsPageStateArmed = 1;
    public const int NativeWindowsPageStateDirty = 2;
    public const int NativeWindowsPageEntrySize = 32;
    public const int NativeWindowsLevelThreePageShift = 10;
    public const int NativeWindowsLevelThreeEntryCount = 1 << NativeWindowsLevelThreePageShift;
    public const int NativeWindowsLevelTwoPageShift = 12;
    public const int NativeWindowsLevelTwoEntryCount = 1 << NativeWindowsLevelTwoPageShift;
    public const int NativeWindowsLevelOneEntryCount = 1 << 13;

    [StructLayout(LayoutKind.Sequential, Size = NativeWindowsPageEntrySize)]
    private struct NativeWindowsPageEntry
    {
        public int State;
        public int Executable;
        public int FaultCount;
        public int Reserved;
        public ulong PageAddress;
        public nint NextDirty;
    }

    [StructLayout(LayoutKind.Sequential, Size = 96)]
    private struct NativeWindowsFaultControl
    {
        public nint LevelOne;
        public long HandledFaults;
        public long ProtectionFailures;
        public long TableMisses;
        public ulong LastFaultAddress;
        public int LastFaultState;
        public int Reserved;
        public nint DirtyHead;
        public long QueuedPages;
        public long DrainedPages;
    }

    private static readonly bool _nativeWindowsFaultTraceEnabled =
        string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_LOG_GUEST_IMAGE_NATIVE_FAULTS"),
            "1",
            StringComparison.Ordinal);
    private static nint _nativeWindowsLevelOne;
    private static nint _nativeWindowsFaultControl;
    private static long _nativeWindowsLastReportedHandled;
    private static long _nativeWindowsLastReportedFailures;
    private static long _nativeWindowsLastReportedMisses;
    private static long _nativeWindowsLastReportedQueued;
    private static long _nativeWindowsLastReportedDrained;

    /// <summary>
    /// Fixed native control block consumed by the emitted Windows VEH helper.
    /// It is created before the helper is emitted and is never relocated.
    /// </summary>
    public static nint NativeWindowsFaultControlAddress
    {
        get
        {
            if (!OperatingSystem.IsWindows() || !_enabled)
            {
                return 0;
            }

            lock (_gate)
            {
                EnsureNativeWindowsFaultTableLocked();
                return _nativeWindowsFaultControl;
            }
        }
    }

    private static void EnsureNativeWindowsFaultTableLocked()
    {
        if (_nativeWindowsFaultControl != 0)
        {
            return;
        }

        var levelOneBytes = checked(
            (nuint)(NativeWindowsLevelOneEntryCount * sizeof(nint)));
        var levelOne = VirtualAlloc(
            0,
            levelOneBytes,
            MemCommit | MemReserve,
            PageReadWrite);
        if (levelOne == 0)
        {
            throw new OutOfMemoryException("Failed to allocate native guest-image page table");
        }

        var control = VirtualAlloc(
            0,
            96,
            MemCommit | MemReserve,
            PageReadWrite);
        if (control == 0)
        {
            _ = VirtualFree(levelOne, 0, MemRelease);
            throw new OutOfMemoryException("Failed to allocate native guest-image fault control");
        }

        new Span<byte>((void*)levelOne, checked((int)levelOneBytes)).Clear();
        new Span<byte>((void*)control, 96).Clear();
        ((NativeWindowsFaultControl*)control)->LevelOne = levelOne;

        // Publish the fully initialized table only after all fields are valid.
        Volatile.Write(ref _nativeWindowsLevelOne, levelOne);
        Volatile.Write(ref _nativeWindowsFaultControl, control);
    }

    private static NativeWindowsPageEntry* GetNativeWindowsPageEntryLocked(
        ulong pageAddress,
        bool create)
    {
        if (!OperatingSystem.IsWindows() || !_enabled)
        {
            return null;
        }

        if (_pagesByAddress.TryGetValue(pageAddress, out var trackedPage) &&
            trackedPage.NativeWindowsEntry != 0)
        {
            return (NativeWindowsPageEntry*)trackedPage.NativeWindowsEntry;
        }

        EnsureNativeWindowsFaultTableLocked();
        var pageIndex = pageAddress >> 12;
        var levelOneIndex = pageIndex >>
            (NativeWindowsLevelTwoPageShift + NativeWindowsLevelThreePageShift);
        if (levelOneIndex >= NativeWindowsLevelOneEntryCount)
        {
            return null;
        }

        var levelOne = (nint*)_nativeWindowsLevelOne;
        ref var levelTwoSlot = ref Unsafe.AsRef<nint>(levelOne + levelOneIndex);
        var levelTwo = Volatile.Read(ref levelTwoSlot);
        if (levelTwo == 0 && create)
        {
            var levelTwoBytes = checked(
                (nuint)(NativeWindowsLevelTwoEntryCount * sizeof(nint)));
            levelTwo = VirtualAlloc(
                0,
                levelTwoBytes,
                MemCommit | MemReserve,
                PageReadWrite);
            if (levelTwo == 0)
            {
                return null;
            }

            new Span<byte>((void*)levelTwo, checked((int)levelTwoBytes)).Clear();
            Volatile.Write(ref levelTwoSlot, levelTwo);
        }

        if (levelTwo == 0)
        {
            return null;
        }

        var levelTwoIndex = (pageIndex >> NativeWindowsLevelThreePageShift) &
            (NativeWindowsLevelTwoEntryCount - 1);
        var levelTwoTable = (nint*)levelTwo;
        ref var levelThreeSlot = ref Unsafe.AsRef<nint>(levelTwoTable + levelTwoIndex);
        var levelThree = Volatile.Read(ref levelThreeSlot);
        if (levelThree == 0 && create)
        {
            var levelThreeBytes = checked(
                (nuint)(NativeWindowsLevelThreeEntryCount * NativeWindowsPageEntrySize));
            levelThree = VirtualAlloc(
                0,
                levelThreeBytes,
                MemCommit | MemReserve,
                PageReadWrite);
            if (levelThree == 0)
            {
                return null;
            }

            new Span<byte>((void*)levelThree, checked((int)levelThreeBytes)).Clear();
            Volatile.Write(ref levelThreeSlot, levelThree);
        }

        if (levelThree == 0)
        {
            return null;
        }

        var levelThreeIndex = (int)(pageIndex &
            (NativeWindowsLevelThreeEntryCount - 1));
        var entry = (NativeWindowsPageEntry*)((byte*)levelThree +
            levelThreeIndex * NativeWindowsPageEntrySize);
        if (trackedPage is not null)
        {
            trackedPage.NativeWindowsEntry = (nint)entry;
        }
        return entry;
    }

    private static bool PublishNativeWindowsPageLocked(ulong pageAddress, bool executable)
    {
        var entry = GetNativeWindowsPageEntryLocked(pageAddress, create: true);
        if (entry is null)
        {
            return false;
        }

        Volatile.Write(ref entry->Executable, executable ? 1 : 0);
        Volatile.Write(ref entry->PageAddress, pageAddress);
        Volatile.Write(ref entry->NextDirty, 0);

        // Publish the claimable entry before VirtualProtect removes write
        // access. A stale entry on a writable page is harmless; a protected
        // page that the native handler cannot resolve is fatal.
        Volatile.Write(ref entry->State, NativeWindowsPageStateArmed);
        return true;
    }

    private static void UnpublishNativeWindowsPageLocked(ulong pageAddress)
    {
        var entry = GetNativeWindowsPageEntryLocked(pageAddress, create: false);
        if (entry is null)
        {
            return;
        }

        // Call only after write permission has been restored. A stale native
        // entry is safe; an unlisted protected page is not.
        Volatile.Write(ref entry->State, NativeWindowsPageStateUntracked);
    }

    private static bool IsNativeWindowsPageDirtyLocked(ulong pageAddress)
    {
        var entry = GetNativeWindowsPageEntryLocked(pageAddress, create: false);
        return entry is not null &&
            Volatile.Read(ref entry->State) == NativeWindowsPageStateDirty;
    }

    private static void RecordNativeWindowsArmRaceLocked(
        TrackedRange range,
        int pageCount)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var count = Math.Min(pageCount, range.Pages.Length);
        for (var index = 0; index < count; index++)
        {
            var pageAddress = range.Pages[index].Address;
            if (!IsNativeWindowsPageDirtyLocked(pageAddress))
            {
                continue;
            }

            MarkFaultedRange(range, pageAddress);
            return;
        }
    }

    /// <summary>
    /// Returns native page state for tests and targeted diagnostics.
    /// </summary>
    public static bool TryGetNativeWindowsFaultState(
        ulong address,
        out int state,
        out int faultCount)
    {
        state = NativeWindowsPageStateUntracked;
        faultCount = 0;
        if (!OperatingSystem.IsWindows() || !_enabled)
        {
            return false;
        }

        lock (_gate)
        {
            var pageAddress = address & ~(TrackingPageSize - 1);
            var entry = GetNativeWindowsPageEntryLocked(pageAddress, create: false);
            if (entry is null)
            {
                return false;
            }

            state = Volatile.Read(ref entry->State);
            faultCount = Volatile.Read(ref entry->FaultCount);
            return true;
        }
    }

    private static bool TryClaimNativeWindowsPageManagedLocked(
        ulong pageAddress,
        out int previousState)
    {
        previousState = NativeWindowsPageStateUntracked;
        var entry = GetNativeWindowsPageEntryLocked(pageAddress, create: false);
        if (entry is null)
        {
            return false;
        }

        previousState = Volatile.Read(ref entry->State);
        while (previousState == NativeWindowsPageStateArmed)
        {
            var observed = Interlocked.CompareExchange(
                ref entry->State,
                NativeWindowsPageStateDirty,
                previousState);
            if (observed == previousState)
            {
                var executable = Volatile.Read(ref entry->Executable) != 0;
                if (!TrySetProtection(
                        pageAddress,
                        TrackingPageSize,
                        writable: true,
                        executable))
                {
                    if (_nativeWindowsFaultControl != 0)
                    {
                        Interlocked.Increment(
                            ref ((NativeWindowsFaultControl*)_nativeWindowsFaultControl)
                                ->ProtectionFailures);
                    }
                    return false;
                }

                Interlocked.Increment(ref entry->FaultCount);
                if (_nativeWindowsFaultControl != 0)
                {
                    var control = (NativeWindowsFaultControl*)_nativeWindowsFaultControl;
                    Interlocked.Increment(ref control->HandledFaults);
                    Volatile.Write(ref control->LastFaultAddress, pageAddress);
                    Volatile.Write(ref control->LastFaultState, previousState);
                }
                return true;
            }

            previousState = observed;
        }

        return previousState == NativeWindowsPageStateDirty;
    }

    private static void DrainNativeWindowsFaultsForRangeLocked(TrackedRange range)
    {
        _ = range;
        DrainNativeWindowsDirtyStackLocked();
    }

    private static bool DrainNativeWindowsDirtyPageLocked(ulong pageAddress)
    {
        if (!IsNativeWindowsPageDirtyLocked(pageAddress))
        {
            return false;
        }

        var pageEnd = pageAddress + TrackingPageSize;
        var handled = false;
        var ranges = _rangesByAddress.Values.ToArray();
        for (var index = 0; index < ranges.Length; index++)
        {
            var range = ranges[index];
            if (range.Start >= pageEnd || range.End <= pageAddress)
            {
                continue;
            }

            var previousState = Interlocked.CompareExchange(
                ref range.Armed,
                RangeInvalidating,
                RangeArmed);
            if (previousState != RangeArmed)
            {
                continue;
            }

            if (!RemovePageWatchers(range, out var changedBytes))
            {
                Volatile.Write(ref range.Armed, RangeDisarmed);
                continue;
            }

            if (_profileEnabled)
            {
                Interlocked.Increment(ref range.ProfileFaultCount);
                Interlocked.Add(ref range.ProfileFaultBytes, changedBytes);
                Interlocked.Increment(ref _profileFaultCount);
                Interlocked.Add(ref _profileFaultBytes, changedBytes);
            }

            MarkFaultedRange(range, pageAddress);
            Volatile.Write(ref range.Armed, RangeDisarmed);
            handled = true;
        }

        return handled;
    }

    private static void DrainAllNativeWindowsFaultsLocked()
    {
        DrainNativeWindowsDirtyStackLocked();
    }

    private static void DrainNativeWindowsDirtyStackLocked()
    {
        if (!OperatingSystem.IsWindows() || _nativeWindowsFaultControl == 0)
        {
            return;
        }

        var control = (NativeWindowsFaultControl*)_nativeWindowsFaultControl;
        var head = Interlocked.Exchange(ref control->DirtyHead, 0);
        long drainedPages = 0;
        while (head != 0)
        {
            var entry = (NativeWindowsPageEntry*)head;
            var next = Volatile.Read(ref entry->NextDirty);
            Volatile.Write(ref entry->NextDirty, 0);
            _ = DrainNativeWindowsDirtyPageLocked(
                Volatile.Read(ref entry->PageAddress));
            drainedPages++;
            head = next;
        }

        if (drainedPages != 0)
        {
            Interlocked.Add(ref control->DrainedPages, drainedPages);
        }
    }

    private static void ReportNativeWindowsFaultsIfDue()
    {
        if (!_nativeWindowsFaultTraceEnabled || _nativeWindowsFaultControl == 0)
        {
            return;
        }

        var control = (NativeWindowsFaultControl*)_nativeWindowsFaultControl;
        var handled = Volatile.Read(ref control->HandledFaults);
        var failures = Volatile.Read(ref control->ProtectionFailures);
        var misses = Volatile.Read(ref control->TableMisses);
        var queued = Volatile.Read(ref control->QueuedPages);
        var drained = Volatile.Read(ref control->DrainedPages);
        if (handled == Volatile.Read(ref _nativeWindowsLastReportedHandled) &&
            failures == Volatile.Read(ref _nativeWindowsLastReportedFailures) &&
            misses == Volatile.Read(ref _nativeWindowsLastReportedMisses) &&
            queued == Volatile.Read(ref _nativeWindowsLastReportedQueued) &&
            drained == Volatile.Read(ref _nativeWindowsLastReportedDrained))
        {
            return;
        }

        Volatile.Write(ref _nativeWindowsLastReportedHandled, handled);
        Volatile.Write(ref _nativeWindowsLastReportedFailures, failures);
        Volatile.Write(ref _nativeWindowsLastReportedMisses, misses);
        Volatile.Write(ref _nativeWindowsLastReportedQueued, queued);
        Volatile.Write(ref _nativeWindowsLastReportedDrained, drained);
        Console.Error.WriteLine(
            "[LOADER][TRACE] guest_image.native_faults " +
            $"handled={handled} protect_failures={failures} table_misses={misses} " +
            $"queued_pages={queued} drained_pages={drained} " +
            $"pending={(Volatile.Read(ref control->DirtyHead) != 0 ? 1 : 0)} " +
            $"last_addr=0x{Volatile.Read(ref control->LastFaultAddress):X16} " +
            $"last_state={Volatile.Read(ref control->LastFaultState)}");
    }
}
