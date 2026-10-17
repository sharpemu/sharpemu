// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Ult;

using Xunit;

namespace SharpEmu.Libs.Tests.Ult;

/// <summary>
/// Contract tests for the libSceUlt ulthread runtime exports. They do not
/// start ulthreads, so no live guest thread scheduler is involved.
/// </summary>
[Collection(UltStateCollection.Name)]
public sealed class UltExportsTests
{
    private const ulong Base = 0x3_1000_0000UL;
    private const int RegionSize = 0x2000;

    private const ulong RuntimeAddress = Base;
    private const ulong NameAddress = Base + 0x400;
    private const ulong WorkAreaAddress = Base + 0x800;

    public UltExportsTests()
    {
        UltExports.ResetRuntimeState();
    }

    [Fact]
    public void RuntimeGetWorkAreaSize_ReturnsSizeInRax()
    {
        var context = new CpuContext(new FakeCpuMemory(Base, RegionSize), Generation.Gen5);
        context[CpuRegister.Rdi] = 0x80;
        context[CpuRegister.Rsi] = 6;

        var result = UltExports.UlthreadRuntimeGetWorkAreaSize(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(UltExports.ComputeRuntimeWorkAreaSize(0x80, 6), context[CpuRegister.Rax]);
        Assert.True(context[CpuRegister.Rax] is > 0 and < 0x8000_0000UL);
    }

    [Fact]
    public void RuntimeCreate_ThenDestroy_Succeeds()
    {
        var memory = new FakeCpuMemory(Base, RegionSize);
        memory.TryWrite(NameAddress, "jobs\0"u8);
        var context = new CpuContext(memory, Generation.Gen5);

        SetRuntimeCreateArgs(context);
        Assert.Equal(0, UltExports.UlthreadRuntimeCreate(context));

        context[CpuRegister.Rdi] = RuntimeAddress;
        Assert.Equal(0, UltExports.UlthreadRuntimeDestroy(context));
    }

    [Fact]
    public void RuntimeCreate_SameRuntimeTwice_ReturnsBusy()
    {
        var context = new CpuContext(new FakeCpuMemory(Base, RegionSize), Generation.Gen5);

        SetRuntimeCreateArgs(context);
        Assert.Equal(0, UltExports.UlthreadRuntimeCreate(context));

        SetRuntimeCreateArgs(context);
        Assert.Equal(UltExports.ErrorBusy, UltExports.UlthreadRuntimeCreate(context));
    }

    [Fact]
    public void RuntimeCreate_NullWorkArea_ReturnsNullError()
    {
        var context = new CpuContext(new FakeCpuMemory(Base, RegionSize), Generation.Gen5);
        SetRuntimeCreateArgs(context);
        context[CpuRegister.R8] = 0;

        Assert.Equal(UltExports.ErrorNull, UltExports.UlthreadRuntimeCreate(context));
    }

    [Fact]
    public void RuntimeDestroy_UnknownRuntime_ReturnsInvalid()
    {
        var context = new CpuContext(new FakeCpuMemory(Base, RegionSize), Generation.Gen5);
        context[CpuRegister.Rdi] = RuntimeAddress;

        Assert.Equal(UltExports.ErrorInvalid, UltExports.UlthreadRuntimeDestroy(context));
    }

    [Fact]
    public void Join_UnknownUlthread_ReturnsInvalid()
    {
        var context = new CpuContext(new FakeCpuMemory(Base, RegionSize), Generation.Gen5);
        context[CpuRegister.Rdi] = Base + 0x100;
        context[CpuRegister.Rsi] = 0;

        Assert.Equal(UltExports.ErrorInvalid, UltExports.UlthreadJoin(context));
    }

    private static void SetRuntimeCreateArgs(CpuContext context)
    {
        context[CpuRegister.Rdi] = RuntimeAddress;
        context[CpuRegister.Rsi] = NameAddress;
        context[CpuRegister.Rdx] = 0x80;
        context[CpuRegister.Rcx] = 6;
        context[CpuRegister.R8] = WorkAreaAddress;
        context[CpuRegister.R9] = 0;
    }
}
