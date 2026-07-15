// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace SharpEmu.HLE;

/// <summary>
/// Cross-platform host virtual memory API with Win32 semantics.
/// On Windows this forwards directly to kernel32. On POSIX systems it is
/// implemented over mmap/mprotect/munmap with a shadow region table that
/// answers VirtualQuery-style questions and tracks page protections.
/// POSIX anonymous mappings are demand-paged by the kernel, so Win32
/// "reserve-only" regions are mapped as committed memory directly and
/// commit requests become protection changes.
/// </summary>
public static unsafe class HostMemory
{
    public const uint MEM_COMMIT = 0x1000;
    public const uint MEM_RESERVE = 0x2000;
    public const uint MEM_RELEASE = 0x8000;
    public const uint MEM_FREE_STATE = 0x10000;
    public const uint MEM_PRIVATE = 0x20000;

    public const uint PAGE_NOACCESS = 0x01;
    public const uint PAGE_READONLY = 0x02;
    public const uint PAGE_READWRITE = 0x04;
    public const uint PAGE_EXECUTE = 0x10;
    public const uint PAGE_EXECUTE_READ = 0x20;
    public const uint PAGE_EXECUTE_READWRITE = 0x40;

    private const ulong PageSize = 0x1000;

    /// <summary>Win32 MEMORY_BASIC_INFORMATION (64-bit) layout.</summary>
    public struct BasicInfo
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

    public static void* Alloc(void* address, nuint size, uint allocationType, uint protect)
    {
        if (OperatingSystem.IsWindows())
        {
            return Win32VirtualAlloc(address, size, allocationType, protect);
        }

        return Posix.Alloc(address, size, allocationType, protect);
    }

    public static bool Free(void* address, nuint size, uint freeType)
    {
        if (OperatingSystem.IsWindows())
        {
            return Win32VirtualFree(address, size, freeType);
        }

        return Posix.Free(address, size, freeType);
    }

    public static bool Protect(void* address, nuint size, uint newProtect, out uint oldProtect)
    {
        if (OperatingSystem.IsWindows())
        {
            return Win32VirtualProtect(address, size, newProtect, out oldProtect);
        }

        return Posix.Protect(address, size, newProtect, out oldProtect);
    }

    public static nuint Query(void* address, out BasicInfo info)
    {
        if (OperatingSystem.IsWindows())
        {
            return Win32VirtualQuery(address, out info, (nuint)sizeof(BasicInfo));
        }

        return Posix.Query(address, out info);
    }

    public static void FlushInstructionCache(void* address, nuint size)
    {
        if (OperatingSystem.IsWindows())
        {
            Win32FlushInstructionCache(Win32GetCurrentProcess(), address, size);
            return;
        }

        // The emulator only executes x86-64 guest code, so a non-Windows host
        // is either x86-64 (including Rosetta 2 translation) with a coherent
        // instruction cache, or would need sys_icache_invalidate for a future
        // arm64 recompiler. Nothing to do today.
    }

    [DllImport("kernel32.dll", EntryPoint = "VirtualAlloc", SetLastError = true)]
    private static extern void* Win32VirtualAlloc(void* lpAddress, nuint dwSize, uint flAllocationType, uint flProtect);

    [DllImport("kernel32.dll", EntryPoint = "VirtualFree", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Win32VirtualFree(void* lpAddress, nuint dwSize, uint dwFreeType);

    [DllImport("kernel32.dll", EntryPoint = "VirtualProtect", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Win32VirtualProtect(void* lpAddress, nuint dwSize, uint flNewProtect, out uint lpflOldProtect);

    [DllImport("kernel32.dll", EntryPoint = "VirtualQuery")]
    private static extern nuint Win32VirtualQuery(void* lpAddress, out BasicInfo lpBuffer, nuint dwLength);

    [DllImport("kernel32.dll", EntryPoint = "GetCurrentProcess")]
    private static extern void* Win32GetCurrentProcess();

    [DllImport("kernel32.dll", EntryPoint = "FlushInstructionCache")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Win32FlushInstructionCache(void* hProcess, void* lpBaseAddress, nuint dwSize);

    private static class Posix
    {
        private const int PROT_NONE = 0x0;
        private const int PROT_READ = 0x1;
        private const int PROT_WRITE = 0x2;
        private const int PROT_EXEC = 0x4;

        private const int MAP_PRIVATE = 0x02;
        private const int MAP_FIXED = 0x10;
        private static readonly int MAP_ANON = OperatingSystem.IsMacOS() ? 0x1000 : 0x20;
        private static readonly int MAP_NORESERVE = OperatingSystem.IsMacOS() ? 0 : 0x4000;

        // Linux-only: fail instead of clobbering an existing mapping.
        private const int MAP_FIXED_NOREPLACE = 0x100000;

        private static readonly nint MAP_FAILED = -1;

        private static readonly object Gate = new();
        private static readonly SortedList<ulong, Region> Regions = new();

        private sealed class Region
        {
            public ulong Base;
            public ulong Size;
            public uint DefaultProtect;
            public Dictionary<ulong, uint>? PageProtects;

            public ulong End => Base + Size;

            public uint ProtectAt(ulong pageAddress)
            {
                if (PageProtects is not null && PageProtects.TryGetValue(pageAddress, out var overriden))
                {
                    return overriden;
                }

                return DefaultProtect;
            }
        }

        public static void* Alloc(void* address, nuint size, uint allocationType, uint protect)
        {
            if (size == 0)
            {
                return null;
            }

            var alignedSize = AlignUp((ulong)size, PageSize);

            lock (Gate)
            {
                if (allocationType == MEM_COMMIT && address != null &&
                    TryFindRegionLocked((ulong)address, out var existing))
                {
                    // Note: MEM_RESERVE requests that overlap an existing
                    // region must fail like Win32 does; only a pure commit
                    // may target pages inside a tracked mapping.
                    // Commit inside an existing mapping: the pages are already
                    // backed (demand paged), so only apply the protection.
                    var start = AlignDown((ulong)address, PageSize);
                    var end = Math.Min(existing.End, AlignUp((ulong)address + alignedSize, PageSize));
                    if (end <= start)
                    {
                        return null;
                    }

                    if (mprotect((nint)start, (nuint)(end - start), ToPosixProtect(protect)) != 0)
                    {
                        return null;
                    }

                    SetProtectRangeLocked(existing, start, end - start, protect);
                    return address;
                }

                if ((allocationType & MEM_RESERVE) == 0)
                {
                    // MEM_COMMIT alone outside any known region is invalid here.
                    return null;
                }

                var posixProtect = ToPosixProtect(protect);
                var flags = MAP_PRIVATE | MAP_ANON;
                if ((allocationType & MEM_COMMIT) == 0)
                {
                    // Reserve-only: keep the requested protection so the region
                    // is usable without a separate commit step, but tell the
                    // kernel not to account swap for it where supported.
                    flags |= MAP_NORESERVE;
                }

                nint result;
                if (address != null)
                {
                    // Win32 maps at exactly the requested address or fails
                    // without touching existing mappings. Fail up front on
                    // any overlap we track, then place the mapping: Linux
                    // gets MAP_FIXED_NOREPLACE (fails cleanly on host
                    // mappings too). Darwin lacks NOREPLACE and plain
                    // MAP_FIXED would silently clobber untracked host
                    // memory (dyld, the runtime's JIT heap, Rosetta), so
                    // pass the address as a hint instead -- the kernel
                    // honors it when the range is free and relocates the
                    // mapping otherwise, which we treat as failure.
                    if (OverlapsTrackedRegionLocked((ulong)address, alignedSize))
                    {
                        Trace($"exact overlap: addr=0x{(ulong)address:X16} size=0x{alignedSize:X}");
                        return null;
                    }

                    var exactFlags = OperatingSystem.IsMacOS() ? flags : flags | MAP_FIXED_NOREPLACE;
                    result = mmap((nint)address, (nuint)alignedSize, posixProtect, exactFlags, -1, 0);
                    if (result == MAP_FAILED || (ulong)result != (ulong)address)
                    {
                        Trace($"exact mmap failed: addr=0x{(ulong)address:X16} got=0x{(ulong)result:X16} size=0x{alignedSize:X} errno={Marshal.GetLastPInvokeError()}");
                        if (result != MAP_FAILED)
                        {
                            munmap(result, (nuint)alignedSize);
                        }

                        return null;
                    }
                }
                else
                {
                    result = mmap(0, (nuint)alignedSize, posixProtect, flags, -1, 0);
                    if (result == MAP_FAILED)
                    {
                        Trace($"mmap failed: size=0x{alignedSize:X} errno={Marshal.GetLastPInvokeError()}");
                        return null;
                    }
                }

                Regions[(ulong)result] = new Region
                {
                    Base = (ulong)result,
                    Size = alignedSize,
                    DefaultProtect = protect
                };

                return (void*)result;
            }
        }

        public static bool Free(void* address, nuint size, uint freeType)
        {
            _ = size;
            _ = freeType;

            lock (Gate)
            {
                if (!Regions.TryGetValue((ulong)address, out var region))
                {
                    return false;
                }

                Regions.Remove((ulong)address);
                return munmap((nint)address, (nuint)region.Size) == 0;
            }
        }

        public static bool Protect(void* address, nuint size, uint newProtect, out uint oldProtect)
        {
            oldProtect = PAGE_NOACCESS;
            if (size == 0)
            {
                return false;
            }

            var start = AlignDown((ulong)address, PageSize);
            var end = AlignUp((ulong)address + size, PageSize);

            lock (Gate)
            {
                if (!TryFindRegionLocked(start, out var region) || end > region.End)
                {
                    return false;
                }

                oldProtect = region.ProtectAt(start);
                if (mprotect((nint)start, (nuint)(end - start), ToPosixProtect(newProtect)) != 0)
                {
                    return false;
                }

                SetProtectRangeLocked(region, start, end - start, newProtect);
                return true;
            }
        }

        public static nuint Query(void* address, out BasicInfo info)
        {
            info = default;
            var pageAddress = AlignDown((ulong)address, PageSize);

            lock (Gate)
            {
                if (TryFindRegionLocked(pageAddress, out var region))
                {
                    // Win32 VirtualQuery reports a run of pages sharing the
                    // same protection, so stop the run where it changes. Do
                    // not walk the whole mapping page-by-page here: PS5 GPU
                    // apertures can span hundreds of GiB, while protection
                    // overrides are sparse. A tiny read inside such a mapping
                    // otherwise performs tens of millions of dictionary
                    // lookups before it can return.
                    var pageProtects = region.PageProtects;
                    uint protect;
                    ulong runEnd;
                    if (pageProtects is null || pageProtects.Count == 0)
                    {
                        protect = region.DefaultProtect;
                        runEnd = region.End;
                    }
                    else if (pageProtects.TryGetValue(pageAddress, out protect))
                    {
                        runEnd = pageAddress + PageSize;
                        while (runEnd < region.End &&
                            pageProtects.TryGetValue(runEnd, out var nextProtect) &&
                            nextProtect == protect)
                        {
                            runEnd += PageSize;
                        }
                    }
                    else
                    {
                        protect = region.DefaultProtect;
                        runEnd = region.End;
                        foreach (var overrideAddress in pageProtects.Keys)
                        {
                            if (overrideAddress > pageAddress && overrideAddress < runEnd)
                            {
                                runEnd = overrideAddress;
                            }
                        }
                    }

                    info.BaseAddress = pageAddress;
                    info.AllocationBase = region.Base;
                    info.AllocationProtect = region.DefaultProtect;
                    info.RegionSize = runEnd - pageAddress;
                    info.State = MEM_COMMIT;
                    info.Protect = protect;
                    info.Type = MEM_PRIVATE;
                    return (nuint)sizeof(BasicInfo);
                }

                // Untracked host memory (runtime heaps, stacks, libraries) is
                // reported as a free block reaching to the next tracked region
                // so scanning callers keep advancing.
                var nextBase = ulong.MaxValue;
                foreach (var regionBase in Regions.Keys)
                {
                    if (regionBase > pageAddress)
                    {
                        nextBase = regionBase;
                        break;
                    }
                }

                info.BaseAddress = pageAddress;
                info.AllocationBase = 0;
                info.AllocationProtect = PAGE_NOACCESS;
                info.RegionSize = (nextBase == ulong.MaxValue ? pageAddress + PageSize : nextBase) - pageAddress;
                info.State = MEM_FREE_STATE;
                info.Protect = PAGE_NOACCESS;
                info.Type = 0;
                return (nuint)sizeof(BasicInfo);
            }
        }

        private static bool OverlapsTrackedRegionLocked(ulong start, ulong size)
        {
            var end = start + size;
            foreach (var region in Regions.Values)
            {
                if (region.Base < end && start < region.End)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryFindRegionLocked(ulong address, out Region region)
        {
            region = null!;
            var keys = Regions.Keys;
            var low = 0;
            var high = keys.Count - 1;
            Region? candidate = null;
            while (low <= high)
            {
                var middle = low + ((high - low) >> 1);
                var entry = Regions.Values[middle];
                if (entry.Base <= address)
                {
                    candidate = entry;
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            if (candidate is null || address >= candidate.End)
            {
                return false;
            }

            region = candidate;
            return true;
        }

        private static void SetProtectRangeLocked(Region region, ulong start, ulong size, uint protect)
        {
            if (start == region.Base && size >= region.Size)
            {
                region.DefaultProtect = protect;
                region.PageProtects = null;
                return;
            }

            region.PageProtects ??= new Dictionary<ulong, uint>();
            var end = start + size;
            for (var pageAddress = start; pageAddress < end; pageAddress += PageSize)
            {
                if (protect == region.DefaultProtect)
                {
                    region.PageProtects.Remove(pageAddress);
                }
                else
                {
                    region.PageProtects[pageAddress] = protect;
                }
            }
        }

        private static int ToPosixProtect(uint win32Protect)
        {
            return win32Protect switch
            {
                PAGE_NOACCESS => PROT_NONE,
                PAGE_READONLY => PROT_READ,
                PAGE_READWRITE => PROT_READ | PROT_WRITE,
                PAGE_EXECUTE => PROT_READ | PROT_EXEC,
                PAGE_EXECUTE_READ => PROT_READ | PROT_EXEC,
                PAGE_EXECUTE_READWRITE => PROT_READ | PROT_WRITE | PROT_EXEC,
                _ => PROT_READ | PROT_WRITE
            };
        }

        private static void Trace(string message)
        {
            if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_VMEM"), "1", StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"[HOSTMEM] {message}");
            }
        }

        private static ulong AlignDown(ulong value, ulong alignment) => value & ~(alignment - 1);

        private static ulong AlignUp(ulong value, ulong alignment) => checked((value + alignment - 1) & ~(alignment - 1));

        [DllImport("libc", SetLastError = true)]
        private static extern nint mmap(nint addr, nuint length, int prot, int flags, int fd, long offset);

        [DllImport("libc", SetLastError = true)]
        private static extern int munmap(nint addr, nuint length);

        [DllImport("libc", SetLastError = true)]
        private static extern int mprotect(nint addr, nuint length, int prot);
    }
}
