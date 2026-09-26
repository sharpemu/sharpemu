// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Buffers.Binary;
using System.Collections.Generic;

using SharpEmu.HLE;
using SharpEmu.Libs.Fiber;

using Xunit;

namespace SharpEmu.Libs.Tests.Fiber;

/// <summary>
/// Continuation contract for a job-system style fiber loop: two fibers that
/// hand control back and forth many times from the same resume point. Each
/// resume has to restore exactly the state its own suspension captured, so a
/// stale or cross-fiber restore shows up here instead of as a null dereference
/// inside guest code several switches later.
/// </summary>
[Collection(FiberStateCollection.Name)]
public sealed class FiberSwitchLoopTests : IDisposable
{
    private const ulong Base = 0x3_0000_0000UL;
    private const int RegionSize = 0x8000;

    private const ulong FiberA = Base + 0x0000;
    private const ulong FiberB = Base + 0x0100;
    private const ulong NameA = Base + 0x0300;
    private const ulong NameB = Base + 0x0340;
    private const ulong ArgSlotRoot = Base + 0x0380;
    private const ulong ArgSlotA = Base + 0x0388;
    private const ulong ArgSlotB = Base + 0x0390;
    private const ulong ContextA = Base + 0x1000;
    private const ulong ContextB = Base + 0x3000;
    private const ulong ContextSize = 0x1000;

    private const ulong EntryA = 0x4_0000_1000UL;
    private const ulong EntryB = 0x4_0000_2000UL;
    private const ulong ArgOnInitializeA = 0xA11AUL;
    private const ulong ArgOnInitializeB = 0xB22BUL;

    // The guest side of a job worker calls sceFiberSwitch from one place, so
    // every suspension of a given fiber reports the same return address and the
    // same resume stack. Only the payload registers differ between rounds.
    private const ulong ResumeRipA = 0x8_0084_1FD1UL;
    private const ulong ResumeRipB = 0x8_0084_1FD1UL;
    private const ulong ResumeRspA = 0x2_0858_FF10UL;
    private const ulong ResumeRspB = 0x2_085A_F3A0UL;
    private const ulong ReturnSlotA = ResumeRspA - 0x10UL;
    private const ulong ReturnSlotB = ResumeRspB - 0x10UL;

    private const ulong RootReturnRip = 0x8_0084_0000UL;
    private const ulong RootResumeRsp = 0x2_0850_0000UL;
    private const ulong RootReturnSlot = RootResumeRsp - 0x10UL;

    private const ulong ThreadHandle = 0x2E45_16AD_090UL;

    private const uint StateRun = 1;
    private const uint StateIdle = 2;

    private readonly IGuestThreadScheduler? _previousScheduler;
    private readonly ulong _previousThread;
    private readonly ulong _previousFiber;

    public FiberSwitchLoopTests()
    {
        FiberExports.ResetRuntimeState();
        _previousScheduler = GuestThreadExecution.Scheduler;
        GuestThreadExecution.Scheduler = new ContextTransferScheduler();
        _previousThread = GuestThreadExecution.EnterGuestThread(ThreadHandle);
        _previousFiber = GuestThreadExecution.EnterFiber(0);
    }

    public void Dispose()
    {
        GuestThreadExecution.EnterFiber(_previousFiber);
        GuestThreadExecution.RestoreGuestThread(_previousThread);
        GuestThreadExecution.Scheduler = _previousScheduler;
        FiberExports.ResetRuntimeState();
    }

    [Fact]
    public void RepeatedSwitch_RestoresEachFiberOwnResumePoint()
    {
        var memory = new FakeCpuMemory(Base, RegionSize);
        var context = new CpuContext(memory, Generation.Gen5);

        InitializeFiber(memory, context, FiberA, NameA, EntryA, ArgOnInitializeA, ContextA);
        InitializeFiber(memory, context, FiberB, NameB, EntryB, ArgOnInitializeB, ContextB);

        // The thread enters the fiber world through sceFiberRun.
        GuestThreadExecution.EnterImportCallFrame(RootReturnRip, RootResumeRsp, RootReturnSlot);
        context[CpuRegister.Rdi] = FiberA;
        context[CpuRegister.Rsi] = 0x1000UL;
        context[CpuRegister.Rdx] = ArgSlotRoot;
        Assert.Equal(0, FiberExports.FiberRun(context));

        Assert.True(GuestThreadExecution.TryConsumeCurrentContextTransfer(out var toA));
        Assert.Equal(EntryA, toA.Rip);
        Assert.Equal(ArgOnInitializeA, toA.Rdi);
        Assert.Equal(0x1000UL, toA.Rsi);
        Assert.Equal(0UL, toA.Rax);
        Assert.Equal(FiberA, GuestThreadExecution.CurrentFiberAddress);
        Assert.Equal(StateRun, ReadUInt32(memory, FiberA + 4));

        // Round 0 suspends A for the first time and starts B at its entry point.
        var sentinelsA = Sentinels(0xA0);
        Switch(memory, context, from: FiberA, to: FiberB, ResumeRipA, ResumeRspA, ReturnSlotA,
            ArgSlotA, argOnRun: 0xB000UL, sentinelsA);

        Assert.True(GuestThreadExecution.TryConsumeCurrentContextTransfer(out var toB));
        Assert.Equal(EntryB, toB.Rip);
        Assert.Equal(ArgOnInitializeB, toB.Rdi);
        Assert.Equal(0xB000UL, toB.Rsi);

        var expectedA = new FiberExpectation(ResumeRipA, ResumeRspA, ReturnSlotA, sentinelsA);
        FiberExpectation? expectedB = null;

        // Eight full round trips over the same pair of resume points.
        for (var round = 1; round <= 8; round++)
        {
            var sentinelsB = Sentinels((ulong)round << 32 | 0xB0);
            var argToA = 0xA000UL + (ulong)round;
            Switch(memory, context, from: FiberB, to: FiberA, ResumeRipB, ResumeRspB, ReturnSlotB,
                ArgSlotB, argToA, sentinelsB);
            expectedB = new FiberExpectation(ResumeRipB, ResumeRspB, ReturnSlotB, sentinelsB);

            Assert.True(GuestThreadExecution.TryConsumeCurrentContextTransfer(out var resumedA));
            AssertResume(resumedA, expectedA, $"round {round}: fiber A");
            Assert.Equal(argToA, ReadUInt64(memory, ArgSlotA));
            Assert.Equal(StateRun, ReadUInt32(memory, FiberA + 4));
            Assert.Equal(StateIdle, ReadUInt32(memory, FiberB + 4));

            var nextSentinelsA = Sentinels((ulong)round << 32 | 0xA0);
            var argToB = 0xB000UL + (ulong)round;
            Switch(memory, context, from: FiberA, to: FiberB, ResumeRipA, ResumeRspA, ReturnSlotA,
                ArgSlotA, argToB, nextSentinelsA);
            expectedA = new FiberExpectation(ResumeRipA, ResumeRspA, ReturnSlotA, nextSentinelsA);

            Assert.True(GuestThreadExecution.TryConsumeCurrentContextTransfer(out var resumedB));
            AssertResume(resumedB, expectedB, $"round {round}: fiber B");
            Assert.Equal(argToB, ReadUInt64(memory, ArgSlotB));
            Assert.Equal(StateRun, ReadUInt32(memory, FiberB + 4));
            Assert.Equal(StateIdle, ReadUInt32(memory, FiberA + 4));
        }
    }

    [Fact]
    public void RepeatedSwitch_KeepsResumeStackInsideTheOwningFiberContext()
    {
        var memory = new FakeCpuMemory(Base, RegionSize);
        var context = new CpuContext(memory, Generation.Gen5);

        InitializeFiber(memory, context, FiberA, NameA, EntryA, ArgOnInitializeA, ContextA);
        InitializeFiber(memory, context, FiberB, NameB, EntryB, ArgOnInitializeB, ContextB);

        GuestThreadExecution.EnterImportCallFrame(RootReturnRip, RootResumeRsp, RootReturnSlot);
        context[CpuRegister.Rdi] = FiberA;
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = ArgSlotRoot;
        Assert.Equal(0, FiberExports.FiberRun(context));
        Assert.True(GuestThreadExecution.TryConsumeCurrentContextTransfer(out var toA));

        // A fresh fiber must start on its own context block, not on the stack of
        // the thread that ran it.
        Assert.InRange(toA.Rsp, ContextA, ContextA + ContextSize);
        Assert.InRange(toA.ReturnSlotAddress, ContextA, ContextA + ContextSize);

        Switch(memory, context, from: FiberA, to: FiberB, ResumeRipA, ResumeRspA, ReturnSlotA,
            ArgSlotA, argOnRun: 0, Sentinels(0xA0));
        Assert.True(GuestThreadExecution.TryConsumeCurrentContextTransfer(out var toB));

        Assert.InRange(toB.Rsp, ContextB, ContextB + ContextSize);
        Assert.InRange(toB.ReturnSlotAddress, ContextB, ContextB + ContextSize);
        Assert.NotEqual(toA.Rsp, toB.Rsp);
    }

    private static void Switch(
        FakeCpuMemory memory,
        CpuContext context,
        ulong from,
        ulong to,
        ulong resumeRip,
        ulong resumeRsp,
        ulong returnSlot,
        ulong argOnReturnAddress,
        ulong argOnRun,
        IReadOnlyDictionary<CpuRegister, ulong> sentinels)
    {
        Assert.Equal(from, GuestThreadExecution.CurrentFiberAddress);

        // The guest reaches sceFiberSwitch through the import trampoline, which
        // is what publishes the resume point for the calling fiber.
        GuestThreadExecution.EnterImportCallFrame(resumeRip, resumeRsp, returnSlot);
        foreach (var (register, value) in sentinels)
        {
            context[register] = value;
        }

        context[CpuRegister.Rdi] = to;
        context[CpuRegister.Rsi] = argOnRun;
        context[CpuRegister.Rdx] = argOnReturnAddress;

        Assert.Equal(0, FiberExports.FiberSwitch(context));
        Assert.Equal(to, GuestThreadExecution.CurrentFiberAddress);
        _ = memory;
    }

    private static void AssertResume(GuestCpuContinuation actual, FiberExpectation? expected, string because)
    {
        Assert.NotNull(expected);
        Assert.Equal(expected.Rip, actual.Rip);
        Assert.Equal(expected.Rsp, actual.Rsp);
        Assert.Equal(expected.ReturnSlotAddress, actual.ReturnSlotAddress);
        // sceFiberSwitch returns SCE_OK to the fiber it resumes.
        Assert.Equal(0UL, actual.Rax);

        foreach (var (register, value) in expected.Sentinels)
        {
            var restored = register switch
            {
                CpuRegister.Rbx => actual.Rbx,
                CpuRegister.Rbp => actual.Rbp,
                CpuRegister.R12 => actual.R12,
                CpuRegister.R13 => actual.R13,
                CpuRegister.R14 => actual.R14,
                CpuRegister.R15 => actual.R15,
                _ => throw new InvalidOperationException($"Unexpected sentinel register {register}."),
            };

            Assert.True(value == restored, $"{because}: {register} was 0x{restored:X16}, expected 0x{value:X16}.");
        }
    }

    private static IReadOnlyDictionary<CpuRegister, ulong> Sentinels(ulong seed) =>
        new Dictionary<CpuRegister, ulong>
        {
            [CpuRegister.Rbx] = 0x1111_0000_0000_0000UL | seed,
            [CpuRegister.Rbp] = 0x2222_0000_0000_0000UL | seed,
            [CpuRegister.R12] = 0x3333_0000_0000_0000UL | seed,
            [CpuRegister.R13] = 0x4444_0000_0000_0000UL | seed,
            [CpuRegister.R14] = 0x5555_0000_0000_0000UL | seed,
            [CpuRegister.R15] = 0x6666_0000_0000_0000UL | seed,
        };

    private static void InitializeFiber(
        FakeCpuMemory memory,
        CpuContext context,
        ulong fiber,
        ulong nameAddress,
        ulong entry,
        ulong argOnInitialize,
        ulong contextAddress)
    {
        memory.WriteCString(nameAddress, $"F{fiber:X}");
        context[CpuRegister.Rdi] = fiber;
        context[CpuRegister.Rsi] = nameAddress;
        context[CpuRegister.Rdx] = entry;
        context[CpuRegister.Rcx] = argOnInitialize;
        context[CpuRegister.R8] = contextAddress;
        context[CpuRegister.R9] = ContextSize;
        Assert.Equal(0, FiberExports.FiberInitialize(context));
    }

    private static uint ReadUInt32(FakeCpuMemory memory, ulong address)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        Assert.True(memory.TryRead(address, buffer));
        return BinaryPrimitives.ReadUInt32LittleEndian(buffer);
    }

    private static ulong ReadUInt64(FakeCpuMemory memory, ulong address)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        Assert.True(memory.TryRead(address, buffer));
        return BinaryPrimitives.ReadUInt64LittleEndian(buffer);
    }

    private sealed record FiberExpectation(
        ulong Rip,
        ulong Rsp,
        ulong ReturnSlotAddress,
        IReadOnlyDictionary<CpuRegister, ulong> Sentinels);

    private sealed class ContextTransferScheduler : IGuestThreadScheduler
    {
        public bool SupportsGuestContextTransfer => true;

        public void RegisterGuestThreadContext(ulong threadHandle, CpuContext context) { }
        public bool TryStartThread(CpuContext creatorContext, GuestThreadStartRequest request, out string? error) => throw new NotSupportedException();
        public bool TryJoinThread(CpuContext callerContext, ulong threadHandle, out ulong returnValue, out string? error) => throw new NotSupportedException();
        public void Pump(CpuContext callerContext, string reason) { }
        public int WakeBlockedThreads(string wakeKey, int maxCount = int.MaxValue) => 0;
        public bool HasPendingGuestExceptionForCurrentThread() => false;
        public bool TrySetGuestThreadPriority(ulong guestThreadHandle, int guestPriority) => throw new NotSupportedException();
        public bool TrySetGuestThreadAffinity(ulong guestThreadHandle, ulong affinityMask) => throw new NotSupportedException();
        public IReadOnlyList<GuestThreadSnapshot> SnapshotThreads() => Array.Empty<GuestThreadSnapshot>();
        public bool TryCallGuestFunction(CpuContext callerContext, ulong entryPoint, ulong arg0, ulong arg1,
            ulong stackAddress, ulong stackSize, string reason, out string? error) => throw new NotSupportedException();
        public bool TryCallGuestFunction(CpuContext callerContext, ulong entryPoint, ulong arg0, ulong arg1,
            ulong arg2, ulong stackAddress, ulong stackSize, string reason, out ulong returnValue,
            out string? error) => throw new NotSupportedException();
        public bool TryCallGuestFunction(CpuContext callerContext, ulong entryPoint, ulong arg0, ulong arg1,
            ulong arg2, ulong arg3, ulong stackAddress, ulong stackSize, string reason, out ulong returnValue,
            out string? error) => throw new NotSupportedException();
        public bool TryCallGuestContinuation(CpuContext callerContext, GuestCpuContinuation continuation, string reason,
            out string? error) => throw new NotSupportedException();
        public bool TryRaiseGuestException(CpuContext callerContext, ulong threadHandle, ulong handler, int exceptionType,
            out string? error) => throw new NotSupportedException();
    }
}
