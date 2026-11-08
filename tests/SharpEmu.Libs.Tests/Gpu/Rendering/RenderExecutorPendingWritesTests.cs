// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Rendering.RenderExecutorFixtures;

namespace SharpEmu.Libs.Tests.Gpu.Rendering;

[Collection(SchedulingStateCollection.Name)]
public sealed class RenderExecutorPendingWritesTests : IDisposable
{
    private sealed class CapturingSink : IResolvedCommandSink
    {
        private long _sequence;

        public long NextSequence() => ++_sequence;

        public List<ResolvedDraw> Draws { get; } = [];

        public List<ResolvedDispatch> Dispatches { get; } = [];

        public void Record(ResolvedDraw record) => Draws.Add(record);

        public void Record(ResolvedDispatch record) => Dispatches.Add(record);
    }

    private sealed class TextureFrontend(TextureRequestResolution resolution) : IShaderFrontend
    {
        public ulong CacheDomain => 1;

        public int Resolutions { get; private set; }

        public TextureRequestResolution ResolveTexture(ReadOnlySpan<uint> words, in ShaderImageShape shape, ulong shaderHash, int imageIndex)
        {
            Resolutions++;
            return resolution;
        }

        public RegisteredShader ReadShader(CpuContext context, ulong codeAddress, string label, ReadOnlySpan<uint> userData = default) =>
            throw new NotSupportedException();

        public bool TryDecode(CpuContext context, RegisteredShader shader, out Gen5ShaderProgram program, out string error) =>
            throw new NotSupportedException();
    }

    private const ulong WrittenBuffer = RecordingRenderHost.MemoryBase + 0x70_0000;
    private readonly RecordingRenderHost _host = new();
    private readonly FakePipelineProvider _pipelines = new();
    private readonly CapturingSink _sink = new();
    private readonly PendingGpuWrites _pending = new();
    private readonly RenderExecutor _executor;
    private readonly FatalScope _fatal = new();

    public RenderExecutorPendingWritesTests() => _executor = new RenderExecutor(_host, _pipelines, _sink, _pending);

    public void Dispose() => _fatal.Dispose();

    private static BufferResourceInfo Written => new(false, true, false, false, false, 4, 0);

    [Fact]
    public void ADrawMarksItsTargetAndItsWrittenBufferUntilTheRecorderFinishesIt()
    {
        _pipelines.Graphics = Programs(
            vertexBuffers: [new VertexInputBuffer(VertexBase, 16, 4)],
            pixelStage: Stage(Program(ShaderStageKind.Pixel, buffers: [Written]), buffers: [BufferDescriptor(WrittenBuffer, 4, 16)]));
        _executor.DrawIndexed(7, Banks(), Indexed(6));

        var record = Assert.Single(_sink.Draws);
        Assert.True(_pending.TryGetPending(WrittenBuffer + 0x20, 4, out var sequence));
        Assert.Equal(record.Sequence, sequence);
        Assert.True(_pending.TryGetPending(0x1_0000_0000, 4, out _));
        Assert.False(_pending.TryGetPending(WrittenBuffer + 0x40, 4, out _));
        Assert.False(_pending.TryGetPending(VertexBase, 4, out _));

        new RenderRecorder(_host, _pipelines, strictDrawResources: true, _pending).Record(record);

        Assert.False(_pending.TryGetPending(WrittenBuffer, 4, out _));
        Assert.False(_pending.TryGetPending(0x1_0000_0000, 4, out _));
        Assert.Equal(sequence, _pending.Released);
    }

    [Fact]
    public void ADeviceAddressProgramMarksOnlyItsWrittenRanges()
    {
        var program = Program(ShaderStageKind.Vertex, usesDeviceAddresses: true);
        var snapshot = new ResourceSnapshot
        {
            DeviceAddressRanges =
            [
                new DeviceAddressRange(1, 0x7_1234_5000, 0x1000, Planned: true, Written: true),
                new DeviceAddressRange(2, 0x7_2000_0000, 0x1000, Planned: true, Written: false),
            ],
        };
        _pipelines.Graphics = Programs(vertexStage: new ShaderStageResources(program, snapshot));
        _executor.DrawAuto(3, Banks(), Auto(3));

        Assert.True(_pending.TryGetPending(0x7_1234_5678, 4, out var sequence));
        Assert.Equal(Assert.Single(_sink.Draws).Sequence, sequence);
        Assert.False(_pending.TryGetPending(0x7_2000_0000, 4, out _));
        Assert.False(_pending.TryGetPending(0x7_FFFF_0000, 4, out _));
    }

    [Fact]
    public void ADispatchMarksItsWrittenBufferAndADispatchWithoutAProgramMarksNothing()
    {
        _pipelines.Compute = ComputeProgram(Stage(Program(ShaderStageKind.Compute, buffers: [Written]), buffers: [BufferDescriptor(WrittenBuffer, 4, 16)]));
        _executor.Dispatch(1, Banks(), 1, 1, 1, 0x41);
        Assert.True(_pending.TryGetPending(WrittenBuffer, 0x40, out var sequence));
        Assert.Equal(Assert.Single(_sink.Dispatches).Sequence, sequence);

        var banks = Banks();
        banks.Shader.Compute.Address = 0;
        _executor.Dispatch(2, banks, 1, 1, 1, 0x41);
        Assert.Equal(2, _sink.Dispatches.Count);
        Assert.Equal(2, _sink.Dispatches[1].Sequence);
        Assert.False(_pending.TryGetPending(WrittenBuffer + 0x40, 4, out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StorageImageWritesUseTheFrontendFootprintAndPreserveItsGuestAddress(bool dispatch)
    {
        const ulong address = 0x170008000;
        var words = RegisterWords.Texture(address, GuestPixelFormat.Bits11_11_10Float, 256, 256, tile: GuestTileMode.RenderTarget);
        var original = words.ToArray();
        var shape = new ShaderImageShape(false, false, false, true, false, TextureNumericClass.Float);
        var aligned = RegisterWords.Texture(address + 0x8000, GuestPixelFormat.Bits11_11_10Float, 256, 256, tile: GuestTileMode.RenderTarget);
        var resolution = ImageRequestBuilders.Texture(aligned, shape);
        var request = resolution.Request;
        request.Description.Data = request.Description.Data with { Address = address };
        var frontend = new TextureFrontend(resolution with { Request = request });
        var image = new ImageResource
        {
            ResourceClass = SharpEmu.ShaderCompiler.Resources.ImageResourceClass.Storage,
            NumericClass = ImageNumericClass.Float,
            Dimension = ImageDimension.Dim2D,
            Written = true,
        };
        var program = new ShaderProgramInfo
        {
            Stage = dispatch ? ShaderStageKind.Compute : ShaderStageKind.Pixel,
            Frontend = frontend,
            Images = [new ImageResourceInfo(SharpEmu.Libs.Gpu.Rendering.ImageResourceClass.Storage, true)],
            Resources = new SpecializedResourceInfo { Info = new ShaderResourceInfo { Images = [image] } },
        };
        var stage = new ShaderStageResources(program, new ResourceSnapshot { Images = [words] });
        if (dispatch)
        {
            _pipelines.Compute = ComputeProgram(stage);
            _executor.Dispatch(1, Banks(), 1, 1, 1, 0x41);
        }
        else
        {
            _pipelines.Graphics = Programs(pixelStage: stage);
            _executor.DrawAuto(1, Banks(), Auto(3));
        }

        Assert.Equal(1, frontend.Resolutions);
        Assert.True(_pending.TryGetPending(address, request.Description.Data.Size, out var sequence));
        Assert.Equal(1, sequence);
        Assert.False(_pending.TryGetPending(address - 1, 1, out _));
        Assert.False(_pending.TryGetPending(address + request.Description.Data.Size, 1, out _));
        Assert.Equal(original, words);
    }

    [Fact]
    public void TheInThreadRecorderLeavesNothingPendingBehindADraw()
    {
        var host = new RecordingRenderHost();
        var pipelines = new FakePipelineProvider
        {
            Graphics = Programs(pixelStage: Stage(Program(ShaderStageKind.Pixel, buffers: [Written]), buffers: [BufferDescriptor(WrittenBuffer, 4, 16)])),
        };
        var executor = new RenderExecutor(host, pipelines);
        executor.DrawAuto(1, Banks(), Auto(3));

        Assert.False(executor.PendingWrites.TryGetPending(WrittenBuffer, 4, out _));
        Assert.Equal(1, executor.PendingWrites.Released);
    }
}
