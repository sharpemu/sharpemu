// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace SharpEmu.HLE.Host.Windows;

/// <summary>
/// Windows implementation over VirtualAlloc/VirtualFree/VirtualProtect/VirtualQuery.
/// Sealed so the JIT can devirtualize interface calls on fault-handling hot paths.
/// </summary>
internal sealed unsafe partial class WindowsHostMemory : IHostMemory
{
    private const uint MEM_COMMIT = 0x1000;
    private const uint MEM_RESERVE = 0x2000;
    private const uint MEM_REPLACE_PLACEHOLDER = 0x4000;
    private const uint MEM_RELEASE = 0x8000;
    private const uint MEM_COALESCE_PLACEHOLDERS = 0x01;
    private const uint MEM_PRESERVE_PLACEHOLDER = 0x02;
    private const uint MEM_RESERVE_PLACEHOLDER = 0x00040000;
    private const uint MEM_FREE = 0x10000;
    private const uint MEM_MAPPED = 0x40000;
    private const uint SEC_RESERVE = 0x04000000;
    private const uint FILE_MAP_ALL_ACCESS = 0x000F001F;
    private const ulong AllocationGranularity = 0x10000;

    private const uint PAGE_NOACCESS = 0x01;
    private const uint PAGE_READONLY = 0x02;
    private const uint PAGE_READWRITE = 0x04;
    private const uint PAGE_WRITECOPY = 0x08;
    private const uint PAGE_EXECUTE = 0x10;
    private const uint PAGE_EXECUTE_READ = 0x20;
    private const uint PAGE_EXECUTE_READWRITE = 0x40;
    private const uint PAGE_EXECUTE_WRITECOPY = 0x80;

    private readonly object _sharedViewGate = new();
    private readonly Dictionary<ulong, SharedView> _sharedViews = [];
    private readonly Dictionary<ulong, ulong> _placeholderRoots = [];

    public bool UsesPlaceholderReservations => true;

    public ulong Allocate(ulong desiredAddress, ulong size, HostPageProtection protection)
    {
        return (ulong)VirtualAlloc((void*)desiredAddress, (nuint)size, MEM_COMMIT | MEM_RESERVE, ToNativeProtection(protection));
    }

    public ulong Reserve(ulong desiredAddress, ulong size, HostPageProtection protection)
    {
        _ = protection;
        var result = (ulong)VirtualAlloc2(
            GetCurrentProcess(),
            (void*)desiredAddress,
            (nuint)size,
            MEM_RESERVE | MEM_RESERVE_PLACEHOLDER,
            PAGE_NOACCESS,
            null,
            0);
        if (result != 0)
        {
            lock (_sharedViewGate)
            {
                _placeholderRoots[result] = size;
            }
        }

        return result;
    }

    public IHostSharedMemory? CreateSharedMemory(ulong size)
    {
        if (size == 0)
        {
            return null;
        }

        var handle = CreateFileMappingW(
            (void*)(nint)(-1),
            null,
            PAGE_READWRITE | SEC_RESERVE,
            (uint)(size >> 32),
            (uint)size,
            null);
        return handle != 0 ? new WindowsSharedMemory(handle, size) : null;
    }

    public ulong MapSharedMemory(
        IHostSharedMemory sharedMemory,
        ulong desiredAddress,
        ulong size,
        ulong offset,
        HostPageProtection protection)
    {
        if (sharedMemory is not WindowsSharedMemory windowsSharedMemory ||
            size == 0 ||
            offset > windowsSharedMemory.Size ||
            size > windowsSharedMemory.Size - offset)
        {
            return 0;
        }

        var viewOffset = offset & ~(AllocationGranularity - 1);
        var delta = offset - viewOffset;
        if (delta > ulong.MaxValue - size)
        {
            return 0;
        }

        var unalignedViewSize = delta + size;
        if (unalignedViewSize > ulong.MaxValue - (AllocationGranularity - 1))
        {
            return 0;
        }

        var viewSize = (unalignedViewSize + AllocationGranularity - 1) & ~(AllocationGranularity - 1);
        var replacingPlaceholder = desiredAddress != 0 &&
            TryPreparePlaceholder(desiredAddress, size);
        void* requestedView = null;
        if (desiredAddress != 0 && !replacingPlaceholder)
        {
            if (desiredAddress < delta || ((desiredAddress - delta) & (AllocationGranularity - 1)) != 0)
            {
                return 0;
            }

            requestedView = (void*)(desiredAddress - delta);
        }

        var view = replacingPlaceholder
            ? MapViewOfFile3(
                windowsSharedMemory.Handle,
                GetCurrentProcess(),
                (void*)desiredAddress,
                offset,
                (nuint)size,
                MEM_REPLACE_PLACEHOLDER,
                ToNativeProtection(protection),
                null,
                0)
            : MapViewOfFileEx(
                windowsSharedMemory.Handle,
                FILE_MAP_ALL_ACCESS,
                (uint)(viewOffset >> 32),
                (uint)viewOffset,
                (nuint)viewSize,
                requestedView);
        if (view == null || (requestedView != null && view != requestedView))
        {
            TraceSharedMemoryFailure(
                replacingPlaceholder ? "MapViewOfFile3" : "MapViewOfFileEx",
                desiredAddress,
                size,
                offset);
            if (view != null)
            {
                CleanupFailedSharedView(view, replacingPlaceholder);
            }

            return 0;
        }

        var guestAddress = replacingPlaceholder ? (ulong)view : (ulong)view + delta;
        if (VirtualAlloc((void*)guestAddress, (nuint)size, MEM_COMMIT, ToNativeProtection(protection)) == null)
        {
            TraceSharedMemoryFailure("VirtualAlloc(commit view)", desiredAddress, size, offset);
            CleanupFailedSharedView(view, replacingPlaceholder);
            return 0;
        }

        lock (_sharedViewGate)
        {
            if (!_sharedViews.TryAdd(
                    guestAddress,
                    new SharedView((ulong)view, size, replacingPlaceholder)))
            {
                CleanupFailedSharedView(view, replacingPlaceholder);
                return 0;
            }
        }

        return guestAddress;
    }

    public bool UnmapSharedMemory(ulong address, ulong size)
    {
        lock (_sharedViewGate)
        {
            if (!_sharedViews.TryGetValue(address, out var view) || view.Size != size)
            {
                return false;
            }

            var unmapped = UnmapSharedView(
                (void*)view.Base,
                view.ReplacedPlaceholder);
            if (unmapped)
            {
                _sharedViews.Remove(address);
            }

            return unmapped;
        }
    }

    public bool UnmapReservedMemory(ulong address, ulong size)
    {
        if (size == 0 || ulong.MaxValue - address < size)
        {
            return TracePlaceholderFailure("invalid range", address, size);
        }

        lock (_sharedViewGate)
        {
            if (!TryPreparePlaceholderLocked(address, size))
            {
                return false;
            }

            return VirtualFree((void*)address, 0, MEM_RELEASE);
        }
    }

    public bool Commit(ulong address, ulong size, HostPageProtection protection)
    {
        var nativeProtection = ToNativeProtection(protection);
        if (VirtualAlloc((void*)address, (nuint)size, MEM_COMMIT, nativeProtection) != null)
        {
            return true;
        }

        lock (_sharedViewGate)
        {
            if (!TryPreparePlaceholderLocked(address, size))
            {
                return false;
            }

            return VirtualAlloc2(
                GetCurrentProcess(),
                (void*)address,
                (nuint)size,
                MEM_RESERVE | MEM_COMMIT | MEM_REPLACE_PLACEHOLDER,
                nativeProtection,
                null,
                0) != null;
        }
    }

    public bool Free(ulong address)
    {
        lock (_sharedViewGate)
        {
            if (_placeholderRoots.Remove(address, out var placeholderSize))
            {
                return ReleasePlaceholderTree(address, placeholderSize);
            }

            if (_sharedViews.TryGetValue(address, out var view))
            {
                var unmapped = UnmapSharedView(
                    (void*)view.Base,
                    view.ReplacedPlaceholder);
                if (unmapped)
                {
                    _sharedViews.Remove(address);
                }

                return unmapped;
            }
        }

        return VirtualFree((void*)address, 0, MEM_RELEASE);
    }

    public bool Protect(ulong address, ulong size, HostPageProtection protection, out uint rawOldProtection)
    {
        return VirtualProtect((void*)address, (nuint)size, ToNativeProtection(protection), out rawOldProtection);
    }

    public bool ProtectRaw(ulong address, ulong size, uint rawProtection, out uint rawOldProtection)
    {
        return VirtualProtect((void*)address, (nuint)size, rawProtection, out rawOldProtection);
    }

    public bool Query(ulong address, out HostRegionInfo info)
    {
        if (VirtualQuery((void*)address, out var mbi, (nuint)sizeof(MemoryBasicInformation64)) == 0)
        {
            info = default;
            return false;
        }

        info = new HostRegionInfo(
            mbi.BaseAddress,
            mbi.AllocationBase,
            mbi.RegionSize,
            ToRegionState(mbi.State),
            mbi.State,
            ToHostProtection(mbi.Protect),
            mbi.Protect,
            mbi.AllocationProtect);
        return true;
    }

    public void FlushInstructionCache(ulong address, ulong size)
    {
        FlushInstructionCache(GetCurrentProcess(), (void*)address, (nuint)size);
    }

    private static uint ToNativeProtection(HostPageProtection protection) => protection switch
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

    private static bool UnmapSharedView(void* view, bool replacedPlaceholder) =>
        replacedPlaceholder
            ? UnmapViewOfFile2(
                GetCurrentProcess(),
                view,
                MEM_PRESERVE_PLACEHOLDER)
            : UnmapViewOfFile(view);

    private static void CleanupFailedSharedView(
        void* view,
        bool replacedPlaceholder)
    {
        if (!UnmapSharedView(view, replacedPlaceholder))
        {
            throw new InvalidOperationException(
                "Failed to remove an untracked shared-memory view after mapping failed");
        }
    }

    private static HostRegionState ToRegionState(uint state) => state switch
    {
        MEM_COMMIT => HostRegionState.Committed,
        MEM_RESERVE => HostRegionState.Reserved,
        MEM_FREE => HostRegionState.Free,
        _ => HostRegionState.Free,
    };

    private static HostPageProtection ToHostProtection(uint rawProtection)
    {
        // Strip PAGE_GUARD/PAGE_NOCACHE/PAGE_WRITECOMBINE modifiers; callers needing
        // them compare HostRegionInfo.RawProtection directly.
        return (rawProtection & 0xFF) switch
        {
            PAGE_READONLY => HostPageProtection.ReadOnly,
            PAGE_READWRITE => HostPageProtection.ReadWrite,
            PAGE_WRITECOPY => HostPageProtection.ReadWrite,
            PAGE_EXECUTE => HostPageProtection.Execute,
            PAGE_EXECUTE_READ => HostPageProtection.ReadExecute,
            PAGE_EXECUTE_READWRITE => HostPageProtection.ReadWriteExecute,
            PAGE_EXECUTE_WRITECOPY => HostPageProtection.ExecuteWriteCopy,
            _ => HostPageProtection.NoAccess,
        };
    }

    private bool TryPreparePlaceholder(ulong address, ulong size)
    {
        lock (_sharedViewGate)
        {
            return TryPreparePlaceholderLocked(address, size);
        }
    }

    private static bool TryPreparePlaceholderLocked(ulong address, ulong size)
    {
        if (size == 0 || ulong.MaxValue - address < size)
        {
            return TracePlaceholderFailure("invalid range", address, size);
        }

        var end = address + size;
        if (VirtualQuery((void*)address, out var startInfo, (nuint)sizeof(MemoryBasicInformation64)) == 0 ||
            startInfo.State != MEM_RESERVE ||
            address < startInfo.BaseAddress ||
            address >= startInfo.BaseAddress + startInfo.RegionSize)
        {
            // An ordinary free address is expected to fall back to the aligned
            // MapViewOfFileEx path; it is not a placeholder failure worth tracing.
            return false;
        }

        // Preserve the original fast path. Windows accepts sub-allocation-sized
        // placeholder splits here even when VirtualQuery later exposes the
        // resulting placeholder as several smaller regions.
        var startInfoEnd = startInfo.BaseAddress + startInfo.RegionSize;
        if (end <= startInfoEnd)
        {
            if (address > startInfo.BaseAddress &&
                !VirtualFree(
                    (void*)startInfo.BaseAddress,
                    (nuint)(address - startInfo.BaseAddress),
                    MEM_RELEASE | MEM_PRESERVE_PLACEHOLDER))
            {
                return TracePlaceholderFailure("split contained start", address, size);
            }

            if (end < startInfoEnd &&
                !VirtualFree(
                    (void*)address,
                    (nuint)size,
                    MEM_RELEASE | MEM_PRESERVE_PLACEHOLDER))
            {
                return TracePlaceholderFailure("split contained end", address, size);
            }

            return true;
        }

        if (address > startInfo.BaseAddress &&
            !VirtualFree(
                (void*)startInfo.BaseAddress,
                (nuint)(address - startInfo.BaseAddress),
                MEM_RELEASE | MEM_PRESERVE_PLACEHOLDER))
        {
            return TracePlaceholderFailure("split spanning start", address, size);
        }

        if (VirtualQuery((void*)(end - 1), out var endInfo, (nuint)sizeof(MemoryBasicInformation64)) == 0 ||
            endInfo.State != MEM_RESERVE ||
            end - 1 < endInfo.BaseAddress ||
            end > endInfo.BaseAddress + endInfo.RegionSize)
        {
            return TracePlaceholderFailure("query spanning end", address, size);
        }

        var endInfoEnd = endInfo.BaseAddress + endInfo.RegionSize;
        if (end < endInfoEnd &&
            !VirtualFree(
                (void*)endInfo.BaseAddress,
                (nuint)(end - endInfo.BaseAddress),
                MEM_RELEASE | MEM_PRESERVE_PLACEHOLDER))
        {
            return TracePlaceholderFailure("split spanning end", address, size);
        }

        var cursor = address;
        var segmentCount = 0;
        while (cursor < end)
        {
            if (VirtualQuery((void*)cursor, out var segment, (nuint)sizeof(MemoryBasicInformation64)) == 0 ||
                segment.State != MEM_RESERVE ||
                segment.BaseAddress != cursor ||
                segment.RegionSize == 0 ||
                segment.RegionSize > end - cursor)
            {
                return TracePlaceholderFailure("walk", address, size);
            }

            cursor += segment.RegionSize;
            segmentCount++;
        }

        return segmentCount != 0 &&
            (VirtualFree(
                 (void*)address,
                 (nuint)size,
                 MEM_RELEASE | MEM_COALESCE_PLACEHOLDERS) ||
             TracePlaceholderFailure("coalesce", address, size));
    }

    private static bool TracePlaceholderFailure(string stage, ulong address, ulong size)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_VMEM"), "1", StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                $"[HOSTMEM] Placeholder preparation failed ({stage}): error={Marshal.GetLastPInvokeError()} " +
                $"va=0x{address:X16} size=0x{size:X}");
        }

        return false;
    }

    private bool ReleasePlaceholderTree(ulong address, ulong size)
    {
        var end = address + size;
        var cursor = address;
        var success = true;
        while (cursor < end &&
               VirtualQuery((void*)cursor, out var info, (nuint)sizeof(MemoryBasicInformation64)) != 0)
        {
            var next = Math.Min(end, info.BaseAddress + Math.Max(info.RegionSize, 0x1000));
            if (info.State == MEM_FREE)
            {
                cursor = next;
                continue;
            }

            if (info.Type == MEM_MAPPED)
            {
                success &= UnmapViewOfFile((void*)info.AllocationBase);
                foreach (var guestAddress in _sharedViews
                             .Where(entry => entry.Value.Base == info.AllocationBase)
                             .Select(entry => entry.Key)
                             .ToArray())
                {
                    _sharedViews.Remove(guestAddress);
                }
            }
            else
            {
                success &= VirtualFree((void*)info.AllocationBase, 0, MEM_RELEASE);
            }

            cursor = next;
        }

        return success;
    }

    private static void TraceSharedMemoryFailure(string operation, ulong address, ulong size, ulong offset)
    {
        if (string.Equals(
                Environment.GetEnvironmentVariable("SHARPEMU_LOG_VMEM"),
                "1",
                StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                $"[HOSTMEM] {operation} failed: error={Marshal.GetLastPInvokeError()} " +
                $"va=0x{address:X16} size=0x{size:X} offset=0x{offset:X}");
        }
    }

    private sealed class WindowsSharedMemory(nint handle, ulong size) : IHostSharedMemory
    {
        private nint _handle = handle;

        internal nint Handle => _handle;

        public ulong Size { get; } = size;

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _handle, 0);
            if (current != 0)
            {
                CloseHandle(current);
            }
        }
    }

    private readonly record struct SharedView(
        ulong Base,
        ulong Size,
        bool ReplacedPlaceholder);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial void* VirtualAlloc(void* lpAddress, nuint dwSize, uint flAllocationType, uint flProtect);

    [LibraryImport("kernelbase.dll", SetLastError = true)]
    private static partial void* VirtualAlloc2(
        void* process,
        void* baseAddress,
        nuint size,
        uint allocationType,
        uint pageProtection,
        void* extendedParameters,
        uint parameterCount);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileMappingW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateFileMappingW(
        void* hFile,
        void* lpFileMappingAttributes,
        uint flProtect,
        uint dwMaximumSizeHigh,
        uint dwMaximumSizeLow,
        string? lpName);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial void* MapViewOfFileEx(
        nint hFileMappingObject,
        uint dwDesiredAccess,
        uint dwFileOffsetHigh,
        uint dwFileOffsetLow,
        nuint dwNumberOfBytesToMap,
        void* lpBaseAddress);

    [LibraryImport("kernelbase.dll", SetLastError = true)]
    private static partial void* MapViewOfFile3(
        nint fileMapping,
        void* process,
        void* baseAddress,
        ulong offset,
        nuint viewSize,
        uint allocationType,
        uint pageProtection,
        void* extendedParameters,
        uint parameterCount);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnmapViewOfFile(void* lpBaseAddress);

    [LibraryImport("kernelbase.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnmapViewOfFile2(
        void* process,
        void* baseAddress,
        uint unmapFlags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint hObject);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool VirtualFree(void* lpAddress, nuint dwSize, uint dwFreeType);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool VirtualProtect(void* lpAddress, nuint dwSize, uint flNewProtect, out uint lpflOldProtect);

    [LibraryImport("kernel32.dll")]
    private static partial nuint VirtualQuery(void* lpAddress, out MemoryBasicInformation64 lpBuffer, nuint dwLength);

    [LibraryImport("kernel32.dll")]
    private static partial void* GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FlushInstructionCache(void* hProcess, void* lpBaseAddress, nuint dwSize);

    private struct MemoryBasicInformation64
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
}
