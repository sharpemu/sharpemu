// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Ampr;
using Xunit;

namespace SharpEmu.Libs.Tests.Ampr;

// The APR counter commands, after the AnyPS5 semantics: a write stores into or updates a field of one of 256
// 32-bit counters, and a wait compares a field with a reference under a mask.
[Collection("AmprFileRegistry")]
public sealed class AmprCounterTests
{
    private const ulong MemoryBase = 0x2_0000_0000;
    private const ulong DefaultCommandBuffer = MemoryBase + 0x100;
    private const ulong Stack = MemoryBase + 0xF00;
    private const int Einval = (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;

    private enum Op : byte { Store, Or, AndComplement, Xor, Add }

    private sealed class Fixture
    {
        private readonly ulong CommandBuffer;

        public Fixture(ulong commandBuffer = DefaultCommandBuffer)
        {
            CommandBuffer = commandBuffer;
            var records = commandBuffer + 0x100;
            AmprExports.ResetCounters();
            Context = new CpuContext(new FakeCpuMemory(MemoryBase, 0x1000), Generation.Gen5);
            Context[CpuRegister.Rdi] = CommandBuffer;
            Context[CpuRegister.Rsi] = records;
            Context[CpuRegister.Rdx] = 0x200;
            Assert.Equal(0, AmprExports.CommandBufferConstructor(Context));
            Assert.Equal(0, AmprExports.CommandBufferSetBuffer(Context));
        }

        public CpuContext Context { get; }

        public int Write(byte counter, byte access, ulong value, Op operation)
        {
            Context[CpuRegister.Rdi] = CommandBuffer;
            Context[CpuRegister.Rsi] = counter;
            Context[CpuRegister.Rdx] = access;
            Context[CpuRegister.Rcx] = value;
            Context[CpuRegister.R8] = (byte)operation;
            Context[CpuRegister.R9] = 1;
            return AmprExports.CommandBufferWriteCounter0400(Context);
        }

        public int Wait(byte counter, byte access, ulong reference, byte compare, byte maskOperation, ulong mask, byte flush = 0)
        {
            Context[CpuRegister.Rdi] = CommandBuffer;
            Context[CpuRegister.Rsi] = counter;
            Context[CpuRegister.Rdx] = access;
            Context[CpuRegister.Rcx] = reference;
            Context[CpuRegister.R8] = compare;
            Context[CpuRegister.R9] = maskOperation;
            // The seventh and eighth arguments sit above the return address.
            Context[CpuRegister.Rsp] = Stack;
            Assert.True(Context.TryWriteUInt64(Stack + 8, mask));
            Assert.True(Context.TryWriteUInt64(Stack + 16, flush));
            return AmprExports.CommandBufferWaitOnCounter0400(Context);
        }

        public int Complete() => AmprExports.CompleteCommandBuffer(Context, CommandBuffer);
    }

    [Fact]
    public void WriteCounterStoresAndUpdatesAFullCounter()
    {
        var f = new Fixture();
        Assert.Equal(0, f.Write(5, 1, 0x1234_5678, Op.Store));
        Assert.Equal(0, f.Write(5, 1, 0x0000_00FF, Op.Or));
        Assert.Equal(0, f.Write(5, 1, 0x0000_0010, Op.AndComplement));
        Assert.Equal(0, f.Write(5, 1, 0x0000_0001, Op.Xor));
        Assert.Equal(0, f.Write(5, 1, 0x10, Op.Add));
        Assert.Equal(0, f.Complete());

        // 0x12345678 | 0xFF = 0x123456FF, & ~0x10 = 0x123456EF, ^ 1 = 0x123456EE, + 0x10 = 0x123456FE.
        Assert.Equal(0x1234_56FEUL, AmprExports.ReadCounter(5, 1));
    }

    [Fact]
    public void WriteCounterRecordsTheCommandAndRunsItOnComplete()
    {
        var f = new Fixture();
        Assert.Equal(0, f.Write(9, 1, 0x77, Op.Store));

        // The command is recorded, not run, until the buffer completes.
        Assert.Equal(0UL, AmprExports.ReadCounter(9, 1));
        Assert.True(f.Context.TryReadUInt32(DefaultCommandBuffer + 4, out var offset));
        Assert.True(f.Context.TryReadUInt32(DefaultCommandBuffer + 8, out var count));
        Assert.Equal(0x8U, offset);
        Assert.Equal(1U, count);

        Assert.Equal(0, f.Complete());
        Assert.Equal(0x77UL, AmprExports.ReadCounter(9, 1));
    }

    [Fact]
    public void WriteCounterHonoursTheAccessWidthAndOffset()
    {
        var f = new Fixture();
        Assert.Equal(0, f.Write(3, 1, 0xFFFF_FFFF, Op.Store));
        Assert.Equal(0, f.Write(3, 3, 0xABCD, Op.Store)); // 16-bit field at offset 1 (bits 16..31)
        Assert.Equal(0, f.Write(3, 4, 0x1_11, Op.Store)); // 8-bit field at byte 0, masked to 0x11
        Assert.Equal(0, f.Write(3, 7, 0x42, Op.Store));   // 8-bit field at byte 3
        Assert.Equal(0, f.Complete());

        Assert.Equal(0x42_CD_FF_11UL, AmprExports.ReadCounter(3, 1));
        Assert.Equal(0x42CDUL, AmprExports.ReadCounter(3, 3));
        Assert.Equal(0x11UL, AmprExports.ReadCounter(3, 4));
    }

    [Fact]
    public void WriteCounterWithEightByteAccessSpansTwoSlots()
    {
        var f = new Fixture();
        Assert.Equal(0, f.Write(10, 0, 0x1122_3344_5566_7788, Op.Store));
        Assert.Equal(0, f.Write(10, 0, 1, Op.Add));
        Assert.Equal(0, f.Complete());

        Assert.Equal(0x1122_3344_5566_7789UL, AmprExports.ReadCounter(10, 0));
        Assert.Equal(0x5566_7789UL, AmprExports.ReadCounter(10, 1));
        Assert.Equal(0x1122_3344UL, AmprExports.ReadCounter(11, 1));
    }

    [Fact]
    public void WaitOnCounterIsSatisfiedWhenTheCounterMatches()
    {
        var f = new Fixture();
        Assert.Equal(0, f.Write(7, 1, 100, Op.Store));
        Assert.Equal(0, f.Wait(7, 1, 100, compare: 0, maskOperation: 0, mask: 0));
        Assert.Equal(0, f.Wait(7, 1, 99, compare: 1, maskOperation: 0, mask: 0));
        Assert.Equal(0, f.Wait(7, 1, 101, compare: 2, maskOperation: 0, mask: 0));
        Assert.Equal(0, f.Wait(7, 1, 0x1_0000_0000UL | 100, compare: 0, maskOperation: 1, mask: 0xFFFF_FFFF));
        Assert.True(f.Context.TryReadUInt32(DefaultCommandBuffer + 8, out var count));
        Assert.Equal(5U, count);
        Assert.Equal(0, f.Complete());
    }

    [Fact]
    public void WaitOnCounterStaysBlockedUntilAnotherBufferWritesTheCounter()
    {
        var f = new Fixture();
        Assert.Equal(0, f.Wait(12, 1, 3, compare: 0, maskOperation: 0, mask: 0));
        var waiting = Task.Run(f.Complete);

        Assert.False(waiting.Wait(150), "the wait completed before the counter was written");

        // A second buffer on its own context writes the counter.
        var writer = new Fixture(MemoryBase + 0x500);
        Assert.Equal(0, writer.Write(12, 1, 3, Op.Store));
        Assert.Equal(0, writer.Complete());
        Assert.True(waiting.Wait(TimeSpan.FromSeconds(10)), "the wait did not complete after the counter was written");
        Assert.Equal(0, waiting.Result);
    }

    [Fact]
    public void WaitOnCounterComparesOnlyTheAccessedField()
    {
        var f = new Fixture();
        // Counter 20 holds 0x0000AA00; the second byte field reads 0xAA.
        Assert.Equal(0, f.Write(20, 1, 0xAA00, Op.Store));
        Assert.Equal(0, f.Wait(20, 5, 0xAA, compare: 0, maskOperation: 0, mask: 0));
        Assert.Equal(0, f.Wait(20, 4, 0, compare: 0, maskOperation: 0, mask: 0));
        Assert.Equal(0, f.Complete());
    }

    [Fact]
    public void CounterCommandsRejectInvalidArguments()
    {
        var f = new Fixture();
        Assert.Equal(Einval, f.Write(0x80, 1, 0, Op.Store));
        Assert.Equal(Einval, f.Write(1, 8, 0, Op.Store));
        Assert.Equal(Einval, f.Write(1, 1, 0, (Op)5));
        Assert.Equal(Einval, f.Wait(1, 8, 0, 0, 0, 0));
        Assert.Equal(Einval, f.Wait(1, 1, 0, 7, 0, 0));
        Assert.Equal(Einval, f.Wait(1, 1, 0, 0, 2, 0));
        Assert.Equal(Einval, f.Wait(1, 1, 0, 0, 0, 0, flush: 2));
        Assert.True(f.Context.TryReadUInt32(DefaultCommandBuffer + 8, out var count));
        Assert.Equal(0U, count);
    }

    [Fact]
    public void MeasureCommandSizeMatchesTheRecords()
    {
        var f = new Fixture();
        var ctx = f.Context;
        ctx[CpuRegister.Rdi] = 1;
        ctx[CpuRegister.Rsi] = 1;
        ctx[CpuRegister.Rdx] = 0;
        ctx[CpuRegister.Rcx] = 0;
        Assert.Equal(0, AmprExports.MeasureCommandSizeWriteCounter0400(ctx));
        Assert.Equal(0x8UL, ctx[CpuRegister.Rax]);
        ctx[CpuRegister.Rsi] = 0;
        Assert.Equal(0, AmprExports.MeasureCommandSizeWriteCounter0400(ctx));
        Assert.Equal(0xCUL, ctx[CpuRegister.Rax]);

        ctx[CpuRegister.Rdi] = 1;
        ctx[CpuRegister.Rsi] = 1;
        ctx[CpuRegister.Rdx] = 0;
        ctx[CpuRegister.Rcx] = 0;
        ctx[CpuRegister.R8] = 0;
        ctx[CpuRegister.R9] = 0;
        ctx[CpuRegister.Rsp] = Stack;
        Assert.True(ctx.TryWriteUInt64(Stack + 8, 0));
        Assert.Equal(0, AmprExports.MeasureCommandSizeWaitOnCounter0400(ctx));
        Assert.Equal(0x8UL, ctx[CpuRegister.Rax]);
        ctx[CpuRegister.Rsi] = 0;
        ctx[CpuRegister.R8] = 1;
        Assert.Equal(0, AmprExports.MeasureCommandSizeWaitOnCounter0400(ctx));
        Assert.Equal(0x14UL, ctx[CpuRegister.Rax]);
        ctx[CpuRegister.Rsi] = 1;
        ctx[CpuRegister.R8] = 0;

        ctx[CpuRegister.Rcx] = 7;
        Assert.Equal(Einval, AmprExports.MeasureCommandSizeWaitOnCounter0400(ctx));
        ctx[CpuRegister.Rdi] = 0x80;
        ctx[CpuRegister.Rcx] = 0;
        Assert.Equal(Einval, AmprExports.MeasureCommandSizeWriteCounter0400(ctx));
    }
}
