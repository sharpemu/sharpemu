// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace SharpEmu.HLE.Host.Windows;

internal sealed unsafe partial class WindowsHostViews : IHostViewMemory
{
    internal static bool FailAliasMapForTests;
    internal static bool FailBackingCommitForTests;
    internal static bool FailProtectForTests;

    private const uint MEM_COMMIT = 0x1000;
    private const uint MEM_RESERVE = 0x2000;
    private const uint MEM_RELEASE = 0x8000;
    private const uint MEM_FREE = 0x10000;
    private const uint MEM_COALESCE_PLACEHOLDERS = 0x1;
    private const uint MEM_PRESERVE_PLACEHOLDER = 0x2;
    private const uint MEM_REPLACE_PLACEHOLDER = 0x4000;
    private const uint MEM_RESERVE_PLACEHOLDER = 0x40000;
    private const uint PAGE_NOACCESS = 0x01;
    private const uint PAGE_READONLY = 0x02;
    private const uint PAGE_READWRITE = 0x04;
    private const uint PAGE_EXECUTE = 0x10;
    private const uint PAGE_EXECUTE_READ = 0x20;
    private const uint PAGE_EXECUTE_READWRITE = 0x40;
    private const uint PAGE_EXECUTE_WRITECOPY = 0x80;
    private const uint SEC_RESERVE = 0x4000000;
    private const ulong MEM_EXTENDED_PARAMETER_ADDRESS_REQUIREMENTS = 1;
    private static readonly nint InvalidHandle = -1;

    public WindowsHostViews()
    {
        GetSystemInfo(out var info);
        PageSize = info.PageSize;
        Granularity = info.AllocationGranularity;
    }

    public ulong PageSize { get; }

    public ulong Granularity { get; }

    public bool TryCreateBacking(ulong size, out HostBackingObject? backing, out HostViewFailure failure)
    {
        backing = null;
        failure = HostViewFailure.BackingUnavailable;
        if (size == 0)
        {
            return false;
        }

        var handle = CreateFileMappingW(InvalidHandle, null, PAGE_EXECUTE_READWRITE | SEC_RESERVE, (uint)(size >> 32), (uint)size, null);
        if (handle == 0)
        {
            return false;
        }

        var alias = FailAliasMapForTests ? null : MapBackingAlias(handle, size);
        if (alias == null)
        {
            CloseHandle(handle);
            return false;
        }

        backing = new HostBackingObject((ulong)alias, size, ReleaseBackingObject) { Handle = handle };
        failure = HostViewFailure.None;
        return true;
    }

    private static void* MapBackingAlias(nint handle, ulong size)
    {
        // Keep host-only storage above every address the guest allocator can return.
        var requirements = new MemAddressRequirements
        {
            LowestStartingAddress = (void*)GuestMemoryLayout.GuestAddressLimit,
            Alignment = 0,
        };
        var parameter = new MemExtendedParameter
        {
            Type = MEM_EXTENDED_PARAMETER_ADDRESS_REQUIREMENTS,
            Pointer = &requirements,
        };

        return MapViewOfFile3(
            handle,
            GetCurrentProcess(),
            null,
            0,
            (nuint)size,
            0,
            PAGE_READWRITE,
            &parameter,
            1);
    }

    public bool TryCommitBacking(HostBackingObject backing, ulong offset, ulong size)
    {
        lock (backing.Gate)
        {
            return !backing.IsDisposed &&
                   HostViewMemory.IsValidOffset(backing, offset, size, PageSize) &&
                   TryCommitBackingCore(backing, offset, size);
        }
    }

    public bool TryReserveFreeRegions(
        ulong startAddress,
        ulong endAddress,
        ulong minimumRegionSize,
        out IReadOnlyList<HostAddressRange> reservations)
    {
        var reserved = new List<HostAddressRange>();
        reservations = reserved;
        if (startAddress == 0 || startAddress >= endAddress)
        {
            return false;
        }

        var process = GetCurrentProcess();
        var current = startAddress;
        while (current < endAddress)
        {
            if (VirtualQuery((void*)current, out var info, (nuint)sizeof(MemoryBasicInformation)) == 0)
            {
                RollBackReservations(reserved);
                reservations = Array.Empty<HostAddressRange>();
                return false;
            }

            var nativeEnd = info.RegionSize > ulong.MaxValue - info.BaseAddress
                ? ulong.MaxValue
                : info.BaseAddress + info.RegionSize;
            var regionEnd = Math.Min(endAddress, nativeEnd);
            if (regionEnd <= current)
            {
                RollBackReservations(reserved);
                reservations = Array.Empty<HostAddressRange>();
                return false;
            }

            var reserveStart = AlignUp(Math.Max(startAddress, info.BaseAddress), Granularity);
            var reserveEnd = AlignDown(regionEnd, Granularity);
            if (info.State == MEM_FREE && reserveStart != 0 && reserveEnd > reserveStart &&
                reserveEnd - reserveStart > minimumRegionSize)
            {
                var size = reserveEnd - reserveStart;
                var ptr = VirtualAlloc2(
                    process,
                    (void*)reserveStart,
                    (nuint)size,
                    MEM_RESERVE | MEM_RESERVE_PLACEHOLDER,
                    PAGE_NOACCESS,
                    null,
                    0);
                if ((ulong)ptr != reserveStart)
                {
                    if (ptr != null)
                    {
                        VirtualFree(ptr, 0, MEM_RELEASE);
                    }

                    RollBackReservations(reserved);
                    reservations = Array.Empty<HostAddressRange>();
                    return false;
                }

                reserved.Add(new HostAddressRange(reserveStart, size));
            }

            current = regionEnd;
        }

        return true;
    }

    private static void RollBackReservations(List<HostAddressRange> reservations)
    {
        for (var index = reservations.Count - 1; index >= 0; index--)
        {
            VirtualFree((void*)reservations[index].Address, 0, MEM_RELEASE);
        }

        reservations.Clear();
    }

    public ulong ReserveHole(ulong address, ulong size)
    {
        if (!HostViewMemory.IsValidRange(address, size) || address % Granularity != 0)
        {
            return 0;
        }

        var ptr = VirtualAlloc2(GetCurrentProcess(), (void*)address, (nuint)size, MEM_RESERVE | MEM_RESERVE_PLACEHOLDER, PAGE_NOACCESS, null, 0);
        if (ptr == null)
        {
            return 0;
        }

        if ((ulong)ptr != address)
        {
            VirtualFree(ptr, 0, MEM_RELEASE);
            return 0;
        }

        return address;
    }

    public IReadOnlyList<HostAddressRange> ReserveFreeAddressRanges(ulong start, ulong end, ulong minimumSize)
    {
        var reserved = new List<HostAddressRange>();
        if (start >= end || minimumSize == 0)
        {
            return reserved;
        }

        var current = start;
        while (current < end)
        {
            if (VirtualQuery((void*)current, out var info, (nuint)sizeof(MemoryBasicInformation)) == 0)
            {
                break;
            }

            var regionEnd = Math.Min(end, info.BaseAddress + info.RegionSize);
            if (info.State == MEM_FREE)
            {
                var reserveStart = AlignUp(Math.Max(start, info.BaseAddress), Granularity);
                var reserveEnd = AlignDown(regionEnd, Granularity);
                if (reserveEnd > reserveStart && reserveEnd - reserveStart >= minimumSize)
                {
                    var size = reserveEnd - reserveStart;
                    if (ReserveHole(reserveStart, size) == reserveStart)
                    {
                        reserved.Add(new HostAddressRange(reserveStart, size));
                    }
                }
            }

            if (regionEnd <= current)
            {
                break;
            }
            current = regionEnd;
        }

        return reserved;
    }

    public bool TryAdoptPlaceholder(ulong address, ulong size)
    {
        if (!HostViewMemory.IsValidRange(address, size) ||
            address % Granularity != 0 || size % Granularity != 0 ||
            VirtualQuery((void*)address, out var info, (nuint)sizeof(MemoryBasicInformation)) == 0)
        {
            return false;
        }

        // VirtualQuery cannot distinguish an ordinary reservation from a placeholder.
        // The trusted launch handshake proves that the suspended parent created this
        // exact allocation with MEM_RESERVE_PLACEHOLDER; these checks ensure that no
        // intervening allocation changed or split it before ownership is published.
        return info.BaseAddress == address &&
               info.AllocationBase == address &&
               info.AllocationProtect == PAGE_NOACCESS &&
               info.RegionSize == size &&
               info.State == MEM_RESERVE;
    }

    public bool SplitHole(ulong address, ulong size) =>
        VirtualFree((void*)address, (nuint)size, MEM_RELEASE | MEM_PRESERVE_PLACEHOLDER);

    public bool JoinHoles(ulong address, ulong size) =>
        VirtualFree((void*)address, (nuint)size, MEM_RELEASE | MEM_COALESCE_PLACEHOLDERS);

    public bool FreeHole(ulong address, ulong size) =>
        VirtualFree((void*)address, 0, MEM_RELEASE);

    public bool TryMapView(HostBackingObject backing, ulong address, ulong offset, ulong size, HostPageProtection protection, out HostViewFailure failure)
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

            // A view cannot map as no-access. Map writable, then apply the protection.
            var mapProtection = protection == HostPageProtection.NoAccess ? PAGE_READWRITE : GetNativeProtection(protection);
            var process = GetCurrentProcess();
            var ptr = MapViewOfFile3(backing.Handle, process, (void*)address, offset, (nuint)size, MEM_REPLACE_PLACEHOLDER, mapProtection, null, 0);
            if (ptr == null)
            {
                failure = HostViewFailure.PlaceholderMapFailed;
                return false;
            }

            if ((ulong)ptr != address)
            {
                UnmapViewOfFile2(process, ptr, MEM_PRESERVE_PLACEHOLDER);
                failure = HostViewFailure.WrongHostAddress;
                return false;
            }

            if (!TryCommitBackingCore(backing, offset, size))
            {
                UnmapViewOfFile2(process, ptr, MEM_PRESERVE_PLACEHOLDER);
                failure = HostViewFailure.BackingCommitFailed;
                return false;
            }

            if (FailProtectForTests || !VirtualProtect(ptr, (nuint)size, GetNativeProtection(protection), out _))
            {
                UnmapViewOfFile2(process, ptr, MEM_PRESERVE_PLACEHOLDER);
                failure = HostViewFailure.ProtectFailed;
                return false;
            }

            failure = HostViewFailure.None;
            return true;
        }
    }

    public bool UnmapView(ulong address, ulong size) =>
        UnmapViewOfFile2(GetCurrentProcess(), (void*)address, MEM_PRESERVE_PLACEHOLDER);

    public bool CommitPrivate(ulong address, ulong size, HostPageProtection protection)
    {
        var ptr = VirtualAlloc2(GetCurrentProcess(), (void*)address, (nuint)size, MEM_RESERVE | MEM_COMMIT | MEM_REPLACE_PLACEHOLDER, GetNativeProtection(protection), null, 0);
        if (ptr == null)
        {
            return false;
        }

        if ((ulong)ptr != address)
        {
            VirtualFree(ptr, 0, MEM_RELEASE);
            return false;
        }

        return true;
    }

    public bool ReleasePrivate(ulong address, ulong size) =>
        VirtualFree((void*)address, (nuint)size, MEM_RELEASE | MEM_PRESERVE_PLACEHOLDER);

    public bool ChangeAccess(ulong address, ulong size, HostPageProtection protection) =>
        VirtualProtect((void*)address, (nuint)size, GetNativeProtection(protection), out _);

    public bool FreeOwnedRange(ulong address, ulong size)
    {
        var end = address + size;
        var current = address;
        while (current < end)
        {
            if (VirtualQuery((void*)current, out var info, (nuint)sizeof(MemoryBasicInformation)) == 0)
            {
                return false;
            }

            if (info.State == MEM_COMMIT)
            {
                if (!VirtualFree((void*)info.BaseAddress, (nuint)info.RegionSize, MEM_RELEASE | MEM_PRESERVE_PLACEHOLDER))
                {
                    return false;
                }

                continue;
            }

            if (info.State == MEM_RESERVE && !VirtualFree((void*)info.AllocationBase, 0, MEM_RELEASE))
            {
                return false;
            }

            current = info.BaseAddress + info.RegionSize;
        }

        return true;
    }

    private static bool TryCommitBackingCore(HostBackingObject backing, ulong offset, ulong size)
    {
        if (FailBackingCommitForTests)
        {
            return false;
        }

        var address = backing.AliasBase + offset;
        var ptr = VirtualAlloc2(GetCurrentProcess(), (void*)address, (nuint)size, MEM_COMMIT, PAGE_READWRITE, null, 0);
        return ptr != null && (ulong)ptr == address;
    }

    private static void ReleaseBackingObject(HostBackingObject backing)
    {
        UnmapViewOfFile((void*)backing.AliasBase);
        CloseHandle(backing.Handle);
    }

    private static uint GetNativeProtection(HostPageProtection protection) => protection switch
    {
        HostPageProtection.NoAccess => PAGE_NOACCESS,
        HostPageProtection.ReadOnly => PAGE_READONLY,
        HostPageProtection.ReadWrite => PAGE_READWRITE,
        HostPageProtection.Execute => PAGE_EXECUTE,
        HostPageProtection.ReadExecute => PAGE_EXECUTE_READ,
        HostPageProtection.ReadWriteExecute => PAGE_EXECUTE_READWRITE,
        HostPageProtection.ExecuteWriteCopy => PAGE_EXECUTE_WRITECOPY,
        _ => throw new ArgumentOutOfRangeException(nameof(protection), protection, null),
    };

    private static ulong AlignDown(ulong value, ulong alignment) => value / alignment * alignment;

    private static ulong AlignUp(ulong value, ulong alignment)
    {
        var remainder = value % alignment;
        return remainder == 0 ? value : value <= ulong.MaxValue - (alignment - remainder)
            ? value + alignment - remainder
            : 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemInfo
    {
        public ushort ProcessorArchitecture;
        public ushort Reserved;
        public uint PageSize;
        public void* MinimumApplicationAddress;
        public void* MaximumApplicationAddress;
        public nuint ActiveProcessorMask;
        public uint NumberOfProcessors;
        public uint ProcessorType;
        public uint AllocationGranularity;
        public ushort ProcessorLevel;
        public ushort ProcessorRevision;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public ulong BaseAddress;
        public ulong AllocationBase;
        public uint AllocationProtect;
        public uint Alignment1;
        public ulong RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
        public uint Alignment2;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemAddressRequirements
    {
        public void* LowestStartingAddress;
        public void* HighestEndingAddress;
        public nuint Alignment;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemExtendedParameter
    {
        public ulong Type;
        public void* Pointer;
    }

    [LibraryImport("kernel32.dll")]
    private static partial nuint VirtualQuery(void* address, out MemoryBasicInformation info, nuint length);

    [LibraryImport("kernelbase.dll", SetLastError = true)]
    private static partial void* VirtualAlloc2(void* process, void* baseAddress, nuint size, uint allocationType, uint pageProtection, void* extendedParameters, uint parameterCount);

    [LibraryImport("kernelbase.dll", SetLastError = true)]
    private static partial void* MapViewOfFile3(nint fileMapping, void* process, void* baseAddress, ulong offset, nuint viewSize, uint allocationType, uint pageProtection, void* extendedParameters, uint parameterCount);

    [LibraryImport("kernelbase.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnmapViewOfFile2(void* process, void* baseAddress, uint unmapFlags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint CreateFileMappingW(nint file, void* attributes, uint protect, uint maximumSizeHigh, uint maximumSizeLow, ushort* name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnmapViewOfFile(void* baseAddress);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool VirtualFree(void* address, nuint size, uint freeType);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool VirtualProtect(void* address, nuint size, uint newProtect, out uint oldProtect);

    [LibraryImport("kernel32.dll")]
    private static partial void GetSystemInfo(out SystemInfo info);

    [LibraryImport("kernel32.dll")]
    private static partial void* GetCurrentProcess();
}
