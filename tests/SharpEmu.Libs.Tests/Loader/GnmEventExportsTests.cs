// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.GnmMode;
using SharpEmu.GnmMode.Libraries.Gnm;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.Tests.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Loader;

[Collection(GraphicsEventQueueStateCollection.Name)]
public sealed class GnmEventExportsTests
{
    private const ulong Base = 0x1_0000_0000;
    private const ulong Events = Base + 0x100;
    private const ulong Count = Base + 0x300;
    private const ulong Timeout = Base + 0x400;
    private const ulong Result = Base + 0x500;
    private const ulong Ring = Base + 0x1000;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GraphicsCompletionDeliversThePs4EventAfterImmediateOrParkedWait(bool parked)
    {
        var (context, handle) = CreateQueue();
        try
        {
            Add(context, handle, 0x40, 0x12345678);
            IGuestThreadBlockWaiter? waiter = parked ? Park(context, handle) : null;
            Assert.False(waiter?.TryWake() ?? false);
            new EndOfPipeEvents().TriggerInterrupt(0, 0x98765432);
            var userEvent = new KernelEventQueueCompatExports.KernelQueuedEvent(
                0, KernelEventQueueCompatExports.KernelEventFilterUser, 0x20, 7, 0xDEADBEEF, 0xAABBCCDD);
            Assert.True(KernelEventQueueCompatExports.EnqueueEvent(handle, userEvent));
            if (waiter is not null)
            {
                Assert.True(waiter.TryWake());
                Assert.Equal(0, waiter.Resume());
            }
            else
                Assert.Equal(0, Poll(context, handle));
            Assert.Equal(2u, Read32(context, Count));
            AssertEvent(context, Events, 0x40, 0x12345678);
            Assert.Equal(0UL, Read64(context, Events + 0x20));
            Assert.Equal(userEvent.Data, Read64(context, Events + 0x30));
            Assert.Equal(userEvent.UserData, Read64(context, Events + 0x38));
        }
        finally
        {
            DeleteQueue(context, handle);
        }
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(3u)]
    [InlineData(4u)]
    [InlineData(5u)]
    [InlineData(6u)]
    public void ComputeReleaseSignalsItsPhysicalPipeRegardlessOfMappingOrder(uint pipe)
    {
        var (context, handle) = CreateQueue();
        try
        {
            Add(context, handle, pipe, 0xCAFEBABE);
            var first = Map(context, pipe, 7, Ring, Base + 0x600);
            var unrelated = Map(context, (pipe + 1) % 7, 0, Ring + 0x1000, Base + 0x604);
            var second = Map(context, pipe, 2, Ring + 0x2000, Base + 0x608);
            Assert.Equal(1u, first);
            Assert.Equal(2u, unrelated);
            Assert.Equal(3u, second);
            Release(context, unrelated, Ring + 0x1000);
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT, Poll(context, handle));
            Assert.Equal(0u, Read32(context, Count));
            Release(context, first, Ring);
            Release(context, second, Ring + 0x2000);
            Assert.Equal(0, Poll(context, handle));
            Assert.Equal(2u, Read32(context, Count));
            AssertEvent(context, Events, pipe, 0xCAFEBABE);
            AssertEvent(context, Events + 0x20, pipe, 0xCAFEBABE);
            Assert.Equal(789u, Read32(context, Result));
        }
        finally
        {
            GnmDriverExports.ResetComputeMappings(context.Memory);
            DeleteQueue(context, handle);
        }
    }

    [Fact]
    public void DeleteComputeEventRemovesEveryQueueSubscriptionAndPendingCompletion()
    {
        var (context, handle) = CreateQueue();
        try
        {
            Add(context, handle, 3, 0x1111);
            var sink = new EndOfPipeEvents();
            sink.TriggerInterrupt(0x20 + 3 * 8 + 7, 0);
            context[CpuRegister.Rdi] = handle;
            context[CpuRegister.Rsi] = 3;
            Assert.Equal(0, GnmDriverExports.DeleteEqEvent(context));
            for (var queue = 0; queue < 8; queue++)
                sink.TriggerInterrupt(0x20 + 3 * 8 + queue, 0);
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT, Poll(context, handle));
            Add(context, handle, 3, 0x2222);
            sink.TriggerInterrupt(0x20 + 3 * 8 + 4, 0x9999);
            Assert.Equal(0, Poll(context, handle));
            Assert.Equal(1u, Read32(context, Count));
            AssertEvent(context, Events, 3, 0x2222);
        }
        finally
        {
            DeleteQueue(context, handle);
        }
    }

    [Fact]
    public void ParkedWaitKeepsQueueDeletionAndTimeoutResults()
    {
        var (context, handle) = CreateQueue();
        Add(context, handle, 0x40, 0);
        var waiter = Park(context, handle);
        Assert.False(waiter.TryWake());
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT, waiter.Resume());
        waiter = Park(context, handle);
        DeleteQueue(context, handle);
        Assert.True(waiter.TryWake());
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_DELETED, waiter.Resume());
        Assert.Equal(0u, Read32(context, Count));
    }

    [Fact]
    public void EventExportsAreGnmOnlyAndKeepTheSharedWaitExport()
    {
        var gnm = GnmExportRegistry.CreateExports();
        foreach (var nid in new[] { "b0xyllnVY-I", "PVT+fuoS9gU", "UoYY0DWMC0U" })
        {
            Assert.Equal(Generation.Gen4, Assert.Single(gnm, export => export.Nid == nid).Target);
            Assert.DoesNotContain(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5), export => export.Nid == nid);
        }
        Assert.Equal(Generation.Gen4, Assert.Single(gnm, export => export.Nid == "fzyMKs9kim0").Target);
        Assert.Single(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5), export => export.Nid == "fzyMKs9kim0");
        var context = new CpuContext(new FakeCpuMemory(Base, 0x10000), Generation.Gen4);
        context[CpuRegister.Rdi] = ulong.MaxValue;
        context[CpuRegister.Rsi] = 0x40;
        Assert.Equal(unchecked((int)0x80020009), GnmDriverExports.AddEqEvent(context));
        Assert.Equal(unchecked((int)0x80020009), GnmDriverExports.DeleteEqEvent(context));
        Assert.Equal(unchecked((int)0x8002000E), GnmDriverExports.GetEqEventType(context));
        Assert.True(context.TryWriteUInt64(Events, 3));
        Assert.True(context.TryWriteUInt64(Events + 0x10, 0x40));
        context[CpuRegister.Rdi] = Events;
        Assert.Equal(0x40, GnmDriverExports.GetEqEventType(context));
    }

    private static (CpuContext Context, ulong Handle) CreateQueue()
    {
        var context = new CpuContext(new FakeCpuMemory(Base, 0x10000), Generation.Gen4);
        context[CpuRegister.Rdi] = Base;
        Assert.Equal(0, KernelEventQueueCompatExports.KernelCreateEqueue(context));
        return (context, Read64(context, Base));
    }

    private static void Add(CpuContext context, ulong handle, ulong id, ulong userData)
    {
        context[CpuRegister.Rdi] = handle;
        context[CpuRegister.Rsi] = id;
        context[CpuRegister.Rdx] = userData;
        Assert.Equal(0, GnmDriverExports.AddEqEvent(context));
    }

    private static void WaitArguments(CpuContext context, ulong handle, ulong timeout)
    {
        context[CpuRegister.Rdi] = handle;
        context[CpuRegister.Rsi] = Events;
        context[CpuRegister.Rdx] = 8;
        context[CpuRegister.Rcx] = Count;
        context[CpuRegister.R8] = timeout;
    }

    private static int Poll(CpuContext context, ulong handle)
    {
        Assert.True(context.TryWriteUInt32(Timeout, 0));
        WaitArguments(context, handle, Timeout);
        return GnmDriverExports.WaitEqueue(context);
    }

    private static IGuestThreadBlockWaiter Park(CpuContext context, ulong handle)
    {
        var previousThread = GuestThreadExecution.EnterGuestThread(0xC043);
        var previousFrame = GuestThreadExecution.EnterImportCallFrame(0x12345678, Base + 0x900, Base + 0x8F8);
        try
        {
            WaitArguments(context, handle, 0);
            Assert.Equal(0, GnmDriverExports.WaitEqueue(context));
            Assert.True(GuestThreadExecution.TryConsumeCurrentThreadBlock(out var reason, out _, out var continuation,
                out var wakeKey, out var waiter, out var deadline));
            Assert.Equal("sceKernelWaitEqueue", reason);
            Assert.True(continuation);
            Assert.StartsWith("sceKernelWaitEqueue:", wakeKey);
            Assert.Equal(0, deadline);
            return Assert.IsAssignableFrom<IGuestThreadBlockWaiter>(waiter);
        }
        finally
        {
            GuestThreadExecution.RestoreImportCallFrame(previousFrame);
            GuestThreadExecution.RestoreGuestThread(previousThread);
        }
    }

    private static uint Map(CpuContext context, uint pipe, uint queue, ulong ring, ulong readPointer)
    {
        context[CpuRegister.Rdi] = pipe;
        context[CpuRegister.Rsi] = queue;
        context[CpuRegister.Rdx] = ring;
        context[CpuRegister.Rcx] = 256;
        context[CpuRegister.R8] = readPointer;
        Assert.True(GnmDriverExports.MapComputeQueue(context) > 0);
        return (uint)context[CpuRegister.Rax];
    }

    private static void Release(CpuContext context, uint id, ulong ring)
    {
        uint[] commands = [PacketHeader.Make(7, PacketOpcode.ReleaseMemory) | 2,
            0x528, (1u << 29) | (2u << 24), unchecked((uint)Result), (uint)(Result >> 32), 789, 0];
        Assert.True(context.Memory.TryWrite(ring, System.Runtime.InteropServices.MemoryMarshal.AsBytes(commands.AsSpan())));
        context[CpuRegister.Rdi] = id;
        context[CpuRegister.Rsi] = 7;
        Assert.Equal(0, GnmDriverExports.DingDong(context));
    }

    private static void AssertEvent(CpuContext context, ulong address, ulong id, ulong userData)
    {
        Assert.Equal(id, Read64(context, address));
        Assert.Equal(id, Read64(context, address + 0x10));
        Assert.Equal(userData, Read64(context, address + 0x18));
        Span<byte> attributes = stackalloc byte[8];
        Assert.True(context.Memory.TryRead(address + 8, attributes));
        Assert.Equal(KernelEventQueueCompatExports.KernelEventFilterGraphics, BinaryPrimitives.ReadInt16LittleEndian(attributes));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(attributes[2..]));
        context[CpuRegister.Rdi] = address;
        Assert.Equal((int)id, GnmDriverExports.GetEqEventType(context));
        Assert.Equal(id, context[CpuRegister.Rax]);
    }

    private static uint Read32(CpuContext context, ulong address)
    {
        Assert.True(context.TryReadUInt32(address, out var value));
        return value;
    }

    private static ulong Read64(CpuContext context, ulong address)
    {
        Assert.True(context.TryReadUInt64(address, out var value));
        return value;
    }

    private static void DeleteQueue(CpuContext context, ulong handle)
    {
        context[CpuRegister.Rdi] = handle;
        Assert.Equal(0, KernelEventQueueCompatExports.KernelDeleteEqueue(context));
    }
}
