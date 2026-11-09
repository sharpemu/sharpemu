// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using Xunit;

namespace SharpEmu.Libs.Tests.Memory.GpuMemory;

[Collection(GpuMemoryStateCollection.Name)]
public sealed class GuestGpuMemoryTests
{
    private const GuestPageProtection ReadWrite = GuestPageProtection.Read | GuestPageProtection.Write;

    private sealed class RecordingStores : IGuestBufferStore, IGuestImageStore
    {
        public List<string> Calls { get; } = new();

        public bool BufferHandles { get; set; }

        public bool ImageHandles { get; set; }

        public Action? Recover { get; set; }

        bool IGuestBufferStore.MarkCpuWrite(ulong address, ulong size)
        {
            Calls.Add($"buffer.write {address:X}+{size:X}");
            Recover?.Invoke();
            return BufferHandles;
        }

        public bool DownloadToCpu(ulong address, ulong size)
        {
            Calls.Add($"buffer.pull {address:X}+{size:X}");
            Recover?.Invoke();
            return BufferHandles;
        }

        bool IGuestImageStore.MarkCpuWrite(ulong address, ulong size)
        {
            Calls.Add($"image.write {address:X}+{size:X}");
            return ImageHandles;
        }

        public void Unregister(ulong address, ulong size) => Calls.Add($"image.unregister {address:X}+{size:X}");
    }

    private sealed class InlineQueue : IGpuQueueRelay
    {
        public int Runs { get; private set; }

        public bool IsGpuQueueThread { get; set; } = true;

        public bool Accepting { get; set; } = true;

        public int Posts { get; private set; }

        public void Post(Action work)
        {
            Posts++;
            work();
        }

        public void RunOnGpuQueue(Action work)
        {
            Runs++;
            work();
        }

        public bool TryRunOnGpuQueue(Action work)
        {
            if (!Accepting)
            {
                return false;
            }

            RunOnGpuQueue(work);
            return true;
        }
    }

    private sealed class RecordingScheduler : IGpuTickScheduler
    {
        public List<string> Calls { get; } = new();

        public bool Active { get; set; }

        public ulong CurrentTick { get; set; } = 7;

        public bool InsideTickCallback { get; set; }

        public void Finish()
        {
            Calls.Add("finish");
            CurrentTick++;
        }

        public void WaitForPriorityOperations(ulong tick) => Calls.Add($"wait_priority {tick}");

        public void FinishMemoryAccess()
        {
            var tick = CurrentTick;
            Finish();
            WaitForPriorityOperations(tick);
        }
    }

    private readonly RecordingStores _stores = new();
    private readonly GuestGpuMemory _memory;

    public GuestGpuMemoryTests()
    {
        _memory = new GuestGpuMemory(new RecordingAddressSpace());
        _memory.AttachStores(_stores, _stores);
    }

    private void RegisterQuietly(ulong address, ulong size, GuestPageProtection protection)
    {
        _memory.Register(address, size, protection);
        _stores.Calls.Clear();
    }

    [Fact]
    public void Register_ThenCoversSubSpansOnly()
    {
        _memory.Register(0x10000, 0x4000, ReadWrite);

        Assert.True(_memory.Covers(0x10000, 0x4000));
        Assert.True(_memory.Covers(0x11000, 0x8));
        Assert.False(_memory.Covers(0x13FF8, 0x10));
        Assert.False(_memory.Covers(1UL << 40, 0x8));
        Assert.Equal(new[] { "buffer.write 10000+4000", "image.write 10000+4000" }, _stores.Calls);

        _stores.Calls.Clear();
        _memory.Unregister(0x10000, 0x4000);
        Assert.False(_memory.Covers(0x11000, 0x8));
        Assert.Equal(new[] { "buffer.write 10000+4000", "image.unregister 10000+4000" }, _stores.Calls);
        _memory.Dispose();
    }

    [Fact]
    public void CoverageQueryRejectsDisposedMemory()
    {
        var memory = new GuestGpuMemory(new RecordingAddressSpace());
        memory.Register(0x10000, 0x1000, ReadWrite);
        memory.Dispose();

        Assert.Throws<ObjectDisposedException>(() => memory.Covers(0x10000, 8));
    }

    [Fact]
    public void CoveragePublicationPreservesSplitRangesAndVersions()
    {
        using var memory = new GuestGpuMemory(new RecordingAddressSpace());
        memory.Register(0x10000, 0x4000, ReadWrite);
        var version = memory.SpanVersion;
        memory.Unregister(0x11000, 0x2000);
        Assert.True(memory.SpanVersion > version);
        Assert.True(memory.Covers(0x10000, 0x1000));
        Assert.True(memory.Covers(0x13000, 0x1000));
        Assert.False(memory.Covers(0x10fff, 2));
        Assert.False(memory.Covers(0x12000, 8));
        memory.Register(0x11000, 0x2000, ReadWrite);
        Assert.True(memory.Covers(0x10000, 0x4000));
        memory.Unregister(0x10000, 0x4000);
        Assert.False(memory.Covers(0x10000, 8));
    }

    [Fact]
    public async Task ReadersObserveCompletedCoveragePublications()
    {
        using var memory = new GuestGpuMemory(new RecordingAddressSpace());
        using var phases = new Barrier(5);
        var failures = 0;
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (var iteration = 0; iteration < 100; iteration++)
            {
                for (var phase = 0; phase < 2; phase++)
                {
                    Assert.True(phases.SignalAndWait(TimeSpan.FromSeconds(10)));
                    for (var read = 0; read < 100; read++)
                        if (memory.Covers(0x10000, 0x1000) != (phase == 0)) Interlocked.Increment(ref failures);
                    Assert.True(phases.SignalAndWait(TimeSpan.FromSeconds(10)));
                }
            }
        })).ToArray();
        for (var iteration = 0; iteration < 100; iteration++)
        {
            memory.Register(0x10000, 0x1000, ReadWrite);
            Assert.True(phases.SignalAndWait(TimeSpan.FromSeconds(10)));
            Assert.True(phases.SignalAndWait(TimeSpan.FromSeconds(10)));
            memory.Unregister(0x10000, 0x1000);
            Assert.True(phases.SignalAndWait(TimeSpan.FromSeconds(10)));
            Assert.True(phases.SignalAndWait(TimeSpan.FromSeconds(10)));
        }
        await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, failures);
    }

    [Fact]
    public async Task CoverageReadersKeepStableRangesDuringMappingChanges()
    {
        using var memory = new GuestGpuMemory(new RecordingAddressSpace());
        memory.Register(0x10000, 0x1000, ReadWrite);
        using var ready = new CountdownEvent(4);
        var stop = 0;
        var failures = 0;
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            ready.Signal();
            while (Volatile.Read(ref stop) == 0)
            {
                if (!memory.Covers(0x10000, 0x1000) || memory.Covers(0x12000, 8))
                    Interlocked.Increment(ref failures);
                memory.Covers(0x14000, 8);
            }
        })).ToArray();
        try
        {
            Assert.True(ready.Wait(TimeSpan.FromSeconds(10)));
            for (var iteration = 0; iteration < 500; iteration++)
            {
                memory.Register(0x14000, 0x2000, ReadWrite);
                memory.Unregister(0x14000, 0x1000);
                memory.Unregister(0x15000, 0x1000);
            }
        }
        finally
        {
            Volatile.Write(ref stop, 1);
            await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.Equal(0, failures);
        Assert.False(memory.Covers(0x14000, 0x2000));
        memory.Unregister(0x10000, 0x1000);
    }

    [Fact]
    public void RegisterLazyRange_CoversWritesAndFaultsWithoutRegisteringSpans()
    {
        _memory.RegisterLazyRange(0x100000, 0x10000);

        Assert.False(_memory.Covers(0x104000, 0x2000));
        Assert.False(_memory.OverlapsRegistered(0x104000, 0x2000));
        Assert.True(_memory.MarkCpuWrite(0x104000, 0x2000));
        Assert.Equal(new[] { "buffer.write 104000+2000", "image.write 104000+2000" }, _stores.Calls);

        _stores.Calls.Clear();
        _stores.ImageHandles = true;
        Assert.True(_memory.TryResolveFault(FaultKind.Write, 0x104010));
        Assert.Equal(new[] { "buffer.write 104000+4000", "image.write 104010+8" }, _stores.Calls);

        _stores.Calls.Clear();
        _memory.UnregisterLazyRange(0x100000, 0x10000);
        Assert.False(_memory.MarkCpuWrite(0x104000, 0x2000));
        Assert.False(_memory.TryResolveFault(FaultKind.Write, 0x104010));
        Assert.Empty(_stores.Calls);
        _memory.Dispose();
    }

    [Fact]
    public void Register_MarksTheMappedContentWrittenOnTheGpuQueue()
    {
        var queue = new InlineQueue { IsGpuQueueThread = false };
        var scheduler = new RecordingScheduler();
        _memory.AttachGpuQueue(queue, scheduler);

        _memory.Register(0x20000, 0x2000, ReadWrite);

        Assert.Equal(1, queue.Posts);
        Assert.Equal(new[] { "buffer.write 20000+2000", "image.write 20000+2000" }, _stores.Calls);
        Assert.Empty(scheduler.Calls);
        _memory.AttachGpuQueue(null, null);
        _memory.Dispose();
    }

    [Theory]
    [InlineData(FaultKind.Write, "buffer.write 10010+8", "image.write 10010+8")]
    [InlineData(FaultKind.Read, "buffer.pull 10010+8")]
    [InlineData(FaultKind.Execute)]
    [InlineData(FaultKind.Unknown, "buffer.pull 10010+8")]
    public void TryResolveFault_NotifiesStoresButDeclinesWhenNoneRecovers(FaultKind kind, params string[] expected)
    {
        RegisterQuietly(0x10000, 0x1000, ReadWrite);

        Assert.False(_memory.TryResolveFault(kind, 0x10010));
        Assert.Equal(expected, _stores.Calls);

        _memory.Unregister(0x10000, 0x1000);
        _memory.Dispose();
    }

    [Theory]
    [InlineData(FaultKind.Write, true, false)]
    [InlineData(FaultKind.Write, false, true)]
    [InlineData(FaultKind.Read, true, false)]
    public void TryResolveFault_ClaimsWhenAStoreRecovers(FaultKind kind, bool buffer, bool image)
    {
        _stores.BufferHandles = buffer;
        _stores.ImageHandles = image;
        RegisterQuietly(0x10000, 0x1000, ReadWrite);

        Assert.True(_memory.TryResolveFault(kind, 0x10010));

        _memory.Unregister(0x10000, 0x1000);
        _memory.Dispose();
    }

    [Fact]
    public void TryResolveFault_WriteNotifiesBothStoresEvenWhenTheFirstRecovers()
    {
        _stores.BufferHandles = true;
        RegisterQuietly(0x10000, 0x1000, ReadWrite);

        Assert.True(_memory.TryResolveFault(FaultKind.Write, 0x10010));
        Assert.Equal(new[] { "buffer.write 10010+8", "image.write 10010+8" }, _stores.Calls);

        _memory.Unregister(0x10000, 0x1000);
        _memory.Dispose();
    }

    [Theory]
    [InlineData(FaultKind.Read, true)]
    [InlineData(FaultKind.Write, false)]
    public void TryResolveFault_RetriesAfterRecoveryWhenAWatchIsRearmed(FaultKind kind, bool blockReads)
    {
        _stores.BufferHandles = true;
        RegisterQuietly(0x10000, 0x1000, ReadWrite);
        _memory.Pages.AddWatch(0x10000, 0x1000, blockReads);
        var rearm = true;
        _stores.Recover = () =>
        {
            _memory.Pages.RemoveWatch(0x10000, 0x1000, blockReads);
            if (rearm)
                _memory.Pages.AddWatch(0x10000, 0x1000, blockReads);
        };

        Assert.True(_memory.TryResolveFault(kind, 0x10010));
        Assert.False(_memory.Pages.Allows(0x10010, kind));
        rearm = false;
        Assert.True(_memory.TryResolveFault(kind, 0x10010));
        Assert.True(_memory.Pages.Allows(0x10010, kind));
        Assert.Equal(2, _stores.Calls.Count(call => call.StartsWith("buffer.", StringComparison.Ordinal)));
        _stores.Recover = null;

        _memory.Unregister(0x10000, 0x1000);
        _memory.Dispose();
    }

    [Fact]
    public void TryResolveFault_OutsideMappedSpansIsDeclined()
    {
        _stores.BufferHandles = true;
        RegisterQuietly(0x10000, 0x1000, ReadWrite);

        Assert.False(_memory.TryResolveFault(FaultKind.Write, 0x20000));
        Assert.False(_memory.MarkCpuWrite(0x20000, 0x10));
        Assert.Empty(_stores.Calls);

        Assert.True(_memory.MarkCpuWrite(0x10100, 0x10));
        Assert.Equal(new[] { "buffer.write 10100+10", "image.write 10100+10" }, _stores.Calls);

        _memory.Unregister(0x10000, 0x1000);
        _memory.Dispose();
    }

    // The fault address lies in the page whose protection stopped the access; a store to the last
    // bytes of a span is resolved there, and an access that also reaches the next page faults again on it.
    [Fact]
    public void TryResolveFault_ResolvesAccessesToTheLastBytesOfASpan()
    {
        _stores.BufferHandles = true;
        RegisterQuietly(0x10000, 0x1000, ReadWrite);

        Assert.True(_memory.TryResolveFault(FaultKind.Write, 0x10FFC));
        Assert.True(_memory.TryResolveFault(FaultKind.Read, 0x10FFC));
        Assert.Equal(new[] { "buffer.write 10FFC+4", "image.write 10FFC+4", "buffer.pull 10FFC+4" }, _stores.Calls);

        _memory.Unregister(0x10000, 0x1000);
        _memory.Dispose();
    }

    [Fact]
    public void TryResolveFault_DeclinesWhatTheGuestForbidsBeforeAnyStore()
    {
        _stores.BufferHandles = true;
        _stores.ImageHandles = true;
        RegisterQuietly(0x10000, 0x1000, GuestPageProtection.Read);
        RegisterQuietly(0x11000, 0x1000, GuestPageProtection.None);

        Assert.False(_memory.TryResolveFault(FaultKind.Write, 0x10010));
        Assert.False(_memory.TryResolveFault(FaultKind.Execute, 0x10010));
        Assert.False(_memory.TryResolveFault(FaultKind.Read, 0x11010));
        Assert.Empty(_stores.Calls);
        Assert.True(_memory.TryResolveFault(FaultKind.Read, 0x10010));
        Assert.Equal(new[] { "buffer.pull 10010+8" }, _stores.Calls);

        _memory.Unregister(0x10000, 0x2000);
        _memory.Dispose();
    }

    [Fact]
    public void NoteProtected_AppliesTheDerivedProtectionOnCoveredRangesOnly()
    {
        var space = new RecordingAddressSpace();
        using var memory = new GuestGpuMemory(space);
        memory.AttachStores(_stores, _stores);
        memory.Register(0x10000, 0x2000, ReadWrite);
        Assert.Equal(new[] { (0x10000UL, 0x2000UL, ReadWrite) }, space.Protects);

        Assert.False(memory.NoteProtected(0x20000, 0x1000, GuestPageProtection.Read));
        Assert.Single(space.Protects);

        Assert.True(memory.NoteProtected(0x10000, 0x1000, GuestPageProtection.Read));
        Assert.Equal((0x10000UL, 0x1000UL, GuestPageProtection.Read), space.Protects[1]);
        Assert.Equal(GuestPageProtection.Read, memory.Pages.Permissions.Lookup(0x10000));
        Assert.Equal(ReadWrite, memory.Pages.Permissions.Lookup(0x11000));

        memory.Unregister(0x10000, 0x2000);
        Assert.Equal(ReadWrite, memory.Pages.Permissions.Lookup(0x10000));
    }

    [Fact]
    public void AttachStores_NullStoresDeclineEveryFault()
    {
        _memory.AttachStores(null, null);
        RegisterQuietly(0x10000, 0x1000, ReadWrite);

        Assert.False(_memory.TryResolveFault(FaultKind.Write, 0x10010));
        Assert.False(_memory.TryResolveFault(FaultKind.Read, 0x10010));
        Assert.True(_memory.MarkCpuWrite(0x10010, 8));
        Assert.Empty(_stores.Calls);

        _memory.Unregister(0x10000, 0x1000);
        _memory.Dispose();
    }

    [Fact]
    public void Unregister_RunsOnTheGpuQueueWhenAttached()
    {
        var queue = new InlineQueue { IsGpuQueueThread = false };
        RegisterQuietly(0x10000, 0x1000, ReadWrite);
        _memory.AttachGpuQueue(queue, null);

        _memory.Unregister(0x10000, 0x1000);

        Assert.Equal(1, queue.Runs);
        Assert.False(_memory.Covers(0x10000, 0x1000));
        _memory.Dispose();
    }

    [Fact]
    public void MappingChangeFinishesGpuWorkBeforeRunningTheTransaction()
    {
        var scheduler = new RecordingScheduler { Active = true };
        var queue = new InlineQueue { IsGpuQueueThread = false };
        _memory.AttachGpuQueue(queue, scheduler);
        _memory.RunMappingChange(() => scheduler.Calls.Add("change"));
        Assert.Equal(new[] { "finish", "wait_priority 7", "change" }, scheduler.Calls);
        Assert.Equal(1, queue.Runs);
        _memory.AttachGpuQueue(null, null);
        _memory.Dispose();
    }

    [Fact]
    public async Task MappingChangeWaitsForDetachWhenTheRelayCloses()
    {
        var queue = new InlineQueue { IsGpuQueueThread = false, Accepting = false };
        var scheduler = new RecordingScheduler { Active = true };
        _memory.AttachGpuQueue(queue, scheduler);
        var changes = 0;
        var completion = Task.Run(() => _memory.RunMappingChange(() => Interlocked.Increment(ref changes)));
        try
        {
            Assert.False(await SchedulingTestSupport.CompletesWithin(completion, 100));
            Assert.Equal(0, Volatile.Read(ref changes));
        }
        finally
        {
            _memory.AttachGpuQueue(null, null);
        }
        Assert.True(await SchedulingTestSupport.CompletesWithin(completion, 5000));
        Assert.Equal(1, changes);
        Assert.Empty(scheduler.Calls);
        _memory.Dispose();
    }

    [Fact]
    public void MappingChangeRejectsCompletionCallbacks()
    {
        var previous = PageGuard.OnFatal;
        var fatals = new List<string>();
        PageGuard.OnFatal = fatals.Add;
        try
        {
            _memory.AttachGpuQueue(new InlineQueue(), new RecordingScheduler { InsideTickCallback = true });
            var changed = false;
            _memory.RunMappingChange(() => changed = true);
            Assert.False(changed);
            Assert.Single(fatals);
        }
        finally
        {
            PageGuard.OnFatal = previous;
            _memory.AttachGpuQueue(null, null);
            _memory.Dispose();
        }
    }

    [Fact]
    public void Unregister_DrainsTheSchedulerThroughTheTickItCaptured()
    {
        var scheduler = new RecordingScheduler { Active = true };
        RegisterQuietly(0x10000, 0x1000, ReadWrite);
        _memory.AttachGpuQueue(new InlineQueue(), scheduler);

        _memory.Unregister(0x10000, 0x1000);

        Assert.Equal(new[] { "finish", "wait_priority 7" }, scheduler.Calls);
        Assert.Equal(new[] { "buffer.write 10000+1000", "image.unregister 10000+1000" }, _stores.Calls);
        Assert.False(_memory.Covers(0x10000, 0x1000));

        scheduler.Active = false;
        RegisterQuietly(0x20000, 0x1000, ReadWrite);
        _memory.Unregister(0x20000, 0x1000);
        Assert.Equal(2, scheduler.Calls.Count);
        _memory.Dispose();
    }

    [Fact]
    public async Task Unregister_AfterClosureWaitsForDetachAndRunsWithoutTheScheduler()
    {
        var scheduler = new RecordingScheduler { Active = true };
        var queue = new InlineQueue { IsGpuQueueThread = false, Accepting = false };
        RegisterQuietly(0x10000, 0x1000, ReadWrite);
        _memory.AttachGpuQueue(queue, scheduler);

        var unregister = Task.Run(() => _memory.Unregister(0x10000, 0x1000));
        Assert.False(await SchedulingTestSupport.CompletesWithin(unregister, 100));
        Assert.Empty(_stores.Calls);

        _memory.AttachGpuQueue(null, null);

        Assert.True(await SchedulingTestSupport.CompletesWithin(unregister, 5000));
        Assert.Equal(0, queue.Runs);
        Assert.Empty(scheduler.Calls);
        Assert.Equal(new[] { "buffer.write 10000+1000", "image.unregister 10000+1000" }, _stores.Calls);
        Assert.False(_memory.Covers(0x10000, 0x1000));
        _memory.Dispose();
    }

    [Fact]
    public void Unregister_RefusesToRunFromATickCallbackBeforeDispatch()
    {
        var previous = PageGuard.OnFatal;
        var fatals = new List<string>();
        PageGuard.OnFatal = fatals.Add;
        try
        {
            var queue = new InlineQueue();
            RegisterQuietly(0x10000, 0x1000, ReadWrite);
            _memory.AttachGpuQueue(queue, new RecordingScheduler { Active = true, InsideTickCallback = true });

            _memory.Unregister(0x10000, 0x1000);

            Assert.Single(fatals);
            Assert.Contains("addr=0x0000000000010000", fatals[0]);
            Assert.Equal(0, queue.Runs);
            Assert.True(_memory.Covers(0x10000, 0x1000));
            _memory.AttachGpuQueue(null, null);
            _memory.Unregister(0x10000, 0x1000);
        }
        finally
        {
            PageGuard.OnFatal = previous;
        }

        _memory.Dispose();
    }
}
