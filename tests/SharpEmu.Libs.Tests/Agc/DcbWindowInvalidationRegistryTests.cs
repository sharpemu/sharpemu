// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class DcbWindowInvalidationRegistryTests : IDisposable
{
    public DcbWindowInvalidationRegistryTests() =>
        DcbWindowInvalidationRegistry.ClearForTests();

    public void Dispose() => DcbWindowInvalidationRegistry.ClearForTests();

    [Fact]
    public void OverlappingWriteInvalidatesOnlyTheMatchingWindow()
    {
        var first = DcbWindowInvalidationRegistry.Register(0x1000, 0x100);
        var second = DcbWindowInvalidationRegistry.Register(0x2000, 0x100);

        Assert.Equal(1, DcbWindowInvalidationRegistry.Invalidate(0x1080, 4));

        Assert.False(DcbWindowInvalidationRegistry.IsValid(first));
        Assert.True(DcbWindowInvalidationRegistry.IsValid(second));
    }

    [Fact]
    public void AdjacentWriteDoesNotInvalidateTheWindow()
    {
        var lease = DcbWindowInvalidationRegistry.Register(0x1000, 0x100);

        Assert.Equal(0, DcbWindowInvalidationRegistry.Invalidate(0x1100, 4));

        Assert.True(DcbWindowInvalidationRegistry.IsValid(lease));
    }

    [Fact]
    public void OverflowingWriteRangeStillInvalidatesAnOverlappingWindow()
    {
        var lease = DcbWindowInvalidationRegistry.Register(ulong.MaxValue - 0x20, 0x20);

        Assert.Equal(
            1,
            DcbWindowInvalidationRegistry.Invalidate(ulong.MaxValue - 0x10, 0x40));

        Assert.False(DcbWindowInvalidationRegistry.IsValid(lease));
    }

    [Fact]
    public void UnregisteredWindowIsNoLongerInvalidated()
    {
        var lease = DcbWindowInvalidationRegistry.Register(0x1000, 0x100);
        DcbWindowInvalidationRegistry.Unregister(lease);

        Assert.Equal(0, DcbWindowInvalidationRegistry.Invalidate(0x1080, 4));
    }
}
