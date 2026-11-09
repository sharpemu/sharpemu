// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class GuestStackBoundsTests
{
    [Fact]
    public unsafe void AllocationQueryHandlesProtectionSplitsAndFallsBackForPartialCommit()
    {
        if (!OperatingSystem.IsWindows()) return;

        var pageSize = (nuint)Environment.SystemPageSize;
        var memory = (byte*)HostMemory.Alloc(null, pageSize * 3,
            HostMemory.MEM_RESERVE | HostMemory.MEM_COMMIT, HostMemory.PAGE_READWRITE);
        Assert.True(memory != null);
        try
        {
            Assert.True(HostMemory.Protect(memory + pageSize, pageSize, HostMemory.PAGE_READONLY, out _));
            // VirtualQuery at the last page would return only that page, not the allocation.
            Assert.True(DirectExecutionBackend.TryGetGuestStackBounds((ulong)(memory + pageSize * 2 + 16),
                out var bottom, out var top));
            Assert.Equal((ulong)memory, bottom);
            Assert.Equal((ulong)(memory + pageSize * 3), top);

            Assert.True(HostMemory.Free(memory, pageSize, 0x4000)); // MEM_DECOMMIT
            Assert.True(DirectExecutionBackend.TryGetGuestStackBounds((ulong)(memory + pageSize * 2 + 16),
                out bottom, out top));
            Assert.Equal((ulong)(memory + pageSize * 2), bottom);
            Assert.Equal((ulong)(memory + pageSize * 3), top);
            Assert.False(DirectExecutionBackend.TryGetGuestStackBounds((ulong)memory, out _, out _));
        }
        finally
        {
            Assert.True(HostMemory.Free(memory, 0, HostMemory.MEM_RELEASE));
        }

        Assert.False(DirectExecutionBackend.TryGetGuestStackBounds(0, out _, out _));
    }
}
