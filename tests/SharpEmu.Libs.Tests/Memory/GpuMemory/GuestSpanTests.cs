// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GpuMemory;
using Xunit;

namespace SharpEmu.Libs.Tests.Memory.GpuMemory;

public sealed class GuestSpanTests
{
    [Theory]
    [InlineData(0UL, 0UL, true)]
    [InlineData(0UL, 1UL << 40, true)]
    [InlineData(0x1000UL, (1UL << 40) - 0x1000, true)]
    [InlineData(1UL << 40, 0UL, false)]
    [InlineData(0x1000UL, (1UL << 40) - 0xFFF, false)]
    public void IsValid_ChecksTheGpuAddressSpaceBound(ulong address, ulong size, bool expected)
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
