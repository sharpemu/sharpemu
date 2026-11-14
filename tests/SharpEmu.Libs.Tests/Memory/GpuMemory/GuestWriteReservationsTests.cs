// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GpuMemory;
using Xunit;

namespace SharpEmu.Libs.Tests.Memory.GpuMemory;

public sealed class GuestWriteReservationsTests
{
    private const ulong Address = 0x10000;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Fact]
    public void StaleCopiedTokenCannotReleaseOrReserveANewerAttempt()
    {
        var owner = new GuestWriteReservations();
        var old = owner.BeginCpuAttempt(Address, 16);
        var copy = old;
        old.Dispose();
        using var current = owner.BeginCpuAttempt(Address, 16);
        copy.Dispose();
        Assert.True(copy.Released);
        Assert.False(current.Released);
        Assert.Throws<InvalidOperationException>(() => copy.TryReserve());
        Assert.True(current.TryReserve());
        copy.Dispose();
        Assert.Throws<InvalidOperationException>(() => owner.BeginGpuClaim(Address, 16));
    }

    [Fact]
    public void InvalidatedTokenStaysInvalidUntilAFreshAttemptIsAdmitted()
    {
        var owner = new GuestWriteReservations();
        var attempt = owner.BeginCpuAttempt(Address, 16);
        var copy = attempt;
        using (owner.BeginGpuClaim(Address, 16)) { }
        Assert.False(attempt.TryReserve());
        Assert.False(copy.TryReserve());
        copy.Dispose();
        Assert.True(attempt.Released);
        using var fresh = owner.BeginCpuAttempt(Address, 16);
        Assert.True(fresh.TryReserve());
        attempt.Dispose();
        Assert.False(fresh.Released);
    }

    [Fact]
    public void InterveningClaimForcesFreshPremarkEvenAfterClaimFinished()
    {
        var owner = new GuestWriteReservations();
        using (var attempt = owner.BeginCpuAttempt(Address, 16))
        {
            using (owner.BeginGpuClaim(Address + 128, 16)) { }
            Assert.False(attempt.TryReserve());
        }
        using var fresh = owner.BeginCpuAttempt(Address, 16);
        Assert.True(fresh.TryReserve());
    }

    [Fact]
    public void UnrelatedPageClaimDoesNotInvalidateTheAttempt()
    {
        var owner = new GuestWriteReservations();
        using var attempt = owner.BeginCpuAttempt(Address, 16);
        using (owner.BeginGpuClaim(Address + TrackerLayout.PageBytes, 16)) { }
        Assert.True(attempt.TryReserve());
    }

    [Fact]
    public async Task ReservationBlocksClaimUntilReleaseAndCoversTheWholePage()
    {
        var owner = new GuestWriteReservations();
        var blocked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseCpu = new ManualResetEventSlim();
        owner.GpuClaimWaitingForTest = () => blocked.TrySetResult(true);
        var cpu = Task.Run(() =>
        {
            using var attempt = owner.BeginCpuAttempt(Address + 7, 1);
            Assert.True(attempt.TryReserve());
            reserved.TrySetResult(true);
            if (!releaseCpu.Wait(Deadline)) throw new TimeoutException("The test did not release the CPU reservation.");
        });
        await reserved.Task.WaitAsync(Deadline);
        var admitted = false;
        var gpu = Task.Run(() =>
        {
            using var claim = owner.BeginGpuClaim(Address + 2048, 1);
            admitted = true;
        });
        try
        {
            await blocked.Task.WaitAsync(Deadline);
            Assert.False(gpu.IsCompleted);
            Assert.False(admitted);
        }
        finally
        {
            releaseCpu.Set();
            await cpu.WaitAsync(Deadline);
            await gpu.WaitAsync(Deadline);
            owner.GpuClaimWaitingForTest = null;
        }
        Assert.True(admitted);
    }

    [Fact]
    public async Task ActiveGpuClaimFinishesBeforeCpuPremarkCanBegin()
    {
        var owner = new GuestWriteReservations();
        using var release = new ManualResetEventSlim();
        var admitted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cpuBlocked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        owner.CpuAttemptWaitingForTest = () => cpuBlocked.TrySetResult(true);
        var gpu = Task.Run(() =>
        {
            using var claim = owner.BeginGpuClaim(Address, 64);
            admitted.TrySetResult(true);
            if (!release.Wait(Deadline)) throw new TimeoutException("The test did not release the GPU claim.");
        });
        await admitted.Task.WaitAsync(Deadline);
        var cpu = Task.Run(() =>
        {
            using var attempt = owner.BeginCpuAttempt(Address, 64);
            return attempt.TryReserve();
        });
        try
        {
            await cpuBlocked.Task.WaitAsync(Deadline);
            Assert.False(cpu.IsCompleted);
            release.Set();
            await gpu.WaitAsync(Deadline);
            Assert.True(await cpu.WaitAsync(Deadline));
        }
        finally
        {
            release.Set();
            await gpu.WaitAsync(Deadline);
            await cpu.WaitAsync(Deadline);
        }
    }

    [Fact]
    public void NestedPremarkAndOverlappingGpuWorkAreRejectedWhileReserved()
    {
        var owner = new GuestWriteReservations();
        using var attempt = owner.BeginCpuAttempt(Address, 16);
        Assert.True(attempt.TryReserve());
        Assert.Throws<InvalidOperationException>(() => owner.BeginCpuAttempt(Address + 0x2000, 16));
        Assert.Throws<InvalidOperationException>(() => owner.BeginGpuClaim(Address, 16));
    }

    [Fact]
    public void AbortedAttemptAndThrownCopyReleaseAdmission()
    {
        var owner = new GuestWriteReservations();
        using (owner.BeginCpuAttempt(Address, 16)) { }
        Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            using var attempt = owner.BeginCpuAttempt(Address, 16);
            Assert.True(attempt.TryReserve());
            throw new InvalidOperationException("Synthetic copy failure");
        }));
        using var claim = owner.BeginGpuClaim(Address, 16);
    }

    [Fact]
    public void GpuOwnerCanPerformAReentrantManagedStore()
    {
        var owner = new GuestWriteReservations();
        using var claim = owner.BeginGpuClaim(Address, 16);
        using var attempt = owner.BeginCpuAttempt(Address, 16);
        Assert.True(attempt.TryReserve());
    }

    [Fact]
    public async Task DisjointGpuOwnersCanEachPerformAReentrantManagedStore()
    {
        var owner = new GuestWriteReservations();
        using var startCpu = new ManualResetEventSlim();
        using var releaseCpu = new ManualResetEventSlim();
        var claims = Enumerable.Range(0, 2).Select(_ =>
            new TaskCompletionSource<GuestWriteReservations.GpuClaim>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var reserved = Enumerable.Range(0, 2).Select(_ =>
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var workers = Enumerable.Range(0, 2).Select(index => Task.Run(() =>
        {
            var address = Address + (ulong)index * TrackerLayout.PageBytes;
            using var claim = owner.BeginGpuClaim(address, 16);
            claims[index].TrySetResult(claim);
            if (!startCpu.Wait(Deadline)) throw new TimeoutException("The test did not start the CPU premarks.");
            using var attempt = owner.BeginCpuAttempt(address, 16);
            Assert.True(attempt.TryReserve());
            reserved[index].TrySetResult(true);
            if (!releaseCpu.Wait(Deadline)) throw new TimeoutException("The test did not release the CPU copies.");
        })).ToArray();
        try
        {
            await Task.WhenAll(claims.Select(x => x.Task)).WaitAsync(Deadline);
            startCpu.Set();
            await Task.WhenAll(reserved.Select(x => x.Task)).WaitAsync(Deadline);
        }
        finally
        {
            startCpu.Set();
            releaseCpu.Set();
            // Also unwind a broken global-gate implementation: neither task may retain
            // its claim forever while waiting for the other task's unrelated claim.
            foreach (var claim in claims)
                if (claim.Task.IsCompletedSuccessfully) claim.Task.Result.Dispose();
            await Task.WhenAll(workers).WaitAsync(Deadline);
        }
    }
}
