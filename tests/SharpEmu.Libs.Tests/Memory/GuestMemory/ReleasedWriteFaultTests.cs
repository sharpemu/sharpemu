// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Memory;
using SharpEmu.HLE;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.HLE.Host;
using Xunit;
using static SharpEmu.Libs.Tests.Memory.HostViews.HostViewTestSupport;

namespace SharpEmu.Libs.Tests.Memory.GuestMemory;

[Collection(GuestMemoryStateCollection.Name)]
public sealed class ReleasedWriteFaultTests
{
    private const GuestPageProtection ReadWrite = GuestPageProtection.Read | GuestPageProtection.Write;

    [Fact]
    public void ReleasedWriteWatchPermitsOneRetryPerPageAndRestoration()
    {
        if (!Supported) return;
        using var memory = new PhysicalVirtualMemory(viewHost: HostViewMemory.Create(), backingBytes: BackingSize);
        using var manager = new GuestGpuMemory(memory);
        var address = Map(memory, manager);
        Assert.False(manager.TryResolveFault(FaultKind.Write, address));
        manager.Pages.AddWatch(address, 0x2000, blockReads: false);
        manager.Pages.RemoveWatch(address, 0x2000, blockReads: false);

        Assert.True(manager.TryResolveFault(FaultKind.Write, address));
        Assert.False(manager.TryResolveFault(FaultKind.Write, address + 8));
        Assert.True(manager.TryResolveFault(FaultKind.Write, address + 0x1000));
        Assert.False(manager.TryResolveFault(FaultKind.Write, address + 0x1008));
        Assert.False(manager.TryResolveFault(FaultKind.Write, address + 0x2000));
        Assert.False(manager.TryResolveFault(FaultKind.Read, address));
        Assert.False(manager.TryResolveFault(FaultKind.Execute, address));
        Assert.False(manager.TryResolveFault(FaultKind.Unknown, address));

        manager.Pages.AddWatch(address, 0x1000, blockReads: false);
        Assert.False(manager.TryResolveFault(FaultKind.Write, address));
        manager.Pages.RemoveWatch(address, 0x1000, blockReads: false);
        Assert.True(manager.TryResolveFault(FaultKind.Write, address));
        Assert.False(manager.TryResolveFault(FaultKind.Write, address));
    }

    [Fact]
    public void RemainingWatchAndHostProtectionPreventRetry()
    {
        if (!Supported) return;
        using var memory = new PhysicalVirtualMemory(viewHost: HostViewMemory.Create(), backingBytes: BackingSize);
        using var manager = new GuestGpuMemory(memory);
        var address = Map(memory, manager);
        manager.Pages.AddWatch(address, 0x1000, blockReads: false);
        manager.Pages.AddWatch(address, 0x1000, blockReads: true);
        manager.Pages.RemoveWatch(address, 0x1000, blockReads: false);
        Assert.False(manager.TryResolveFault(FaultKind.Write, address));
        manager.Pages.RemoveWatch(address, 0x1000, blockReads: true);

        Assert.True(memory.TryProtect(address, 0x1000, GuestPageProtection.None));
        Assert.False(manager.TryResolveFault(FaultKind.Write, address));
        Assert.True(memory.TryProtect(address, 0x1000, GuestPageProtection.Read));
        Assert.False(manager.TryResolveFault(FaultKind.Write, address));
        if (OperatingSystem.IsWindows())
        {
            Assert.True(PlatformMemory.ProtectRaw(address, 0x1000, 0x104, out _));
            Assert.False(manager.TryResolveFault(FaultKind.Write, address));
        }

        Assert.True(manager.NoteProtected(address, 0x1000, GuestPageProtection.Read));
        Assert.True(memory.TryProtect(address, 0x1000, ReadWrite));
        Assert.False(manager.TryResolveFault(FaultKind.Write, address));
        Assert.True(manager.NoteProtected(address, 0x1000, ReadWrite));
        Assert.True(manager.TryResolveFault(FaultKind.Write, address));
    }

    [Fact]
    public void NewMappingCannotUseThePreviousMappingsRestoration()
    {
        if (!Supported) return;
        using var memory = new PhysicalVirtualMemory(viewHost: HostViewMemory.Create(), backingBytes: BackingSize);
        using var manager = new GuestGpuMemory(memory);
        var address = Map(memory, manager);
        manager.Pages.AddWatch(address, 0x1000, blockReads: false);
        manager.Pages.RemoveWatch(address, 0x1000, blockReads: false);
        manager.Unregister(address, 4 * Segment);
        Assert.False(manager.TryResolveFault(FaultKind.Write, address));
        Assert.True(memory.TryUnmapBacked(address, 4 * Segment));
        Assert.True(memory.TryMapBacked(address, 4 * Segment, 0, ReadWrite, out _));
        manager.Register(address, 4 * Segment, ReadWrite);
        Assert.False(manager.TryResolveFault(FaultKind.Write, address));
    }

    [Fact]
    public void EachFaultingThreadCanRetryAReleasedWatch()
    {
        if (!Supported) return;
        using var memory = new PhysicalVirtualMemory(viewHost: HostViewMemory.Create(), backingBytes: BackingSize);
        using var manager = new GuestGpuMemory(memory);
        var address = Map(memory, manager);
        manager.Pages.AddWatch(address, 0x1000, blockReads: false);
        manager.Pages.RemoveWatch(address, 0x1000, blockReads: false);
        var results = new bool[4];
        var threads = Enumerable.Range(0, 2).Select(index => new Thread(() =>
        {
            results[index * 2] = manager.TryResolveFault(FaultKind.Write, address);
            results[index * 2 + 1] = manager.TryResolveFault(FaultKind.Write, address);
        })).ToArray();
        foreach (var thread in threads) thread.Start();
        foreach (var thread in threads) Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        Assert.Equal(new[] { true, false, true, false }, results);
    }

    private static ulong Map(PhysicalVirtualMemory memory, GuestGpuMemory manager)
    {
        Assert.True(memory.TryHoldRangeAtOrAbove(0x2_0000_0000, 4 * Segment, 4 * Segment, out var address));
        Assert.True(memory.TryMapBacked(address, 4 * Segment, 0, ReadWrite, out _));
        manager.Register(address, 4 * Segment, ReadWrite);
        return address;
    }
}
