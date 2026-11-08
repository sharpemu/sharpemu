// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.HLE.GpuMemory;
using Xunit;

namespace SharpEmu.Libs.Tests.Memory.GpuMemory;

[Collection(GpuMemoryStateCollection.Name)]
public sealed class CpuStackRangesTests : IDisposable
{
    private const GuestPageProtection ReadWrite = GuestPageProtection.Read | GuestPageProtection.Write;

    private readonly Action<string> _previousFatal = PageGuard.OnFatal;
    private readonly List<string> _fatals = new();
    private readonly RecordingAddressSpace _space = new();

    public CpuStackRangesTests()
    {
        PageGuard.OnFatal = _fatals.Add;
    }

    public void Dispose()
    {
        PageGuard.OnFatal = _previousFatal;
    }

    [Fact]
    public void Ranges_MergeAcrossOwnersAndLeaveWithTheirOwner()
    {
        var stacks = new CpuStackRanges();
        Assert.True(stacks.Set(1, 0x11000, 0x13000, out var previous));
        Assert.Null(previous);
        Assert.True(stacks.Set(2, 0x12000, 0x14000, out _));
        Assert.True(stacks.Set(3, 0x20000, 0x21000, out _));
        Assert.Equal(new[] { (0x11000UL, 0x14000UL), (0x20000UL, 0x21000UL) }, stacks.Snapshot);
        Assert.Equal(1, CpuStackRanges.FirstEndingAfter(stacks.Snapshot, 0x14000));

        Assert.False(stacks.Set(3, 0x20000, 0x21000, out _));
        Assert.True(stacks.Set(3, 0x30000, 0x31000, out previous));
        Assert.Equal((0x20000UL, 0x21000UL), previous);

        Assert.True(stacks.Remove(1, out var removed));
        Assert.Equal((0x11000UL, 0x13000UL), removed);
        Assert.False(stacks.Remove(1, out _));
        Assert.Equal(new[] { (0x12000UL, 0x14000UL), (0x30000UL, 0x31000UL) }, stacks.Snapshot);
    }

    // Windows writes an exception's record below the stack pointer before any handler runs: watches leave a stack's
    // pages the guest's own access, and the pages around it the watched access.
    [Fact]
    public void Watches_LeaveStackPagesTheGuestAccess()
    {
        using var guard = new PageGuard(_space);
        guard.Permissions.Set(0x12000, 0x1000, GuestPageProtection.Read);
        guard.Stacks.Set(1, 0x11000, 0x13000, out _);

        guard.AddWatch(0x10000, 0x4000, blockReads: false);
        Assert.Equal(new[]
        {
            (0x10000UL, 0x1000UL, GuestPageProtection.Read),
            (0x11000UL, 0x1000UL, ReadWrite),
            (0x12000UL, 0x1000UL, GuestPageProtection.Read),
            (0x13000UL, 0x1000UL, GuestPageProtection.Read),
        }, _space.Protects);

        guard.AddWatch(0x11000, 0x1000, blockReads: true);
        Assert.Equal((0x11000UL, 0x1000UL, ReadWrite), _space.Protects[^1]);

        guard.Stacks.Remove(1, out _);
        guard.Reapply(0x10000, 0x4000);
        Assert.Equal(new[]
        {
            (0x10000UL, 0x1000UL, GuestPageProtection.Read),
            (0x11000UL, 0x1000UL, GuestPageProtection.None),
            (0x12000UL, 0x2000UL, GuestPageProtection.Read),
        }, _space.Protects.GetRange(_space.Protects.Count - 3, 3));

        guard.RemoveWatch(0x11000, 0x1000, blockReads: true);
        guard.RemoveWatch(0x10000, 0x4000, blockReads: false);
        Assert.Empty(_fatals);
    }

    // A stack noted over watched pages gets its access back at once; once released, its range is marked written and
    // watched again.
    [Fact]
    public void NotedStacks_TakeTheirPagesOutOfWatchesUntilReleased()
    {
        var store = new RecordingStore();
        using var memory = new GuestGpuMemory(_space);
        memory.Register(0x10000, 0x4000, ReadWrite);
        memory.AttachStores(store, null);
        memory.Pages.AddWatch(0x10000, 0x4000, blockReads: false);
        _space.Protects.Clear();

        memory.NoteStack(1, 0x11010, 0x1FF0);
        Assert.Equal(new[] { (0x11000UL, 0x2000UL, ReadWrite) }, _space.Protects);
        memory.NoteStack(1, 0x11000, 0x2000);
        Assert.Single(_space.Protects);
        Assert.Empty(store.Writes);

        memory.NoteStackReleased(1);
        Assert.Equal(new[] { (0x11000UL, 0x2000UL) }, store.Writes);
        Assert.Equal((0x11000UL, 0x2000UL, GuestPageProtection.Read), _space.Protects[^1]);
        memory.NoteStackReleased(1);
        Assert.Single(store.Writes);

        memory.Pages.RemoveWatch(0x10000, 0x4000, blockReads: false);
        Assert.Empty(_fatals);
    }

    private sealed class RecordingStore : IGuestBufferStore
    {
        public List<(ulong Address, ulong Size)> Writes { get; } = new();

        public bool MarkCpuWrite(ulong address, ulong size)
        {
            Writes.Add((address, size));
            return true;
        }

        public bool DownloadToCpu(ulong address, ulong size) => false;
    }
}
