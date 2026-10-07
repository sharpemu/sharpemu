// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Reflection;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

[Collection("GuestThreadFlowProfile")]
public sealed class KernelHostWaitExceptionTests
{
    private const ulong MemoryBase = 0x1_3000_0000;
    private const ulong SemaphoreAddress = MemoryBase + 0x100;
    private const ulong NameAddress = MemoryBase + 0x200;
    private const ulong ValueAddress = MemoryBase + 0x300;
    private const ulong TimeoutAddress = MemoryBase + 0x400;

    [Fact]
    public void SemaphoreHostWaitDeliversPendingExceptionBeforeReturning()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var createContext = new CpuContext(memory, Generation.Gen5);
        Assert.True(memory.TryWrite(NameAddress, "exception-sema\0"u8));
        createContext[CpuRegister.Rdi] = SemaphoreAddress;
        createContext[CpuRegister.Rsi] = NameAddress;
        createContext[CpuRegister.Rdx] = 1;
        createContext[CpuRegister.Rcx] = 0;
        createContext[CpuRegister.R8] = 1;
        createContext[CpuRegister.R9] = 0;
        Assert.Equal(0, KernelSemaphoreCompatExports.KernelCreateSema(createContext));
        Assert.True(createContext.TryReadUInt64(SemaphoreAddress, out var handle));

        WriteUInt32(memory, TimeoutAddress, 1_000_000);
        var waitContext = new CpuContext(memory, Generation.Gen5);
        waitContext[CpuRegister.Rdi] = handle;
        waitContext[CpuRegister.Rsi] = 1;
        waitContext[CpuRegister.Rdx] = TimeoutAddress;
        var signalContext = new CpuContext(memory, Generation.Gen5);
        var scheduler = DispatchProxy.Create<IGuestThreadScheduler, PendingExceptionScheduler>();
        var proxy = (PendingExceptionScheduler)(object)scheduler;
        proxy.DeliverException = context =>
        {
            Assert.Same(waitContext, context);
            Assert.Equal(
                0,
                KernelSemaphoreCompatExports.KernelSignalSema(
                    signalContext,
                    unchecked((uint)handle),
                    1));
        };

        var previousScheduler = GuestThreadExecution.Scheduler;
        try
        {
            GuestThreadExecution.Scheduler = scheduler;
            Assert.Equal(0, KernelSemaphoreCompatExports.KernelWaitSema(waitContext));
            Assert.Equal(1, proxy.DeliverCalls);
        }
        finally
        {
            GuestThreadExecution.Scheduler = previousScheduler;
            createContext[CpuRegister.Rdi] = handle;
            Assert.Equal(0, KernelSemaphoreCompatExports.KernelDeleteSema(createContext));
        }
    }

    [Fact]
    public void SyncOnAddressHostWaitDeliversPendingExceptionBeforeReturning()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        WriteUInt32(memory, ValueAddress, 7);
        WriteUInt32(memory, TimeoutAddress, 1_000_000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = ValueAddress;
        context[CpuRegister.Rsi] = 7;
        context[CpuRegister.Rdx] = TimeoutAddress;
        var scheduler = DispatchProxy.Create<IGuestThreadScheduler, PendingExceptionScheduler>();
        var proxy = (PendingExceptionScheduler)(object)scheduler;
        proxy.DeliverException = deliveredContext =>
        {
            Assert.Same(context, deliveredContext);
            WriteUInt32(memory, ValueAddress, 8);
        };

        var previousScheduler = GuestThreadExecution.Scheduler;
        try
        {
            GuestThreadExecution.Scheduler = scheduler;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelSyncOnAddressCompatExports.SyncOnAddressWait32(context));
            Assert.Equal(1, proxy.DeliverCalls);
        }
        finally
        {
            GuestThreadExecution.Scheduler = previousScheduler;
        }
    }

    private static void WriteUInt32(FakeCpuMemory memory, ulong address, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        Assert.True(memory.TryWrite(address, bytes));
    }

    public class PendingExceptionScheduler : DispatchProxy
    {
        private int _deliverCalls;

        public Action<CpuContext>? DeliverException { get; set; }

        public int DeliverCalls => Volatile.Read(ref _deliverCalls);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IGuestThreadScheduler.DeliverPendingGuestExceptionIfReady))
            {
                Interlocked.Increment(ref _deliverCalls);
                DeliverException!((CpuContext)args![0]!);
                return null;
            }

            if (targetMethod?.Name == nameof(IGuestThreadScheduler.WakeBlockedThreads))
            {
                return 0;
            }

            throw new NotSupportedException(targetMethod?.Name);
        }
    }
}
