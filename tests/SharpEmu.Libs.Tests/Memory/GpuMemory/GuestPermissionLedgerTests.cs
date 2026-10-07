// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.HLE.GpuMemory;
using Xunit;

namespace SharpEmu.Libs.Tests.Memory.GpuMemory;

public sealed class GuestPermissionLedgerTests
{
    private const GuestPageProtection ReadWrite = GuestPageProtection.Read | GuestPageProtection.Write;

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
