// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.Host.Posix;

// View mappings bypass the anonymous-allocation tables. Keep their query state together.
internal static class PosixViewRegions
{
    internal static readonly object Gate = new();
    private readonly record struct Region(ulong Start, ulong End, uint State, uint Protection,
        uint[]? PageProtections, ulong PageBase);
    private static readonly List<Region> Regions = new();

    internal static void Replace(ulong address, ulong size, uint state, uint protection)
    {
        HostMemory.OnMappingChanged();
        var end = RoundPageEnd(address, size);
        var replacement = new List<Region>(Regions.Count + 2);
        foreach (var region in Regions)
        {
            if (region.End <= address || region.Start >= end)
            {
                replacement.Add(region);
                continue;
            }
            if (region.Start < address) replacement.Add(region with { End = address });
            if (region.End > end) replacement.Add(region with { Start = end });
        }
        if (state != HostMemory.MEM_FREE_STATE)
        {
            uint[]? pages = null;
            if (state == HostMemory.MEM_COMMIT)
            {
                pages = new uint[checked((int)((end - address) / (ulong)Environment.SystemPageSize))];
                Array.Fill(pages, protection);
            }
            replacement.Add(new Region(address, end, state, protection, pages, address));
        }
        replacement.Sort((left, right) => left.Start.CompareTo(right.Start));
        Regions.Clear();
        foreach (var region in replacement)
        {
            if (region.PageProtections is null && Regions.Count > 0 && Regions[^1].End == region.Start &&
                Regions[^1].State == region.State && Regions[^1].Protection == region.Protection)
                Regions[^1] = Regions[^1] with { End = region.End };
            else
                Regions.Add(region);
        }
    }

    // Mapping creates the storage. Fault-time protection updates must not allocate.
    internal static void ChangeProtection(ulong address, ulong size, uint protection)
    {
        HostMemory.OnMappingChanged();
        var end = RoundPageEnd(address, size);
        var pageSize = (ulong)Environment.SystemPageSize;
        foreach (var region in Regions)
        {
            if (region.Start >= end) break;
            if (region.End <= address || region.PageProtections is null) continue;
            var startIndex = (int)((Math.Max(address, region.Start) - region.PageBase) / pageSize);
            var endIndex = (int)((Math.Min(end, region.End) - region.PageBase) / pageSize);
            Array.Fill(region.PageProtections, protection, startIndex, endIndex - startIndex);
        }
    }

    internal static bool TryQuery(ulong address, out HostMemory.BasicInfo info)
    {
        lock (Gate)
        {
            foreach (var region in Regions)
            {
                if (region.Start > address) break;
                if (address >= region.End) continue;
                var start = address - address % (ulong)Environment.SystemPageSize;
                var end = region.End;
                var protection = region.Protection;
                if (region.PageProtections is { } pages)
                {
                    var pageSize = (ulong)Environment.SystemPageSize;
                    var index = (int)((start - region.PageBase) / pageSize);
                    protection = pages[index];
                    end = start + pageSize;
                    while (end < region.End && pages[++index] == protection) end += pageSize;
                }
                info = new HostMemory.BasicInfo
                {
                    BaseAddress = start,
                    AllocationBase = region.Start,
                    RegionSize = end - start,
                    State = region.State,
                    Protect = protection,
                    AllocationProtect = region.Protection,
                };
                return true;
            }
        }
        info = default;
        return false;
    }

    private static ulong RoundPageEnd(ulong address, ulong size)
    {
        var pageSize = (ulong)Environment.SystemPageSize;
        var end = checked(address + size);
        var remainder = end % pageSize;
        return remainder == 0 ? end : checked(end + pageSize - remainder);
    }

    internal static uint RawProtection(HostPageProtection protection) => protection switch
    {
        HostPageProtection.NoAccess => HostMemory.PAGE_NOACCESS,
        HostPageProtection.ReadOnly => HostMemory.PAGE_READONLY,
        HostPageProtection.ReadWrite => HostMemory.PAGE_READWRITE,
        HostPageProtection.Execute => HostMemory.PAGE_EXECUTE,
        HostPageProtection.ReadExecute => HostMemory.PAGE_EXECUTE_READ,
        HostPageProtection.ReadWriteExecute or HostPageProtection.ExecuteWriteCopy => HostMemory.PAGE_EXECUTE_READWRITE,
        _ => throw new ArgumentOutOfRangeException(nameof(protection)),
    };
}
