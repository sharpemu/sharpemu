// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Diagnostics;

/// <summary>
/// Classifies a guest virtual address into a memory region type. This is the
/// "Pointer Validator" from the reviewer's list — it prevents the PointerChainViewer
/// from treating code bytes as data pointers.
///
/// Region layout (matches SharpEmu's guest address space):
///   0x0000_0000_0000_0000 - 0x0000_0000_FFFF_FFFF  → Reserved / unmapped
///   0x0000_0001_0000_0000 - 0x0000_0001_FFFF_FFFF  → GPU / DirectMemory (0x1FE000000 lives here)
///   0x0000_0004_0000_0000 - 0x0000_0006_FFFF_FFFF  → DirectMemory pool
///   0x0000_0008_0000_0000 - 0x0000_0009_0000_0000  → Main image (eboot.bin)
///   0x0000_7000_0000_0000 - 0x0000_7000_0010_0000  → Import stubs
///   0x0000_6FFF_0000_0000 - 0x0000_6FFF_FFFF_FFFF  → Stack (POSIX)
///   0x0000_7D00_0000_0000 - 0x0000_7DFF_FFFF_FFFF  → TLS (POSIX)
///   0x0000_7E00_0000_0000 - 0x0000_7EFF_FFFF_FFFF  → Stack (POSIX)
///   0x0000_7C00_0000_0000 - 0x0000_7CFF_FFFF_FFFF  → Bootstrap (POSIX)
/// </summary>
public static class MemoryRegionClassifier
{
    public enum RegionKind
    {
        Null,
        LowMemory,          // < 0x10000 — NULL deref
        GpuMemory,          // 0x1FE000000 — hardcoded GPU addresses
        DirectMemoryPool,   // 0x40000000 - 0x6FFFFFFF
        MainImage,          // 0x800000000+
        ImportStubs,        // 0x7000000000000
        Stack,
        Tls,
        Bootstrap,
        Heap,               // dynamically allocated
        HostLeak,           // looks like a host pointer (Marshal.AllocHGlobal)
        Unknown
    }

    public static RegionKind Classify(ulong address)
    {
        if (address == 0) return RegionKind.Null;
        if (address < 0x10000) return RegionKind.LowMemory;

        // GPU hardcoded addresses (PS5 GPU register space)
        if (address >= 0x1FE000000UL && address < 0x200000000UL) return RegionKind.GpuMemory;
        if (address >= 0xC0000000UL && address < 0x100000000UL) return RegionKind.GpuMemory;

        // DirectMemory pool (allocated by sceKernelAllocateDirectMemory)
        if (address >= 0x04000000UL && address < 0x07000000UL) return RegionKind.DirectMemoryPool;

        // Main image (eboot.bin at 0x800000000)
        if (address >= 0x0000000800000000UL && address < 0x0000000900000000UL) return RegionKind.MainImage;

        // Import stubs
        if (address >= 0x0000700000000000UL && address < 0x0000700000100000UL) return RegionKind.ImportStubs;

        // POSIX guest regions (stack, TLS, bootstrap)
        if (address >= 0x00006FFFF0000000UL && address < 0x0000700000000000UL) return RegionKind.Stack;
        if (address >= 0x00007D0000000000UL && address < 0x00007E0000000000UL) return RegionKind.Tls;
        if (address >= 0x00007E0000000000UL && address < 0x00007F0000000000UL) return RegionKind.Stack;
        if (address >= 0x00007C0000000000UL && address < 0x00007D0000000000UL) return RegionKind.Bootstrap;

        // Host leak detection (Marshal.AllocHGlobal returns pointers in low host range)
        if (address >= 0x0000550000000000UL && address < 0x0000560000000000UL) return RegionKind.HostLeak;
        if (address >= 0x00007F0000000000UL && address < 0x0000800000000000UL) return RegionKind.HostLeak;

        return RegionKind.Unknown;
    }

    public static string Describe(ulong address)
    {
        var kind = Classify(address);
        return kind switch
        {
            RegionKind.Null => "NULL pointer",
            RegionKind.LowMemory => $"NULL-page deref (0x{address:X16} < 0x10000)",
            RegionKind.GpuMemory => $"GPU memory (hardcoded 0x{address:X16})",
            RegionKind.DirectMemoryPool => $"DirectMemory pool (0x{address:X16})",
            RegionKind.MainImage => $"Main image (0x{address:X16})",
            RegionKind.ImportStubs => $"Import stubs (0x{address:X16})",
            RegionKind.Stack => $"Stack (0x{address:X16})",
            RegionKind.Tls => $"TLS (0x{address:X16})",
            RegionKind.Bootstrap => $"Bootstrap (0x{address:X16})",
            RegionKind.Heap => $"Heap (0x{address:X16})",
            RegionKind.HostLeak => $"*** HOST POINTER LEAK (0x{address:X16}) — guest got a host address! ***",
            _ => $"Unknown region (0x{address:X16})"
        };
    }

    /// <summary>
    /// Returns true if the address points to executable code (not data).
    /// The PointerChainViewer must NOT dereference code addresses.
    /// </summary>
    public static bool IsCode(ulong address)
    {
        var kind = Classify(address);
        return kind == RegionKind.MainImage || kind == RegionKind.ImportStubs;
    }

    /// <summary>
    /// Returns true if the address is a valid data pointer (heap, TLS, stack, DirectMemory).
    /// </summary>
    public static bool IsData(ulong address)
    {
        var kind = Classify(address);
        return kind == RegionKind.DirectMemoryPool ||
               kind == RegionKind.Heap ||
               kind == RegionKind.Tls ||
               kind == RegionKind.Stack;
    }
}
