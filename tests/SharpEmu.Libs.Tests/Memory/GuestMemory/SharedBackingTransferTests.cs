// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GuestMemory;
using SharpEmu.HLE.Host;
using Xunit;
using static SharpEmu.Libs.Tests.Memory.HostViews.HostViewTestSupport;

namespace SharpEmu.Libs.Tests.Memory.GuestMemory;

[Collection(GuestMemoryStateCollection.Name)]
public sealed unsafe class SharedBackingTransferTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SingleMappingTransfersDoNotAllocateTemporarySegments(bool copy)
    {
        if (!Supported) return;
        using var mapping = new TransferMappings();
        var data = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        var source = mapping.Address + Segment - (ulong)data.Length;
        var destination = mapping.Address + 2 * Segment - (ulong)data.Length;
        Assert.True(mapping.Store.TryWriteBacking(source, data));
        for (var iteration = 0; iteration < 256; iteration++)
        {
            Assert.True(copy
                ? mapping.Store.TryCopyBacking(destination, source, (ulong)data.Length)
                : mapping.Store.TryWriteBacking(destination, data));
        }

        var initialAllocation = GC.GetAllocatedBytesForCurrentThread();
        var succeeded = true;
        for (var iteration = 0; iteration < 1024; iteration++)
        {
            succeeded &= copy
                ? mapping.Store.TryCopyBacking(destination, source, (ulong)data.Length)
                : mapping.Store.TryWriteBacking(destination, data);
        }
        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - initialAllocation;

        Assert.True(succeeded);
        Assert.Equal(0L, allocatedBytes);
        Assert.Equal(data, new ReadOnlySpan<byte>((void*)destination, data.Length).ToArray());
    }

    [Theory]
    [InlineData(false, 0, 1)]
    [InlineData(false, 1, 0)]
    [InlineData(false, 0, 0)]
    [InlineData(true, 0, 1)]
    [InlineData(true, 1, 0)]
    [InlineData(true, 0, 0)]
    public void SingleMappingCopiesPreserveOverlappingBytes(bool separateViews, int sourceOffset, int destinationOffset)
    {
        if (!Supported) return;
        using var mapping = new TransferMappings(aliasSecondView: true);
        var expected = Enumerable.Range(0, 64).Select(value => (byte)value).ToArray();
        Assert.True(mapping.Store.TryWriteBacking(mapping.Address, expected));
        expected.AsSpan(sourceOffset, 32).CopyTo(expected.AsSpan(destinationOffset, 32));
        var destination = mapping.Address + (separateViews ? Segment : 0) + (ulong)destinationOffset;

        Assert.True(mapping.Store.TryCopyBacking(destination, mapping.Address + (ulong)sourceOffset, 32));

        Assert.Equal(expected, new ReadOnlySpan<byte>((void*)mapping.Address, expected.Length).ToArray());
    }

    [Fact]
    public void CrossMappingTransfersPreserveBytes()
    {
        if (!Supported) return;
        using var mapping = new TransferMappings();
        var expected = Enumerable.Range(0, 64).Select(value => (byte)value).ToArray();
        var source = mapping.Address + Segment - 32;
        Assert.True(mapping.Store.TryWriteBacking(source, expected));
        expected.AsSpan(0, 48).CopyTo(expected.AsSpan(4, 48));

        Assert.True(mapping.Store.TryCopyBacking(source + 4, source, 48));

        Assert.Equal(expected, new ReadOnlySpan<byte>((void*)source, expected.Length).ToArray());
    }

    [Fact]
    public void InvalidTransfersLeaveBackingBytesUnchanged()
    {
        if (!Supported) return;
        using var mapping = new TransferMappings();
        var data = Enumerable.Repeat((byte)0xA5, (int)(2 * Segment)).ToArray();
        Assert.True(mapping.Store.TryWriteBacking(mapping.Address, data));
        var end = mapping.Address + 2 * Segment;
        var replacement = new byte[8];

        Assert.False(mapping.Store.TryWriteBacking(end - 4, replacement));
        Assert.False(mapping.Store.TryWriteBacking(ulong.MaxValue - 3, replacement));
        Assert.False(mapping.Store.TryWriteBacking(mapping.Address, ReadOnlySpan<byte>.Empty));
        Assert.False(mapping.Store.TryCopyBacking(end - 4, mapping.Address, 8));
        Assert.False(mapping.Store.TryCopyBacking(mapping.Address, end - 4, 8));
        Assert.False(mapping.Store.TryCopyBacking(mapping.Address, ulong.MaxValue - 3, 8));
        Assert.False(mapping.Store.TryCopyBacking(ulong.MaxValue - 3, mapping.Address, 8));
        Assert.False(mapping.Store.TryCopyBacking(mapping.Address, mapping.Address, 0));
        Assert.False(mapping.Store.TryCopyBacking(mapping.Address, mapping.Address, ulong.MaxValue));

        Assert.Equal(data, new ReadOnlySpan<byte>((void*)mapping.Address, data.Length).ToArray());
    }

    [Fact]
    public void SingleMappingCopyUsesBackingAliasForProtectedViews()
    {
        if (!Supported) return;
        using var mapping = new TransferMappings();
        var data = BitConverter.GetBytes(Marker);
        Assert.True(mapping.Store.TryWriteBacking(mapping.Address, data));
        Assert.True(mapping.Host.ChangeAccess(mapping.Address, Segment, HostPageProtection.NoAccess));
        Assert.True(mapping.Host.ChangeAccess(mapping.Address + Segment, Segment, HostPageProtection.ReadOnly));

        Assert.True(mapping.Store.TryCopyBacking(mapping.Address + Segment, mapping.Address, (ulong)data.Length));

        Assert.Equal(Marker, *(ulong*)(mapping.Address + Segment));
    }

    private sealed class TransferMappings : IDisposable
    {
        public IHostViewMemory Host { get; } = HostViewMemory.Create();
        public SharedBackingViews Store { get; }
        public ulong Address { get; }
        private readonly ulong _holeSize;

        public TransferMappings(bool aliasSecondView = false)
        {
            Store = new SharedBackingViews(Host, BackingSize);
            _holeSize = HoleSize(Host);
            Address = ReserveFreeHole(Host, _holeSize);
            Assert.True(Host.SplitHole(Address, Segment));
            Assert.True(Host.SplitHole(Address + Segment, Segment));
            Assert.True(Store.TryMapReservedRange(Address, Segment, Segment, HostPageProtection.ReadWrite, out _));
            Assert.True(Store.TryMapReservedRange(Address + Segment, Segment,
                aliasSecondView ? Segment : 2 * Segment, HostPageProtection.ReadWrite, out _));
        }

        public void Dispose()
        {
            Store.Dispose();
            Assert.True(Host.JoinHoles(Address, _holeSize));
            Assert.True(Host.FreeHole(Address, _holeSize));
        }
    }
}
