// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.HLE.GuestMemory;

namespace SharpEmu.HLE.Host.Windows;

internal sealed unsafe partial class WindowsHostViews : IHostViewMemory
{
    internal static bool FailAliasMapForTests;
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
    private const uint SEC_COMMIT = 0x8000000;
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint GENERIC_EXECUTE = 0x20000000;
    private const uint FILE_SHARE_READ = 0x1;
    private const uint FILE_SHARE_WRITE = 0x2;
    private const uint FILE_SHARE_DELETE = 0x4;
    private const uint CREATE_ALWAYS = 2;
    private const uint FILE_ATTRIBUTE_TEMPORARY = 0x100;
    private const uint FILE_FLAG_DELETE_ON_CLOSE = 0x04000000;
    private const uint FSCTL_SET_SPARSE = 0x900C4;
    private const uint FILE_BEGIN = 0;
    private static readonly nint InvalidHandle = -1;

    // A pagefile-backed SEC_COMMIT section charges the whole guest backing
    // against the system commit limit at startup (~16.4 GiB). A sparse
    // delete-on-close file mapping commits page tables up front and file pages
    // only as the guest touches them, which lets the same 16 GiB guest address
    // space boot on hosts that cannot supply that much commit.
    //
    // The committed section is the default: it is the path the project ships
    // today and the one with the most testing. SHARPEMU_GUEST_BACKING=sparse
    // opts into demand commit. `auto` is an experimental opt-in that selects
    // sparse when the system has less than 20 GiB of available commit, i.e.
    // when the 16.4 GiB section is close to not fitting.
    private const ulong SparseAutoCommitReserveBytes = 20UL * 1024 * 1024 * 1024;
    private static readonly (bool Sparse, string Description) BackingMode = ResolveBackingMode();
    private static bool UseSparseBacking => BackingMode.Sparse;
    private static string BackingModeDescription => BackingMode.Description;

    private static (bool Sparse, string Description) ResolveBackingMode()
    {
        var value = Environment.GetEnvironmentVariable("SHARPEMU_GUEST_BACKING")?.Trim();
        if (string.Equals(value, "sparse", StringComparison.OrdinalIgnoreCase))
        {
            return (true, "requested");
        }

        if (string.Equals(value, "commit", StringComparison.OrdinalIgnoreCase))
        {
            return (false, "requested");
        }

        if (string.IsNullOrEmpty(value))
        {
            return (false, "default");
        }

        if (!string.Equals(value, "auto", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine(
                $"[MEMORY][WARN] Unknown SHARPEMU_GUEST_BACKING value '{value}'; using the committed section.");
            return (false, "fallback");
        }

        if (GetAvailableCommitBytes(out var availableCommit))
        {
            return availableCommit < SparseAutoCommitReserveBytes
                ? (true, $"auto, available_commit_mib={availableCommit / (1024 * 1024)}")
                : (false, $"auto, available_commit_mib={availableCommit / (1024 * 1024)}");
        }

        // If the commit query is unavailable, fall back to physical RAM as a
        // coarse signal rather than silently choosing the path that may OOM.
        if (GetPhysicallyInstalledSystemMemory(out var installedKb) &&
            installedKb > 0 &&
            installedKb <= 32UL * 1024 * 1024)
        {
            return (true, $"auto, physical_mib={installedKb / 1024}");
        }

        return (false, "auto, commit query unavailable and physical memory above 32 GiB");
    }

    private static bool GetAvailableCommitBytes(out ulong availableCommit)
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref status))
        {
            availableCommit = status.ullAvailPageFile;
            return true;
        }

        availableCommit = 0;
        return false;
    }

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

        var fileHandle = InvalidHandle;
        var sparse = false;
        if (UseSparseBacking)
        {
            try
            {
                sparse = TryCreateSparseFile(size, out fileHandle);
                if (!sparse)
                {
                    Console.Error.WriteLine(
                        "[MEMORY][WARN] Sparse guest backing could not be created; falling back to a committed section.");
                }
            }
            catch (Exception exception)
            {
                fileHandle = InvalidHandle;
                Console.Error.WriteLine(
                    $"[MEMORY][WARN] Sparse guest backing failed ({exception.Message}); falling back to a committed section.");
            }
        }

        var handle = sparse
            ? CreateFileMappingW(fileHandle, null, PAGE_EXECUTE_READWRITE, (uint)(size >> 32), (uint)size, null)
            : CreateFileMappingW(InvalidHandle, null, PAGE_EXECUTE_READWRITE | SEC_COMMIT, (uint)(size >> 32), (uint)size, null);
        if (handle == 0)
        {
            if (fileHandle != InvalidHandle)
            {
                CloseHandle(fileHandle);
            }

            return false;
        }

        // Keep the host alias above the guest mapping range.
        var requirements = new MemoryAddressRequirements
        {
            LowestStartingAddress = (void*)(WindowsGuestAddressReservation.DataStart + WindowsGuestAddressReservation.DataSize),
        };
        var parameter = new MemoryExtendedParameter { Type = 1, Pointer = &requirements };
        var alias = FailAliasMapForTests ? null : MapViewOfFile3(
            handle, GetCurrentProcess(), null, 0, (nuint)size, 0, PAGE_READWRITE, &parameter, 1);
        if (alias == null)
        {
            CloseHandle(handle);
            if (fileHandle != InvalidHandle)
            {
                CloseHandle(fileHandle);
            }

            return false;
        }

        backing = new HostBackingObject((ulong)alias, size, ReleaseBackingObject)
        {
            Handle = handle,
            FileHandle = sparse ? fileHandle : InvalidHandle,
        };
        if (sparse)
        {
            Console.Error.WriteLine(
                $"[MEMORY][INFO] Guest backing mode=sparse size_mib={size / (1024 * 1024)} " +
                $"({BackingModeDescription}; demand-committed temporary file).");
        }

        failure = HostViewFailure.None;
        return true;
    }

    // Creates a delete-on-close temporary file, marks it sparse, and sizes it to
    // the guest backing. The file removes itself when the last handle closes,
    // including after a crash; no guest contents are left on disk.
    private static bool TryCreateSparseFile(ulong size, out nint handle)
    {
        handle = InvalidHandle;
        var path = Path.Combine(
            Path.GetTempPath(),
            $"sharpemu-guest-backing-{Environment.ProcessId}-{Guid.NewGuid():N}.bin");
        var created = CreateFileW(
            path,
            GENERIC_READ | GENERIC_WRITE | GENERIC_EXECUTE,
            FILE_SHARE_READ | FILE_SHARE_DELETE,
            0,
            CREATE_ALWAYS,
            FILE_ATTRIBUTE_TEMPORARY | FILE_FLAG_DELETE_ON_CLOSE,
            0);
        if (created == InvalidHandle)
        {
            return false;
        }

        // The file is sparse, but the guest can still make it grow to the full
        // backing size. Do not start on a volume that cannot hold that worst
        // case; the caller will fall back to the committed section.
        try
        {
            var root = Path.GetPathRoot(path);
            if (!string.IsNullOrEmpty(root))
            {
                var free = new DriveInfo(root).AvailableFreeSpace;
                if (free < (long)size || free - (long)size < 256L * 1024 * 1024)
                {
                    CloseHandle(created);
                    Console.Error.WriteLine(
                        $"[MEMORY][WARN] Sparse guest backing skipped: volume has {free / (1024 * 1024)} MiB free, " +
                        $"needs {size / (1024 * 1024)} MiB plus 256 MiB headroom.");
                    return false;
                }
            }
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            CloseHandle(created);
            Console.Error.WriteLine($"[MEMORY][WARN] Sparse guest backing skipped: free-space query failed ({exception.Message}).");
            return false;
        }

        if (!DeviceIoControl(created, FSCTL_SET_SPARSE, 0, 0, 0, 0, out _, 0) ||
            !SetFilePointerEx(created, (long)size, out _, FILE_BEGIN) ||
            !SetEndOfFile(created))
        {
            CloseHandle(created);
            return false;
        }

        handle = created;
        return true;
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
            ReservationDiagnostics.Record("native-reserve-failed", address, size, Granularity,
                error: Marshal.GetLastPInvokeError());
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

    public bool SplitHole(ulong address, ulong size) =>
        VirtualFree((void*)address, (nuint)size, MEM_RELEASE | MEM_PRESERVE_PLACEHOLDER);

    public bool JoinHoles(ulong address, ulong size)
    {
        if (VirtualQuery((void*)address, out var region, (nuint)sizeof(MemoryBasicInformation)) != 0 &&
            region.BaseAddress == address && region.AllocationBase == address &&
            region.State == MEM_RESERVE && region.RegionSize == size)
            return true;
        return VirtualFree((void*)address, (nuint)size, MEM_RELEASE | MEM_COALESCE_PLACEHOLDERS);
    }

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
            void* ptr;
            using (GuestMemoryProfile.Measure(GuestMemoryProfile.Operation.HostViewNativeMap))
                ptr = MapViewOfFile3(backing.Handle, process, (void*)address, offset, (nuint)size, MEM_REPLACE_PLACEHOLDER, mapProtection, null, 0);
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

            if (protection == HostPageProtection.NoAccess)
            {
                bool protectedView;
                using (GuestMemoryProfile.Measure(GuestMemoryProfile.Operation.HostViewProtection))
                    protectedView = !FailProtectForTests && VirtualProtect(ptr, (nuint)size, PAGE_NOACCESS, out _);
                if (!protectedView)
                {
                    UnmapViewOfFile2(process, ptr, MEM_PRESERVE_PLACEHOLDER);
                    failure = HostViewFailure.ProtectFailed;
                    return false;
                }
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

    private static ulong AlignUp(ulong value, ulong alignment)
    {
        var remainder = value % alignment;
        return remainder == 0 ? value : value + (alignment - remainder);
    }

    private static ulong AlignDown(ulong value, ulong alignment) => value - value % alignment;

    private static void ReleaseBackingObject(HostBackingObject backing)
    {
        UnmapViewOfFile((void*)backing.AliasBase);
        CloseHandle(backing.Handle);
        if (backing.FileHandle != InvalidHandle)
        {
            // FILE_FLAG_DELETE_ON_CLOSE removes the sparse backing here.
            CloseHandle(backing.FileHandle);
        }
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

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateFileW(string name, uint access, uint share, nint security, uint creation, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(nint device, uint code, nint inBuffer, uint inSize, nint outBuffer, uint outSize, out uint returned, nint overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFilePointerEx(nint file, long distance, out long newPosition, uint method);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetEndOfFile(nint file);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetPhysicallyInstalledSystemMemory(out ulong totalKilobytes);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryAddressRequirements
    {
        public void* LowestStartingAddress;
        public void* HighestEndingAddress;
        public nuint Alignment;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryExtendedParameter
    {
        public ulong Type;
        public void* Pointer;
    }

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
