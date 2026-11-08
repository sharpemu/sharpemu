// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Ult;

using Xunit;

namespace SharpEmu.Libs.Tests.Ult;

/// <summary>
/// Contract tests for the libSceUlt mutex, condition variable and queue
/// exports. Every call here completes without blocking, so no guest thread
/// scheduler is involved.
/// </summary>
[Collection(UltStateCollection.Name)]
public sealed class UltSyncExportsTests
{
    private const ulong Base = 0x3_2000_0000UL;
    private const int RegionSize = 0x2000;

    private const ulong MutexAddress = Base;
    private const ulong ConditionAddress = Base + 0x200;
    private const ulong QueueAddress = Base + 0x400;
    private const ulong NameAddress = Base + 0x600;
    private const ulong DataAddress = Base + 0x800;

    public UltSyncExportsTests()
    {
        UltExports.ResetRuntimeState();
    }

    [Fact]
    public void MutexLockUnlock_RoundTrips()
    {
        var context = CreateContext();
        CreateMutex(context);

        context[CpuRegister.Rdi] = MutexAddress;
        Assert.Equal(0, UltExports.MutexLock(context));

        context[CpuRegister.Rdi] = MutexAddress;
        Assert.Equal(0, UltExports.MutexUnlock(context));

        context[CpuRegister.Rdi] = MutexAddress;
        Assert.Equal(0, UltExports.MutexDestroy(context));
    }

    [Fact]
    public void MutexTryLock_WhenHeld_ReturnsPermissionForOwner()
    {
        var context = CreateContext();
        CreateMutex(context);

        context[CpuRegister.Rdi] = MutexAddress;
        Assert.Equal(0, UltExports.MutexTryLock(context));

        context[CpuRegister.Rdi] = MutexAddress;
        Assert.Equal(unchecked((int)0x80810005), UltExports.MutexTryLock(context));

        context[CpuRegister.Rdi] = MutexAddress;
        Assert.Equal(unchecked((int)0x80810007), UltExports.MutexDestroy(context));
    }

    [Fact]
    public void MutexUnlock_WhenNotHeld_ReturnsPermission()
    {
        var context = CreateContext();
        CreateMutex(context);

        context[CpuRegister.Rdi] = MutexAddress;
        Assert.Equal(unchecked((int)0x80810005), UltExports.MutexUnlock(context));
    }

    [Fact]
    public void ConditionSignal_WithoutWaiters_Succeeds()
    {
        var context = CreateContext();
        context[CpuRegister.Rdi] = ConditionAddress;
        context[CpuRegister.Rsi] = NameAddress;
        Assert.Equal(0, UltExports.ConditionVariableCreate(context));

        context[CpuRegister.Rdi] = ConditionAddress;
        Assert.Equal(0, UltExports.ConditionVariableSignal(context));

        context[CpuRegister.Rdi] = ConditionAddress;
        Assert.Equal(0, UltExports.ConditionVariableDestroy(context));
    }

    [Fact]
    public void QueuePushThenTryPop_PreservesFifoOrder()
    {
        var memory = new FakeCpuMemory(Base, RegionSize);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = QueueAddress;
        context[CpuRegister.Rsi] = NameAddress;
        context[CpuRegister.Rdx] = sizeof(ulong);
        Assert.Equal(0, UltExports.QueueCreate(context));

        foreach (var value in new ulong[] { 0x1111, 0x2222 })
        {
            Assert.True(context.TryWriteUInt64(DataAddress, value));
            context[CpuRegister.Rdi] = QueueAddress;
            context[CpuRegister.Rsi] = DataAddress;
            Assert.Equal(0, UltExports.QueuePush(context));
        }

        foreach (var expected in new ulong[] { 0x1111, 0x2222 })
        {
            context[CpuRegister.Rdi] = QueueAddress;
            context[CpuRegister.Rsi] = DataAddress;
            Assert.Equal(0, UltExports.QueueTryPop(context));
            Assert.True(context.TryReadUInt64(DataAddress, out var popped));
            Assert.Equal(expected, popped);
        }

        context[CpuRegister.Rdi] = QueueAddress;
        context[CpuRegister.Rsi] = DataAddress;
        Assert.Equal(unchecked((int)0x80810007), UltExports.QueueTryPop(context));
    }

    private static CpuContext CreateContext()
    {
        var memory = new FakeCpuMemory(Base, RegionSize);
        memory.TryWrite(NameAddress, "sync\0"u8);
        return new CpuContext(memory, Generation.Gen5);
    }

    private static void CreateMutex(CpuContext context)
    {
        context[CpuRegister.Rdi] = MutexAddress;
        context[CpuRegister.Rsi] = NameAddress;
        Assert.Equal(0, UltExports.MutexCreate(context));
    }
}

// Both Ult test classes reset the exports' shared static state, so they must
// not run in parallel with each other.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class UltStateCollection
{
    public const string Name = "UltState";
}
