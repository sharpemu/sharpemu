// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.Host.Posix;
using SharpEmu.HLE.Host.Windows;

namespace SharpEmu.HLE.Host;

public static class HostViewMemory
{
    public static IHostViewMemory Create() =>
        OperatingSystem.IsWindows() ? new WindowsHostViews() : new PosixHostViews();

    internal static bool IsValidRange(ulong address, ulong size) =>
        address != 0 && size != 0 && size <= ulong.MaxValue - address;

    internal static bool IsValidOffset(HostBackingObject backing, ulong offset, ulong size, ulong pageSize) =>
        offset % pageSize == 0 && offset < backing.Size && size != 0 && size <= backing.Size - offset;

    // A page's access as the host reports it (the PAGE_* values; the Posix region table keeps the same values). False for an
    // access with a modifier such as a guard page, or one HostPageProtection has no value for.
    internal static bool TryGetProtection(uint raw, out HostPageProtection protection)
    {
        (var known, protection) = raw switch
        {
            HostMemory.PAGE_NOACCESS => (true, HostPageProtection.NoAccess),
            HostMemory.PAGE_READONLY => (true, HostPageProtection.ReadOnly),
            HostMemory.PAGE_READWRITE => (true, HostPageProtection.ReadWrite),
            HostMemory.PAGE_EXECUTE => (true, HostPageProtection.Execute),
            HostMemory.PAGE_EXECUTE_READ => (true, HostPageProtection.ReadExecute),
            HostMemory.PAGE_EXECUTE_READWRITE => (true, HostPageProtection.ReadWriteExecute),
            0x80 => (true, HostPageProtection.ExecuteWriteCopy),
            _ => (false, HostPageProtection.NoAccess),
        };
        return known;
    }

    // Walks the range with a host query that describes the run of equal access a page starts. A run it cannot describe (not
    // committed, an access TryGetProtection does not know) is left out, and so is the rest of the range once the query fails.
    internal static IReadOnlyList<HostPageAccess> CollectAccess(ulong address, ulong size, Func<ulong, (bool Found, HostMemory.BasicInfo Info)> query)
    {
        var runs = new List<HostPageAccess>();
        var end = address + size;
        for (var current = address; current < end;)
        {
            var (found, info) = query(current);
            if (!found || info.BaseAddress + info.RegionSize <= current)
            {
                break;
            }

            var stop = Math.Min(end, info.BaseAddress + info.RegionSize);
            if (info.State == HostMemory.MEM_COMMIT && TryGetProtection(info.Protect, out var protection))
            {
                runs.Add(new HostPageAccess(current, stop - current, protection));
            }

            current = stop;
        }

        return runs;
    }
}
