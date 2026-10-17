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
}
