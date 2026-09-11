// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Metal;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Metal;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Metal;

// The executor over the Metal host with a recording backend: the copied draw record and its per-draw bookkeeping.
[Collection(SchedulingStateCollection.Name)]
public sealed class MetalRenderHostTests : IDisposable
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const int MemorySize = 0x10_0000;
    private const ulong ColorBase = MemoryBase;
    private const ulong VertexBase = MemoryBase + 0x4_0000;
    private const ulong IndexBase = MemoryBase + 0x5_0000;
    private const ulong PixelGlobal = MemoryBase + 0x6_0000;
    private const ulong VertexGlobal = MemoryBase + 0x7_0000;
    private const uint Size = 64;
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.NonPublic;

    private readonly FatalScope _fatal = new();
    private readonly FakeCpuMemory _memory = new(MemoryBase, MemorySize);
    private readonly RecordingBackend _backend = new();
    private readonly MetalCommandStreamHost _host;
    private readonly RegisterBanks _banks;

    public MetalRenderHostTests()
    {
        _host = new MetalCommandStreamHost(_memory, _backend, _backend);
        var queue = new CommandStreamQueue(_host);
        _host.AttachQueue(queue);
        _host.BeginSubmission(0, 1, null);
        _banks = queue.GetInterpreter(0).TypedRegisters;
        var context = _banks.Context;
        context.ColorTargets[0] = RegisterWords.Color(ColorBase, Size, Size);
        context.RenderTargetMask = 0xF;
        context.ShaderInterface.ColorShaderMask = 0xF;
        context.ScreenViewport.Viewports[0] = new ViewportRegisters { XScale = 32, XOffset = 32, YScale = 32, YOffset = 32, ZScale = 0.5f, ZOffset = 0.5f, MaxDepth = 1 };
        _banks.Shader.Vertex.ExportAddress = 0x1000;
        _banks.Shader.Pixel.Address = 0x2000;
        _banks.Shader.Compute.Address = 0x3000;
        _banks.Shader.Compute.ThreadsX = 64;
        _banks.Shader.Compute.ThreadsY = 1;
        _banks.Shader.Compute.ThreadsZ = 1;
        _banks.UserConfig.PrimitiveType = 4;
    }

    public void Dispose() => _fatal.Dispose();

    private static MetalCompiledGuestShader Shader(string name, Gen5MslStage stage) =>
        new(new Gen5MslShader($"// {name}", name, stage, [], [], AttributeCount: 0, []));

    private static GuestDrawTexture Texture(ulong address) => new(address, 4, 4, 0, 0, [], false, false);

    private static GuestMemoryBuffer Global(ulong address) => new(address, [], 0, 16, Pooled: false);

    // A pixel stage with one global, one scalar block and one image, then a vertex stage with the same, laid out flat.
    private sealed class TwoStageProvider : IShaderPipelineProvider
    {
        public GuestMemoryBuffer PixelScalars { get; } = new(0, new byte[16], 16, 16, Pooled: false);

        public GuestMemoryBuffer VertexScalars { get; } = new(0, new byte[16], 16, 16, Pooled: false);

        public GuestDrawTexture PixelTexture { get; } = Texture(0x10_0000);

        public GuestDrawTexture VertexTexture { get; } = Texture(0x20_0000);

        public GraphicsPrograms GetGraphicsPrograms(VertexStageRegisters vertex, PixelStageRegisters pixel, ShaderInterfaceRegisters shaderInterface, ContextRegisters context, ReadOnlySpan<ColorComponentMap> targetExportMapping, bool pixelActive)
        {
            var pixelProgram = new AgcExports.CompiledStageProgram
            {
                Shader = Shader("pixel", Gen5MslStage.Pixel),
                Stage = ShaderStageKind.Pixel,
                Hash = 2,
                Address = 0x2000,
                Textures = [PixelTexture],
                GlobalBuffers = [Global(PixelGlobal)],
                ScalarBuffer = PixelScalars,
                GlobalBufferBase = 0,
                ImageBindingBase = 0,
                TotalGlobalBuffers = 4,
                ScalarBufferIndex = 2,
            };
            var vertexProgram = new AgcExports.CompiledStageProgram
            {
                Shader = Shader("vertex", Gen5MslStage.Vertex),
                Stage = ShaderStageKind.Vertex,
                Hash = 1,
                Address = 0x1000,
                Textures = [VertexTexture],
                GlobalBuffers = [Global(VertexGlobal)],
                ScalarBuffer = VertexScalars,
                GlobalBufferBase = 1,
                ImageBindingBase = 1,
                TotalGlobalBuffers = 4,
                ScalarBufferIndex = 3,
                VertexAttributes = [new AgcExports.VertexAttributeLayout(0, 0, 2, 11, 7, 0, false)],
            };
            return new GraphicsPrograms
            {
                Vertex = new ShaderProgram(1),
                Pixel = new ShaderProgram(2),
                VertexInput = new VertexInputInfo
                {
                    Buffers = [new VertexInputBuffer(VertexBase, 8, 3)],
                    Stage = new ShaderStageResources(vertexProgram, new ResourceSnapshot()),
                },
                PixelInput = new PixelInputInfo { InputCount = 1, Stage = new ShaderStageResources(pixelProgram, new ResourceSnapshot()) },
            };
        }

        public PipelineHandle CreateGraphicsPipeline(ReadOnlySpan<ColorTargetState> colors, in DepthAttachmentState depth, VertexInputInfo vertexInput, PixelInputInfo? pixelInput, ContextRegisters context, in RenderingState rendering, PrimitiveTopology topology, bool primitiveRestartEnabled, ShaderProgram vertexProgram, ShaderProgram pixelProgram) =>
            Factory!.CreateGraphicsPipeline(colors, in depth, vertexInput, pixelInput, context, in rendering, topology, primitiveRestartEnabled, vertexProgram, pixelProgram);

        public ComputeProgram GetComputeProgram(ComputeStageRegisters compute, ShaderInterfaceRegisters shaderInterface, uint dispatchInitiator)
        {
            var program = new AgcExports.CompiledStageProgram
            {
                Shader = Shader("compute", Gen5MslStage.Compute),
                Stage = ShaderStageKind.Compute,
                Hash = 3,
                Address = 0x3000,
                Textures = [PixelTexture],
                GlobalBuffers = [Global(PixelGlobal)],
                ScalarBuffer = PixelScalars,
                TotalGlobalBuffers = 2,
                ScalarBufferIndex = 1,
                Buffers = [new BufferResourceInfo(false, true, false, false, false, 16, 0)],
            };
            return new ComputeProgram
            {
                Program = new ShaderProgram(3),
                Input = new ComputeInputInfo
                {
                    ThreadsX = 64,
                    ThreadsY = 1,
                    ThreadsZ = 1,
                    DispatchThreadDimensions = (dispatchInitiator & (1u << 5)) != 0,
                    GroupIdX = true,
                    ThreadIdCount = 1,
                    Stage = new ShaderStageResources(program, new ResourceSnapshot { Buffers = [[unchecked((uint)PixelGlobal), (uint)(PixelGlobal >> 32), 16, 0]] }),
                },
            };
        }

        public PipelineHandle CreateComputePipeline(ComputeInputInfo input, ShaderProgram program) => Factory!.CreateComputePipeline(input, program);

        public AgcExports.IHostPipelineFactory? Factory { get; set; }
    }

    private sealed record RecordedDraw(
        IReadOnlyList<GuestDrawTexture> Textures,
        IReadOnlyList<GuestMemoryBuffer> Globals,
        IReadOnlyList<GuestRenderTarget> Targets,
        uint VertexCount,
        uint PrimitiveType,
        GuestIndexBuffer? IndexBuffer,
        IReadOnlyList<GuestVertexBuffer>? VertexBuffers,
        GuestRenderState? RenderState,
        int BaseVertex);

    private sealed record RecordedDispatch(IReadOnlyList<GuestDrawTexture> Textures, IReadOnlyList<GuestMemoryBuffer> Globals, uint GroupsX, uint LocalX, bool WritesGlobalMemory, uint ThreadCountX);

    private static Exception Unsupported() => new NotSupportedException("The recording backend does not implement this call.");

    private sealed class RecordingBackend : IGuestGpuBackend, IGuestImageSnapshotBackend
    {
        public List<RecordedDraw> Draws { get; } = new();

        public List<RecordedDispatch> Dispatches { get; } = new();

        public string BackendName => "Recording";


        public ulong GuestStorageBufferOffsetAlignment => 16;

        public void SubmitOffscreenTranslatedDraw(IGuestCompiledShader pixelShader, IReadOnlyList<GuestDrawTexture> textures, IReadOnlyList<GuestMemoryBuffer> globalMemoryBuffers, uint attributeCount, IReadOnlyList<GuestRenderTarget> targets, IGuestCompiledShader? vertexShader = null, uint vertexCount = 3, uint instanceCount = 1, uint primitiveType = 4, GuestIndexBuffer? indexBuffer = null, IReadOnlyList<GuestVertexBuffer>? vertexBuffers = null, GuestRenderState? renderState = null, GuestDepthTarget? depthTarget = null, ulong shaderAddress = 0, int baseVertex = 0) =>
            Draws.Add(new RecordedDraw(textures, globalMemoryBuffers, targets, vertexCount, primitiveType, indexBuffer, vertexBuffers, renderState, baseVertex));

        public long SubmitComputeDispatch(ulong shaderAddress, IGuestCompiledShader computeShader, IReadOnlyList<GuestDrawTexture> textures, IReadOnlyList<GuestMemoryBuffer> globalMemoryBuffers, uint groupCountX, uint groupCountY, uint groupCountZ, uint baseGroupX, uint baseGroupY, uint baseGroupZ, uint localSizeX, uint localSizeY, uint localSizeZ, bool isIndirect, bool writesGlobalMemory, uint threadCountX = uint.MaxValue, uint threadCountY = uint.MaxValue, uint threadCountZ = uint.MaxValue)
        {
            Dispatches.Add(new RecordedDispatch(textures, globalMemoryBuffers, groupCountX, localSizeX, writesGlobalMemory, threadCountX));
            return Dispatches.Count;
        }

        public void EnsureStarted(uint width, uint height) => throw Unsupported();

        public bool TryCompileVertexShader(Gen5ShaderState state, Gen5ShaderEvaluation evaluation, out IGuestCompiledShader? shader, out string error, int globalBufferBase = 0, int totalGlobalBufferCount = -1, int imageBindingBase = 0, int scalarRegisterBufferIndex = -1, int requiredVertexOutputCount = 0, ulong storageBufferOffsetAlignment = 1) => throw Unsupported();

        public bool TryCompilePixelShader(Gen5ShaderState state, Gen5ShaderEvaluation evaluation, IReadOnlyList<Gen5PixelOutputBinding> outputs, out IGuestCompiledShader? shader, out string error, int globalBufferBase = 0, int totalGlobalBufferCount = -1, int imageBindingBase = 0, int scalarRegisterBufferIndex = -1, uint pixelInputEnable = 0, uint pixelInputAddress = 0, IReadOnlyList<uint>? pixelInputCntl = null, ulong storageBufferOffsetAlignment = 1) => throw Unsupported();

        public bool TryCompileComputeShader(Gen5ShaderState state, Gen5ShaderEvaluation evaluation, uint localSizeX, uint localSizeY, uint localSizeZ, out IGuestCompiledShader? shader, out string error, int totalGlobalBufferCount = -1, int initialScalarBufferIndex = -1, uint waveLaneCount = 32, ulong storageBufferOffsetAlignment = 1) => throw Unsupported();

        public IGuestCompiledShader GetDepthOnlyFragmentShader() => Shader("depth_only", Gen5MslStage.Pixel);

        public void HideSplashScreen() => throw Unsupported();

        public void Submit(byte[] bgraFrame, uint width, uint height) => throw Unsupported();

        public void SubmitGuestDraw(GuestDrawKind drawKind, uint width, uint height) => throw Unsupported();

        public void SubmitTranslatedDraw(IGuestCompiledShader pixelShader, IReadOnlyList<GuestDrawTexture> textures, IReadOnlyList<GuestMemoryBuffer> globalMemoryBuffers, uint width, uint height, uint attributeCount, IGuestCompiledShader? vertexShader = null, uint vertexCount = 3, uint instanceCount = 1, uint primitiveType = 4, GuestIndexBuffer? indexBuffer = null, IReadOnlyList<GuestVertexBuffer>? vertexBuffers = null, GuestRenderState? renderState = null) => throw Unsupported();

        public void SubmitDepthOnlyTranslatedDraw(IGuestCompiledShader pixelShader, IReadOnlyList<GuestDrawTexture> textures, IReadOnlyList<GuestMemoryBuffer> globalMemoryBuffers, uint attributeCount, GuestDepthTarget depthTarget, IGuestCompiledShader? vertexShader = null, uint vertexCount = 3, uint instanceCount = 1, uint primitiveType = 4, GuestIndexBuffer? indexBuffer = null, IReadOnlyList<GuestVertexBuffer>? vertexBuffers = null, GuestRenderState? renderState = null, ulong shaderAddress = 0, int baseVertex = 0) => throw Unsupported();

        public void SubmitStorageTranslatedDraw(IGuestCompiledShader pixelShader, IReadOnlyList<GuestDrawTexture> textures, IReadOnlyList<GuestMemoryBuffer> globalMemoryBuffers, uint attributeCount, uint width, uint height, ulong shaderAddress = 0) => throw Unsupported();

        public bool TrySubmitGuestImage(int videoOutHandle, int displayBufferIndex, ulong address, uint width, uint height, uint pitchInPixel, ulong flipRequestId) => throw Unsupported();

        public void SubmitCommandStream(ICpuMemory memory, uint queue, ulong address, uint dwordCount, ulong submissionId, object? geometrySnapshots) => throw Unsupported();

        public IdleOutcome SubmitDone(ICpuMemory memory) => throw Unsupported();

        public void RegisterKnownDisplayBuffer(ulong address, uint guestFormat) => throw Unsupported();

        public bool IsGpuGuestImageAvailable(ulong address, uint format, uint numberType) => false;

        public bool TrySubmitGuestImageBlit(GuestRenderTarget source, GuestRenderTarget destination) => throw Unsupported();


        public void CountShaderCompilation() => throw Unsupported();

        public (long Draws, double DrawMs, long Pipelines, long ShaderCompilations) ReadAndResetPerfCounters() => throw Unsupported();

        public void RequestClose() => throw Unsupported();

        public bool IsGuestImageUploadKnown(ulong address, uint format, uint numberType) => false;

        public bool GuestImageWantsInitialData(ulong address) => false;

        public void ProvideGuestImageInitialData(ulong address, byte[] rgbaPixels) => throw Unsupported();

        public void SubmitGuestImageFill(ulong address, uint fillValue) => throw Unsupported();

        public void SubmitGuestImageWrite(ulong address, byte[] pixels, uint rowOffset = 0) => throw Unsupported();

        public void RequestCpuWrittenGuestImageSync(ulong scopeAddress = 0, ulong scopeByteCount = ulong.MaxValue) => throw Unsupported();

        public bool TryGetGuestImageExtent(ulong address, out uint width, out uint height, out ulong byteCount)
        {
            width = 0;
            height = 0;
            byteCount = 0;
            return false;
        }

        public IReadOnlyList<(ulong Address, uint Width, uint Height, ulong ByteCount)> GetGuestImageExtents() => [];

        public bool IsTextureContentCached(in TextureCacheLookupIdentity identity) => false;

        public void AttachGuestMemory(ICpuMemory memory)
        {
        }
    }

    private static byte[] Floats(params float[] values)
    {
        var bytes = new byte[values.Length * sizeof(float)];
        for (var index = 0; index < values.Length; index++)
        {
            BitConverter.TryWriteBytes(bytes.AsSpan(index * sizeof(float)), values[index]);
        }

        return bytes;
    }

    private RenderExecutor Executor(TwoStageProvider provider)
    {
        provider.Factory = _host;
        return new RenderExecutor(_host, provider);
    }

    private int RecordCount(string field) => ((System.Collections.ICollection)typeof(MetalCommandStreamHost).GetField(field, Members)!.GetValue(_host)!).Count;

    [Fact]
    public void Draw_BindsEveryResourceByTheProgramsFlatSlots()
    {
        var provider = new TwoStageProvider();
        var executor = Executor(provider);
        _memory.TryWrite(VertexBase, Floats(-1f, -1f, 3f, -1f, -1f, 3f));

        executor.DrawAuto(1, _banks, new DrawAutoArguments(0, 0, 3, 1, 0, 0, DrawOffsetSource.Packet));

        var draw = Assert.Single(_backend.Draws);
        Assert.Equal([PixelGlobal, VertexGlobal, 0UL, 0UL], draw.Globals.Select(global => global.BaseAddress));
        Assert.Same(provider.PixelScalars, draw.Globals[2]);
        Assert.Same(provider.VertexScalars, draw.Globals[3]);
        Assert.Equal([provider.PixelTexture, provider.VertexTexture], draw.Textures);
        Assert.Equal(ColorBase, Assert.Single(draw.Targets).Address);
        Assert.Equal(0xFu, draw.Targets[0].WriteMask);
        Assert.Equal(3u, draw.VertexCount);
        Assert.Equal(4u, draw.PrimitiveType);
        Assert.Null(draw.IndexBuffer);
        var vertex = Assert.Single(draw.VertexBuffers!);
        Assert.Equal(Floats(-1f, -1f, 3f, -1f, -1f, 3f), vertex.Data);
        Assert.Equal(VertexBase, vertex.BaseAddress);
        Assert.Equal(8u, vertex.Stride);
        var blend = Assert.Single(draw.RenderState!.Blends);
        Assert.Equal(0xFu, blend.WriteMask);
        Assert.Equal(new GuestRect(0, 0, Size, Size), draw.RenderState.Scissor);
    }

    [Fact]
    public void IndexedDraw_CopiesTheIndicesItWasRecordedWith()
    {
        var provider = new TwoStageProvider();
        var executor = Executor(provider);
        _memory.TryWrite(VertexBase, Floats(-1f, -1f, 3f, -1f, -1f, 3f));
        _memory.TryWrite(IndexBase, new byte[] { 2, 0, 1, 0, 0, 0 });

        executor.DrawIndexed(1, _banks, new DrawIndexedArguments(0, 0, 3, IndexBase, 0, 1, 5, 0, DrawOffsetSource.Packet));

        var draw = Assert.Single(_backend.Draws);
        var indices = Assert.IsType<GuestIndexBuffer>(draw.IndexBuffer);
        Assert.Equal(new byte[] { 2, 0, 1, 0, 0, 0 }, indices.Data);
        Assert.False(indices.Is32Bit);
        Assert.Equal(IndexBase, indices.GuestAddress);
        Assert.Equal(5, draw.BaseVertex);
    }

    [Fact]
    public void EightBitIndices_ArriveExpandedFromTheUploadedBytes()
    {
        var provider = new TwoStageProvider();
        var executor = Executor(provider);
        _memory.TryWrite(VertexBase, Floats(-1f, -1f, 3f, -1f, -1f, 3f));
        _memory.TryWrite(IndexBase, new byte[] { 2, 0, 1 });

        executor.DrawIndexed(1, _banks, new DrawIndexedArguments(0, 0, 3, IndexBase, 2, 1, 0, 0, DrawOffsetSource.Packet));

        var indices = Assert.IsType<GuestIndexBuffer>(Assert.Single(_backend.Draws).IndexBuffer);
        Assert.Equal(new byte[] { 2, 0, 0, 0, 1, 0 }, indices.Data);
        Assert.Equal(0UL, indices.GuestAddress);
    }

    [Fact]
    public void EveryDrawAndDispatch_ReleasesItsRecordsAtTheReset()
    {
        var provider = new TwoStageProvider();
        var executor = Executor(provider);
        _memory.TryWrite(VertexBase, Floats(-1f, -1f, 3f, -1f, -1f, 3f));

        for (var index = 0; index < 3; index++)
        {
            executor.DrawAuto((ulong)index, _banks, new DrawAutoArguments(0, 0, 3, 1, 0, 0, DrawOffsetSource.Packet));
            executor.Dispatch((ulong)index, _banks, 4, 1, 1, 0x1);
        }

        Assert.Equal(3, _backend.Draws.Count);
        Assert.Equal(3, _backend.Dispatches.Count);
        Assert.Equal(0, RecordCount("_buffers"));
        Assert.Equal(0, RecordCount("_pipelines"));
        Assert.Equal(0, RecordCount("_committedPrograms"));
        Assert.Equal(0, RecordCount("_colorTargets"));
    }

    [Fact]
    public void Dispatch_SubmitsTheFlatSlotsAndTheThreadLimits()
    {
        var provider = new TwoStageProvider();
        var executor = Executor(provider);

        executor.Dispatch(1, _banks, 128, 1, 1, 0x1 | (1u << 5));

        var dispatch = Assert.Single(_backend.Dispatches);
        Assert.Equal([PixelGlobal, 0UL], dispatch.Globals.Select(global => global.BaseAddress));
        Assert.Same(provider.PixelScalars, dispatch.Globals[1]);
        Assert.Equal([provider.PixelTexture], dispatch.Textures);
        Assert.Equal(2u, dispatch.GroupsX);
        Assert.Equal(64u, dispatch.LocalX);
        Assert.True(dispatch.WritesGlobalMemory);
        Assert.Equal(128u, dispatch.ThreadCountX);
    }
}
