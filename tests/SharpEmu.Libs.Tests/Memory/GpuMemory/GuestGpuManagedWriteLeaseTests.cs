// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using Xunit;

namespace SharpEmu.Libs.Tests.Memory.GpuMemory;

[Collection(GpuMemoryStateCollection.Name)]
public sealed class GuestGpuManagedWriteLeaseTests
{
    private sealed class Store : IGuestBufferStore
    {
        internal readonly List<(bool Landed, ulong Address, ulong Size)> Calls = [];
        internal bool ThrowOnLanded;
        internal bool GpuData = true;
        internal readonly List<(ulong Address, ulong Size)> EmulatorWrites = [];
        public bool MayHoldGpuData(ulong address, ulong size) => GpuData;
        public bool NoteEmulatorWrite(ulong address, ulong size)
        {
            EmulatorWrites.Add((address, size));
            MarkCpuWriteLanded(address, size);
            return true;
        }
        public bool MarkCpuWrite(ulong address, ulong size)
        {
            Calls.Add((false, address, size));
            return true;
        }
        public void MarkCpuWriteLanded(ulong address, ulong size)
        {
            Calls.Add((true, address, size));
            if (ThrowOnLanded) throw new InvalidOperationException("Synthetic landed notification failure.");
        }
        public bool DownloadToCpu(ulong address, ulong size) => true;
    }

    [Fact]
    public void WriteAcrossUnregisteredGapNotifiesEveryMappedIntersection()
    {
        using var memory = new GuestGpuMemory(new RecordingAddressSpace());
        var store = new Store();
        memory.AttachStores(store, null);
        const GuestPageProtection access = GuestPageProtection.Read | GuestPageProtection.Write;
        memory.Register(0x10000, 0x4000, access);
        memory.Register(0x18000, 0x4000, access);
        // Registration reports each span to the store; only the managed write's notifications are under test.
        store.Calls.Clear();
        Assert.False(memory.Covers(0x12000, 0x8000));
        using (memory.BeginManagedWrite(0x12000, 0x8000)) { }
        Assert.Equal(new (bool, ulong, ulong)[]
        {
            (false, 0x12000, 0x2000), (false, 0x18000, 0x2000),
            (true, 0x12000, 0x2000), (true, 0x18000, 0x2000)
        }, store.Calls);
    }

    // A write through one backing view: the stores take PrepareEmulatorWrite and NoteEmulatorWrite for every intersection.
    [Fact]
    public void EmulatorWriteAcrossUnregisteredGapNotifiesEveryMappedIntersection()
    {
        using var memory = new GuestGpuMemory(new RecordingAddressSpace());
        var store = new Store();
        memory.AttachStores(store, null);
        const GuestPageProtection access = GuestPageProtection.Read | GuestPageProtection.Write;
        memory.Register(0x10000, 0x4000, access);
        memory.Register(0x18000, 0x4000, access);
        store.Calls.Clear();
        using (memory.BeginManagedWrite(0x12000, 0x8000, emulatorWrite: true)) { }
        Assert.Equal(new (bool, ulong, ulong)[]
        {
            (false, 0x12000, 0x2000), (false, 0x18000, 0x2000),
            (true, 0x12000, 0x2000), (true, 0x18000, 0x2000)
        }, store.Calls);
        Assert.Equal(new (ulong, ulong)[] { (0x12000, 0x2000), (0x18000, 0x2000) }, store.EmulatorWrites);
    }

    // A GPU claim between the premark and the reservation invalidates the attempt: the write recovers GPU data again before
    // its bytes land, in a registered span and in a lazily backed range alike.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmulatorWriteRetriesRecoveryAfterAnInterveningGpuClaim(bool lazy)
    {
        using var memory = new GuestGpuMemory(new RecordingAddressSpace());
        var store = new Store { GpuData = false };
        memory.AttachStores(store, null);
        if (lazy) memory.RegisterLazyRange(0x10000, 0x4000);
        else memory.Register(0x10000, 0x4000, GuestPageProtection.Read | GuestPageProtection.Write);
        store.Calls.Clear();
        var attempts = 0;
        memory.BeforeManagedWriteReservationForTest = retry =>
        {
            attempts++;
            if (retry != 0) return;
            using var claim = memory.Pages.ManagedWrites.BeginGpuClaim(0x10000, 16);
            store.GpuData = true;
        };
        using (memory.BeginManagedWrite(0x10000, 16, emulatorWrite: true))
        {
            Assert.Equal(2, attempts);
            Assert.Equal(new (bool, ulong, ulong)[] { (false, 0x10000, 16) }, store.Calls);
            Assert.Empty(store.EmulatorWrites);
        }

        Assert.Equal(new (bool, ulong, ulong)[] { (false, 0x10000, 16), (true, 0x10000, 16) }, store.Calls);
        Assert.Equal(new (ulong, ulong)[] { (0x10000, 16) }, store.EmulatorWrites);
    }

    // A write over a registered span and a lazily backed range marks both intersections.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PartialWriteIncludesLazyAndRegisteredIntersections(bool emulatorWrite)
    {
        using var memory = new GuestGpuMemory(new RecordingAddressSpace());
        var store = new Store();
        memory.AttachStores(store, null);
        memory.Register(0x10000, 0x4000, GuestPageProtection.Read | GuestPageProtection.Write);
        memory.RegisterLazyRange(0x18000, 0x4000);
        store.Calls.Clear();
        using (memory.BeginManagedWrite(0x12000, 0x8000, emulatorWrite)) { }
        Assert.Equal(new (bool, ulong, ulong)[]
        {
            (false, 0x12000, 0x2000), (false, 0x18000, 0x2000),
            (true, 0x12000, 0x2000), (true, 0x18000, 0x2000)
        }, store.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReservedHookOrLandedExceptionReleasesAdmission(bool failLanded)
    {
        using var memory = new GuestGpuMemory(new RecordingAddressSpace());
        var store = new Store();
        memory.AttachStores(store, null);
        memory.Register(0x10000, 0x4000, GuestPageProtection.Read | GuestPageProtection.Write);
        // Registration reports the span to the store; arm the failure and count calls from the write on.
        store.Calls.Clear();
        store.ThrowOnLanded = failLanded;
        if (!failLanded)
            memory.ManagedWriteReservedForTest = () => throw new InvalidOperationException("Synthetic reserved hook failure.");
        Assert.Throws<InvalidOperationException>(() =>
        {
            using var write = memory.BeginManagedWrite(0x10000, 16);
        });
        using var claim = memory.Pages.ManagedWrites.BeginGpuClaim(0x10000, 16);
        Assert.Equal(failLanded ? 2 : 1, store.Calls.Count);
    }
}
