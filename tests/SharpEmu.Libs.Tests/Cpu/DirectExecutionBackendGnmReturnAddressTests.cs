// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using System.Runtime.InteropServices;
using SharpEmu.Core.Cpu.Native;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class DirectExecutionBackendGnmReturnAddressTests
{
    [Theory]
    [InlineData(Generation.Gen4, 0x400000UL, true)]
    [InlineData(Generation.Gen5, 0x400000UL, false)]
    [InlineData(Generation.Gen4, 0x12A00123UL, true)]
    [InlineData(Generation.Gen5, 0x12A00123UL, false)]
    [InlineData(Generation.Gen4, 0x300000000UL, true)]
    [InlineData(Generation.Gen5, 0x300000000UL, true)]
    [InlineData(Generation.Gen4, 0xFFFFUL, false)]
    [InlineData(Generation.Gen5, 0xFFFFUL, false)]
    [InlineData(Generation.Gen4, 0xFFFFFFFEUL, false)]
    [InlineData(Generation.Gen5, 0xFFFFFFFEUL, false)]
    [InlineData(Generation.Gen4, 0x100000000000UL, false)]
    [InlineData(Generation.Gen5, 0x100000000000UL, false)]
    public void Ps4ImageCallersArePlausibleOnlyDuringGnmExecution(Generation generation, ulong address, bool expected)
    {
        Assert.Equal(expected, Classify("IsPlausibleReturnAddress", generation, address));
    }

    [Fact]
    public void GnmStillRejectsReadableMemoryThatIsNotExecutable()
    {
        var data = Marshal.AllocHGlobal(16);
        try
        {
            Assert.False(Classify("IsLikelyReturnAddress", Generation.Gen4, unchecked((ulong)data)));
        }
        finally
        {
            Marshal.FreeHGlobal(data);
        }
    }

    private static bool Classify(string methodName, Generation generation, ulong address)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
        var activeContext = typeof(DirectExecutionBackend).GetField("_activeCpuContext", flags)!;
        var previous = activeContext.GetValue(null);
        try
        {
            activeContext.SetValue(null, new CpuContext(new FakeCpuMemory(0x400000, 0x1000), generation));
            return (bool)typeof(DirectExecutionBackend).GetMethod(methodName, flags)!.Invoke(null, [address])!;
        }
        finally
        {
            activeContext.SetValue(null, previous);
        }
    }
}
