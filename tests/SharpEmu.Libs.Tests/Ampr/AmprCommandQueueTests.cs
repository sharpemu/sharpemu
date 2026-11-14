// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Ampr;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Ampr;

[Collection("AmprFileRegistry")]
public sealed class AmprCommandQueueTests
{
    private const ulong MemoryBase = 0x3_0000_0000;
    private const ulong Label = MemoryBase + 0x800;
    private const ulong Output = MemoryBase + 0x808;
    private const ulong SecondOutput = MemoryBase + 0x810;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void ABufferWhoseWaitsAreMetRunsBeforeTheSubmitReturns()
    {
        var context = CreateContext();
        Assert.True(context.TryWriteUInt64(Label, 5));
        var buffer = Build(context, 0, [Wait(Label, 5), Write(Output, 7)]);

        var submission = Submit(context, buffer, AmprExports.AmprEngine.Apr, 0x7101);

        Assert.True(submission.CompletedInline);
        Assert.Equal(7UL, Read(context, Output));
    }

    [Fact]
    public void ABufferThatReachesAnUnmetWaitFinishesOnItsQueue()
    {
        var context = CreateContext();
        var buffer = Build(context, 0, [Wait(Label, 1), Write(Output, 9)]);

        var submission = Submit(context, buffer, AmprExports.AmprEngine.Apr, 0x7102);

        Assert.False(submission.IsCompleted);
        Assert.Equal(0UL, Read(context, Output));
        Assert.True(context.TryWriteUInt64(Label, 1));
        Assert.True(submission.Wait(Timeout));
        Assert.False(submission.CompletedInline);
        Assert.Equal(9UL, Read(context, Output));
    }

    [Fact]
    public void ALaterBufferOfTheSameQueueRunsAfterTheOneThatWaits()
    {
        var context = CreateContext();
        var first = Build(context, 0, [Wait(Label, 1), Write(Output, 1)]);
        var second = Build(context, 1, [Write(Output, 2)]);

        var waiting = Submit(context, first, AmprExports.AmprEngine.Apr, 0x7103);
        var queued = Submit(context, second, AmprExports.AmprEngine.Apr, 0x7103);

        Assert.False(queued.IsCompleted);
        Assert.Equal(0UL, Read(context, Output));
        Assert.True(context.TryWriteUInt64(Label, 1));
        Assert.True(waiting.Wait(Timeout));
        Assert.True(queued.Wait(Timeout));
        Assert.Equal(2UL, Read(context, Output));
    }

    // Reads wait for the AMM work that maps their destination, so neither another engine nor another priority
    // queues behind them.
    [Fact]
    public void AWaitingBufferHoldsUpOnlyItsOwnQueue()
    {
        var context = CreateContext();
        var waiting = Submit(context, Build(context, 0, [Wait(Label, 1), Write(Output, 1)]), AmprExports.AmprEngine.Apr, 0x7104);

        var otherEngine = Submit(context, Build(context, 1, [Write(SecondOutput, 3)]), AmprExports.AmprEngine.Amm, 0x7104);
        Assert.True(otherEngine.CompletedInline);
        Assert.Equal(3UL, Read(context, SecondOutput));

        var otherPriority = Submit(context, Build(context, 2, [Write(SecondOutput, 4)]), AmprExports.AmprEngine.Apr, 0x7105);
        Assert.True(otherPriority.CompletedInline);
        Assert.Equal(4UL, Read(context, SecondOutput));

        Assert.False(waiting.IsCompleted);
        Assert.True(context.TryWriteUInt64(Label, 1));
        Assert.True(waiting.Wait(Timeout));
    }

    [Fact]
    public void WaitingForASubmissionReturnsItsResultOnceTheBufferFinishes()
    {
        const ulong resultAddress = MemoryBase + 0x900;
        const ulong submissionIdAddress = MemoryBase + 0x910;
        var context = CreateContext();
        var buffer = Build(context, 0, [Wait(Label, 1), Write(Output, 5)]);
        Assert.True(context.TryWriteUInt64(resultAddress, ulong.MaxValue));

        context[CpuRegister.Rdi] = buffer;
        context[CpuRegister.Rsi] = 0x7106;
        context[CpuRegister.Rdx] = resultAddress;
        context[CpuRegister.Rcx] = submissionIdAddress;
        Assert.Equal(0, KernelAprCompatExports.KernelAprSubmitCommandBufferAndGetResult(context));
        Assert.Equal(ulong.MaxValue, Read(context, resultAddress));
        Assert.True(context.TryReadUInt32(submissionIdAddress, out var submissionId));

        var release = Task.Run(async () =>
        {
            await Task.Delay(50);
            Assert.True(context.TryWriteUInt64(Label, 1));
        });
        var waiter = new CpuContext(context.Memory, Generation.Gen5);
        waiter[CpuRegister.Rdi] = submissionId;
        Assert.Equal(0, KernelAprCompatExports.KernelAprWaitCommandBuffer(waiter));
        release.Wait(Timeout);

        Assert.Equal(5UL, Read(context, Output));
        Assert.Equal(0UL, Read(context, resultAddress));
    }

    private static CpuContext CreateContext() =>
        new(new FakeCpuMemory(MemoryBase, 0x1000), Generation.Gen5);

    private static AmprExports.AmprSubmission Submit(CpuContext context, ulong buffer, AmprExports.AmprEngine engine, uint priority)
    {
        Assert.Equal(0, AmprExports.SubmitCommandBuffer(context, buffer, engine, priority, null, out var submission));
        return submission!;
    }

    private static Action<CpuContext, ulong> Wait(ulong address, ulong reference) => (context, buffer) =>
    {
        context[CpuRegister.Rdi] = buffer;
        context[CpuRegister.Rsi] = address;
        context[CpuRegister.Rdx] = reference;
        context[CpuRegister.Rcx] = 0;
        context[CpuRegister.R8] = 0;
        Assert.Equal(0, AmprExports.CommandBufferWaitOnAddress(context));
    };

    private static Action<CpuContext, ulong> Write(ulong address, ulong value) => (context, buffer) =>
    {
        context[CpuRegister.Rdi] = buffer;
        context[CpuRegister.Rsi] = address;
        context[CpuRegister.Rdx] = value;
        Assert.Equal(0, AmprExports.CommandBufferWriteAddress0400(context));
    };

    // Command buffer objects sit at 0x100 + 0x40 * slot, their records at 0x200 + 0x100 * slot.
    private static ulong Build(CpuContext context, uint slot, Action<CpuContext, ulong>[] commands)
    {
        var buffer = MemoryBase + 0x100 + 0x40UL * slot;
        context[CpuRegister.Rdi] = buffer;
        context[CpuRegister.Rsi] = MemoryBase + 0x200 + 0x100UL * slot;
        context[CpuRegister.Rdx] = 0x100;
        Assert.Equal(0, AmprExports.CommandBufferConstructor(context));
        Assert.Equal(0, AmprExports.CommandBufferSetBuffer(context));
        foreach (var command in commands)
        {
            command(context, buffer);
        }

        return buffer;
    }

    private static ulong Read(CpuContext context, ulong address)
    {
        Assert.True(context.TryReadUInt64(address, out var value));
        return value;
    }
}
