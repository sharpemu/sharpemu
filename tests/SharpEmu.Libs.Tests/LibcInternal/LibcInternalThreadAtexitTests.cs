// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.LibcInternal;

using Xunit;

namespace SharpEmu.Libs.Tests.LibcInternal;

// The scheduler is process-wide state.
[Collection("GuestThreadFlowProfile")]
public sealed class LibcInternalThreadAtexitTests
{
    private const ulong Base = 0x3_0000_0000;

    // The guest's libc registers a destructor for each thread_local object. The exiting thread runs them newest
    // first, and a destructor registered while they run runs in the same pass.
    [Fact]
    public void ThreadAtexit_RunsDestructorsNewestFirstWhenTheThreadExits()
    {
        var context = new CpuContext(new FakeCpuMemory(Base, 0x1000), Generation.Gen5);
        var scheduler = new RecordingScheduler();
        var previousScheduler = GuestThreadExecution.Scheduler;
        try
        {
            GuestThreadExecution.Scheduler = scheduler;
            Register(context, 0x1000, 0xA);
            Register(context, 0x2000, 0xB);
            scheduler.OnCall = entryPoint =>
            {
                if (entryPoint == 0x2000)
                {
                    Register(context, 0x3000, 0xC);
                }
            };

            LibcInternalExports.RunThreadAtexitDestructors(context);
            Assert.Equal([(0x2000UL, 0xBUL), (0x3000UL, 0xCUL), (0x1000UL, 0xAUL)], scheduler.Calls);

            scheduler.Calls.Clear();
            LibcInternalExports.RunThreadAtexitDestructors(context);
            Assert.Empty(scheduler.Calls);
        }
        finally
        {
            GuestThreadExecution.Scheduler = previousScheduler;
        }
    }

    private static void Register(CpuContext context, ulong destructor, ulong obj)
    {
        context[CpuRegister.Rdi] = destructor;
        context[CpuRegister.Rsi] = obj;
        context[CpuRegister.Rdx] = Base;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, LibcInternalExports.LibcInternalThreadAtexit(context));
        Assert.Equal(0UL, context[CpuRegister.Rax]);
    }

    private sealed class RecordingScheduler : IGuestThreadScheduler
    {
        public List<(ulong EntryPoint, ulong Argument)> Calls { get; } = [];
        public Action<ulong>? OnCall { get; set; }
        public bool SupportsGuestContextTransfer => false;

        public bool TryCallGuestFunction(CpuContext callerContext, ulong entryPoint, ulong firstArgument, ulong secondArgument,
            ulong stackAddress, ulong stackSize, string reason, out string? error)
        {
            Calls.Add((entryPoint, firstArgument));
            OnCall?.Invoke(entryPoint);
            error = null;
            return true;
        }

        public void RegisterGuestThreadContext(ulong threadHandle, CpuContext context) => throw new NotSupportedException();
        public bool TryStartThread(CpuContext creatorContext, GuestThreadStartRequest request, out string? error) => throw new NotSupportedException();
        public bool TryJoinThread(CpuContext callerContext, ulong threadHandle, out ulong returnValue, out string? error) => throw new NotSupportedException();
        public void Pump(CpuContext callerContext, string reason) => throw new NotSupportedException();
        public int WakeBlockedThreads(string wakeKey, int maxCount = int.MaxValue) => throw new NotSupportedException();
        public bool HasPendingGuestExceptionForCurrentThread() => false;
        public bool TrySetGuestThreadPriority(ulong guestThreadHandle, int guestPriority) => throw new NotSupportedException();
        public bool TrySetGuestThreadAffinity(ulong guestThreadHandle, ulong affinityMask) => throw new NotSupportedException();
        public IReadOnlyList<GuestThreadSnapshot> SnapshotThreads() => throw new NotSupportedException();
        public bool TryCallGuestFunction(CpuContext callerContext, ulong entryPoint, ulong firstArgument, ulong secondArgument,
            ulong thirdArgument, ulong stackAddress, ulong stackSize, string reason, out ulong returnValue, out string? error) =>
            throw new NotSupportedException();
        public bool TryCallGuestFunction(CpuContext callerContext, ulong entryPoint, ulong firstArgument, ulong secondArgument,
            ulong thirdArgument, ulong fourthArgument, ulong stackAddress, ulong stackSize, string reason,
            out ulong returnValue, out string? error) => throw new NotSupportedException();
        public bool TryCallGuestContinuation(CpuContext callerContext, GuestCpuContinuation continuation, string reason,
            out string? error) => throw new NotSupportedException();
        public bool TryRaiseGuestException(CpuContext callerContext, ulong threadHandle, ulong handler, int exceptionType,
            out string? error) => throw new NotSupportedException();
    }
}
