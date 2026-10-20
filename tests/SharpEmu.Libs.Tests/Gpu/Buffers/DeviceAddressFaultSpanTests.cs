// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Buffers;

public sealed class DeviceAddressFaultSpanTests
{
    private const ulong Window = GuestBufferCache.DeviceAddressFaultWindow;
    private const ulong Page = 0x4000;

    // One fault brings in the whole aligned window around it, not a single page.
    [Fact]
    public void AFaultInsideALargeMapping_CoversItsAlignedWindow()
    {
        var mapping = 0x51_0000_0000UL;
        var page = mapping + 3 * Window + 0x7C000;
        var span = GuestBufferCache.DeviceAddressFaultSpan(page, Page, mapping, 64 * Window);
        Assert.Equal(mapping + 3 * Window, span.Address);
        Assert.Equal(Window, span.Size);
    }

    // The window never reaches past the guest mapping, so it never covers unmapped memory.
    [Fact]
    public void TheWindowIsClippedToTheMapping()
    {
        var mapping = 0x51_0000_0000UL + 0x10000;
        var span = GuestBufferCache.DeviceAddressFaultSpan(mapping + 0x8000, Page, mapping, 0x30000);
        Assert.Equal(mapping, span.Address);
        Assert.Equal(0x30000UL, span.Size);
    }

    // Without a known mapping the fault keeps its single page.
    [Fact]
    public void AFaultOutsideAnyMapping_KeepsItsPage()
    {
        var span = GuestBufferCache.DeviceAddressFaultSpan(0x51_0000_4000UL, Page, 0, 0);
        Assert.Equal(0x51_0000_4000UL, span.Address);
        Assert.Equal(Page, span.Size);
    }
}
