// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using System.Runtime.InteropServices;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Memory;

internal static unsafe class NativePageProtection
{
    // Inspect the kernel state because direct mprotect calls bypass allocation records.
    public static uint Query(ulong address)
    {
        if (OperatingSystem.IsLinux())
        {
            foreach (var line in File.ReadLines("/proc/self/maps"))
            {
                var separator = line.IndexOf('-');
                var permissionStart = line.IndexOf(' ') + 1;
                var start = ulong.Parse(line.AsSpan(0, separator), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                var end = ulong.Parse(line.AsSpan(separator + 1, permissionStart - separator - 2),
                    NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                if (address < start || address >= end) continue;
                var permissions = line.AsSpan(permissionStart, 3);
                return ConvertProtection(permissions[0] == 'r', permissions[1] == 'w', permissions[2] == 'x');
            }
            Assert.Fail("The address has no native mapping.");
        }
        else if (OperatingSystem.IsMacOS())
        {
            const int basicInformation64 = 9;
            var regionAddress = address;
            ulong regionSize = 0;
            uint informationCount = 9;
            var information = stackalloc int[9];
            var task = MachTaskSelf();
            var result = MachVmRegion(task, ref regionAddress, ref regionSize, basicInformation64,
                information, ref informationCount, out var objectName);
            try
            {
                Assert.Equal(0, result);
                Assert.Equal(9u, informationCount);
                Assert.True(regionAddress <= address && address - regionAddress < regionSize);
                return ConvertProtection((information[0] & 1) != 0, (information[0] & 2) != 0,
                    (information[0] & 4) != 0);
            }
            finally
            {
                if (objectName != 0) Assert.Equal(0, MachPortDeallocate(task, objectName));
            }
        }
        else
        {
            Assert.NotEqual((nuint)0, HostMemory.Query((void*)address, out var information));
            return information.Protect & 0xFF;
        }
        return 0;
    }

    private static uint ConvertProtection(bool readable, bool writable, bool executable) =>
        (readable, writable, executable) switch
        {
            (_, true, true) => HostMemory.PAGE_EXECUTE_READWRITE,
            (true, false, true) => HostMemory.PAGE_EXECUTE_READ,
            (false, false, true) => HostMemory.PAGE_EXECUTE,
            (_, true, false) => HostMemory.PAGE_READWRITE,
            (true, false, false) => HostMemory.PAGE_READONLY,
            _ => HostMemory.PAGE_NOACCESS,
        };

    [DllImport("libSystem.dylib", EntryPoint = "mach_task_self")]
    private static extern uint MachTaskSelf();

    [DllImport("libSystem.dylib", EntryPoint = "mach_vm_region")]
    private static extern int MachVmRegion(uint task, ref ulong address, ref ulong size, int flavor,
        int* information, ref uint informationCount, out uint objectName);

    [DllImport("libSystem.dylib", EntryPoint = "mach_port_deallocate")]
    private static extern int MachPortDeallocate(uint task, uint name);
}
