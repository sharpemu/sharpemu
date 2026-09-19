// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

/// <summary>
/// GuestThreadExecution.Scheduler is process-wide, so every test that installs one shares this
/// collection and runs serially.
/// </summary>
[CollectionDefinition("GuestThreadSchedulerState", DisableParallelization = true)]
public sealed class GuestThreadSchedulerStateCollection
{
}

/// <summary>Installs a scheduler for the duration of a test and puts the previous one back.</summary>
internal sealed class SchedulerScope : IDisposable
{
    private readonly IGuestThreadScheduler? _previous;

    public SchedulerScope(IGuestThreadScheduler scheduler)
    {
        _previous = GuestThreadExecution.Scheduler;
        GuestThreadExecution.Scheduler = scheduler;
    }

    public void Dispose() => GuestThreadExecution.Scheduler = _previous;
}

/// <summary>
/// Stands in for the CPU backend on the one call a blocking HLE wait makes into the scheduler
/// while it is parked. Only the members such a wait actually reaches are implemented; anything
/// else throws, so a change that starts routing through this stub cannot pass unnoticed.
/// </summary>
internal sealed class DeliveryStubScheduler : IGuestThreadScheduler
{
    private int _deliverCalls;

    /// <summary>Runs on the parked thread, standing in for the guest's exception handler.</summary>
    public Action? OnDeliver { get; set; }

    public int DeliverCalls => Volatile.Read(ref _deliverCalls);

    public volatile bool SignalCompleted;

    public bool SupportsGuestContextTransfer => false;

    public bool TryDeliverPendingGuestException(CpuContext callerContext)
    {
        // Act once only: the park polls this every slice, and the production hook likewise stops
        // reporting work once this thread's queue is drained.
        if (Interlocked.Increment(ref _deliverCalls) != 1)
        {
            return false;
        }

        OnDeliver?.Invoke();
        return true;
    }

    // Blocking exports notify the cooperative scheduler after posting; nothing is registered
    // here, so report that nothing was woken.
    public int WakeBlockedThreads(string wakeKey, int maxCount = int.MaxValue) => 0;

    public void RegisterGuestThreadContext(ulong threadHandle, CpuContext context) =>
        throw new NotSupportedException();

    public bool TryStartThread(CpuContext creatorContext, GuestThreadStartRequest request, out string? error) =>
        throw new NotSupportedException();

    public bool TryJoinThread(CpuContext callerContext, ulong threadHandle, out ulong returnValue, out string? error) =>
        throw new NotSupportedException();

    // The event-flag park pumps the scheduler on every tick; nothing to advance here.
    public void Pump(CpuContext callerContext, string reason)
    {
    }

    public bool TrySetGuestThreadPriority(ulong guestThreadHandle, int guestPriority) =>
        throw new NotSupportedException();

    public bool TrySetGuestThreadAffinity(ulong guestThreadHandle, ulong affinityMask) =>
        throw new NotSupportedException();

    public IReadOnlyList<GuestThreadSnapshot> SnapshotThreads() => throw new NotSupportedException();

    public bool TryCallGuestFunction(
        CpuContext callerContext,
        ulong entryPoint,
        ulong arg0,
        ulong arg1,
        ulong stackAddress,
        ulong stackSize,
        string reason,
        out string? error) => throw new NotSupportedException();

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
        out string? error) => throw new NotSupportedException();

    public bool TryCallGuestContinuation(
        CpuContext callerContext,
        GuestCpuContinuation continuation,
        string reason,
        out string? error) => throw new NotSupportedException();

    public bool TryRaiseGuestException(
        CpuContext callerContext,
        ulong threadHandle,
        ulong handler,
        int exceptionType,
        out string? error) => throw new NotSupportedException();
}
