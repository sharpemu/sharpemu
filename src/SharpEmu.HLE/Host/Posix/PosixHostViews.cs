// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace SharpEmu.HLE.Host.Posix;

internal sealed unsafe partial class PosixHostViews : IHostViewMemory
{
    internal static bool FailAliasMapForTests;

    private const int PROT_NONE = 0x0;
    private const int PROT_READ = 0x1;
    private const int PROT_WRITE = 0x2;
    private const int PROT_EXEC = 0x4;
    private const int MAP_SHARED = 0x01;
    private const int MAP_PRIVATE = 0x02;
    private const int MAP_FIXED = 0x10;
    private const int MAP_FIXED_NOREPLACE = 0x100000;
    private const int O_RDWR = 0x2;
    private static readonly int MAP_ANON = OperatingSystem.IsMacOS() ? 0x1000 : 0x20;
    private static readonly int MAP_NORESERVE = OperatingSystem.IsMacOS() ? 0 : 0x4000;
    private static readonly int O_CREAT = OperatingSystem.IsMacOS() ? 0x200 : 0x40;
    private static readonly int O_EXCL = OperatingSystem.IsMacOS() ? 0x800 : 0x80;
    private static readonly nint MAP_FAILED = -1;
    private static int _sharedObjectCounter;

    public ulong PageSize { get; } = (ulong)Environment.SystemPageSize;

    public ulong Granularity => PageSize;

    public bool TryCreateBacking(ulong size, out HostBackingObject? backing, out HostViewFailure failure)
    {
        backing = null;
        failure = HostViewFailure.BackingUnavailable;
        if (size == 0)
        {
            return false;
        }

        var descriptor = OperatingSystem.IsMacOS() ? OpenSharedObject() : memfd_create("SharpEmuBacking", 0);
        if (descriptor < 0)
        {
            return false;
        }

        if (ftruncate(descriptor, (long)size) != 0)
        {
            close(descriptor);
            return false;
        }

        var alias = FailAliasMapForTests ? MAP_FAILED : mmap(0, (nuint)size, PROT_READ | PROT_WRITE, MAP_SHARED, descriptor, 0);
        if (alias == MAP_FAILED)
        {
            close(descriptor);
            return false;
        }

        backing = new HostBackingObject((ulong)alias, size, ReleaseBackingObject) { Descriptor = descriptor };
        failure = HostViewFailure.None;
        return true;
    }

    public ulong ReserveHole(ulong address, ulong size)
    {
        lock (PosixViewRegions.Gate)
        {
            var result = ReserveHoleCore(address, size);
            if (result != 0)
                PosixViewRegions.Replace(address, size, HostMemory.MEM_RESERVE, HostMemory.PAGE_NOACCESS);
            return result;
        }
    }

    private ulong ReserveHoleCore(ulong address, ulong size)
    {
        if (!HostViewMemory.IsValidRange(address, size) || address % Granularity != 0)
        {
            return 0;
        }

        var flags = MAP_PRIVATE | MAP_ANON | MAP_NORESERVE | (OperatingSystem.IsLinux() ? MAP_FIXED_NOREPLACE : 0);
        var ptr = mmap((nint)address, (nuint)size, PROT_NONE, flags, -1, 0);
        if (ptr == MAP_FAILED)
        {
            return 0;
        }

        if ((ulong)ptr != address)
        {
            munmap(ptr, (nuint)size);
            return 0;
        }

        return address;
    }

    // POSIX has no VirtualQuery for foreign host mappings, so find the free
    // ranges by reserving halves until each attempt fits or falls below the
    // minimum. Occupied host ranges are few, so this needs few attempts.
    public IReadOnlyList<HostAddressRange> ReserveFreeAddressRanges(ulong start, ulong end, ulong minimumSize)
    {
        var reserved = new List<HostAddressRange>();
        start = AlignUp(start, Granularity);
        end -= end % Granularity;
        if (start >= end || minimumSize == 0)
        {
            return reserved;
        }

        lock (PosixViewRegions.Gate)
        {
            ReserveFreeAddressRangesLocked(start, end - start, Math.Max(minimumSize, Granularity), reserved);
        }

        return reserved;
    }

    private void ReserveFreeAddressRangesLocked(ulong address, ulong size, ulong minimumSize, List<HostAddressRange> reserved)
    {
        if (ReserveHoleCore(address, size) == address)
        {
            PosixViewRegions.Replace(address, size, HostMemory.MEM_RESERVE, HostMemory.PAGE_NOACCESS);
            if (reserved.Count != 0 && reserved[^1].Address + reserved[^1].Size == address)
            {
                reserved[^1] = reserved[^1] with { Size = reserved[^1].Size + size };
            }
            else
            {
                reserved.Add(new HostAddressRange(address, size));
            }
            return;
        }

        var half = size / 2;
        half -= half % Granularity;
        if (half < minimumSize)
        {
            return;
        }

        ReserveFreeAddressRangesLocked(address, half, minimumSize, reserved);
        ReserveFreeAddressRangesLocked(address + half, size - half, minimumSize, reserved);
    }

    private static ulong AlignUp(ulong value, ulong alignment)
    {
        var remainder = value % alignment;
        return remainder == 0 ? value : value + alignment - remainder;
    }

    public bool SplitHole(ulong address, ulong size) => size != 0;

    public bool JoinHoles(ulong address, ulong size) => size != 0;

    public bool FreeHole(ulong address, ulong size) => FreeOwnedRange(address, size);

    public bool TryMapView(HostBackingObject backing, ulong address, ulong offset, ulong size, HostPageProtection protection, out HostViewFailure failure)
    {
        lock (PosixViewRegions.Gate)
        {
            if (!TryMapViewCore(backing, address, offset, size, protection, out failure)) return false;
            PosixViewRegions.Replace(address, size, HostMemory.MEM_COMMIT, PosixViewRegions.RawProtection(protection));
            return true;
        }
    }

    private bool TryMapViewCore(HostBackingObject backing, ulong address, ulong offset, ulong size, HostPageProtection protection, out HostViewFailure failure)
    {
        if (address == 0 || address % PageSize != 0)
        {
            failure = HostViewFailure.WrongHostAddress;
            return false;
        }

        lock (backing.Gate)
        {
            if (backing.IsDisposed)
            {
                failure = HostViewFailure.BackingUnavailable;
                return false;
            }

            if (!HostViewMemory.IsValidOffset(backing, offset, size, PageSize))
            {
                failure = HostViewFailure.OffsetOutOfBounds;
                return false;
            }

            if (size > ulong.MaxValue - address)
            {
                failure = HostViewFailure.WrongHostAddress;
                return false;
            }

            var ptr = mmap((nint)address, (nuint)size, GetNativeProtection(protection), MAP_SHARED | MAP_FIXED, backing.Descriptor, (long)offset);
            if (ptr == MAP_FAILED)
            {
                failure = HostViewFailure.FixedMapFailed;
                return false;
            }

            if ((ulong)ptr != address)
            {
                munmap(ptr, (nuint)size);
                failure = HostViewFailure.WrongHostAddress;
                return false;
            }

            failure = HostViewFailure.None;
            return true;
        }
    }

    public bool UnmapView(ulong address, ulong size) => RestoreHole(address, size);

    public bool CommitPrivate(ulong address, ulong size, HostPageProtection protection)
    {
        lock (PosixViewRegions.Gate)
        {
            if (mprotect((nint)address, (nuint)size, GetNativeProtection(protection)) != 0) return false;
            PosixViewRegions.Replace(address, size, HostMemory.MEM_COMMIT, PosixViewRegions.RawProtection(protection));
            return true;
        }
    }

    public bool ReleasePrivate(ulong address, ulong size) => RestoreHole(address, size);

    public bool ChangeAccess(ulong address, ulong size, HostPageProtection protection)
    {
        lock (PosixViewRegions.Gate)
        {
            if (mprotect((nint)address, (nuint)size, GetNativeProtection(protection)) != 0) return false;
            PosixViewRegions.ChangeProtection(address, size, PosixViewRegions.RawProtection(protection));
            return true;
        }
    }

    public bool FreeOwnedRange(ulong address, ulong size)
    {
        lock (PosixViewRegions.Gate)
        {
            if (munmap((nint)address, (nuint)size) != 0) return false;
            PosixViewRegions.Replace(address, size, HostMemory.MEM_FREE_STATE, HostMemory.PAGE_NOACCESS);
            return true;
        }
    }

    private static bool RestoreHole(ulong address, ulong size)
    {
        lock (PosixViewRegions.Gate)
        {
            var ptr = mmap((nint)address, (nuint)size, PROT_NONE, MAP_PRIVATE | MAP_ANON | MAP_FIXED, -1, 0);
            if (ptr == MAP_FAILED || (ulong)ptr != address) return false;
            PosixViewRegions.Replace(address, size, HostMemory.MEM_RESERVE, HostMemory.PAGE_NOACCESS);
            return true;
        }
    }

    private static int OpenSharedObject()
    {
        var name = $"/sharpemu-{Environment.ProcessId}-{Interlocked.Increment(ref _sharedObjectCounter)}";
        var descriptor = shm_open(name, O_RDWR | O_CREAT | O_EXCL, 0x180);
        if (descriptor >= 0)
        {
            shm_unlink(name);
        }

        return descriptor;
    }

    private static void ReleaseBackingObject(HostBackingObject backing)
    {
        munmap((nint)backing.AliasBase, (nuint)backing.Size);
        close(backing.Descriptor);
    }

    private static int GetNativeProtection(HostPageProtection protection) => protection switch
    {
        HostPageProtection.NoAccess => PROT_NONE,
        HostPageProtection.ReadOnly => PROT_READ,
        HostPageProtection.ReadWrite => PROT_READ | PROT_WRITE,
        HostPageProtection.Execute => PROT_EXEC,
        HostPageProtection.ReadExecute => PROT_READ | PROT_EXEC,
        HostPageProtection.ReadWriteExecute => PROT_READ | PROT_WRITE | PROT_EXEC,
        HostPageProtection.ExecuteWriteCopy => PROT_READ | PROT_WRITE | PROT_EXEC,
        _ => throw new ArgumentOutOfRangeException(nameof(protection), protection, null),
    };

    [LibraryImport("libc", SetLastError = true)]
    private static partial nint mmap(nint address, nuint length, int protection, int flags, int descriptor, long offset);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int munmap(nint address, nuint length);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int mprotect(nint address, nuint length, int protection);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int ftruncate(int descriptor, long length);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int close(int descriptor);

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int memfd_create(string name, uint flags);

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int shm_open(string name, int flags, int mode);

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int shm_unlink(string name);
}
