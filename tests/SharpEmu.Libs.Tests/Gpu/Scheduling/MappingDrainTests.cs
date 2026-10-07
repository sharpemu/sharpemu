// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Memory.GpuMemory;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Scheduling.SchedulingTestSupport;

namespace SharpEmu.Libs.Tests.Gpu.Scheduling;

[Collection(SchedulingStateCollection.Name)]
public sealed class MappingDrainTests
{
    private const ulong Address = 0x10000;
    private const ulong Size = 0x1000;

    [Fact]
    public void NestedMappingChangesAndUnmapsReuseTheCompletedDrain()
    {
        var device = new FakeTickDevice { CompleteOnSubmit = true };
        using var scheduler = NewActiveScheduler(device);
        using var memory = CreateMemory(scheduler);

        memory.RunMappingChange(() =>
        {
            memory.RunMappingChange(() => memory.Unregister(Address, Size));
            memory.Unregister(Address + Size, Size);
        });

        Assert.Single(device.Submits);
        Assert.False(memory.Covers(Address, Size));
        Assert.False(memory.Covers(Address + Size, Size));
        Assert.False(scheduler.Current.IsInvalid);
    }

    [Fact]
    public void ConsecutiveMappingChangesWithoutGpuWorkReuseTheCompletedDrain()
    {
        var device = new FakeTickDevice { CompleteOnSubmit = true };
        using var scheduler = NewActiveScheduler(device);
        using var memory = CreateMemory(scheduler);

        memory.RunMappingChange(() => memory.Unregister(Address, Size));
        memory.RunMappingChange(() => memory.Unregister(Address + Size, Size));

        Assert.Single(device.Submits);
    }

    [Fact]
    public void AcquiringTheHandleThroughAnExistingWrapperRequiresAnotherDrain()
    {
        var device = new FakeTickDevice { CompleteOnSubmit = true };
        using var scheduler = NewActiveScheduler(device);
        using var memory = CreateMemory(scheduler);
        var command = scheduler.Current;

        memory.RunMappingChange(() =>
        {
            _ = command.Handle;
            memory.Unregister(Address, Size);
        });

        Assert.Equal(2, device.Submits.Count);
        Assert.True(scheduler.IsTickComplete(2));
    }

    [Fact]
    public void AnInterveningSubmissionRequiresAnotherDrain()
    {
        var device = new FakeTickDevice { CompleteOnSubmit = true };
        using var scheduler = NewActiveScheduler(device);
        using var memory = CreateMemory(scheduler);

        memory.RunMappingChange(() =>
        {
            scheduler.Flush();
            memory.Unregister(Address, Size);
        });

        Assert.Equal(3, device.Submits.Count);
        Assert.True(scheduler.IsTickComplete(3));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompletionWorkAddedAfterTheDrainRunsBeforeTheUnmap(bool priority)
    {
        var device = new FakeTickDevice { CompleteOnSubmit = true };
        using var scheduler = NewActiveScheduler(device);
        using var memory = CreateMemory(scheduler);
        var order = new List<string>();
        memory.AttachStores(new ObservingStore(() => order.Add("unmap")), null);

        memory.RunMappingChange(() =>
        {
            if (priority)
                scheduler.QueuePriorityCompletionAction(() => order.Add("callback"));
            else
                scheduler.QueueCompletionAction(() => order.Add("callback"));
            memory.Unregister(Address, Size);
        });

        Assert.Equal(new[] { "callback", "unmap" }, order);
        Assert.Equal(2, device.Submits.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WorkAddedByADrainedCallbackIsNotCountedAsCompleted(bool priority)
    {
        var device = new FakeTickDevice { CompleteOnSubmit = true };
        using var scheduler = NewActiveScheduler(device);
        using var memory = CreateMemory(scheduler);
        var completed = false;
        scheduler.QueueCompletionAction(() =>
        {
            if (priority)
                scheduler.QueuePriorityCompletionAction(() => completed = true);
            else
                _ = scheduler.Current.Handle;
        });

        memory.RunMappingChange(() => memory.Unregister(Address, Size));

        Assert.Equal(2, device.Submits.Count);
        Assert.True(scheduler.IsTickComplete(2));
        if (priority)
            Assert.True(completed);
    }

    [Fact]
    public void OpenRenderingPreventsDrainReuse()
    {
        var device = new FakeTickDevice { CompleteOnSubmit = true };
        var rendering = new RecordingRenderingState();
        using var scheduler = NewActiveScheduler(device, rendering);
        using var memory = CreateMemory(scheduler);

        memory.RunMappingChange(() =>
        {
            rendering.IsRendering = true;
            memory.Unregister(Address, Size);
        });

        Assert.Equal(2, device.Submits.Count);
        Assert.False(rendering.IsRendering);
        Assert.Single(rendering.Log);
    }

    [Fact]
    public void MemoryDrainRejectsInactiveSchedulersAndCompletionCallbacks()
    {
        using var fatal = new FatalScope();
        var device = new FakeTickDevice { CompleteOnSubmit = true };
        using var scheduler = new SubmissionScheduler(device, new RecordingRenderingState());
        Assert.Throws<SchedulerFatalException>(scheduler.FinishMemoryAccess);
        scheduler.Begin(new SubmissionContext());
        scheduler.FinishMemoryAccess();
        scheduler.QueueCompletionAction(() => Assert.Throws<SchedulerFatalException>(scheduler.FinishMemoryAccess));

        scheduler.FinishMemoryAccess();

        Assert.Equal(2, fatal.Messages.Count);
        Assert.Equal(2, device.Submits.Count);
    }

    [Fact]
    public void ASubmissionFromADrainedCallbackInvalidatesTheCompletedDrain()
    {
        var device = new FakeTickDevice { CompleteOnSubmit = true };
        using var scheduler = NewActiveScheduler(device);
        using var memory = CreateMemory(scheduler);
        scheduler.QueueCompletionAction(() => scheduler.Flush());

        memory.RunMappingChange(() => memory.Unregister(Address, Size));

        Assert.Equal(3, device.Submits.Count);
        Assert.True(scheduler.IsTickComplete(3));
    }

    [Fact]
    public void AnExceptionDoesNotHideGpuWorkFromTheNextMappingChange()
    {
        var device = new FakeTickDevice { CompleteOnSubmit = true };
        using var scheduler = NewActiveScheduler(device);
        using var memory = CreateMemory(scheduler);

        Assert.Throws<InvalidOperationException>(() => memory.RunMappingChange(() =>
        {
            _ = scheduler.Current.Handle;
            throw new InvalidOperationException("The mapping change failed.");
        }));
        memory.RunMappingChange(() => memory.Unregister(Address, Size));

        Assert.Equal(2, device.Submits.Count);
        Assert.True(scheduler.IsTickComplete(2));
    }

    [Fact]
    public async Task MappingRemainsRegisteredUntilGpuWorkCompletes()
    {
        var device = new FakeTickDevice();
        using var scheduler = NewActiveScheduler(device);
        using var memory = CreateMemory(scheduler);
        _ = scheduler.Current.Handle;

        var change = Task.Run(() => memory.RunMappingChange(() => memory.Unregister(Address, Size)));
        try
        {
            Assert.True(WaitUntil(() => device.Log.Contains("wait 1")));
            Assert.False(change.IsCompleted);
            Assert.True(memory.Covers(Address, Size));
        }
        finally
        {
            device.CompleteOnSubmit = true;
            device.Complete(1);
            Assert.True(await CompletesWithin(change, 5000));
            await change;
        }

        Assert.False(memory.Covers(Address, Size));
        Assert.Single(device.Submits);
    }

    [Fact]
    public async Task MappingRemainsRegisteredUntilTheActivePriorityCallbackCompletes()
    {
        var device = new FakeTickDevice { CompleteOnSubmit = true };
        using var scheduler = NewActiveScheduler(device);
        using var memory = CreateMemory(scheduler);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        scheduler.QueuePriorityCompletionAction(() =>
        {
            entered.Set();
            release.Wait();
        });

        var change = Task.Run(() => memory.RunMappingChange(() => memory.Unregister(Address, Size)));
        try
        {
            Assert.True(entered.Wait(5000));
            Assert.False(change.IsCompleted);
            Assert.True(memory.Covers(Address, Size));
        }
        finally
        {
            release.Set();
            Assert.True(await CompletesWithin(change, 5000));
            await change;
        }

        Assert.False(memory.Covers(Address, Size));
        Assert.Single(device.Submits);
    }

    private static GuestGpuMemory CreateMemory(SubmissionScheduler scheduler)
    {
        var memory = new GuestGpuMemory(new RecordingAddressSpace());
        memory.Register(Address, 2 * Size, GuestPageProtection.Read | GuestPageProtection.Write);
        memory.AttachGpuQueue(null, scheduler);
        return memory;
    }

    private sealed class ObservingStore(Action unmap) : IGuestBufferStore
    {
        public bool MarkCpuWrite(ulong address, ulong size)
        {
            unmap();
            return false;
        }

        public bool DownloadToCpu(ulong address, ulong size) => false;
    }
}
