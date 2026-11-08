// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SharpEmu.HLE.Host.Windows;

public static unsafe class WindowsGuestAddressReservation
{
    public const uint CreateSuspended = 0x4;
    public const ulong ImageStart = 0x8_0000_0000;
    public const ulong ImageSize = 0x8_0000_0000;
    public const ulong DataStart = 0x10_0000_0000;
    public const ulong DataSize = 0xEC_0000_0000;
    private const uint ReserveMemory = 0x2000;
    private const uint ReservePlaceholder = 0x40000;
    private const uint DecommitMemory = 0x4000;
    private const uint NoAccess = 0x1;

    public static void PrepareAndResume(nint process, nint thread)
    {
        // The child must remain suspended until both ranges are protected from runtime allocations.
        if (!ReserveRange(process, ImageStart, ImageSize, ReserveMemory) ||
            !ReserveRange(process, DataStart, DataSize, ReserveMemory | ReservePlaceholder))
        {
            var error = Marshal.GetLastWin32Error();
            TerminateProcess(process, 5);
            throw new Win32Exception(error, "Could not reserve the guest address ranges before startup.");
        }

        if (ResumeThread(thread) == uint.MaxValue)
        {
            var error = Marshal.GetLastWin32Error();
            TerminateProcess(process, 5);
            throw new Win32Exception(error, "Could not resume the prepared guest process.");
        }
    }

    private static bool ReserveRange(nint process, ulong address, ulong size, uint allocationType) =>
        VirtualAlloc2(process, (void*)address, checked((nuint)size), allocationType, NoAccess, null, 0) == (void*)address;

    public static void Validate(IHostMemory memory)
    {
        ValidateRange(memory, ImageStart, ImageSize);
        ValidateRange(memory, DataStart, DataSize);
    }

    private static void ValidateRange(IHostMemory memory, ulong address, ulong size)
    {
        if (!OperatingSystem.IsWindows() || !memory.Query(address, out var region) ||
            region.BaseAddress != address || region.AllocationBase != address || region.RegionSize != size ||
            region.State != HostRegionState.Reserved || region.RawProtection != 0)
        {
            throw new InvalidOperationException($"The startup guest reservation is invalid: address=0x{address:X16} size=0x{size:X}.");
        }
    }

    public static bool ContainsImageRange(ulong address, ulong size) =>
        address >= ImageStart && address - ImageStart < ImageSize && size <= ImageSize - (address - ImageStart);

    public static void DecommitImageRange(ulong address, ulong size)
    {
        if (!ContainsImageRange(address, size) || size == 0)
            throw new ArgumentOutOfRangeException(nameof(address));
        if (!VirtualFree((void*)address, checked((nuint)size), DecommitMemory))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not decommit guest image pages.");
    }

    [DllImport("kernelbase.dll", SetLastError = true)]
    private static extern void* VirtualAlloc2(nint process, void* address, nuint size, uint allocationType, uint protection, void* parameters, uint count);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(nint thread);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFree(void* address, nuint size, uint freeType);
    [DllImport("kernel32.dll")]
    private static extern bool TerminateProcess(nint process, uint exitCode);
}
