// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Pthread;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PthreadStackBoundsCollection
{
    public const string Name = "PthreadStackBounds";
}

[Collection(PthreadStackBoundsCollection.Name)]
public sealed class PthreadStackBoundsTests : IDisposable
{
    private const ulong MemoryBase = 0x6_2000_0000;
    private const ulong ThreadHandle = 0x1234_5000;
    private const ulong AttrAddress = MemoryBase + 0x100;
    private const ulong OutStackAddress = MemoryBase + 0x200;
    private const ulong OutStackSizeAddress = MemoryBase + 0x208;
    private readonly IGuestThreadScheduler? _previousScheduler = GuestThreadExecution.Scheduler;

    [Fact]
    public void PosixGetAttrUsesSchedulerBoundsForRemoteHighSlotThread()
    {
        const ulong stackBase = 0x0000_7FFF_A800_0000;
        const ulong stackSize = 0x20_0000;
        var scheduler = CreateScheduler(ThreadHandle, stackBase, stackSize);
        GuestThreadExecution.Scheduler = scheduler;

        var context = CreateContext();
        context[CpuRegister.Rsp] = stackBase + stackSize - 0xEA0;
        context[CpuRegister.Rdi] = ThreadHandle;
        context[CpuRegister.Rsi] = AttrAddress;

        Assert.Equal(0, KernelPthreadExtendedCompatExports.PthreadAttrGetNpPOSIX(context));
        AssertStackBounds(context, stackBase, stackSize);
    }

    [Fact]
    public void CurrentPrimaryThreadRetainsBoundedStackPointerFallback()
    {
        const ulong primaryThreadHandle = ThreadHandle + 1;
        var stackBase = OperatingSystem.IsWindows()
            ? 0x0000_7FFF_F000_0000UL
            : 0x0000_6FFF_F000_0000UL;
        GuestThreadExecution.Scheduler = CreateScheduler();
        var previousThread = GuestThreadExecution.EnterGuestThread(primaryThreadHandle);
        try
        {
            var context = CreateContext();
            context[CpuRegister.Rsp] = stackBase + 0x1F_F160;
            context[CpuRegister.Rdi] = primaryThreadHandle;
            context[CpuRegister.Rsi] = AttrAddress + 0x20;

            Assert.Equal(0, KernelPthreadExtendedCompatExports.PthreadAttrGet(context));
            AssertStackBounds(context, stackBase, 0x20_0000, AttrAddress + 0x20);
        }
        finally
        {
            GuestThreadExecution.RestoreGuestThread(previousThread);
        }
    }

    [Fact]
    public void InvalidSchedulerBoundsDoNotReplaceDefaultAttributes()
    {
        const ulong invalidThreadHandle = ThreadHandle + 2;
        GuestThreadExecution.Scheduler = CreateScheduler(invalidThreadHandle, 0, 0x20_0000);
        var context = CreateContext();
        context[CpuRegister.Rdi] = invalidThreadHandle;
        context[CpuRegister.Rsi] = AttrAddress + 0x40;

        Assert.Equal(0, KernelPthreadExtendedCompatExports.PthreadAttrGet(context));
        AssertStackBounds(context, 0, 0x10_0000, AttrAddress + 0x40);
    }

    public void Dispose()
    {
        GuestThreadExecution.Scheduler = _previousScheduler;
    }

    private static CpuContext CreateContext() =>
        new(new FakeCpuMemory(MemoryBase, 0x4000), Generation.Gen5);

    private static IGuestThreadScheduler CreateScheduler(
        ulong threadHandle = 0,
        ulong stackBase = 0,
        ulong stackSize = 0)
    {
        var scheduler = DispatchProxy.Create<IGuestThreadScheduler, StackBoundsSchedulerProxy>();
        var proxy = (StackBoundsSchedulerProxy)(object)scheduler;
        if (threadHandle != 0)
        {
            proxy.Bounds[threadHandle] = (stackBase, stackSize);
        }

        return scheduler;
    }

    private static void AssertStackBounds(
        CpuContext context,
        ulong expectedBase,
        ulong expectedSize,
        ulong attrAddress = AttrAddress)
    {
        context[CpuRegister.Rdi] = attrAddress;
        context[CpuRegister.Rsi] = OutStackAddress;
        context[CpuRegister.Rdx] = OutStackSizeAddress;
        Assert.Equal(0, KernelPthreadExtendedCompatExports.PthreadAttrGetstackPOSIX(context));
        Assert.True(context.TryReadUInt64(OutStackAddress, out var actualBase));
        Assert.True(context.TryReadUInt64(OutStackSizeAddress, out var actualSize));
        Assert.Equal(expectedBase, actualBase);
        Assert.Equal(expectedSize, actualSize);
    }

    public class StackBoundsSchedulerProxy : DispatchProxy
    {
        public Dictionary<ulong, (ulong Base, ulong Size)> Bounds { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IGuestThreadScheduler.TryGetGuestThreadStackBounds))
            {
                var found = Bounds.TryGetValue((ulong)args![0]!, out var bounds);
                args[1] = found ? bounds.Base : 0UL;
                args[2] = found ? bounds.Size : 0UL;
                return found;
            }

            throw new NotSupportedException(targetMethod?.Name);
        }
    }
}
