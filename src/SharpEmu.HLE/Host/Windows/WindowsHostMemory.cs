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
    private const uint MEM_RELEASE = 0x8000;
    private const uint MEM_FREE = 0x10000;

    private const uint PAGE_NOACCESS = 0x01;
    private const uint PAGE_READONLY = 0x02;
    private const uint PAGE_READWRITE = 0x04;
    private const uint PAGE_WRITECOPY = 0x08;
    private const uint PAGE_EXECUTE = 0x10;
    private const uint PAGE_EXECUTE_READ = 0x20;
    private const uint PAGE_EXECUTE_READWRITE = 0x40;
    private const uint PAGE_EXECUTE_WRITECOPY = 0x80;

    public ulong Allocate(ulong desiredAddress, ulong size, HostPageProtection protection)
    {
        return (ulong)VirtualAlloc((void*)desiredAddress, (nuint)size, MEM_COMMIT | MEM_RESERVE, ToNativeProtection(protection));
    }

    public ulong Reserve(ulong desiredAddress, ulong size, HostPageProtection protection)
    {
        return (ulong)VirtualAlloc((void*)desiredAddress, (nuint)size, MEM_RESERVE, ToNativeProtection(protection));
    }

    public bool Commit(ulong address, ulong size, HostPageProtection protection)
    {
        return VirtualAlloc((void*)address, (nuint)size, MEM_COMMIT, ToNativeProtection(protection)) != null;
    }

    public bool Free(ulong address)
    {
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

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial void* VirtualAlloc(void* lpAddress, nuint dwSize, uint flAllocationType, uint flProtect);

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
