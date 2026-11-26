// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Gpu.Vulkan;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.VideoOut;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed partial class ShortSliceTests
{
    private sealed class InterruptProbe(Action<int, uint> observe) : IEndOfPipeSink
    {
        public void TriggerInterrupt(int eventId, uint contextId) => observe(eventId, contextId);
    }

    [Fact]
    public void StealingAndSplittingMixedSlicesExecutesEveryStoreAndCallOnce()
    {
        if (!GatePrerequisites.Ready(fixture.Vulkan)) return;
        using var presenter = new PresenterUnderTest(fixture.Vulkan!);
        var label = presenter.Harness.MapBacked(0x10000, GuestPageProtection.Read | GuestPageProtection.Write);
        presenter.Run(() =>
        {
            using var cutter = new Cutter(presenter);
            for (var first = -80; first < 0; first++) cutter.Add(first);
            cutter.BetweenSegments(() => cutter.Executed.Add(161));
            for (var i = 0; i < 80; i++)
            {
                cutter.Add(2 * i, dispatch: true);
                cutter.Write(label, (byte)i);
                cutter.BetweenSegments(() => cutter.Executed.Add(2 * presenter.Harness.Read(label, 1)[0] + 1));
            }
            cutter.Call(() => cutter.Executed.Add(160));
            Assert.Equal(80, cutter.Cut().Sum(slice => slice.Count));
            var firstPass = cutter.Run(1);
            Assert.InRange(firstPass, 1, 80);
            Assert.Equal(Enumerable.Range(-80, firstPass), cutter.Executed);
            Assert.Equal(322 - firstPass, cutter.Run());
            Assert.Equal(Enumerable.Range(-80, 80).Append(161).Concat(Enumerable.Range(0, 161)), cutter.Executed);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CarriedStoresSupplyEachIndirectDispatchAtItsOwnSequence(bool refuseSecond)
    {
        if (!GatePrerequisites.Ready(fixture.Vulkan, shaderInt64: true)) return;
        using var fatal = new FatalScope();
        using var presenter = new PresenterUnderTest(fixture.Vulkan!);
        presenter.SetField("_minStorageBufferOffsetAlignment", 256UL);
        presenter.LoadRenderingCommands();
        var harness = presenter.Harness;
        var args = harness.MapBacked(0x10000, GuestPageProtection.Read | GuestPageProtection.Write);
        var output = harness.MapBacked(0x10000, GuestPageProtection.Read | GuestPageProtection.Write);
        harness.Write(args, [2, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0]);
        harness.Write(output, new byte[4]);
        var host = (ICommandStreamHost)presenter.Instance;
        var translation = AgcExports.CreateCommandStreamTranslation(host.Memory, host);
        presenter.SetField("_translation", translation);
        var interrupts = new System.Collections.Concurrent.ConcurrentQueue<(int, uint)>();
        presenter.SetField("_endOfPipe", new EndOfPipe(new InterruptProbe((id, context) => interrupts.Enqueue((id, context))),
            (IFlipSubmitter)presenter.Instance));
        var buffer = presenter.Run(() =>
        {
            var stage = MakeAtomicAddStage(presenter, output, out var program);
            var result = harness.Cache.GetBuffer(harness.Cache.FindBuffer(output, 4));
            var type = typeof(AgcExports.CommandStreamTranslation).GetNestedType("ResolvedDispatchWork", BindingFlags.NonPublic)!;
            AgcExports.DeferredWork Dispatch(long sequence, bool permute)
            {
                var input = new ComputeInputInfo { Stage = stage, ThreadsX = 1, ThreadsY = 1, ThreadsZ = 1, GroupIdY = permute };
                return (AgcExports.DeferredWork)Activator.CreateInstance(type, new ResolvedDispatch
                {
                    Sequence = sequence, IndirectArgumentsAddress = args, HasStorageWrites = true,
                    Program = new ComputeProgram { Program = program, Input = input },
                })!;
            }
            var inlineDispatches = 0;
            using var cutter = new Cutter(presenter, record: work =>
            {
                if (work is AgcExports.GlobalBarrierWork) host.EmitGlobalBarrier();
                else
                {
                    inlineDispatches++;
                    translation.RunDeferred(work);
                }
            });
            cutter.BindToPresenter();
            var calls = new List<(byte, long)>();
            void Observe()
            {
                Assert.Equal(17UL, presenter.GetField<SubmissionContext>("_submissionContext").SubmissionId);
                calls.Add((harness.Read(args, 1)[0], translation.PendingWrites!.Released));
            }
            cutter.Add(Dispatch(1, false));
            for (var i = 0; i < 7; i++) cutter.Add(AgcExports.GlobalBarrierWork.Instance);
            cutter.Write(args, 7);
            cutter.AsProducer(() => host.BeginSubmission(1, 17, null));
            cutter.BetweenSegments(Observe);
            for (var i = 0; i < 7; i++) cutter.Add(AgcExports.GlobalBarrierWork.Instance);
            cutter.Add(Dispatch(5, refuseSecond));
            cutter.Write(args, 11);
            cutter.BetweenSegments(Observe);
            cutter.AsProducer(() =>
            {
                host.RecordEndOfPipe(new(EndOfPipeWriteKind.Write32, 17, args, 11));
                host.FlushSlice();
            });
            cutter.Add(AgcExports.GlobalBarrierWork.Instance);
            cutter.Add(AgcExports.GlobalBarrierWork.Instance);
            cutter.AsProducer(() => host.RecordEndOfPipe(new(EndOfPipeWriteKind.InterruptOnly, 17, EventId: 3, ContextId: 9)));
            cutter.Add(AgcExports.GlobalBarrierWork.Instance);
            cutter.Call(() => calls.Add((0, 0)));
            var slices = cutter.Cut();
            var workers = presenter.GetField<object?>("_workers") is not null;
            Assert.Equal(workers ? new[] { 8, 8 } : [], slices.Select(slice => slice.Count));
            foreach (var slice in slices) Assert.True(slice.Done.Wait(TimeSpan.FromSeconds(10)));
            Assert.Empty(calls);
            Assert.Equal(2, harness.Read(args, 1)[0]);
            try
            {
                Assert.Equal(28, cutter.Run());
                Assert.Equal(new (byte, long)[] { (7, 1), (11, 5), (0, 0) }, calls);
                Assert.Equal(workers ? (refuseSecond ? 1 : 0) : 2, inlineDispatches);
            }
            finally
            {
                harness.Scheduler.Finish();
                harness.Scheduler.WaitForAllPriorityOperations();
                presenter.SetField("_commandThread", null!);
            }
            return result;
        });
        // Each invocation atomically increments one word. The first dispatch must read 2,
        // the second 7, and neither may see the final 11, even when both recorded ahead.
        Assert.Equal(9u, BitConverter.ToUInt32(harness.ReadBack(buffer, buffer.Offset(output), 4)));
        Assert.Equal(new[] { (3, 9u) }, interrupts);
        harness.Shutdown();
        fixture.Vulkan!.AssertNoValidationMessages();
    }

    internal static ShaderStageResources MakeAtomicAddStage(PresenterUnderTest presenter, ulong output, out ShaderProgram program)
    {
        var decoded = Program(MoveVector(0, 4, 1), BufferAtomicAdd(8, 0), EndProgram(16));
        var plan = Extract(decoded, userDataCount: 4);
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        uint[] words = [(uint)output, (uint)(output >> 32), sizeof(uint), 0];
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs(words), ref snapshot, ref specialization));
        var resources = ResourceMaterializer.ApplyTo(plan, specialization);
        var layout = BindingLayout.Allocate(resources.Info, [0, 1, 2, 3], false, false, false, 0, false);
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 1, LocalSizeY = 1, LocalSizeZ = 1 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        var info = new ShaderProgramInfo { Stage = ShaderStageKind.Compute, Hash = Hash, UserDataCount = 4, Resources = resources, Bindings = layout };
        var pipelines = (IShaderPipelineHost)presenter.Instance;
        program = new ShaderProgram(3, pipelines.CreateShaderModule(new VulkanCompiledGuestShader(shader.Spirv), ShaderStage.Compute, Hash, 3));
        return new ShaderStageResources(info, snapshot);
    }
}
