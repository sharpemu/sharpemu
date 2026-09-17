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
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Metal;
using SharpEmu.ShaderCompiler.Resources;
using Silk.NET.Vulkan;
using Xunit;
using ImageResourceClass = SharpEmu.ShaderCompiler.Resources.ImageResourceClass;
using ResourceSnapshot = SharpEmu.ShaderCompiler.Resources.ResourceSnapshot;

namespace SharpEmu.Libs.Tests.Gpu.Metal;

// The executor over the Metal host with a recording backend: the copied draw record, its stage bindings,
// the device-address ranges and the global data share transfers it submits.
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
    private const uint Float2Format = 64;
    private const ulong PageSize = DeviceAddressPaging.PageSize;
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.NonPublic;

    private readonly FatalScope _fatal = new();
    private readonly FakeCpuMemory _memory;
    private readonly RecordingBackend _backend = new();
    private readonly MetalCommandStreamHost _host;
    private readonly RegisterBanks _banks;

    public MetalRenderHostTests()
        : this(MemorySize)
    {
    }

    private MetalRenderHostTests(int memorySize, ulong memoryBase = MemoryBase)
    {
        _memory = new FakeCpuMemory(memoryBase, memorySize);
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
        new(new Gen5MslShader($"// {name}", name, stage, AttributeCount: 0));

    private static uint[] BufferWords(ulong address, uint bytes) => [unchecked((uint)address), (uint)(address >> 32), bytes, 0];

    private static uint[] NullImageWords() => [0, 0, 0, 9u << 28, 0, 0, 0, 0];

    private static uint[] SamplerWords() => [0x7u << 12, 0, 0, 0];

    private static ShaderProgramInfo Program(ShaderStageKind stage, ulong hash, ShaderResourceInfo info, IReadOnlyList<uint>? userDataRegisters = null, bool usesGlobalDataShare = false, bool usesDispatchThreadLimits = false, uint pushCursor = 0)
    {
        var program = new ShaderProgramInfo
        {
            Stage = stage,
            Hash = hash,
            UserDataBase = 0,
            UserDataCount = (uint)(userDataRegisters?.Count ?? 0),
            UsesDeviceAddresses = info.UsesDeviceAddresses,
            Buffers = info.Buffers.Select(buffer => new BufferResourceInfo(buffer.Read, buffer.Written, buffer.Atomic, buffer.Formatted, buffer.Scalar, buffer.MaxByteExtent, buffer.PackedStride)).ToArray(),
            Images = info.Images.Select(image => new ImageResourceInfo(image.ResourceClass == ImageResourceClass.Storage ? Libs.Gpu.Rendering.ImageResourceClass.Storage : Libs.Gpu.Rendering.ImageResourceClass.Sampled, image.Written)).ToArray(),
            SamplerCount = info.Samplers.Count,
            Resources = new SpecializedResourceInfo { Info = info },
            Bindings = BindingLayout.Allocate(info, userDataRegisters ?? [], usesGlobalDataShare, usesFlattenedTable: false, usesShaderBase: false,
                pushCursor, usesDispatchThreadLimits),
        };
        program.VertexFetchComponents[0] = 2;
        return program;
    }

    private static ShaderResourceInfo WithBuffer(bool written = false) => new()
    {
        Buffers = [new BufferResource { Read = true, Written = written, MaxByteExtent = 16 }],
    };

    // A pixel stage with one buffer, one null image, one sampler and two user registers; a vertex stage with one buffer.
    private sealed class TwoStageProvider(IShaderPipelineHost host) : IShaderPipelineProvider
    {
        private static readonly ShaderResourceInfo PixelInfo = new()
        {
            Buffers = [new BufferResource { Read = true, MaxByteExtent = 16 }],
            Images = [new ImageResource { ResourceClass = ImageResourceClass.Sampled, NumericClass = ImageNumericClass.Float, Dimension = ImageDimension.Dim2D, Read = true }],
            Samplers = [new SamplerResource()],
        };

        public ShaderProgramInfo Pixel { get; } = Program(ShaderStageKind.Pixel, 2, PixelInfo, [0, 1]);

        public ShaderProgramInfo Vertex { get; } = Program(ShaderStageKind.Vertex, 1, WithBuffer());

        public ShaderProgramInfo Compute { get; } = Program(ShaderStageKind.Compute, 3, WithBuffer(written: true));

        public ShaderProgramInfo? ComputeOverride { get; set; }

        public VertexInputInfo? VertexInputOverride { get; set; }
        public bool ReuseGraphicsPipeline { get; set; }
        private PipelineHandle? _graphicsPipeline;

        public ResourceSnapshot? ComputeSnapshotOverride { get; set; }

        private ShaderProgram _vertexProgram;
        private ShaderProgram _pixelProgram;
        private ShaderProgram _computeProgram;

        public GraphicsPrograms GetGraphicsPrograms(VertexStageRegisters vertex, PixelStageRegisters pixel, ShaderInterfaceRegisters shaderInterface, ContextRegisters context, ReadOnlySpan<ColorComponentMap> targetExportMapping, bool pixelActive)
        {
            if (!_vertexProgram.IsValid)
            {
                _vertexProgram = new ShaderProgram(1, host.CreateShaderModule(Shader("vertex", Gen5MslStage.Vertex), ShaderStage.Vertex, 1, 1));
                _pixelProgram = new ShaderProgram(2, host.CreateShaderModule(Shader("pixel", Gen5MslStage.Pixel), ShaderStage.Pixel, 2, 2));
            }

            return new GraphicsPrograms
            {
                Vertex = _vertexProgram,
                Pixel = _pixelProgram,
                VertexInput = VertexInputOverride ?? new VertexInputInfo
                {
                    Buffers = [new VertexInputBuffer(VertexBase, 8, 3)],
                    Attributes = [new VertexAttributeResource(new BufferDescriptorWords(unchecked((uint)VertexBase), (uint)(VertexBase >> 32) | (8u << 16), 3, Float2Format << 12), 0, 2, 0, 0, 0, 0)],
                    Stage = new ShaderStageResources(Vertex, new ResourceSnapshot { Buffers = [BufferWords(VertexGlobal, 16)] }, 0x1000),
                },
                PixelInput = new PixelInputInfo
                {
                    InputCount = 1,
                    Stage = new ShaderStageResources(Pixel, new ResourceSnapshot
                    {
                        Buffers = [BufferWords(PixelGlobal, 16)],
                        Images = [NullImageWords()],
                        Samplers = [SamplerWords()],
                        UserData = [0x11, 0x22],
                    }, 0x2000),
                },
            };
        }

        public PipelineHandle CreateGraphicsPipeline(ReadOnlySpan<ColorTargetState> colors, in DepthAttachmentState depth, VertexInputInfo vertexInput, PixelInputInfo? pixelInput, ContextRegisters context, in RenderingState rendering, PrimitiveTopology topology, bool primitiveRestartEnabled, bool disableBlending, ShaderProgram vertexProgram, ShaderProgram pixelProgram)
        {
            if (ReuseGraphicsPipeline && _graphicsPipeline is { } cached) return cached;
            var pipeline = host.CreateGraphicsPipeline(ShaderPipelineCache.BuildGraphicsDescription(
                colors, in depth, vertexInput, pixelInput, context, in rendering, topology, primitiveRestartEnabled, disableBlending, vertexProgram, pixelProgram, host.NoAttachmentSampleCounts));
            _graphicsPipeline = pipeline;
            return pipeline;
        }

        public ComputeProgram GetComputeProgram(ComputeStageRegisters compute, ShaderInterfaceRegisters shaderInterface, uint dispatchInitiator, uint dimensionX, uint dimensionY, uint dimensionZ)
        {
            var program = ComputeOverride ?? Compute;
            if (!_computeProgram.IsValid)
            {
                _computeProgram = new ShaderProgram(3, host.CreateShaderModule(Shader("compute", Gen5MslStage.Compute), ShaderStage.Compute, 3, 3));
            }

            var threadDimensions = (dispatchInitiator & (1u << 5)) != 0;
            return new ComputeProgram
            {
                Program = _computeProgram,
                Input = new ComputeInputInfo
                {
                    ThreadsX = 64,
                    ThreadsY = 1,
                    ThreadsZ = 1,
                    DispatchThreadDimensions = threadDimensions,
                    DispatchThreadsX = threadDimensions ? dimensionX : 0,
                    DispatchThreadsY = threadDimensions ? dimensionY : 0,
                    DispatchThreadsZ = threadDimensions ? dimensionZ : 0,
                    GroupIdX = true,
                    ThreadIdCount = 1,
                    Stage = new ShaderStageResources(program, ComputeSnapshotOverride ?? new ResourceSnapshot { Buffers = [BufferWords(PixelGlobal, 16)] }, 0x3000)
                    {
                        ThreadLimits = threadDimensions ? new DispatchThreadLimits(dimensionX, dimensionY, dimensionZ) : null,
                    },
                },
            };
        }

        public PipelineHandle CreateComputePipeline(ComputeInputInfo input, ShaderProgram program) =>
            host.CreateComputePipeline(new ComputePipelineDescription { Input = input, Program = program, Stage = input.Stage.Program! });
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
        int BaseVertex,
        IReadOnlyList<GuestStageBindings>? Stages);

    private sealed record RecordedDispatch(IReadOnlyList<GuestDrawTexture> Textures, IReadOnlyList<GuestMemoryBuffer> Globals, uint GroupsX, uint LocalX, bool WritesGlobalMemory, uint ThreadCountX, GuestStageBindings? Stage);

    private sealed record RecordedFill(ulong Offset, ulong Size, byte Value);

    private sealed record RecordedCopyFromGuest(ulong Offset, byte[] Bytes);

    private sealed record RecordedCopyToGuest(ulong GuestAddress, ulong Offset, ulong Size);

    private sealed record RecordedSynchronization(string DebugName);

    private static Exception Unsupported() => new NotSupportedException("The recording backend does not implement this call.");

    private sealed class RecordingBackend : IGuestGpuBackend, IGuestImageSnapshotBackend
    {
        public List<object> Records { get; } = new();

        public IEnumerable<RecordedDraw> Draws => Records.OfType<RecordedDraw>();

        public IEnumerable<RecordedDispatch> Dispatches => Records.OfType<RecordedDispatch>();

        public uint[] GlobalDataShare { get; } = new uint[ManagedCommandStreamHost.GdsBytes / sizeof(uint)];

        public int GlobalDataShareReads { get; private set; }

        public string BackendName => "Recording";


        public void SubmitOffscreenTranslatedDraw(IGuestCompiledShader pixelShader, IReadOnlyList<GuestDrawTexture> textures, IReadOnlyList<GuestMemoryBuffer> globalMemoryBuffers, uint attributeCount, IReadOnlyList<GuestRenderTarget> targets, IGuestCompiledShader? vertexShader = null, uint vertexCount = 3, uint instanceCount = 1, uint primitiveType = 4, GuestIndexBuffer? indexBuffer = null, IReadOnlyList<GuestVertexBuffer>? vertexBuffers = null, GuestRenderState? renderState = null, GuestDepthTarget? depthTarget = null, ulong shaderAddress = 0, int baseVertex = 0, IReadOnlyList<GuestStageBindings>? stageBindings = null) =>
            Records.Add(new RecordedDraw(textures, globalMemoryBuffers, targets, vertexCount, primitiveType, indexBuffer, vertexBuffers, renderState, baseVertex, stageBindings));

        public long SubmitComputeDispatch(ulong shaderAddress, IGuestCompiledShader computeShader, IReadOnlyList<GuestDrawTexture> textures, IReadOnlyList<GuestMemoryBuffer> globalMemoryBuffers, uint groupCountX, uint groupCountY, uint groupCountZ, uint baseGroupX, uint baseGroupY, uint baseGroupZ, uint localSizeX, uint localSizeY, uint localSizeZ, bool isIndirect, bool writesGlobalMemory, uint threadCountX = uint.MaxValue, uint threadCountY = uint.MaxValue, uint threadCountZ = uint.MaxValue, GuestStageBindings? stageBindings = null)
        {
            Records.Add(new RecordedDispatch(textures, globalMemoryBuffers, groupCountX, localSizeX, writesGlobalMemory, threadCountX, stageBindings));
            return Records.Count;
        }

        public long SubmitGlobalDataShareFill(ulong offset, ulong size, byte value)
        {
            Records.Add(new RecordedFill(offset, size, value));
            return Records.Count;
        }

        public long SubmitGlobalDataShareCopyFromGuest(ulong offset, byte[] bytes)
        {
            Records.Add(new RecordedCopyFromGuest(offset, bytes));
            return Records.Count;
        }

        public long SubmitGlobalDataShareCopyToGuest(ulong guestAddress, ulong offset, ulong size)
        {
            Records.Add(new RecordedCopyToGuest(guestAddress, offset, size));
            return Records.Count;
        }

        public void ReadGlobalDataShare(Span<uint> destination, uint wordOffset, uint wordCount)
        {
            GlobalDataShareReads++;
            GlobalDataShare.AsSpan((int)wordOffset, (int)wordCount).CopyTo(destination);
        }

        // Nothing waits on the recording backend; a zero sequence tells the host so.
        public long SubmitGpuSynchronization(string debugName)
        {
            Records.Add(new RecordedSynchronization(debugName));
            return 0;
        }

        public void EnsureStarted(uint width, uint height) => throw Unsupported();




        public bool TryCompileProgram(ShaderCompileRequest request, out IGuestCompiledShader? shader, out string error) => throw Unsupported();

        public IGuestCompiledShader GetDepthOnlyFragmentShader() => Shader("depth_only", Gen5MslStage.Pixel);

        public void HideSplashScreen() => throw Unsupported();

        public void Submit(byte[] bgraFrame, uint width, uint height) => throw Unsupported();

        public void SubmitGuestDraw(GuestDrawKind drawKind, uint width, uint height) => throw Unsupported();

        public void SubmitTranslatedDraw(IGuestCompiledShader pixelShader, IReadOnlyList<GuestDrawTexture> textures, IReadOnlyList<GuestMemoryBuffer> globalMemoryBuffers, uint width, uint height, uint attributeCount, IGuestCompiledShader? vertexShader = null, uint vertexCount = 3, uint instanceCount = 1, uint primitiveType = 4, GuestIndexBuffer? indexBuffer = null, IReadOnlyList<GuestVertexBuffer>? vertexBuffers = null, GuestRenderState? renderState = null) => throw Unsupported();

        public void SubmitDepthOnlyTranslatedDraw(IGuestCompiledShader pixelShader, IReadOnlyList<GuestDrawTexture> textures, IReadOnlyList<GuestMemoryBuffer> globalMemoryBuffers, uint attributeCount, GuestDepthTarget depthTarget, IGuestCompiledShader? vertexShader = null, uint vertexCount = 3, uint instanceCount = 1, uint primitiveType = 4, GuestIndexBuffer? indexBuffer = null, IReadOnlyList<GuestVertexBuffer>? vertexBuffers = null, GuestRenderState? renderState = null, ulong shaderAddress = 0, int baseVertex = 0, IReadOnlyList<GuestStageBindings>? stageBindings = null) => throw Unsupported();

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

    private static byte[] Pattern(int length, byte seed)
    {
        var bytes = new byte[length];
        for (var index = 0; index < length; index++)
        {
            bytes[index] = (byte)(seed + index);
        }

        return bytes;
    }

    private RenderExecutor Executor(out TwoStageProvider provider)
    {
        provider = new TwoStageProvider(_host);
        return new RenderExecutor(_host, provider);
    }

    private int RecordCount(string field) => ((System.Collections.ICollection)typeof(MetalCommandStreamHost).GetField(field, Members)!.GetValue(_host)!).Count;

    private static ResourceSnapshot RangeSnapshot(params DeviceAddressRange[] ranges) => new()
    {
        Buffers = [BufferWords(PixelGlobal, 16)],
        DeviceAddressRanges = ranges,
    };

    private RecordedDispatch DispatchWithRanges(TwoStageProvider provider, RenderExecutor executor, params DeviceAddressRange[] ranges)
    {
        provider.ComputeOverride = Program(ShaderStageKind.Compute, 4, new ShaderResourceInfo { Buffers = [new BufferResource { Read = true, MaxByteExtent = 16 }], UsesDeviceAddresses = true });
        provider.ComputeSnapshotOverride = RangeSnapshot(ranges);
        executor.Dispatch(1, _banks, 4, 1, 1, 0x1);
        return Assert.Single(_backend.Dispatches);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void DrawUsesSubmittedVerticesOnlyWhenAllChecksMatch(bool changedShader, bool changedLayout, bool changedDraw)
    {
        var executor = Executor(out var provider);
        var input = provider.GetGraphicsPrograms(_banks.Shader.Vertex, _banks.Shader.Pixel,
            _banks.Context.ShaderInterface, _banks.Context, [], true).VertexInput;
        var original = Floats(-1f, -1f, 3f, -1f, -1f, 3f);
        Assert.True(_memory.TryWrite(VertexBase, original));
        if (changedLayout) input.Attributes[0] = input.Attributes[0] with { OffsetBytes = 4 };
        Assert.True(SubmittedVertexData.TryCapture(_memory, input, original.Length, out var data));
        var registers = _host.Queue.GetInterpreter(0).Registers.Shader
            .Select(pair => (pair.Key, pair.Value)).ToArray();
        var snapshots = AgcExports.CreateGeometrySnapshotsForTests(0, 0, 0, 3, 1,
            registers, data, changedShader ? 0x9000ul : 0x1000ul);
        _host.BeginSubmission(0, 2, snapshots);
        var translation = (AgcExports.CommandStreamTranslation)typeof(AgcExports.TranslatingCommandStreamHost)
            .GetProperty("Translation", Members)!.GetValue(_host)!;
        var arguments = new DrawAutoArguments(0, 0, changedDraw ? 2u : 3u, 1, 0, 0, DrawOffsetSource.Packet);
        translation.RecordAutoDrawState(in arguments);
        var live = new byte[original.Length];
        Assert.True(_memory.TryWrite(VertexBase, live));
        executor.DrawAuto(2, _banks, in arguments);
        var stream = Assert.Single(Assert.Single(_backend.Draws).VertexBuffers!);
        Assert.Equal(changedShader || changedLayout || changedDraw ? live : original, stream.Data);
    }

    [Fact]
    public void ReusedPipelineUsesTheCurrentVertexBufferLayout()
    {
        var executor = Executor(out var provider);
        provider.ReuseGraphicsPipeline = true;
        var first = provider.GetGraphicsPrograms(_banks.Shader.Vertex, _banks.Shader.Pixel,
            _banks.Context.ShaderInterface, _banks.Context, [], true).VertexInput;
        Assert.True(_memory.TryWrite(VertexBase, new byte[24]));
        executor.DrawAuto(1, _banks, new DrawAutoArguments(0, 0, 3, 1, 0, 0, DrawOffsetSource.Packet));
        var address = VertexBase + 64;
        var bytes = Pattern(48, 1);
        Assert.True(_memory.TryWrite(address, bytes));
        provider.VertexInputOverride = new VertexInputInfo
        {
            Buffers = [new(address, 16, 3)],
            Attributes = [first.Attributes[0] with { Descriptor = first.Attributes[0].Descriptor.WithAddress(address) }],
            Stage = first.Stage,
        };
        executor.DrawAuto(2, _banks, new DrawAutoArguments(0, 0, 3, 1, 0, 0, DrawOffsetSource.Packet));
        var draws = _backend.Draws.ToArray();
        Assert.Equal(2, draws.Length);
        var stream = Assert.Single(draws[1].VertexBuffers!);
        Assert.Equal(address, stream.BaseAddress);
        Assert.Equal(16u, stream.Stride);
        Assert.Equal(bytes, stream.Data);
    }

    [Fact]
    public void Draw_BindsEveryResourceThroughTheStageBindings()
    {
        var executor = Executor(out var provider);
        _memory.TryWrite(VertexBase, Floats(-1f, -1f, 3f, -1f, -1f, 3f));
        _memory.TryWrite(PixelGlobal, Pattern(16, 1));
        _memory.TryWrite(VertexGlobal, Pattern(16, 100));

        executor.DrawAuto(1, _banks, new DrawAutoArguments(0, 0, 3, 1, 0, 0, DrawOffsetSource.Packet));

        var draw = Assert.Single(_backend.Draws);
        Assert.Equal([VertexGlobal, PixelGlobal], draw.Globals.Select(global => global.BaseAddress));
        Assert.Equal(Pattern(16, 100), draw.Globals[0].Data);
        Assert.Equal(Pattern(16, 1), draw.Globals[1].Data);
        Assert.All(draw.Globals, global => Assert.False(global.Writable));
        var stages = Assert.IsAssignableFrom<IReadOnlyList<GuestStageBindings>>(draw.Stages);
        Assert.Equal([GuestStageKind.Vertex, GuestStageKind.Pixel], stages.Select(stage => stage.Stage));
        var vertex = stages[0];
        var pixel = stages[1];
        Assert.Equal([0], vertex.BufferIndices);
        Assert.Equal([1], pixel.BufferIndices);
        Assert.Equal([0], pixel.ImageElements);
        Assert.Empty(vertex.ImageElements);
        Assert.True(Assert.Single(draw.Textures).IsFallback);
        var sampler = Assert.Single(pixel.Samplers);
        Assert.Equal(0u, sampler.Word0 & (0x7u << 12));
        // Two user registers, then the packed memory offset of the one buffer.
        Assert.Equal([0x11u, 0x22u, 0u], pixel.ShaderData);
        Assert.Equal(provider.Pixel.Hash, pixel.ProgramHash);
        Assert.Equal(provider.Vertex.Hash, vertex.ProgramHash);
        Assert.False(pixel.UsesGlobalDataShare);
        Assert.False(pixel.UsesDeviceAddresses);
        Assert.Empty(pixel.AddressRanges);
        Assert.Equal(ColorBase, Assert.Single(draw.Targets).Address);
        Assert.Equal(0xFu, draw.Targets[0].WriteMask);
        Assert.Equal(3u, draw.VertexCount);
        Assert.Equal(4u, draw.PrimitiveType);
        Assert.Null(draw.IndexBuffer);
        var stream = Assert.Single(draw.VertexBuffers!);
        Assert.Equal(Floats(-1f, -1f, 3f, -1f, -1f, 3f), stream.Data);
        Assert.Equal(VertexBase, stream.BaseAddress);
        Assert.Equal(8u, stream.Stride);
        Assert.Equal(2u, stream.ComponentCount);
        Assert.Equal((11u, 7u), (stream.DataFormat, stream.NumberFormat));
        var blend = Assert.Single(draw.RenderState!.Blends);
        Assert.Equal(0xFu, blend.WriteMask);
        Assert.Equal(new GuestRect(0, 0, Size, Size), draw.RenderState.Scissor);
    }

    // The pixel block sits first in the shared push data and the vertex block follows it.
    [Fact]
    public void Draw_PlacesEachStagesPushBlockAtItsStartDword()
    {
        var executor = Executor(out var provider);
        _memory.TryWrite(VertexBase, Floats(-1f, -1f, 3f, -1f, -1f, 3f));

        executor.DrawAuto(1, _banks, new DrawAutoArguments(0, 0, 3, 1, 0, 0, DrawOffsetSource.Packet));

        var stages = Assert.Single(_backend.Draws).Stages!;
        var pixel = Assert.Single(stages, stage => stage.Stage == GuestStageKind.Pixel);
        Assert.True(provider.Pixel.Bindings!.UsesPushData);
        Assert.Equal(0u, provider.Pixel.Bindings.PushDataStartDword);
        var pushData = Assert.IsType<uint[]>(pixel.PushData);
        Assert.Equal((int)PushData.DwordCount, pushData.Length);
        Assert.Equal([0x11u, 0x22u], pushData[..2]);
        Assert.Equal(0u, provider.Vertex.Bindings!.PushDataStartDword);
        Assert.Equal(1u, provider.Vertex.Bindings.ShaderDataDwordCount);
    }

    [Fact]
    public void IndexedDraw_CopiesTheIndicesItWasRecordedWith()
    {
        var executor = Executor(out _);
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
        var executor = Executor(out _);
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
        var executor = Executor(out _);
        _memory.TryWrite(VertexBase, Floats(-1f, -1f, 3f, -1f, -1f, 3f));

        for (var index = 0; index < 3; index++)
        {
            executor.DrawAuto((ulong)index, _banks, new DrawAutoArguments(0, 0, 3, 1, 0, 0, DrawOffsetSource.Packet));
            executor.Dispatch((ulong)index, _banks, 4, 1, 1, 0x1);
        }

        Assert.Equal(3, _backend.Draws.Count());
        Assert.Equal(3, _backend.Dispatches.Count());
        Assert.Equal(0, RecordCount("_buffers"));
        Assert.Equal(0, RecordCount("_committedStages"));
        Assert.Equal(0, RecordCount("_colorTargets"));
        // The host keeps every pipeline and module the provider creates; the provider is the cache.
        Assert.Equal(6, RecordCount("_pipelineRecords"));
        Assert.Equal(3, RecordCount("_modules"));
    }

    [Fact]
    public void Dispatch_SubmitsTheComputeStageAndTheThreadLimits()
    {
        var executor = Executor(out var provider);
        _memory.TryWrite(PixelGlobal, Pattern(16, 7));

        executor.Dispatch(1, _banks, 128, 1, 1, 0x1 | (1u << 5));

        var dispatch = Assert.Single(_backend.Dispatches);
        var global = Assert.Single(dispatch.Globals);
        Assert.Equal(PixelGlobal, global.BaseAddress);
        Assert.Equal(Pattern(16, 7), global.Data);
        Assert.True(global.Writable);
        Assert.Empty(dispatch.Textures);
        Assert.Equal(2u, dispatch.GroupsX);
        Assert.Equal(64u, dispatch.LocalX);
        Assert.True(dispatch.WritesGlobalMemory);
        Assert.Equal(128u, dispatch.ThreadCountX);
        var stage = Assert.IsType<GuestStageBindings>(dispatch.Stage);
        Assert.Equal(GuestStageKind.Compute, stage.Stage);
        Assert.Equal([0], stage.BufferIndices);
        Assert.Equal(provider.Compute.Hash, stage.ProgramHash);
    }

    [Fact]
    public void DynamicOffsetAccess_CoversTheMappedRangeUpToTheCap()
    {
        var executor = Executor(out var provider);
        var handleBase = MemoryBase + 0x8_0000;

        var dispatch = DispatchWithRanges(provider, executor, new DeviceAddressRange(0, handleBase, DeviceAddressRangePlanner.MaxRangeBytes, Planned: true, Written: false));

        var stage = dispatch.Stage!;
        Assert.True(stage.UsesDeviceAddresses);
        var range = Assert.Single(stage.AddressRanges);
        Assert.Equal((uint)(handleBase >> DeviceAddressPaging.PageBits), range.FirstPage);
        Assert.Equal((uint)((MemoryBase + MemorySize - handleBase) / PageSize), range.PageCount);
        var buffer = dispatch.Globals[range.BufferIndex];
        Assert.Equal(handleBase, buffer.BaseAddress);
        Assert.Equal(MemoryBase + MemorySize - handleBase, (ulong)buffer.Length);
        Assert.False(buffer.Writable);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(32u)]
    public void ComputeShaderDataKeepsTheLimitsOfEachRecordedDispatch(uint pushCursor)
    {
        var executor = Executor(out var provider);
        provider.ComputeOverride = Program(ShaderStageKind.Compute, 3, WithBuffer(written: true),
            usesDispatchThreadLimits: true, pushCursor: pushCursor);
        executor.Dispatch(1, _banks, 100, 1, 1, 0x21);
        executor.Dispatch(2, _banks, 4, 1, 1, 0x21);
        var dispatches = _backend.Dispatches.ToArray();
        Assert.Equal(2, dispatches.Length);
        var first = dispatches[0].Stage!;
        var second = dispatches[1].Stage!;
        var offset = (int)provider.ComputeOverride.Bindings!.DispatchThreadLimitsDword;
        Assert.Equal(new uint[] { 100, 1, 1 }, first.ShaderData.AsSpan(offset, 3).ToArray());
        Assert.Equal(new uint[] { 4, 1, 1 }, second.ShaderData.AsSpan(offset, 3).ToArray());
    }

    [Fact]
    public void UnalignedBase_RoundsDownToThePage()
    {
        var executor = Executor(out var provider);
        var handleBase = MemoryBase + 0x4_0100;
        _memory.TryWrite(MemoryBase + 0x4_0000, Pattern(0x40, 9));

        var dispatch = DispatchWithRanges(provider, executor, new DeviceAddressRange(0, handleBase, 0x100, Planned: true, Written: false));

        var range = Assert.Single(dispatch.Stage!.AddressRanges);
        Assert.Equal((uint)((MemoryBase + 0x4_0000) >> DeviceAddressPaging.PageBits), range.FirstPage);
        Assert.Equal(1u, range.PageCount);
        var buffer = dispatch.Globals[range.BufferIndex];
        Assert.Equal(MemoryBase + 0x4_0000, buffer.BaseAddress);
        Assert.Equal((int)PageSize, buffer.Length);
        Assert.Equal(Pattern(0x40, 9), buffer.Data[..0x40]);
    }

    [Fact]
    public void TwoRangesSharingAPage_MergeIntoOneEntry()
    {
        var executor = Executor(out var provider);

        var dispatch = DispatchWithRanges(
            provider,
            executor,
            new DeviceAddressRange(0, MemoryBase + 0x4_0000, 0x100, Planned: true, Written: false),
            new DeviceAddressRange(1, MemoryBase + 0x4_2000, 0x100, Planned: true, Written: false),
            new DeviceAddressRange(2, MemoryBase + 0x8_0000, 0x100, Planned: true, Written: false));

        var ranges = dispatch.Stage!.AddressRanges;
        Assert.Equal(2, ranges.Length);
        Assert.Equal((uint)((MemoryBase + 0x4_0000) >> DeviceAddressPaging.PageBits), ranges[0].FirstPage);
        Assert.Equal(1u, ranges[0].PageCount);
        Assert.Equal((uint)((MemoryBase + 0x8_0000) >> DeviceAddressPaging.PageBits), ranges[1].FirstPage);
        Assert.Equal(3, dispatch.Globals.Count);
    }

    [Fact]
    public void PartialPage_ReadsZeroPastTheMappedSize()
    {
        using var partial = new MetalRenderHostTests(0x8_0100);
        var executor = partial.Executor(out var provider);
        var handleBase = MemoryBase + 0x8_0000;
        partial._memory.TryWrite(handleBase, Pattern(0x100, 3));

        var dispatch = partial.DispatchWithRanges(provider, executor, new DeviceAddressRange(0, handleBase, 0x1000, Planned: true, Written: false));

        var range = Assert.Single(dispatch.Stage!.AddressRanges);
        Assert.Equal(1u, range.PageCount);
        var buffer = dispatch.Globals[range.BufferIndex];
        Assert.Equal((int)PageSize, buffer.Length);
        Assert.Equal(Pattern(0x100, 3), buffer.Data[..0x100]);
        Assert.All(buffer.Data[0x100..], value => Assert.Equal(0, value));
    }

    // A page whose first 4 KiB the guest never mapped still carries every mapped byte after them.
    [Fact]
    public void UnmappedPagePrefix_KeepsTheMappedRemainder()
    {
        const ulong mappedStart = MemoryBase + 0x1000;
        using var partial = new MetalRenderHostTests(0x8_0000, mappedStart);
        var executor = partial.Executor(out var provider);
        partial._memory.TryWrite(mappedStart, Pattern(0x100, 5));
        partial._memory.TryWrite(mappedStart + 0x2F00, Pattern(0x100, 9));

        var dispatch = partial.DispatchWithRanges(provider, executor, new DeviceAddressRange(0, mappedStart, 0x3000, Planned: true, Written: false));

        var range = Assert.Single(dispatch.Stage!.AddressRanges);
        Assert.Equal((uint)(MemoryBase >> DeviceAddressPaging.PageBits), range.FirstPage);
        var buffer = dispatch.Globals[range.BufferIndex];
        Assert.Equal(MemoryBase, buffer.BaseAddress);
        Assert.All(buffer.Data[..0x1000], value => Assert.Equal(0, value));
        Assert.Equal(Pattern(0x100, 5), buffer.Data[0x1000..0x1100]);
        Assert.Equal(Pattern(0x100, 9), buffer.Data[0x3F00..0x4000]);
    }

    [Fact]
    public void WrittenRange_IsCopiedBackToGuestOnCompletion()
    {
        var executor = Executor(out var provider);

        var dispatch = DispatchWithRanges(provider, executor, new DeviceAddressRange(0, MemoryBase + 0x4_0000, 0x100, Planned: true, Written: true));

        var range = Assert.Single(dispatch.Stage!.AddressRanges);
        var buffer = dispatch.Globals[range.BufferIndex];
        Assert.True(buffer.Writable);
        Assert.True(buffer.WriteBackToGuest);
        Assert.True(dispatch.WritesGlobalMemory);
    }

    [Fact]
    public void RangeTableOverflow_IsFatalWithTheHash()
    {
        using var wide = new MetalRenderHostTests(0x80_0000);
        var executor = wide.Executor(out var provider);
        var ranges = Enumerable.Range(0, (int)Gen5MslTranslator.MaxAddressRangeCount + 1)
            .Select(index => new DeviceAddressRange((uint)index, MemoryBase + ((ulong)index * 2 * PageSize), 0x10, Planned: true, Written: false))
            .ToArray();

        var fatal = Assert.Throws<SchedulerFatalException>(() => wide.DispatchWithRanges(provider, executor, ranges));

        Assert.Contains("hash=0x0000000000000004", fatal.Message);
        Assert.Contains($"limit={Gen5MslTranslator.MaxAddressRangeCount}", fatal.Message);
    }

    [Fact]
    public void UnplannedReadHandle_IsFatalWithTheHash()
    {
        var executor = Executor(out var provider);

        var fatal = Assert.Throws<SchedulerFatalException>(() => DispatchWithRanges(provider, executor, new DeviceAddressRange(0, 0, 0, Planned: false, Written: false)));

        Assert.Contains("cannot be planned on Metal", fatal.Message);
        Assert.Contains("hash=0x0000000000000004", fatal.Message);
    }

    [Fact]
    public void Fill_ShaderAtomic_CopyToGuest_RecordInStreamOrder()
    {
        var executor = Executor(out var provider);
        provider.ComputeOverride = Program(ShaderStageKind.Compute, 5, WithBuffer(), usesGlobalDataShare: true);

        _host.FillBuffer(0x100, 0x40, 0xFFFF_FFFF, isGds: true);
        executor.Dispatch(1, _banks, 4, 1, 1, 0x1);
        _host.CopyBuffer(MemoryBase + 0x2_0000, 0x100, 0x40, destinationIsGds: false, sourceIsGds: true);

        Assert.Collection(
            _backend.Records,
            record => Assert.Equal(new RecordedFill(0x100, 0x40, 0xFF), record),
            record => Assert.True(Assert.IsType<RecordedDispatch>(record).Stage!.UsesGlobalDataShare),
            record => Assert.Equal(new RecordedCopyToGuest(MemoryBase + 0x2_0000, 0x100, 0x40), record));
    }

    [Fact]
    public void ShaderWrite_ThenFill_QueuesTheFillAfterTheDispatch()
    {
        var executor = Executor(out var provider);
        provider.ComputeOverride = Program(ShaderStageKind.Compute, 5, WithBuffer(), usesGlobalDataShare: true);

        executor.Dispatch(1, _banks, 4, 1, 1, 0x1);
        _host.FillBuffer(0, 0x10, 0, isGds: true);

        Assert.Collection(
            _backend.Records,
            record => Assert.IsType<RecordedDispatch>(record),
            record => Assert.Equal(new RecordedFill(0, 0x10, 0), record));
    }

    [Fact]
    public void GdsFill_WithANonUniformDwordPattern_RecordsAPatternCopy()
    {
        _host.FillBuffer(0x40, 0x10, 0x12345678, isGds: true);
        _host.FillBuffer(0x80, 0x8, 0xFFFF_FFFF, isGds: true);

        var copy = Assert.IsType<RecordedCopyFromGuest>(_backend.Records[0]);
        Assert.Equal(0x40UL, copy.Offset);
        Assert.Equal(new byte[] { 0x78, 0x56, 0x34, 0x12, 0x78, 0x56, 0x34, 0x12, 0x78, 0x56, 0x34, 0x12, 0x78, 0x56, 0x34, 0x12 }, copy.Bytes);
        Assert.Equal(new RecordedFill(0x80, 0x8, 0xFF), _backend.Records[1]);
    }

    [Fact]
    public void GuestToGdsCopy_CarriesTheGuestBytes()
    {
        _memory.TryWrite(MemoryBase + 0x3_0000, Pattern(0x20, 40));

        _host.CopyBuffer(0x200, MemoryBase + 0x3_0000, 0x20, destinationIsGds: true, sourceIsGds: false);

        var copy = Assert.IsType<RecordedCopyFromGuest>(Assert.Single(_backend.Records));
        Assert.Equal(0x200UL, copy.Offset);
        Assert.Equal(Pattern(0x20, 40), copy.Bytes);
    }

    [Fact]
    public void ReadGds_ReadsTheBackendAfterTheGpuSynchronization()
    {
        _backend.GlobalDataShare[3] = 0xCAFE;
        Span<uint> words = stackalloc uint[2];

        _host.SynchronizeGpu();
        _host.ReadGds(words, 2, 2);

        Assert.Equal(new RecordedSynchronization("command_stream synchronize"), Assert.Single(_backend.Records));
        Assert.Equal(1, _backend.GlobalDataShareReads);
        Assert.Equal([0u, 0xCAFEu], words.ToArray());
    }

    [Fact]
    public void GdsRangeOutsideTheBuffer_IsFatal()
    {
        var fatal = Assert.Throws<SchedulerFatalException>(() => _host.FillBuffer(ManagedCommandStreamHost.GdsBytes - 4, 8, 0, isGds: true));

        Assert.Contains("outside the buffer", fatal.Message);
        Assert.Empty(_backend.Records);
    }
}
