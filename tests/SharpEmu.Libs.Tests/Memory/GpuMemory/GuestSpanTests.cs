// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GpuMemory;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Memory.GpuMemory;

public sealed class GuestSpanTests
{
    [Theory]
    [InlineData(0UL, 0UL, true)]
    [InlineData(0UL, GuestMemoryLayout.GuestGpuLowAddressLimit, true)]
    [InlineData(GuestMemoryLayout.GuestGpuLowAddressLimit - 1, 1UL, true)]
    [InlineData(GuestMemoryLayout.GuestGpuLowAddressLimit - 1, 2UL, false)]
    [InlineData(GuestMemoryLayout.GuestGpuLowAddressLimit, 1UL, false)]
    [InlineData(GuestMemoryLayout.GuestExtendedAddressStart - 1, 1UL, false)]
    [InlineData(GuestMemoryLayout.GuestExtendedAddressStart - 1, 2UL, false)]
    [InlineData(GuestMemoryLayout.GuestExtendedAddressStart, GuestMemoryLayout.GuestExtendedAddressSize, true)]
    [InlineData(GuestMemoryLayout.GuestExtendedAddressLimit - 1, 1UL, true)]
    [InlineData(GuestMemoryLayout.GuestExtendedAddressLimit - 1, 2UL, false)]
    [InlineData(TrackerLayout.SpaceBytes, 0UL, false)]
    [InlineData(ulong.MaxValue - 1, 4UL, false)]
    public void IsValid_ChecksTheDisjointGpuAddressApertures(ulong address, ulong size, bool expected)
    {
        Assert.Equal(expected, new GuestSpan(address, size).IsValid);
    }

    [Fact]
    public void End_IsAddressPlusSize()
    {
        Assert.Equal(0x3000UL, new GuestSpan(0x1000, 0x2000).End);
        Assert.Equal(GuestSpan.Empty, default(GuestSpan));
    }
}
