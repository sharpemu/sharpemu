// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
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
}
