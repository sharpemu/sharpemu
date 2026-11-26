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
    public async Task CopiedGpuClaimCannotRetireANewerClaimInTheSameSlot()
    {
        var owner = new GuestWriteReservations();
        var first = owner.BeginGpuClaim(Address, 16);
        var copy = first;
        using var other = owner.BeginGpuClaim(Address + 0x4000, 16);
        first.Dispose();
        var current = owner.BeginGpuClaim(Address, 16);
        var blocked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        owner.CpuAttemptWaitingForTest = () => blocked.TrySetResult(true);
        var cpu = Task.Run(() =>
        {
            using var attempt = owner.BeginCpuAttempt(Address, 16);
            return attempt.TryReserve();
        });
        try
        {
            await blocked.Task.WaitAsync(Deadline);
            copy.Dispose();
            Assert.False(cpu.IsCompleted);
        }
        finally { current.Dispose(); }
        Assert.True(await cpu.WaitAsync(Deadline));
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

    // A write across two 2 MiB regions reserves on the locked path and still holds back an overlapping claim.
    [Fact]
    public async Task WriteAcrossRegionsBlocksAnOverlappingClaim()
    {
        var owner = new GuestWriteReservations();
        const ulong boundary = 0x400000;
        var blocked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        owner.GpuClaimWaitingForTest = () => blocked.TrySetResult(true);
        var attempt = owner.BeginCpuAttempt(boundary - 16, 32);
        Assert.True(attempt.TryReserve());
        var gpu = Task.Run(() =>
        {
            using var claim = owner.BeginGpuClaim(boundary + 64, 16);
        });
        await blocked.Task.WaitAsync(Deadline);
        Assert.False(gpu.IsCompleted);
        attempt.Dispose();
        await gpu.WaitAsync(Deadline);
        owner.GpuClaimWaitingForTest = null;
    }

    // More claims on other pages of the region than the claim history keeps invalidate the attempt: the history no
    // longer shows whether one of them covered its pages.
    [Fact]
    public void ClaimsBeyondTheHistoryInvalidateAnAttemptConservatively()
    {
        var owner = new GuestWriteReservations();
        using (var attempt = owner.BeginCpuAttempt(Address, 16))
        {
            for (var index = 0; index < 65; index++)
            {
                using (owner.BeginGpuClaim(Address + TrackerLayout.PageBytes * (ulong)(1 + index % 8), 16)) { }
            }
            Assert.False(attempt.TryReserve());
        }
        using var fresh = owner.BeginCpuAttempt(Address, 16);
        Assert.True(fresh.TryReserve());
    }

    // Writers on several threads and a claimer on another, over a few shared pages: an admitted claim never overlaps a
    // reserved write, and a reserved write never overlaps an admitted claim.
    [Fact]
    public async Task ConcurrentWritersAndClaimsNeverOverlap()
    {
        var owner = new GuestWriteReservations();
        const int pages = 6;
        var reserved = new int[pages];
        var claimed = new int[pages];
        var violations = 0;
        var stop = DateTime.UtcNow + TimeSpan.FromSeconds(1.5);
        var writers = Enumerable.Range(0, 4).Select(seed => Task.Run(() =>
        {
            var random = new System.Random(seed);
            while (DateTime.UtcNow < stop)
            {
                var page = random.Next(pages);
                var address = Address + (ulong)page * TrackerLayout.PageBytes;
                while (true)
                {
                    using var attempt = owner.BeginCpuAttempt(address, 8);
                    if (!attempt.TryReserve()) continue;
                    Interlocked.Increment(ref reserved[page]);
                    if (Volatile.Read(ref claimed[page]) != 0) Interlocked.Increment(ref violations);
                    Thread.SpinWait(random.Next(50));
                    Interlocked.Decrement(ref reserved[page]);
                    break;
                }
            }
        })).ToArray();
        var claimer = Task.Run(() =>
        {
            var random = new System.Random(99);
            while (DateTime.UtcNow < stop)
            {
                var first = random.Next(pages);
                var count = 1 + random.Next(pages - first);
                using var claim = owner.BeginGpuClaim(Address + (ulong)first * TrackerLayout.PageBytes, (ulong)count * TrackerLayout.PageBytes);
                for (var page = first; page < first + count; page++)
                {
                    Interlocked.Increment(ref claimed[page]);
                    if (Volatile.Read(ref reserved[page]) != 0) Interlocked.Increment(ref violations);
                }
                Thread.SpinWait(random.Next(50));
                for (var page = first; page < first + count; page++) Interlocked.Decrement(ref claimed[page]);
            }
        });
        await Task.WhenAll(writers.Append(claimer)).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(0, violations);
    }

    // Writes on chunks that no claim has covered reserve in their threads' records and do not wait for each other.
    [Fact]
    public async Task WritesOnUnclaimedChunksDoNotExcludeEachOther()
    {
        var owner = new GuestWriteReservations();
        var reserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var first = Task.Run(() =>
        {
            using var attempt = owner.BeginCpuAttempt(Address, 16);
            Assert.True(attempt.TryReserve());
            reserved.TrySetResult(true);
            if (!release.Wait(Deadline)) throw new TimeoutException("The test did not release the first write.");
        });
        try
        {
            await reserved.Task.WaitAsync(Deadline);
            var second = await Task.Run(() =>
            {
                using var attempt = owner.BeginCpuAttempt(Address + 8, 16);
                return attempt.TryReserve();
            }).WaitAsync(Deadline);
            Assert.True(second);
        }
        finally
        {
            release.Set();
            await first.WaitAsync(Deadline);
        }
    }

    // The claim that marks a chunk waits for a write recorded anywhere in the chunk, not only over its own pages: a later
    // claim over the recorded pages finds the chunk marked and reads no records.
    [Fact]
    public async Task ClaimThatMarksAChunkWaitsForAWriteRecordedElsewhereInIt()
    {
        var owner = new GuestWriteReservations();
        var blocked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseCpu = new ManualResetEventSlim();
        owner.GpuClaimWaitingForTest = () => blocked.TrySetResult(true);
        var cpu = Task.Run(() =>
        {
            using var attempt = owner.BeginCpuAttempt(Address, 16);
            Assert.True(attempt.TryReserve());
            reserved.TrySetResult(true);
            if (!releaseCpu.Wait(Deadline)) throw new TimeoutException("The test did not release the CPU reservation.");
        });
        await reserved.Task.WaitAsync(Deadline);
        var admitted = false;
        var gpu = Task.Run(() =>
        {
            using var claim = owner.BeginGpuClaim(Address + 5 * TrackerLayout.PageBytes, 16);
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

    // A claimer marks a fresh chunk every 20 ms while writers reserve all over the chunks, so writes keep meeting a chunk
    // as it is marked: an admitted claim never overlaps a reserved write, and a reserved write never overlaps an admitted
    // claim.
    [Fact]
    public async Task ClaimsMarkingFreshChunksNeverOverlapReservedWrites()
    {
        var owner = new GuestWriteReservations();
        const int chunks = 64;
        const ulong chunkBytes = 0x10000;
        var reserved = new int[chunks];
        var claimed = new int[chunks];
        var violations = 0;
        var stop = DateTime.UtcNow + TimeSpan.FromSeconds(1.5);
        var writers = Enumerable.Range(0, 4).Select(seed => Task.Run(() =>
        {
            var random = new System.Random(seed);
            while (DateTime.UtcNow < stop)
            {
                var chunk = random.Next(chunks);
                var address = Address + (ulong)chunk * chunkBytes + (ulong)random.Next(16) * TrackerLayout.PageBytes;
                while (true)
                {
                    using var attempt = owner.BeginCpuAttempt(address, 8);
                    if (!attempt.TryReserve()) continue;
                    Interlocked.Increment(ref reserved[chunk]);
                    if (Volatile.Read(ref claimed[chunk]) != 0) Interlocked.Increment(ref violations);
                    Thread.SpinWait(random.Next(50));
                    Interlocked.Decrement(ref reserved[chunk]);
                    break;
                }
            }
        })).ToArray();
        var claimer = Task.Run(() =>
        {
            var random = new System.Random(99);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var next = 0;
            while (DateTime.UtcNow < stop)
            {
                int chunk;
                if (next < chunks && clock.ElapsedMilliseconds >= next * 20L) chunk = next++;
                else if (next > 0) chunk = random.Next(next);
                else continue;
                using var claim = owner.BeginGpuClaim(Address + (ulong)chunk * chunkBytes, chunkBytes);
                Interlocked.Increment(ref claimed[chunk]);
                if (Volatile.Read(ref reserved[chunk]) != 0) Interlocked.Increment(ref violations);
                Thread.SpinWait(random.Next(50));
                Interlocked.Decrement(ref claimed[chunk]);
            }
        });
        await Task.WhenAll(writers.Append(claimer)).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(0, violations);
    }
}
