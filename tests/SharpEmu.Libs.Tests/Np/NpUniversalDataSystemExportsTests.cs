// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Np;
using Xunit;

namespace SharpEmu.Libs.Tests.Np;

public sealed class NpUniversalDataSystemExportsTests
{
    [Fact]
    public void EventPropertyArraySetString_AllowsNullTelemetryDestination()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong valueAddress = memoryBase + 0x100;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        memory.WriteCString(valueAddress, "new_game");
        context[CpuRegister.Rdi] = 0;
        context[CpuRegister.Rsi] = valueAddress;

        Assert.Equal(0, NpUniversalDataSystemExports.NpUniversalDataSystemEventPropertyArraySetString(context));
        Assert.Equal(0UL, context[CpuRegister.Rax]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0x1_0000_2000)]
    public void EventPropertyArraySetString_RejectsInvalidValuePointer(ulong valueAddress)
    {
        const ulong memoryBase = 0x1_0000_0000;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rsi] = valueAddress;

        Assert.NotEqual(0, NpUniversalDataSystemExports.NpUniversalDataSystemEventPropertyArraySetString(context));
    }
}
