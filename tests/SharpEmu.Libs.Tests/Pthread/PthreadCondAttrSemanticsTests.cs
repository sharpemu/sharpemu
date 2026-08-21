// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections;
using System.Reflection;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Pthread;

public sealed class PthreadCondAttrSemanticsTests
{
    [Fact]
    public void PosixCondAttr_StoresDefaultsAndSelectedValues()
    {
        const ulong memoryBase = 0x4_0000_0000;
        const ulong attrAddress = memoryBase + 0x100;
        const ulong valueAddress = memoryBase + 0x200;
        var context = new CpuContext(new FakeCpuMemory(memoryBase, 0x4000), Generation.Gen5);

        context[CpuRegister.Rdi] = attrAddress;
        Assert.Equal(0, KernelPthreadCompatExports.PosixPthreadCondattrInit(context));
        Assert.True(context.TryReadUInt64(attrAddress, out var handle));
        Assert.NotEqual(0UL, handle);

        context[CpuRegister.Rsi] = valueAddress;
        Assert.Equal(0, KernelPthreadCompatExports.PosixPthreadCondattrGetclock(context));
        Assert.True(context.TryReadUInt32(valueAddress, out var clockId));
        Assert.Equal(0U, clockId);

        Assert.Equal(0, KernelPthreadCompatExports.PosixPthreadCondattrGetpshared(context));
        Assert.True(context.TryReadUInt32(valueAddress, out var processShared));
        Assert.Equal(0U, processShared);

        context[CpuRegister.Rsi] = 4;
        Assert.Equal(0, KernelPthreadCompatExports.PosixPthreadCondattrSetclock(context));
        context[CpuRegister.Rsi] = 1;
        Assert.Equal(0, KernelPthreadCompatExports.PosixPthreadCondattrSetpshared(context));

        context[CpuRegister.Rsi] = valueAddress;
        Assert.Equal(0, KernelPthreadCompatExports.PosixPthreadCondattrGetclock(context));
        Assert.True(context.TryReadUInt32(valueAddress, out clockId));
        Assert.Equal(4U, clockId);
        Assert.Equal(0, KernelPthreadCompatExports.PosixPthreadCondattrGetpshared(context));
        Assert.True(context.TryReadUInt32(valueAddress, out processShared));
        Assert.Equal(1U, processShared);

        context[CpuRegister.Rsi] = 1;
        Assert.Equal(22, KernelPthreadCompatExports.PosixPthreadCondattrSetclock(context));
        context[CpuRegister.Rsi] = 2;
        Assert.Equal(22, KernelPthreadCompatExports.PosixPthreadCondattrSetpshared(context));

        context[CpuRegister.Rdi] = attrAddress;
        Assert.Equal(0, KernelPthreadCompatExports.PosixPthreadCondattrDestroy(context));
        context[CpuRegister.Rsi] = valueAddress;
        Assert.Equal(22, KernelPthreadCompatExports.PosixPthreadCondattrGetclock(context));
    }

    [Fact]
    public void PosixCondInit_CopiesTheAttributeClockAndSharingMode()
    {
        const ulong memoryBase = 0x4_0001_0000;
        const ulong attrAddress = memoryBase + 0x100;
        const ulong condAddress = memoryBase + 0x200;
        var context = new CpuContext(new FakeCpuMemory(memoryBase, 0x4000), Generation.Gen5);

        context[CpuRegister.Rdi] = attrAddress;
        Assert.Equal(0, KernelPthreadCompatExports.PosixPthreadCondattrInit(context));
        context[CpuRegister.Rsi] = 4;
        Assert.Equal(0, KernelPthreadCompatExports.PosixPthreadCondattrSetclock(context));
        context[CpuRegister.Rsi] = 1;
        Assert.Equal(0, KernelPthreadCompatExports.PosixPthreadCondattrSetpshared(context));

        context[CpuRegister.Rdi] = condAddress;
        context[CpuRegister.Rsi] = attrAddress;
        Assert.Equal(0, KernelPthreadCompatExports.PosixPthreadCondInit(context));
        Assert.True(context.TryReadUInt64(condAddress, out var condHandle));

        var statesField = typeof(KernelPthreadCompatExports).GetField(
            "_condStates",
            BindingFlags.NonPublic | BindingFlags.Static);
        var states = Assert.IsAssignableFrom<IDictionary>(statesField?.GetValue(null));
        var state = states[condHandle];
        Assert.NotNull(state);
        var stateType = state.GetType();
        Assert.Equal(4, stateType.GetProperty("ClockId")?.GetValue(state));
        Assert.Equal(1, stateType.GetProperty("ProcessShared")?.GetValue(state));
    }

    [Fact]
    public void PosixClockGettime_UsesTheMonotonicClockForClockFour()
    {
        const ulong memoryBase = 0x4_0002_0000;
        const ulong timeAddress = memoryBase + 0x100;
        var context = new CpuContext(new FakeCpuMemory(memoryBase, 0x1000), Generation.Gen5);
        context[CpuRegister.Rdi] = 4;
        context[CpuRegister.Rsi] = timeAddress;

        Assert.Equal(0, KernelMemoryCompatExports.ClockGettime(context));
        Assert.True(context.TryReadUInt64(timeAddress, out var seconds));
        Assert.True(context.TryReadUInt64(timeAddress + sizeof(long), out var nanoseconds));
        Assert.True(
            seconds < 1_000_000_000UL && nanoseconds < 1_000_000_000UL,
            $"seconds={seconds} nanoseconds={nanoseconds}");
    }
}
