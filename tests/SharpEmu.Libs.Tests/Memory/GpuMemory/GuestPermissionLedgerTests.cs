// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.HLE.GpuMemory;
using Xunit;

namespace SharpEmu.Libs.Tests.Memory.GpuMemory;

public sealed class GuestPermissionLedgerTests
{
    private const GuestPageProtection ReadWrite = GuestPageProtection.Read | GuestPageProtection.Write;

    [Theory]
    [InlineData(17)]
    [InlineData(631)]
    public void RandomUpdatesMatchPermissionArray(int seed)
    {
        var ledger = new GuestPermissionLedger();
        var expected = Enumerable.Repeat(ReadWrite, 256).ToArray();
        var random = new System.Random(seed);
        for (var iteration = 0; iteration < 3000; iteration++)
        {
            var start = random.Next(expected.Length);
            var length = random.Next(1, expected.Length - start + 1);
            var protection = (GuestPageProtection)random.Next(8);
            if (random.Next(3) == 0)
            {
                ledger.Clear((ulong)start, (ulong)length);
                protection = ReadWrite;
            }
            else
                ledger.Set((ulong)start, (ulong)length, protection);
            Array.Fill(expected, protection, start, length);
            for (var address = 0; address < expected.Length; address++)
                Assert.Equal(expected[address], ledger.Lookup((ulong)address));
        }
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (ulong address = 0; address < 256; address++)
            _ = ledger.Lookup(address);
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }

    [Fact]
    public void Set_SplitsTheSpansItOverlaps()
    {
        var ledger = new GuestPermissionLedger();
        ledger.Set(0x1000, 0x3000, GuestPageProtection.None);
        ledger.Set(0x2000, 0x1000, GuestPageProtection.Read);

        Assert.Equal(GuestPageProtection.None, ledger.Lookup(0x1000));
        Assert.Equal(GuestPageProtection.None, ledger.Lookup(0x1FFF));
        Assert.Equal(GuestPageProtection.Read, ledger.Lookup(0x2000));
        Assert.Equal(GuestPageProtection.Read, ledger.Lookup(0x2FFF));
        Assert.Equal(GuestPageProtection.None, ledger.Lookup(0x3000));
        Assert.Equal(ReadWrite, ledger.Lookup(0x4000));

        ledger.Set(0x0000, 0x8000, GuestPageProtection.Execute);
        Assert.Equal(GuestPageProtection.Execute, ledger.Lookup(0x2800));
    }

    [Fact]
    public void Clear_KeepsTheOuterParts()
    {
        var ledger = new GuestPermissionLedger();
        ledger.Set(0x1000, 0x3000, GuestPageProtection.None);

        ledger.Clear(0x2000, 0x1000);

        Assert.Equal(GuestPageProtection.None, ledger.Lookup(0x1000));
        Assert.Equal(ReadWrite, ledger.Lookup(0x2000));
        Assert.Equal(GuestPageProtection.None, ledger.Lookup(0x3000));
    }

    [Fact]
    public void Lookup_DefaultsToReadWriteWithoutARecord()
    {
        var ledger = new GuestPermissionLedger();

        Assert.Equal(ReadWrite, ledger.Lookup(0));
        Assert.Equal(ReadWrite, ledger.Lookup(0x10000));
    }
}
