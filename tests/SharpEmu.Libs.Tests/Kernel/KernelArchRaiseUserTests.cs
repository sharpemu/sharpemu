// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using SharpEmu.Core.Cpu.Native;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

[Collection("GuestThreadFlowProfile")]
public sealed class KernelArchRaiseUserTests
{
    private const int Esrch = unchecked((int)0x80020003);
    private const ulong Callback = 0x1_2345_6780;
    private const ulong Thread = 0x1_7000_0010;
    private const ulong MemoryBase = 0x1_3000_0000;

    [Fact]
    public void RaiseWithoutRegisteredCallbackReportsNoSuchThread()
    {
        var ctx = new CpuContext(new FakeCpuMemory(MemoryBase, 0x1000), Generation.Gen5);
        Register(0);
        ctx[CpuRegister.Rdi] = Thread;

        Assert.Equal(Esrch, KernelExceptionCompatExports.ArchRaiseUser(ctx));
        Assert.Equal(unchecked((ulong)Esrch), ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void RaiseDeliversTheGcCallbackWithUnityArguments()
    {
        var ctx = new CpuContext(new FakeCpuMemory(MemoryBase, 0x1000), Generation.Gen5);
        var scheduler = DispatchProxy.Create<IGuestThreadScheduler, RaiseScheduler>();
        var proxy = (RaiseScheduler)(object)scheduler;
        var previous = GuestThreadExecution.Scheduler;
        try
        {
            GuestThreadExecution.Scheduler = scheduler;
            Register(Callback);
            ctx[CpuRegister.Rdi] = Thread;

            Assert.Equal(0, KernelExceptionCompatExports.ArchRaiseUser(ctx));
            Assert.Equal(0UL, ctx[CpuRegister.Rax]);
            Assert.Equal(1, proxy.Calls);
            Assert.Equal(Thread, proxy.Thread);
            Assert.Equal(Callback, proxy.Handler);
            Assert.Equal(GuestExceptionHandlerArguments.UnityGcSuspend, proxy.Arguments);
        }
        finally
        {
            GuestThreadExecution.Scheduler = previous;
            Register(0);
        }
    }

    [Fact]
    public void SchedulerFailureReportsNoSuchThread()
    {
        var ctx = new CpuContext(new FakeCpuMemory(MemoryBase, 0x1000), Generation.Gen5);
        var scheduler = DispatchProxy.Create<IGuestThreadScheduler, RaiseScheduler>();
        ((RaiseScheduler)(object)scheduler).Succeed = false;
        var previous = GuestThreadExecution.Scheduler;
        try
        {
            GuestThreadExecution.Scheduler = scheduler;
            Register(Callback);
            ctx[CpuRegister.Rdi] = Thread;

            Assert.Equal(Esrch, KernelExceptionCompatExports.ArchRaiseUser(ctx));
            Assert.Equal(unchecked((ulong)Esrch), ctx[CpuRegister.Rax]);
        }
        finally
        {
            GuestThreadExecution.Scheduler = previous;
            Register(0);
        }
    }

    [Fact]
    public void HandlerArgumentsFollowTheConvention()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var contextAddress = MemoryBase + 0x100;
        var continuation = default(GuestCpuContinuation) with
        {
            Rip = 0x1_1000_0000,
            Rsp = 0x1_6000_0F00,
            Rflags = 0x202,
        };
        Assert.True(DirectExecutionBackend.TryWriteGuestExceptionContext(ctx, contextAddress, continuation, 0x500));

        DirectExecutionBackend.ResolveGuestExceptionHandlerArguments(
            ctx, GuestExceptionHandlerArguments.Signal, 11, contextAddress, out var a0, out var a1);
        Assert.Equal(11UL, a0);
        Assert.Equal(contextAddress, a1);

        DirectExecutionBackend.ResolveGuestExceptionHandlerArguments(
            ctx, GuestExceptionHandlerArguments.UnityGcSuspend, 0, contextAddress, out a0, out a1);
        Assert.Equal(contextAddress, a0);
        Assert.Equal(0x1_6000_0F00UL, a1);
    }

    private static void Register(ulong callback)
    {
        var ctx = new CpuContext(new FakeCpuMemory(MemoryBase, 0x1000), Generation.Gen5);
        ctx[CpuRegister.Rdi] = callback;
        Assert.Equal(0, KernelExceptionCompatExports.ArchInitGc(ctx));
    }

    public class RaiseScheduler : DispatchProxy
    {
        private int _calls;

        public bool Succeed { get; set; } = true;

        public int Calls => Volatile.Read(ref _calls);

        public ulong Thread { get; private set; }

        public ulong Handler { get; private set; }

        public GuestExceptionHandlerArguments Arguments { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IGuestThreadScheduler.TryRaiseGuestException) &&
                args is { Length: 6 })
            {
                Interlocked.Increment(ref _calls);
                Thread = (ulong)args[1]!;
                Handler = (ulong)args[2]!;
                Arguments = (GuestExceptionHandlerArguments)args[4]!;
                args[5] = Succeed ? null : "refused";
                return Succeed;
            }

            throw new NotSupportedException(targetMethod?.Name);
        }
    }
}
