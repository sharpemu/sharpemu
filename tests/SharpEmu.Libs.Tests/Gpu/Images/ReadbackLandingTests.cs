// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using SharpEmu.Libs.Gpu.Images;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Images;

public sealed class ReadbackLandingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LandingKeepsThePageACpuStoreCovered(bool sameAsReadback)
    {
        var backing = new ArrayBackedSpace();
        const ulong address = ArrayBackedSpace.Base;
        var queued = new ReadbackLandings();
        var landing = queued.Queue(address, backing.Bytes.AsSpan(0, 3 * 4096));
        var payload = Enumerable.Repeat((byte)0x66, 3 * 4096).ToArray();
        var cpu = sameAsReadback ? (byte)0x66 : (byte)0x77;
        queued.NoteGuestStore(address + 4096, 4096);
        backing.Bytes.AsSpan(4096, 4096).Fill(cpu);

        Assert.True(landing.TryLand(backing, payload));

        Assert.All(backing.Bytes.Take(4096), value => Assert.Equal((byte)0x66, value));
        Assert.All(backing.Bytes.Skip(4096).Take(4096), value => Assert.Equal(cpu, value));
        Assert.All(backing.Bytes.Skip(8192).Take(4096), value => Assert.Equal((byte)0x66, value));
        Assert.Empty(queued.Pending);
    }

    [Fact]
    public void LandingKeepsTheRunsBeforeARefusedWrite()
    {
        var inner = new ArrayBackedSpace();
        const ulong address = ArrayBackedSpace.Base;
        var backing = new RefuseWriteBacking(inner, address + 8192);
        var queued = new ReadbackLandings();
        var landing = queued.Queue(address, inner.Bytes.AsSpan(0, 3 * 4096));
        queued.NoteGuestStore(address + 4096, 4096);
        inner.Bytes.AsSpan(4096, 4096).Fill(0x77);
        var payload = Enumerable.Repeat((byte)0x66, 3 * 4096).ToArray();

        Assert.False(landing.TryLand(backing, payload));

        Assert.All(inner.Bytes.Take(4096), value => Assert.Equal((byte)0x66, value));
        Assert.All(inner.Bytes.Skip(4096).Take(4096), value => Assert.Equal((byte)0x77, value));
        Assert.All(inner.Bytes.Skip(8192).Take(4096), value => Assert.Equal((byte)0, value));
        Assert.Empty(queued.Pending);
    }

    private sealed class RefuseWriteBacking(IGuestBackedSpace inner, ulong refused) : IGuestBackedSpace
    {
        public bool TryHoldRange(ulong address, ulong size) => inner.TryHoldRange(address, size);
        public bool TryHoldRangeAtOrAbove(ulong start, ulong size, ulong alignment, out ulong address) =>
            inner.TryHoldRangeAtOrAbove(start, size, alignment, out address);
        public bool TryMapBacked(ulong address, ulong size, ulong offset, GuestPageProtection protection, out HostViewFailure failure) =>
            inner.TryMapBacked(address, size, offset, protection, out failure);
        public bool TryUnmapBacked(ulong address, ulong size) => inner.TryUnmapBacked(address, size);
        public bool TryClearBacking(ulong offset, ulong size) => inner.TryClearBacking(offset, size);
        public bool IsBackedView(ulong address) => inner.IsBackedView(address);
        public bool IsBackedRange(ulong address, ulong size) => inner.IsBackedRange(address, size);
        public bool TryReadBacking(ulong address, Span<byte> bytes) => inner.TryReadBacking(address, bytes);
        public bool TryWriteBacking(ulong address, ReadOnlySpan<byte> bytes) =>
            address != refused && inner.TryWriteBacking(address, bytes);
    }
}
