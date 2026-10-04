// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Buffers;

public sealed class PageOwnerTableTests
{
    [Fact]
    public void DensePageIndexPacksTheExtendedApertureAfterLowMemory()
    {
        const ulong low = 0x0000_0010_1234_4000;
        const ulong extended = GuestMemoryLayout.GuestExtendedAddressStart + 0x1234_4000;

        Assert.Equal(low >> PageOwnerTable.PageBits, PageOwnerTable.PageIndex(low));
        Assert.Equal(
            (PageOwnerTable.LowerAddressSpaceSize + 0x1234_4000) >> PageOwnerTable.PageBits,
            PageOwnerTable.PageIndex(extended));
        Assert.NotEqual(PageOwnerTable.PageIndex(0x1234_4000), PageOwnerTable.PageIndex(extended));
        Assert.Equal(low, PageOwnerTable.GuestAddress(PageOwnerTable.PageIndex(low) << PageOwnerTable.PageBits));
        Assert.Equal(extended, PageOwnerTable.GuestAddress(PageOwnerTable.PageIndex(extended) << PageOwnerTable.PageBits));
    }

    [Theory]
    [InlineData(DeviceAddressPaging.LowerAddressSize - 1)]
    [InlineData(DeviceAddressPaging.ExtendedAddressBase)]
    [InlineData(DeviceAddressPaging.ExtendedAddressBase + 0x3fff)]
    [InlineData(DeviceAddressPaging.ExtendedAddressLimit - 1)]
    public void DensePageIndexRoundTripsEveryApertureSeam(ulong address)
    {
        var pageBase = address & ~DeviceAddressPaging.PageOffsetMask;
        var packedPageBase = PageOwnerTable.PageIndex(address) << DeviceAddressPaging.PageBits;
        Assert.Equal(pageBase, PageOwnerTable.GuestAddress(packedPageBase));
    }

    [Theory]
    [InlineData(DeviceAddressPaging.LowerAddressSize)]
    [InlineData(DeviceAddressPaging.ExtendedAddressBase - 1)]
    [InlineData(DeviceAddressPaging.ExtendedAddressLimit)]
    public void DensePageIndexRejectsTheHoleAndAddressesPastTheApertures(ulong address)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PageOwnerTable.PageIndex(address));
    }

    [Fact]
    public void PackedAddressReverseMappingRejectsPastTheDenseTable()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PageOwnerTable.GuestAddress(DeviceAddressPaging.PackedAddressSize));
    }

    [Fact]
    public void HostAndShaderPagingConstantsStayIdentical()
    {
        Assert.Equal(DeviceAddressPaging.PageBits, PageOwnerTable.PageBits);
        Assert.Equal(GuestMemoryLayout.GuestGpuLowAddressLimit, DeviceAddressPaging.LowerAddressSize);
        Assert.Equal(GuestMemoryLayout.GuestExtendedAddressStart, DeviceAddressPaging.ExtendedAddressBase);
        Assert.Equal(GuestMemoryLayout.GuestExtendedAddressSize, DeviceAddressPaging.ExtendedAddressSize);
        Assert.Equal(DeviceAddressPaging.PageCount, PageOwnerTable.PackedPageCount);
    }

    [Fact]
    public void RawOwnerPagesAcceptOnlyTheTwoGuestApertures()
    {
        Assert.True(PageOwnerTable.TryGetPageRange(PageOwnerTable.LowerAddressSpaceSize - 1, 1, out _, out _));
        Assert.False(PageOwnerTable.TryGetPageRange(PageOwnerTable.LowerAddressSpaceSize, 1, out _, out _));
        Assert.False(PageOwnerTable.TryGetPageRange(PageOwnerTable.ExtendedAddressStart - 1, 2, out _, out _));
        Assert.True(PageOwnerTable.TryGetPageRange(PageOwnerTable.ExtendedAddressStart, 1, out var first, out var last));
        Assert.Equal(PageOwnerTable.ExtendedAddressStart >> PageOwnerTable.PageBits, first);
        Assert.Equal(first + 1, last);
        Assert.True(PageOwnerTable.TryGetPageRange(PageOwnerTable.ExtendedAddressLimit - 1, 1, out _, out _));
        Assert.False(PageOwnerTable.TryGetPageRange(PageOwnerTable.ExtendedAddressLimit - 1, 2, out _, out _));
    }
}
