// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using Silk.NET.Vulkan;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Images.ImageCacheTestSupport;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed unsafe partial class RenderHostDeviceTests
{
    private sealed class SliceDrawSink : IResolvedCommandSink
    {
        public ResolvedDraw Draw = null!;
        public long NextSequence() => 1;
        public void Record(ResolvedDraw record) => Draw = record;
        public void Record(ResolvedDispatch record) => throw new InvalidOperationException();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SliceCarrySubmitsEachDrawAndDispatchOnceAtItsGuestObservation(bool fullPrefix)
    {
        if (!GatePrerequisites.Ready(_vulkan, shaderInt64: true)) return;
        _vulkan.Vk.GetPhysicalDeviceFeatures(_vulkan.Physical, out var features);
        Assert.True(features.OcclusionQueryPrecise);
        using var fatal = new FatalScope();
        using var presenter = new PresenterUnderTest(_vulkan);
        presenter.LoadRenderingCommands();
        presenter.SetField("_supportsPreciseOcclusion", true);
        presenter.SetField("_minStorageBufferOffsetAlignment", 256UL);
        var harness = presenter.Harness;
        var target = harness.MapBacked(0x10000, ReadWrite);
        var vertices = harness.MapBacked(0x10000, ReadWrite);
        var args = harness.MapBacked(0x10000, ReadWrite);
        var label = harness.MapBacked(0x10000, ReadWrite);
        var samples = harness.MapBacked(0x10000, ReadWrite);
        var checkpoint = harness.MapBacked(0x10000, ReadWrite);
        var output = harness.MapBacked(0x10000, ReadWrite);
        harness.Write(vertices, Triangle(-1f, -1f, 3f, -1f, -1f, 3f));
        harness.Write(args, [2, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0]);
        harness.Write(output, new byte[4]);
        var host = (ICommandStreamHost)presenter.Instance;
        var translation = AgcExports.CreateCommandStreamTranslation(host.Memory, host);
        presenter.SetField("_translation", translation);
        var provider = new FixedProgramProvider((IShaderPipelineHost)presenter.Instance, vertices);
        var banks = Banks(RegisterWords.Color(target, Size, Size));
        banks.Context.DepthCountControl = 0x100;
        GuestGpuMemoryHook.Attach(harness.Gpu);
        try
        {
            var (buffer, intermediate) = presenter.Run(() =>
            {
                // Warm the target and pipeline so a worker can record a real draw in the full prefix.
                new RenderExecutor(presenter.RenderHost, provider).DrawAuto(1, banks, Draw());
                Assert.True(host.ResetOcclusionCounter(0));
                var result = harness.Cache.GetBuffer(harness.Cache.FindBuffer(output, 4));
                var middle = harness.Cache.GetBuffer(harness.Cache.FindBuffer(checkpoint, 4));
                var stage = ShortSliceTests.MakeAtomicAddStage(presenter, output, out var program);
                var compute = new ComputeProgram { Program = program, Input = new ComputeInputInfo
                    { Stage = stage, ThreadsX = 1, ThreadsY = 1, ThreadsZ = 1 } };
                var sink = new SliceDrawSink();
                var resolve = new RenderExecutor(presenter.RenderHost, provider, sink);
                using var cutter = new ShortSliceTests.Cutter(presenter, record: item =>
                {
                    if (item is AgcExports.GlobalBarrierWork) host.EmitGlobalBarrier();
                    else translation.RunDeferred(item);
                });
                cutter.BindToPresenter();
                void Add(string type, object record) => cutter.Add((AgcExports.DeferredWork)Activator.CreateInstance(
                    typeof(AgcExports.CommandStreamTranslation).GetNestedType(type, BindingFlags.NonPublic)!, record)!);
                void Pair(ulong id)
                {
                    resolve.DrawAuto(id, banks, Draw());
                    Add("ResolvedDrawWork", sink.Draw);
                    Add("ResolvedDispatchWork", new ResolvedDispatch { Sequence = (long)id,
                        Program = compute, HasStorageWrites = true, IndirectArgumentsAddress = args });
                }
                var observations = new List<byte>();
                Pair(2);
                if (fullPrefix)
                    for (var i = 0; i < 6; i++) cutter.Add(AgcExports.GlobalBarrierWork.Instance);
                cutter.Write(label, 1);
                cutter.AsProducer(host.FlushSlice);
                Pair(3);
                cutter.Write(label, 2);
                cutter.AsProducer(host.FlushSlice);
                cutter.Add(AgcExports.GlobalBarrierWork.Instance);
                cutter.Call(() => observations.Add(3));
                void RespondToLabel()
                {
                    var current = harness.Read(label, 1)[0];
                    if (current == 0 || observations.Count >= current) return;
                    observations.Add(current);
                    Assert.True(host.DumpOcclusionCounter(0, samples + (ulong)(current - 1) * 8, 1, 16));
                    if (current != 1) return;
                    middle.CopyFrom(harness.Scheduler.Current, result, result.Offset(output), middle.Offset(checkpoint), 4,
                        AccessFlags.MemoryWriteBit, AccessFlags.None, AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit, AccessFlags.HostReadBit);
                    // Model the guest supplying fresh arguments after observing the label. No
                    // command-thread store to args exists for a speculative read overlay to find.
                    Assert.True(harness.Memory.TryWrite(args, new byte[] { 7, 0, 0, 0 }));
                }
                try
                {
                    foreach (var slice in cutter.Cut()) Assert.True(slice.Done.Wait(TimeSpan.FromSeconds(10)));
                    Assert.Equal(fullPrefix ? 16 : 10, cutter.Run(betweenItems: RespondToLabel));
                    Assert.Equal(new byte[] { 1, 2, 3 }, observations);
                    harness.Scheduler.Finish();
                    harness.Scheduler.WaitForAllPriorityOperations();
                    harness.Scheduler.RunCompletedOperations();
                }
                finally { presenter.SetField("_commandThread", null!); }
                return (result, middle);
            });
            const ulong ready = 1UL << 63;
            var counts = harness.Read(samples, 16);
            Assert.Equal(ready | Size * Size, BitConverter.ToUInt64(counts));
            Assert.Equal(ready | (2UL * Size * Size), BitConverter.ToUInt64(counts, 8));
            Assert.Equal(2u, BitConverter.ToUInt32(harness.ReadBack(intermediate, intermediate.Offset(checkpoint), 4)));
            Assert.Equal(9u, BitConverter.ToUInt32(harness.ReadBack(buffer, buffer.Offset(output), 4)));
        }
        finally { GuestGpuMemoryHook.Attach(null); }
        harness.Shutdown();
    }
}
