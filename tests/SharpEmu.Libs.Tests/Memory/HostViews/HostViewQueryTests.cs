// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using Xunit;
using static SharpEmu.Libs.Tests.Memory.HostViews.HostViewTestSupport;

namespace SharpEmu.Libs.Tests.Memory.HostViews;

public sealed unsafe class HostViewQueryTests
{
    [Fact]
    public void QueriesFollowViewProtectionRestorationAndRelease()
    {
        if (!Supported) return;
        var views = HostViewMemory.Create();
        var size = HoleSize(views);
        var address = ReserveFreeHole(views, size);
        try
        {
            Check(address, HostMemory.MEM_RESERVE, HostMemory.PAGE_NOACCESS);
            using var backing = CreateBacking(views);
            Assert.True(views.SplitHole(address, Segment));
            Assert.True(views.TryMapView(backing, address, 0, Segment, HostPageProtection.ReadWrite, out _));
            Check(address, HostMemory.MEM_COMMIT, HostMemory.PAGE_READWRITE);
            Check(address + Segment, HostMemory.MEM_RESERVE, HostMemory.PAGE_NOACCESS);
            Assert.True(views.ChangeAccess(address, 1, HostPageProtection.ReadOnly));
            Check(address, HostMemory.MEM_COMMIT, HostMemory.PAGE_READONLY);
            Assert.True(views.ChangeAccess(address, views.PageSize, HostPageProtection.ReadWrite));
            Assert.True(views.ChangeAccess(address, views.PageSize, HostPageProtection.ReadOnly));
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var changed = views.ChangeAccess(address, views.PageSize, HostPageProtection.ReadWrite);
            var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            Assert.True(changed);
            Assert.Equal(0, allocatedBytes);
            Assert.True(views.ChangeAccess(address, views.PageSize, HostPageProtection.NoAccess));
            Check(address, HostMemory.MEM_COMMIT, HostMemory.PAGE_NOACCESS);
            Assert.False(views.TryMapView(backing, address, BackingSize, Segment, HostPageProtection.ReadWrite, out _));
            Check(address, HostMemory.MEM_COMMIT, HostMemory.PAGE_NOACCESS);
            Assert.True(views.UnmapView(address, Segment));
            Check(address, HostMemory.MEM_RESERVE, HostMemory.PAGE_NOACCESS);
        }
        finally
        {
            Assert.True(views.FreeOwnedRange(address, size));
        }
        Assert.NotEqual((nuint)0, HostMemory.Query((void*)address, out var released));
        Assert.Equal(HostMemory.MEM_FREE_STATE, released.State);
    }

    private static void Check(ulong address, uint state, uint protection)
    {
        if (OperatingSystem.IsWindows() && state == HostMemory.MEM_RESERVE)
            protection = 0;
        Assert.NotEqual((nuint)0, HostMemory.Query((void*)address, out var direct));
        Assert.Equal(state, direct.State);
        Assert.Equal(protection, direct.Protect);
        Assert.True(PlatformMemory.Query(address, out var host));
        Assert.Equal(state, host.RawState);
        Assert.Equal(protection, host.RawProtection);
    }
}
