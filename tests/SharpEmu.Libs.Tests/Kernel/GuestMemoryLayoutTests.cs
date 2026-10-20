// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

public sealed class GuestMemoryLayoutTests
{
    [Fact]
    public void DirectMemorySizeIsTheTwelveAndAHalfGiBAGameGetsOnTheConsole()
    {
        var context = new CpuContext(new FakeCpuMemory(0x1_0000_0000, 0x1000), Generation.Gen5);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, KernelMemoryCompatExports.KernelGetDirectMemorySize(context));

        Assert.Equal(12UL * 1024 * 1024 * 1024 + 512UL * 1024 * 1024, context[CpuRegister.Rax]);
    }

    [Fact]
    public void FlexibleMemoryFollowsDirectMemoryInTheBackingAndBothAreGranuleAligned()
    {
        Assert.Equal(GuestMemoryLayout.DirectBytes, GuestMemoryLayout.FlexibleOffset);
        Assert.Equal(GuestMemoryLayout.DirectBytes + GuestMemoryLayout.FlexibleBytes, GuestMemoryLayout.BackingBytes);
        // The backing view is mapped in 64 KiB allocation granules on Windows.
        Assert.Equal(0UL, GuestMemoryLayout.DirectBytes % 0x1_0000);
        Assert.Equal(0UL, GuestMemoryLayout.FlexibleBytes % 0x1_0000);
        Assert.Equal(0UL, GuestMemoryLayout.DirectBytes % GuestMemoryLayout.GuestPage);
    }
}
