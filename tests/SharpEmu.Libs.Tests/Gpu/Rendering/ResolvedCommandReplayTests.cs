// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Rendering.RenderExecutorFixtures;

namespace SharpEmu.Libs.Tests.Gpu.Rendering;

[Collection(SchedulingStateCollection.Name)]
public sealed class ResolvedCommandReplayTests : IDisposable
{
    private sealed class CapturingSink : IResolvedCommandSink
    {
        private long _sequence;

        public long NextSequence() => ++_sequence;

        public List<object> Records { get; } = [];

        public void Record(ResolvedDraw record) => Records.Add(record);

        public void Record(ResolvedDispatch record) => Records.Add(record);
    }

    private readonly FatalScope _fatal = new();

    public void Dispose() => _fatal.Dispose();

    private static (RecordingRenderHost Host, FakePipelineProvider Pipelines) Arrange()
    {
        var pipelines = new FakePipelineProvider
        {
            Graphics = Programs(vertexBuffers: [new VertexInputBuffer(VertexBase, 16, 4)]),
            Compute = ComputeProgram(Stage(Program(ShaderStageKind.Compute, buffers: [new BufferResourceInfo(false, true, false, false, false, 4, 0)]),
                buffers: [BufferDescriptor(VertexBase, 4, 16)])),
        };
        return (new RecordingRenderHost(), pipelines);
    }

    private static void Run(RenderExecutor executor, RegisterBanks banks)
    {
        executor.DrawIndexed(7, banks, Indexed(6, instances: 2, baseVertex: 3));
        executor.DrawAuto(8, banks, Auto(3));
        executor.Dispatch(9, banks, 1, 1, 1, 0x41);
    }

    [Fact]
    public void RecordsReplayedAfterTheBanksWereClearedMakeTheSameHostCalls()
    {
        var (direct, directPipelines) = Arrange();
        Run(new RenderExecutor(direct, directPipelines), Banks());

        var (host, pipelines) = Arrange();
        var sink = new CapturingSink();
        var banks = Banks();
        Run(new RenderExecutor(host, pipelines, sink), banks);
        Assert.Empty(host.Calls);
        Assert.Equal(3, sink.Records.Count);
        Assert.Equal([1L, 2L, 3L], sink.Records.Select(static record => record is ResolvedDraw draw ? draw.Sequence : ((ResolvedDispatch)record).Sequence));

        banks.Reset();
        var recorder = new RenderRecorder(host, pipelines, strictDrawResources: true);
        foreach (var record in sink.Records)
        {
            if (record is ResolvedDraw draw)
            {
                recorder.Record(draw);
            }
            else
            {
                recorder.Record((ResolvedDispatch)record);
            }
        }

        Assert.NotEmpty(host.Calls);
        Assert.Equal(direct.Calls, host.Calls);
        Assert.Equal(directPipelines.Calls.Order(), pipelines.Calls.Order());
        Assert.Equal(["get_compute_program threadDimensions=False", "get_graphics_programs pixelActive=True", "get_graphics_programs pixelActive=True"],
            pipelines.Calls.Take(3).Order());
    }

    [Fact]
    public void ARecordRecordedASecondTimeMakesTheSameHostCalls()
    {
        var (host, pipelines) = Arrange();
        var sink = new CapturingSink();
        var banks = Banks(withDepth: true);
        banks.Context.ColorTargets[1] = SharpEmu.Libs.Tests.Gpu.Images.RegisterWords.Color(SecondColorBase, 64, 64);
        banks.Context.RenderTargetMask = 0xFF;
        new RenderExecutor(host, pipelines, sink).DrawIndexed(7, banks, Indexed(6));
        var draw = Assert.IsType<ResolvedDraw>(Assert.Single(sink.Records));

        new RenderRecorder(host, pipelines, strictDrawResources: true, returnsRecords: false).Record(draw);
        var first = host.Calls.ToList();
        host.Calls.Clear();
        new RenderRecorder(host, pipelines, strictDrawResources: true, returnsRecords: false).Record(draw);

        static string[] Targets(IEnumerable<string> calls) =>
            calls.Where(static call => call.StartsWith("find_image", StringComparison.Ordinal) || call.StartsWith("acquire_", StringComparison.Ordinal) ||
                call.StartsWith("begin_rendering", StringComparison.Ordinal) || call.StartsWith("draw_indexed", StringComparison.Ordinal)).ToArray();
        Assert.Contains(first, call => call.StartsWith("begin_rendering", StringComparison.Ordinal));
        Assert.Equal(Targets(first), Targets(host.Calls));
    }

    [Fact]
    public void AResetRecordStartsFromAnEmptyDrawState()
    {
        var record = new ResolvedDraw();
        record.State.ColorCount = 3;
        record.State.DepthResolved = true;
        record.State.PixelActive = false;
        record.State.StartInstanceUserData = 4;
        record.State.Programs = Programs();
        record.State.Rendering.ColorAttachmentCount = 3;

        record.Reset();

        Assert.Equal(0u, record.State.ColorCount);
        Assert.False(record.State.DepthResolved);
        Assert.False(record.State.Depth.HasTarget);
        Assert.True(record.State.PixelActive);
        Assert.Equal(-1, record.State.StartInstanceUserData);
        Assert.Null(record.State.Programs);
        Assert.Equal(0u, record.State.Rendering.ColorAttachmentCount);
    }

    private sealed class FixedPrograms(GraphicsPrograms graphics) : IShaderPipelineProvider
    {
        public GraphicsPrograms GetGraphicsPrograms(RegisterBanks banks, ReadOnlySpan<SharpEmu.Libs.Gpu.Images.ColorComponentMap> targetExportMapping, bool pixelActive,
            bool depthBound) => graphics;

        public PipelineHandle CreateGraphicsPipeline(ReadOnlySpan<ColorTargetState> colors, in DepthAttachmentState depth, VertexInputInfo vertexInput,
            PixelInputInfo? pixelInput, ContextRegisters context, in RenderingState rendering, Silk.NET.Vulkan.PrimitiveTopology topology, bool primitiveRestartEnabled,
            bool disableBlending, ShaderProgram vertexProgram, ShaderProgram pixelProgram) => throw new InvalidOperationException();

        public ComputeProgram GetComputeProgram(ComputeStageRegisters compute, ShaderInterfaceRegisters shaderInterface, uint dispatchInitiator, uint dimensionX,
            uint dimensionY, uint dimensionZ) => throw new InvalidOperationException();

        public PipelineHandle CreateComputePipeline(ComputeInputInfo input, ShaderProgram program) => throw new InvalidOperationException();
    }

    private sealed class ReturningSink(RecordPool pool) : IResolvedCommandSink
    {
        private long _sequence;

        public long NextSequence() => ++_sequence;

        public void Record(ResolvedDraw record) => pool.Return(record);

        public void Record(ResolvedDispatch record) => pool.Return(record);
    }

    [Fact]
    public void ADrawBetweenDepthControlWritesAllocatesOnlyTheContextCopyOfTheWrite()
    {
        var (host, pipelines) = Arrange();
        var pool = new RecordPool();
        var executor = new RenderExecutor(host, new FixedPrograms(pipelines.Graphics), new ReturningSink(pool), pool: pool);
        var banks = Banks(withDepth: true);
        var arguments = Indexed(6);
        var depthControl = new uint[1];
        var packet = new SharpEmu.Libs.Gpu.GpuCommands.PacketContext(
            SharpEmu.Libs.Gpu.GpuCommands.PacketHeader.Make(3, SharpEmu.Libs.Gpu.GpuCommands.PacketOpcode.SetContextRegister), 0x1_0000_1000, 0, 100, 100);
        void Draw(int index)
        {
            depthControl[0] = 0x70u | (uint)(index & 1);
            RegisterWriteTable.WriteContextPacket(banks, in packet, ContextRegisterOffset.DbDepthControl, depthControl);
            executor.DrawIndexed(7, banks, in arguments);
        }

        var allocated = AllocationMeasurement.SteadyState(
            () =>
            {
                for (var index = 0; index < 8; index++)
                {
                    Draw(index);
                }
            },
            () =>
            {
                for (var index = 0; index < 64; index++)
                {
                    Draw(index);
                }
            });

        var contextCopy = AllocationMeasurement.SteadyState(null, () => banks.Context.ShallowCopy());
        Assert.Equal(64 * contextCopy, allocated);
    }
}
