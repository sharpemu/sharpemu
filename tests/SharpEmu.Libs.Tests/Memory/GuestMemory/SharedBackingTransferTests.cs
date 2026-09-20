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
    public void CrossMappingBufferWaitsForReservedAlias(bool read)
    {
        if (!Supported) return;
        using var mapping = new TransferMappings(aliasSecondView: true);
        Assert.True(mapping.Store.TryReserveCopy(mapping.Address + 64, mapping.Address, 8, out var reservation));
        Exception? failure = null;
        var transfer = new Thread(() =>
        {
            try
            {
                var bufferAddress = mapping.Address + Segment - 4;
                if (read)
                    Assert.True(mapping.Store.TryReadBacking(mapping.Address + 128, new Span<byte>((void*)bufferAddress, 8)));
                else
                    Assert.True(mapping.Store.TryWriteBacking(mapping.Address + 128, new ReadOnlySpan<byte>((void*)bufferAddress, 8)));
            }
            catch (Exception exception) { failure = exception; }
        }) { IsBackground = true };
        try
        {
            transfer.Start();
            Assert.True(SpinWait.SpinUntil(() => !transfer.IsAlive || HasWaitingCopyOperation(mapping.Store), TimeSpan.FromSeconds(10)));
            Assert.True(HasWaitingCopyOperation(mapping.Store));
        }
        finally
        {
            reservation.Dispose();
            Assert.True(transfer.Join(TimeSpan.FromSeconds(10)));
        }
        Assert.Null(failure);
    }

    [Fact]
    public void DisjointTransfersProceedWhileACopyIsReserved()
    {
        if (!Supported) return;
        using var mapping = new TransferMappings(segmentSize: 256 * 1024);
        const ulong copySize = 64 * 1024;
        var data = Enumerable.Repeat((byte)0xA5, (int)copySize).ToArray();
        Assert.True(mapping.Store.TryWriteBacking(mapping.Address + copySize, data));
        Assert.True(mapping.Store.TryReserveCopy(mapping.Address + 256 * 1024, mapping.Address, copySize, out var reservation));
        using (reservation)
        {
            Exception? transferFailure = null;
            var transfer = new Thread(() =>
            {
                try
                {
                    var destination = mapping.Address + 320 * 1024;
                    Assert.True(mapping.Store.TryCopyBacking(destination, mapping.Address + copySize, copySize));
                    var actual = new byte[data.Length];
                    Assert.True(mapping.Store.TryReadBacking(destination, actual));
                    Assert.Equal(data, actual);
                    Assert.True(mapping.Store.TryWriteBacking(destination, BitConverter.GetBytes(Marker)));
                }
                catch (Exception exception) { transferFailure = exception; }
            })
            { IsBackground = true };
            transfer.Start();
            Assert.True(transfer.Join(TimeSpan.FromSeconds(10)));
            Assert.Null(transferFailure);
        }
    }

    [Theory]
    [InlineData("read")]
    [InlineData("write")]
    [InlineData("read_buffer")]
    [InlineData("write_buffer")]
    [InlineData("copy")]
    [InlineData("clear")]
    [InlineData("unmap")]
    [InlineData("dispose")]
    public void ConflictingAccessAndMappingChangesWaitForCopy(string operation)
    {
        if (!Supported) return;
        using var mapping = new TransferMappings(aliasSecondView: true);
        Assert.True(mapping.Store.TryReserveCopy(mapping.Address + 64, mapping.Address, 32, out var reservation));
        Thread? pending = null;
        Exception? transferFailure = null;
        try
        {
            pending = new Thread(() =>
            {
                try
                {
                    // The second guest view has a different address but aliases the reserved bytes.
                    var alias = mapping.Address + Segment;
                    switch (operation)
                    {
                        case "read": Assert.True(mapping.Store.TryReadBacking(alias, new byte[8])); break;
                        case "write": Assert.True(mapping.Store.TryWriteBacking(alias, new byte[8])); break;
                        case "read_buffer": Assert.True(mapping.Store.TryReadBacking(alias + 128, new Span<byte>((void*)alias, 8))); break;
                        case "write_buffer": Assert.True(mapping.Store.TryWriteBacking(alias + 128, new ReadOnlySpan<byte>((void*)alias, 8))); break;
                        case "copy": Assert.True(mapping.Store.TryCopyBacking(alias + 128, alias, 8)); break;
                        case "clear": Assert.True(mapping.Store.Clear(Segment, 8)); break;
                        case "unmap": Assert.True(mapping.Store.Unmap(mapping.Address, Segment, out _)); break;
                        case "dispose": mapping.Store.Dispose(); break;
                    }
                }
                catch (Exception exception) { transferFailure = exception; }
            })
            { IsBackground = true };
            pending.Start();
            Assert.True(SpinWait.SpinUntil(() => HasWaitingCopyOperation(mapping.Store), TimeSpan.FromSeconds(10)));
            Assert.True(pending.IsAlive);
        }
        finally
        {
            reservation.Dispose();
            if (pending is not null) Assert.True(pending.Join(TimeSpan.FromSeconds(10)));
        }
        Assert.Null(transferFailure);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    public void LargeAliasedCopiesPreserveOverlappingBytes(int sourceOffset, int destinationOffset)
    {
        if (!Supported) return;
        using var mapping = new TransferMappings(aliasSecondView: true, segmentSize: 128 * 1024);
        var expected = Enumerable.Range(0, 128 * 1024).Select(value => (byte)value).ToArray();
        Assert.True(mapping.Store.TryWriteBacking(mapping.Address, expected));
        expected.AsSpan(sourceOffset, 64 * 1024).CopyTo(expected.AsSpan(destinationOffset, 64 * 1024));
        Assert.True(mapping.Store.TryCopyBacking(mapping.Address + 128 * 1024 + (ulong)destinationOffset,
            mapping.Address + (ulong)sourceOffset, 64 * 1024));
        Assert.Equal(expected, new ReadOnlySpan<byte>((void*)mapping.Address, expected.Length).ToArray());
    }

    private static bool HasWaitingCopyOperation(SharedBackingViews store)
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var gate = typeof(SharedBackingViews).GetField("_lock", flags)!.GetValue(store)!;
        lock (gate)
            return (int)typeof(SharedBackingViews).GetField("_copyWaiters", flags)!.GetValue(store)! != 0;
    }

    [Theory]
    [InlineData(false, 32)]
    [InlineData(true, 32)]
    [InlineData(true, 65536)]
    public void SingleMappingTransfersDoNotAllocateTemporarySegments(bool copy, int length)
    {
        if (!Supported) return;
        var segmentSize = Math.Max(Segment, (ulong)length);
        using var mapping = new TransferMappings(segmentSize: segmentSize);
        var data = Enumerable.Range(0, length).Select(value => (byte)value).ToArray();
        var source = mapping.Address + segmentSize - (ulong)data.Length;
        var destination = mapping.Address + 2 * segmentSize - (ulong)data.Length;
        Assert.True(mapping.Store.TryWriteBacking(source, data));
        var succeeded = true;
        void Transfer(int count)
        {
            for (var iteration = 0; iteration < count; iteration++)
            {
                succeeded &= copy
                    ? mapping.Store.TryCopyBacking(destination, source, (ulong)data.Length)
                    : mapping.Store.TryWriteBacking(destination, data);
            }
        }

        var allocatedBytes = AllocationMeasurement.SteadyState(() => Transfer(256), () => Transfer(1024));

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

    [Fact]
    public void AResolvedAliasReadsTheViewBytesUntilTheMappingChanges()
    {
        if (!Supported) return;
        using var mapping = new TransferMappings();
        Assert.True(mapping.Store.TryWriteBacking(mapping.Address + Segment + 8, BitConverter.GetBytes(Marker)));
        var snapshot = mapping.Store.AliasSnapshot;
        Assert.True(mapping.Store.TryEnterAliasAccess());
        try
        {
            Assert.True(mapping.Store.TryResolveAlias(mapping.Address + Segment, 16, out var alias));
            Assert.Equal(Marker, *(ulong*)(alias + 8));
            Assert.Same(snapshot, mapping.Store.AliasSnapshot);
            Assert.False(mapping.Store.TryResolveAlias(mapping.Address + Segment - 8, 16, out _));
        }
        finally
        {
            mapping.Store.ExitAliasAccess();
        }

        Assert.True(mapping.Store.Unmap(mapping.Address + Segment, Segment, out _));
        Assert.NotSame(snapshot, mapping.Store.AliasSnapshot);
        Assert.False(mapping.Store.TryResolveAlias(mapping.Address + Segment, 16, out _));
    }

    private sealed class TransferMappings : IDisposable
    {
        public IHostViewMemory Host { get; } = HostViewMemory.Create();
        public SharedBackingViews Store { get; }
        public ulong Address { get; }
        private readonly ulong _holeSize;

        public TransferMappings(bool aliasSecondView = false, ulong segmentSize = Segment)
        {
            Store = new SharedBackingViews(Host, Math.Max(BackingSize, 4 * segmentSize));
            _holeSize = AlignUp(4 * segmentSize, Host.Granularity);
            Address = ReserveFreeHole(Host, _holeSize);
            Assert.True(Host.SplitHole(Address, segmentSize));
            Assert.True(Host.SplitHole(Address + segmentSize, segmentSize));
            Assert.True(Store.TryMapReservedRange(Address, segmentSize, segmentSize, HostPageProtection.ReadWrite, out _));
            Assert.True(Store.TryMapReservedRange(Address + segmentSize, segmentSize,
                aliasSecondView ? segmentSize : 2 * segmentSize, HostPageProtection.ReadWrite, out _));
        }

        public void Dispose()
        {
            Store.Dispose();
            Assert.True(Host.JoinHoles(Address, _holeSize));
            Assert.True(Host.FreeHole(Address, _holeSize));
        }
    }
}
