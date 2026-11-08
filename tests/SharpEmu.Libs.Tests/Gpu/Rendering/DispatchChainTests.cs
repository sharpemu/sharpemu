// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Rendering.RenderExecutorFixtures;

namespace SharpEmu.Libs.Tests.Gpu.Rendering;

[Collection(SchedulingStateCollection.Name)]
public sealed class DispatchChainTests : IDisposable
{
    private const ulong BufferA = RecordingRenderHost.MemoryBase + 0x70_0000;
    private const ulong BufferB = RecordingRenderHost.MemoryBase + 0x71_0000;
    private readonly RecordingRenderHost _host = new() { DeferBarriers = true };
    private readonly FakePipelineProvider _pipelines = new();
    private readonly RenderExecutor _executor;
    private readonly FatalScope _fatal = new();

    public DispatchChainTests() => _executor = new RenderExecutor(_host, _pipelines);

    public void Dispose() => _fatal.Dispose();

    private static BufferResourceInfo Written => new(false, true, false, false, false, 4, 0);

    private static BufferResourceInfo Read => new(true, false, false, false, false, 4, 0);

    private void Dispatch(ulong submitId, BufferResourceInfo[] buffers, uint[][] descriptors)
    {
        _pipelines.Compute = ComputeProgram(Stage(Program(ShaderStageKind.Compute, buffers: buffers), buffers: descriptors));
        _executor.Dispatch(submitId, Banks(), 1, 1, 1, 0x41);
    }

    private int HazardBarriers => _host.Calls.Count(call => call == "write_hazard_barrier");

    [Fact]
    public void DisjointDispatchesShareOneHazardBarrierAndDeferTheirAccessBarriers()
    {
        Dispatch(1, [Written], [BufferDescriptor(BufferA, 4, 16)]);
        Dispatch(2, [Written], [BufferDescriptor(BufferB, 4, 16)]);

        Assert.Equal(1, HazardBarriers);
        Assert.Equal(2, _host.Calls.Count(call => call == "defer_access_barrier"));
        Assert.DoesNotContain("access_barrier", _host.Calls);
        var window = _host.Calls.IndexOf("checked_begin");
        Assert.True(window >= 0 && _host.Calls.IndexOf("dispatch 1 1 1") > window && _host.Calls.IndexOf("checked_end") > _host.Calls.IndexOf("dispatch 1 1 1"));
    }

    [Fact]
    public void AReadOfAChainWriteTakesTheHazardBarrier()
    {
        Dispatch(1, [Written], [BufferDescriptor(BufferA, 4, 16)]);
        Dispatch(2, [Read], [BufferDescriptor(BufferA + 0x20, 4, 4)]);

        Assert.Equal(2, HazardBarriers);
    }

    [Fact]
    public void AWriteOverAChainReadTakesTheHazardBarrier()
    {
        Dispatch(1, [Read, Written], [BufferDescriptor(BufferA, 4, 16), BufferDescriptor(BufferB, 4, 16)]);
        Dispatch(2, [Written], [BufferDescriptor(BufferA + 0x10, 4, 4)]);

        Assert.Equal(2, HazardBarriers);
    }

    [Fact]
    public void APromotionOutsideTheChainStartsANewOne()
    {
        Dispatch(1, [Written], [BufferDescriptor(BufferA, 4, 16)]);
        _host.Promotions++;
        Dispatch(2, [Written], [BufferDescriptor(BufferB, 4, 16)]);

        Assert.Equal(2, HazardBarriers);
    }

    [Fact]
    public void AnUndeferredHostKeepsTheAccessBarrierAfterEveryDispatch()
    {
        _host.DeferBarriers = false;
        Dispatch(1, [Written], [BufferDescriptor(BufferA, 4, 16)]);
        Dispatch(2, [Written], [BufferDescriptor(BufferB, 4, 16)]);

        Assert.Equal(2, HazardBarriers);
        Assert.Equal(2, _host.Calls.Count(call => call == "access_barrier"));
    }
}
