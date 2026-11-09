// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Scheduling;
using Silk.NET.Vulkan;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Scheduling.SchedulingTestSupport;

namespace SharpEmu.Libs.Tests.Gpu.Scheduling;

[Collection(SchedulingStateCollection.Name)]
public sealed class RecordingBufferBarrierTests
{
    private static readonly MemoryBarrierRequest ComputeToAll = new(
        PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.AllCommandsBit,
        AccessFlags.ShaderWriteBit, AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit);

    private static readonly MemoryBarrierRequest AllToCompute = new(
        PipelineStageFlags.AllCommandsBit, PipelineStageFlags.ComputeShaderBit,
        AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit, AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit);

    private readonly FakeTickDevice _device = new();
    private readonly RecordingRenderingState _rendering = new();

    [Fact]
    public void RequestsBetweenCommandsAreRecordedAsOneUnion()
    {
        using var scheduler = NewActiveScheduler(_device, _rendering);
        var recorded = new List<MemoryBarrierRequest>();
        scheduler.MemoryBarrierRecorder = (_, request) => recorded.Add(request);

        scheduler.Current.RequestMemoryBarrier(ComputeToAll);
        _ = scheduler.Current.StateHandle;
        scheduler.Current.RequestMemoryBarrier(AllToCompute);
        Assert.Empty(recorded);
        _ = scheduler.Current.Handle;
        _ = scheduler.Current.Handle;

        Assert.Equal([ComputeToAll.Union(AllToCompute)], recorded);
        Assert.Equal((1L, 1L), (scheduler.Current.MergedBarriers, scheduler.Current.RecordedBarriers));
        _device.CompleteOnSubmit = true;
    }

    [Fact]
    public void ADeferredBarrierWaitsForTheFirstNativeCommandOutsideACheckedWindow()
    {
        using var scheduler = NewActiveScheduler(_device, _rendering);
        var recorded = new List<MemoryBarrierRequest>();
        scheduler.MemoryBarrierRecorder = (_, request) => recorded.Add(request);
        var promotions = scheduler.Current.DeferredPromotions;

        scheduler.Current.DeferMemoryBarrier(ComputeToAll);
        scheduler.Current.EnterCheckedWindow();
        _ = scheduler.Current.Handle;
        _ = scheduler.Current.Handle;
        scheduler.Current.ExitCheckedWindow();
        Assert.Empty(recorded);
        Assert.Equal(promotions, scheduler.Current.DeferredPromotions);

        _ = scheduler.Current.Handle;
        Assert.Equal([ComputeToAll], recorded);
        Assert.Equal(promotions + 1, scheduler.Current.DeferredPromotions);
        _device.CompleteOnSubmit = true;
    }

    [Fact]
    public void ATransferHandleFlushesPendingRequestsButKeepsTheDeferredBarrier()
    {
        using var scheduler = NewActiveScheduler(_device, _rendering);
        _device.CompleteOnSubmit = true;
        var recorded = new List<MemoryBarrierRequest>();
        scheduler.MemoryBarrierRecorder = (_, request) => recorded.Add(request);
        var promotions = scheduler.Current.DeferredPromotions;

        scheduler.Current.DeferMemoryBarrier(ComputeToAll);
        _ = scheduler.Current.TransferHandle;
        Assert.Empty(recorded);
        Assert.Equal(promotions, scheduler.Current.DeferredPromotions);

        scheduler.Current.RequestMemoryBarrier(AllToCompute);
        scheduler.Current.DeferMemoryBarrier(ComputeToAll);
        _ = scheduler.Current.TransferHandle;
        Assert.Equal([AllToCompute.Union(ComputeToAll)], recorded);

        _ = scheduler.Current.Handle;
        Assert.Single(recorded);
    }

    [Fact]
    public void ARequestFoldsTheDeferredBarrierIntoOneRecord()
    {
        using var scheduler = NewActiveScheduler(_device, _rendering);
        var recorded = new List<MemoryBarrierRequest>();
        scheduler.MemoryBarrierRecorder = (_, request) => recorded.Add(request);
        var promotions = scheduler.Current.DeferredPromotions;

        scheduler.Current.DeferMemoryBarrier(ComputeToAll);
        scheduler.Current.RequestMemoryBarrier(AllToCompute);
        scheduler.Current.EnterCheckedWindow();
        _ = scheduler.Current.Handle;
        scheduler.Current.ExitCheckedWindow();

        Assert.Equal([AllToCompute.Union(ComputeToAll)], recorded);
        Assert.Equal(promotions + 1, scheduler.Current.DeferredPromotions);
        Assert.Equal(1L, scheduler.Current.RecordedBarriers);
        _device.CompleteOnSubmit = true;
    }

    [Fact]
    public void ADeferredBarrierIsRecordedBeforeTheBufferEnds()
    {
        using var scheduler = NewActiveScheduler(_device, _rendering);
        var recorded = new List<string>();
        scheduler.MemoryBarrierRecorder = (buffer, _) => recorded.Add($"barrier {_device.Name(buffer)} after {_device.Log[^1]}");
        scheduler.Current.DeferMemoryBarrier(ComputeToAll);
        scheduler.Current.EnterCheckedWindow();
        _device.CompleteOnSubmit = true;

        scheduler.Submit();

        Assert.Equal(["barrier b0 after begin b0"], recorded);
    }

    [Fact]
    public void APendingRequestIsRecordedBeforeTheBufferEnds()
    {
        using var scheduler = NewActiveScheduler(_device, _rendering);
        var recorded = new List<string>();
        scheduler.MemoryBarrierRecorder = (buffer, _) => recorded.Add($"barrier {_device.Name(buffer)} after {_device.Log[^1]}");
        scheduler.Current.RequestMemoryBarrier(ComputeToAll);
        _device.CompleteOnSubmit = true;

        scheduler.Submit();

        Assert.Equal(["barrier b0 after begin b0"], recorded);
        Assert.Contains("end b0", _device.Log);
    }

    [Fact]
    public void ARequestInsideRenderingIsRefused()
    {
        using var fatal = new FatalScope();
        using var scheduler = NewActiveScheduler(_device, _rendering);
        scheduler.MemoryBarrierRecorder = (_, _) => { };
        _rendering.IsRendering = true;

        Assert.Throws<SchedulerFatalException>(() => scheduler.Current.RequestMemoryBarrier(ComputeToAll));

        _rendering.IsRendering = false;
        _device.CompleteOnSubmit = true;
    }

    [Fact]
    public void ADiscardedBufferRecordsNeitherItsBarriersNorTheHostReadBarrier()
    {
        using var scheduler = NewActiveScheduler(_device, _rendering);
        _device.CompleteOnSubmit = true;
        var recorded = new List<MemoryBarrierRequest>();
        scheduler.MemoryBarrierRecorder = (_, request) => recorded.Add(request);
        var worker = scheduler.CreateRecordingBuffer(new RecordingRenderingState());
        scheduler.BeginRecording(worker);
        var name = _device.Name(worker.Buffer);
        worker.RequestMemoryBarrier(AllToCompute);
        worker.DeferMemoryBarrier(ComputeToAll);
        worker.NoteMappedMemoryWrite();

        scheduler.EndDiscardedRecording(worker);
        scheduler.DiscardRecording(worker);

        Assert.Empty(recorded);
        Assert.Equal(0, _device.HostReadBarriers);
        Assert.True(worker.IsInvalid);
        Assert.Contains($"end {name}", _device.Log);
    }

    [Fact]
    public void EverySubmittedBufferEndsWithTheHostReadBarrier()
    {
        using var fatal = new FatalScope();
        using var scheduler = NewActiveScheduler(_device, _rendering);
        _device.CompleteOnSubmit = true;
        scheduler.MemoryBarrierRecorder = (_, _) => { };
        _ = scheduler.Current.Handle;
        scheduler.Flush();
        Assert.Equal(1, _device.HostReadBarriers);

        _ = scheduler.Current.Handle;
        scheduler.NoteMappedMemoryWrite();
        scheduler.Flush();
        Assert.Equal(2, _device.HostReadBarriers);
        Assert.False(scheduler.Current.MayWriteMappedMemory);
    }

    [Fact]
    public void ARequestThePendingBarrierCoversMergesWithoutAnotherRecord()
    {
        using var scheduler = NewActiveScheduler(_device, _rendering);
        _device.CompleteOnSubmit = true;
        var recorded = new List<MemoryBarrierRequest>();
        scheduler.MemoryBarrierRecorder = (_, request) => recorded.Add(request);
        var full = new MemoryBarrierRequest(PipelineStageFlags.AllCommandsBit, PipelineStageFlags.AllCommandsBit,
            AccessFlags.MemoryWriteBit, AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit);

        scheduler.Current.RequestMemoryBarrier(full);
        scheduler.Current.RequestMemoryBarrier(full);
        _ = scheduler.Current.Handle;

        Assert.Equal([full], recorded);
        Assert.True(full.Covers(full));
        Assert.True(full.Covers(new MemoryBarrierRequest(PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.TransferBit,
            AccessFlags.MemoryWriteBit, AccessFlags.MemoryReadBit)));
        Assert.False(full.Covers(AllToCompute));
    }
}
