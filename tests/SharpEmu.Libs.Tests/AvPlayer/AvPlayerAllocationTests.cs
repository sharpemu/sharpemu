// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Memory;
using SharpEmu.HLE;
using SharpEmu.Libs.AvPlayer;
using SharpEmu.Libs.Tests.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.AvPlayer;

[Collection(KernelMemoryCompatStateCollection.Name)]
public sealed class AvPlayerAllocationTests : IDisposable
{
    private const ulong Handle = 0xA0_0000_1000;
    private readonly IGuestThreadScheduler? _previousScheduler = GuestThreadExecution.Scheduler;

    [Fact]
    public void FailedGuestAllocatorsFallBackToHleMemoryInTheSameAttempt()
    {
        using var memory = new PhysicalVirtualMemory();
        var context = new CpuContext(memory, Generation.Gen5);
        var scheduler = new FailingAllocatorScheduler();
        GuestThreadExecution.Scheduler = scheduler;
        AvPlayerExports.RegisterPlayerForTest(
            Handle,
            width: 16,
            height: 16,
            durationMilliseconds: 1,
            allocateTextureCallback: 0x1000,
            allocateCallback: 0x2000);

        Assert.True(AvPlayerExports.AllocateGuestVideoBuffersForTest(
            context,
            Handle,
            out var firstBuffer));
        Assert.NotEqual(0UL, firstBuffer);
        Assert.Equal(2, scheduler.CallCount);
    }

    public void Dispose()
    {
        AvPlayerExports.RemovePlayerForTest(Handle);
        GuestThreadExecution.Scheduler = _previousScheduler;
    }

    private sealed class FailingAllocatorScheduler : IGuestThreadScheduler
    {
        public int CallCount { get; private set; }

        public bool SupportsGuestContextTransfer => false;

        public void RegisterGuestThreadContext(ulong threadHandle, CpuContext context)
        {
        }

        public bool TryStartThread(
            CpuContext creatorContext,
            GuestThreadStartRequest request,
            out string? error)
        {
            error = "not supported";
            return false;
        }

        public bool TryJoinThread(
            CpuContext callerContext,
            ulong threadHandle,
            out ulong returnValue,
            out string? error)
        {
            returnValue = 0;
            error = "not supported";
            return false;
        }

        public void Pump(CpuContext callerContext, string reason)
        {
        }

        public int WakeBlockedThreads(string wakeKey, int maxCount = int.MaxValue) => 0;

        public bool HasPendingGuestExceptionForCurrentThread() => false;

        public bool TrySetGuestThreadPriority(ulong guestThreadHandle, int guestPriority) => false;

        public bool TrySetGuestThreadAffinity(ulong guestThreadHandle, ulong affinityMask) => false;

        public IReadOnlyList<GuestThreadSnapshot> SnapshotThreads() => [];

        public bool TryCallGuestFunction(
            CpuContext callerContext,
            ulong entryPoint,
            ulong arg0,
            ulong arg1,
            ulong stackAddress,
            ulong stackSize,
            string reason,
            out string? error)
        {
            error = "not supported";
            return false;
        }

        public bool TryCallGuestFunction(
            CpuContext callerContext,
            ulong entryPoint,
            ulong arg0,
            ulong arg1,
            ulong arg2,
            ulong stackAddress,
            ulong stackSize,
            string reason,
            out ulong returnValue,
            out string? error)
        {
            CallCount++;
            returnValue = 0;
            error = "allocator rejected the request";
            return false;
        }

        public bool TryCallGuestFunction(
            CpuContext callerContext,
            ulong entryPoint,
            ulong arg0,
            ulong arg1,
            ulong arg2,
            ulong arg3,
            ulong stackAddress,
            ulong stackSize,
            string reason,
            out ulong returnValue,
            out string? error) =>
            TryCallGuestFunction(
                callerContext,
                entryPoint,
                arg0,
                arg1,
                arg2,
                stackAddress,
                stackSize,
                reason,
                out returnValue,
                out error);

        public bool TryCallGuestContinuation(
            CpuContext callerContext,
            GuestCpuContinuation continuation,
            string reason,
            out string? error)
        {
            error = "not supported";
            return false;
        }

        public bool TryRaiseGuestException(
            CpuContext callerContext,
            ulong threadHandle,
            ulong handler,
            int exceptionType,
            out string? error)
        {
            error = "not supported";
            return false;
        }
    }
}
