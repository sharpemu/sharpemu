// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Memory;

// A POSIX query reports the run of pages sharing the queried page's protection, ending at the
// next protection change, as Win32 VirtualQuery does.
public sealed unsafe class HostMemoryQueryRunTests
{
    [Fact]
    public void QueryRunsEndAtTheNextProtectionChange()
    {
        if (OperatingSystem.IsWindows()) return;
        var page = (ulong)Environment.SystemPageSize;
        var memory = (byte*)HostMemory.Alloc(null, (nuint)(page * 16), HostMemory.MEM_RESERVE | HostMemory.MEM_COMMIT, HostMemory.PAGE_READWRITE);
        Assert.True(memory != null);
        var start = (ulong)memory;
        try
        {
            Assert.True(HostMemory.Protect((void*)(start + 3 * page), (nuint)(3 * page), HostMemory.PAGE_READONLY, out _));
            Assert.True(HostMemory.Protect((void*)(start + 9 * page), (nuint)page, HostMemory.PAGE_NOACCESS, out _));
            Assert.True(HostMemory.Protect((void*)(start + 15 * page), (nuint)page, HostMemory.PAGE_READONLY, out _));

            AssertRun(start, 0, 3, HostMemory.PAGE_READWRITE);
            AssertRun(start, 3, 3, HostMemory.PAGE_READONLY);
            AssertRun(start, 4, 2, HostMemory.PAGE_READONLY);
            AssertRun(start, 6, 3, HostMemory.PAGE_READWRITE);
            AssertRun(start, 9, 1, HostMemory.PAGE_NOACCESS);
            AssertRun(start, 10, 5, HostMemory.PAGE_READWRITE);
            // A run at the last page of the mapping.
            AssertRun(start, 15, 1, HostMemory.PAGE_READONLY);

            // Restoring the default protection removes the overrides from the run.
            Assert.True(HostMemory.Protect((void*)(start + 3 * page), (nuint)(7 * page), HostMemory.PAGE_READWRITE, out _));
            AssertRun(start, 0, 15, HostMemory.PAGE_READWRITE);
        }
        finally
        {
            Assert.True(HostMemory.Free(memory, 0, HostMemory.MEM_RELEASE));
        }

        void AssertRun(ulong baseAddress, ulong firstPage, ulong pages, uint protect)
        {
            Assert.NotEqual(0u, (uint)HostMemory.Query((void*)(baseAddress + firstPage * page + 8), out var info));
            Assert.Equal(baseAddress + firstPage * page, info.BaseAddress);
            Assert.Equal(pages * page, info.RegionSize);
            Assert.Equal(protect, info.Protect);
        }
    }
}
