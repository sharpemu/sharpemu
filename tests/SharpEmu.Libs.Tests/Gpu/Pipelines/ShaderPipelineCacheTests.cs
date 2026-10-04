// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using Silk.NET.Vulkan;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Rendering.RenderExecutorFixtures;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

// The graphics pipeline description folds the draw's static state byte for byte; the keys fold all of it.
[Collection(SchedulingStateCollection.Name)]
public sealed class ShaderPipelineCacheTests : IDisposable
{
    private const uint Format32x4Float = 77;
    private const uint LegacyFusedGeometryStageMask = 0x0000_2030;

    private readonly FatalScope _fatal = new();

    public void Dispose() => _fatal.Dispose();

    // Forwards each pipeline request to the static builder and keeps the description.
    private sealed class DescribingProvider : IShaderPipelineProvider
    {
        public GraphicsPrograms Graphics { get; set; } = Programs();

        public List<GraphicsPipelineDescription> Descriptions { get; } = new();

        public GraphicsPrograms GetGraphicsPrograms(VertexStageRegisters vertex, PixelStageRegisters pixel, ShaderInterfaceRegisters shaderInterface, ContextRegisters context, UserConfigRegisters userConfig, ReadOnlySpan<ColorComponentMap> targetExportMapping, bool pixelActive, bool depthBound) => Graphics;

        public PipelineHandle CreateGraphicsPipeline(ReadOnlySpan<ColorTargetState> colors, in DepthAttachmentState depth, VertexInputInfo vertexInput, PixelInputInfo? pixelInput, ContextRegisters context, in RenderingState rendering, PrimitiveTopology topology, bool primitiveRestartEnabled, bool disableBlending, ShaderProgram vertexProgram, ShaderProgram pixelProgram)
        {
            Descriptions.Add(ShaderPipelineCache.BuildGraphicsDescription(
                colors, in depth, vertexInput, pixelInput, context, in rendering, topology, primitiveRestartEnabled, disableBlending,
                vertexProgram, pixelProgram, SampleCountFlags.Count1Bit | SampleCountFlags.Count4Bit));
            return new PipelineHandle(0xA1, 0xB1, false);
        }

        public ComputeProgram GetComputeProgram(ComputeStageRegisters compute, ShaderInterfaceRegisters shaderInterface, uint dispatchInitiator, uint dimensionX, uint dimensionY, uint dimensionZ) =>
            throw new InvalidOperationException("The describing provider has no compute program.");

        public PipelineHandle CreateComputePipeline(ComputeInputInfo input, ShaderProgram program) =>
            throw new InvalidOperationException("The describing provider has no compute pipeline.");
    }

    private static GraphicsPipelineDescription Describe(
        Action<RegisterBanks>? configure = null,
        GraphicsPrograms? programs = null,
        uint primitiveType = PrimitiveTriangleList,
        bool withDepth = false,
        bool provokingVertexLastSupported = false)
    {
        var host = new RecordingRenderHost
        {
            ProvokingVertexLastSupported = provokingVertexLastSupported,
        };
        var provider = new DescribingProvider();
        if (programs is not null)
        {
            provider.Graphics = programs;
        }

        var banks = Banks(primitiveType, withDepth);
        configure?.Invoke(banks);
        new RenderExecutor(host, provider).DrawAuto(1, banks, Auto(3));
        return Assert.Single(provider.Descriptions);
    }

    private static GraphicsPipelineDescription With(GraphicsPipelineDescription description, PipelineStaticParameters parameters) => new()
    {
        Rendering = description.Rendering,
        VertexInput = description.VertexInput,
        VertexInfo = description.VertexInfo,
        VertexProgram = description.VertexProgram,
        VertexStage = description.VertexStage,
        PixelInfo = description.PixelInfo,
        PixelProgram = description.PixelProgram,
        PixelStage = description.PixelStage,
        StaticParameters = parameters,
    };

    [Fact]
    public void StaticParameters_FoldTheBlendCullAndTopologyRegisters()
    {
        var description = Describe(banks =>
        {
            banks.Context.BlendControls[0] = new BlendRegisters
            {
                Enable = true, ColorSourceFactor = 4, ColorDestinationFactor = 5, ColorFunction = 1,
                AlphaSourceFactor = 2, AlphaDestinationFactor = 3, AlphaFunction = 2, SeparateAlpha = true,
            };
            banks.Context.RasterMode.CullBack = true;
            banks.Context.RasterMode.FrontFaceClockwise = true;
        });

        var parameters = description.StaticParameters;
        Assert.Equal(PrimitiveTopology.TriangleList, parameters.Topology);
        Assert.False(parameters.PrimitiveRestartEnable);
        Assert.Equal(1u, parameters.Samples);
        Assert.False(parameters.SampleShadingEnable);
        Assert.Equal(1u, parameters.ColorCount);
        Assert.NotEqual(0u, parameters.GetColorMask(0));
        Assert.True(parameters.GetBlendEnable(0));
        Assert.False(parameters.GetBlendBypass(0));
        Assert.Equal((4, 5, 1), ((int)parameters.GetColorSourceBlend(0), (int)parameters.GetColorDestinationBlend(0), (int)parameters.GetColorBlendFunction(0)));
        Assert.Equal((2, 3, 2), ((int)parameters.GetAlphaSourceBlend(0), (int)parameters.GetAlphaDestinationBlend(0), (int)parameters.GetAlphaBlendFunction(0)));
        Assert.True(parameters.GetSeparateAlphaBlend(0));
        Assert.True(parameters.CullBack);
        Assert.False(parameters.CullFront);
        Assert.True(parameters.FrontFaceClockwise);
        Assert.Equal(PolygonMode.Fill, parameters.PolygonMode);
        Assert.False(parameters.ProvokingVertexLast);
        Assert.False(parameters.WithDepth);
        Assert.False(parameters.StencilTestEnable);
        Assert.Equal(Format.Undefined, description.Rendering.DepthFormat);
        Assert.NotEqual(Format.Undefined, description.Rendering.ColorFormats[0]);
        Assert.Equal(1u, description.Rendering.ColorCount);
    }

    [Fact]
    public void StaticParameters_FoldTheGuestProvokingVertexMode()
    {
        var description = Describe(
            banks => banks.Context.RasterMode.ProvokingVertexLast = true,
            provokingVertexLastSupported: true);

        Assert.True(description.StaticParameters.ProvokingVertexLast);
    }

    [Fact]
    public void GraphicsPipelineKey_DistinguishesFirstAndLastProvokingVertex()
    {
        var first = Describe();
        var last = Describe(
            banks => banks.Context.RasterMode.ProvokingVertexLast = true,
            provokingVertexLastSupported: true);

        Assert.NotEqual(
            ShaderPipelineCache.KeyOf(first),
            ShaderPipelineCache.KeyOf(last));
    }

    [Fact]
    public void DisableBlending_ClearsTheBlendEnableButKeepsTheFactors()
    {
        var programs = Programs();
        var description = Describe(
            banks => banks.Context.BlendControls[0] = new BlendRegisters { Enable = true, ColorSourceFactor = 4, ColorDestinationFactor = 5 },
            new GraphicsPrograms { Vertex = programs.Vertex, Pixel = programs.Pixel, VertexInput = programs.VertexInput, PixelInput = programs.PixelInput, DisableBlending = true });

        Assert.False(description.StaticParameters.GetBlendEnable(0));
        Assert.Equal(4, description.StaticParameters.GetColorSourceBlend(0));
    }

    [Theory]
    [InlineData(7u)]
    [InlineData(17u)]
    public void RectangleListEncodings_DisableCullingAndKeepThePatchTopology(uint primitiveType)
    {
        var description = Describe(banks =>
        {
            banks.Context.RasterMode.CullFront = true;
            banks.Context.RasterMode.CullBack = true;
        }, primitiveType: primitiveType);

        Assert.Equal(PrimitiveTopology.PatchList, description.StaticParameters.Topology);
        Assert.False(description.StaticParameters.CullFront);
        Assert.False(description.StaticParameters.CullBack);
    }

    [Theory]
    [InlineData(0, PolygonMode.Point)]
    [InlineData(1, PolygonMode.Line)]
    [InlineData(2, PolygonMode.Fill)]
    public void EnabledPolygonMode_MapsTheGuestPolygonType(byte polygonType, PolygonMode expected)
    {
        var description = Describe(banks => banks.Context.RasterMode = new RasterModeRegisters
        {
            PolygonMode = 1,
            FrontPolygonType = polygonType,
            BackPolygonType = polygonType,
        });

        Assert.Equal(expected, description.StaticParameters.PolygonMode);
    }

    [Fact]
    public void DisabledPolygonMode_IgnoresBothPerFaceTypes()
    {
        var description = Describe(banks => banks.Context.RasterMode = new RasterModeRegisters
        {
            PolygonMode = 0,
            FrontPolygonType = 7,
            BackPolygonType = 6,
        });

        Assert.Equal(PolygonMode.Fill, description.StaticParameters.PolygonMode);
    }

    [Fact]
    public void EnabledPolygonMode_UsesOnlyTheVisibleFace()
    {
        var description = Describe(banks => banks.Context.RasterMode = new RasterModeRegisters
        {
            PolygonMode = 1,
            FrontPolygonType = 0,
            BackPolygonType = 1,
            CullFront = true,
        });

        Assert.Equal(PolygonMode.Line, description.StaticParameters.PolygonMode);
    }

    [Fact]
    public void EnabledPolygonMode_WithBothFacesCulledUsesFill()
    {
        var description = Describe(banks => banks.Context.RasterMode = new RasterModeRegisters
        {
            PolygonMode = 1,
            FrontPolygonType = 7,
            BackPolygonType = 6,
            CullFront = true,
            CullBack = true,
        });

        Assert.Equal(PolygonMode.Fill, description.StaticParameters.PolygonMode);
    }

    [Fact]
    public void DepthTarget_FoldsTheDepthAndStencilState()
    {
        var description = Describe(withDepth: true);

        Assert.True(description.StaticParameters.WithDepth);
        Assert.NotEqual(Format.Undefined, description.Rendering.DepthFormat);
        Assert.Equal(StencilOperations.Default, description.StaticParameters.StencilFront);
    }

    [Fact]
    public void VertexInputState_FoldsEveryBindingAndAttribute()
    {
        var defaults = Programs();
        var vertexInput = new VertexInputInfo
        {
            Buffers = [new VertexInputBuffer(VertexBase, 32, 3), new VertexInputBuffer(VertexBase + 0x1000, 16, 3, PerInstance: true)],
            Attributes =
            [
                new VertexAttributeResource(new BufferDescriptorWords(unchecked((uint)VertexBase), (uint)(VertexBase >> 32) | (32u << 16), 3, Format32x4Float << 12), 0, 4, 0, 0, 0, 0),
                new VertexAttributeResource(new BufferDescriptorWords(unchecked((uint)VertexBase), (uint)(VertexBase >> 32) | (32u << 16), 3, Format32x4Float << 12), 4, 2, 1, 0, 0, 16),
                new VertexAttributeResource(new BufferDescriptorWords(unchecked((uint)VertexBase), (uint)(VertexBase >> 32) | (16u << 16), 3, Format32x4Float << 12), 6, 4, 2, 1, 1, 0),
            ],
            Stage = defaults.VertexInput.Stage,
        };
        var programs = new GraphicsPrograms { Vertex = defaults.Vertex, Pixel = defaults.Pixel, VertexInput = vertexInput, PixelInput = defaults.PixelInput };

        var state = Describe(programs: programs).VertexInput;

        Assert.Equal(2, state.BindingCount);
        Assert.Equal(3, state.AttributeCount);
        Assert.Equal(new PipelineVertexBinding(32, false), state.Bindings[0]);
        Assert.Equal(new PipelineVertexBinding(16, true), state.Bindings[1]);
        Assert.Equal(new PipelineVertexAttribute(16, 0), state.Attributes[1]);
        Assert.Equal(new PipelineVertexAttribute(0, 1), state.Attributes[2]);
    }

    [Fact]
    public void GraphicsPipelineKey_FoldsAll168StaticBytes()
    {
        var description = Describe();
        var key = ShaderPipelineCache.KeyOf(description);
        Assert.Equal(key, ShaderPipelineCache.KeyOf(With(description, PipelineStaticParameters.FromBytes(description.StaticParameters.Bytes))));
        Assert.Equal(168, description.StaticParameters.Bytes.Length);

        for (var index = 0; index < description.StaticParameters.Bytes.Length; index++)
        {
            var bytes = description.StaticParameters.Bytes.ToArray();
            bytes[index] ^= 0x01;
            var flipped = ShaderPipelineCache.KeyOf(With(description, PipelineStaticParameters.FromBytes(bytes)));
            Assert.False(key.Equals(flipped), $"byte {index} did not change the key");
        }
    }

    [Fact]
    public void GraphicsPipelineKey_FoldsTheProgramsTheRenderingAndTheVertexInput()
    {
        var description = Describe();
        var key = ShaderPipelineCache.KeyOf(description);

        var otherVertex = new GraphicsPipelineDescription
        {
            Rendering = description.Rendering, VertexInput = description.VertexInput, VertexInfo = description.VertexInfo,
            VertexProgram = new ShaderProgram(0x99), VertexStage = description.VertexStage, PixelInfo = description.PixelInfo,
            PixelProgram = description.PixelProgram, PixelStage = description.PixelStage, StaticParameters = description.StaticParameters,
        };
        Assert.NotEqual(key, ShaderPipelineCache.KeyOf(otherVertex));

        var otherRendering = new PipelineRenderingState { ColorCount = 1 };
        otherRendering.ColorFormats[0] = Format.R16G16B16A16Sfloat;
        var renderingChanged = new GraphicsPipelineDescription
        {
            Rendering = otherRendering, VertexInput = description.VertexInput, VertexInfo = description.VertexInfo,
            VertexProgram = description.VertexProgram, VertexStage = description.VertexStage, PixelInfo = description.PixelInfo,
            PixelProgram = description.PixelProgram, PixelStage = description.PixelStage, StaticParameters = description.StaticParameters,
        };
        Assert.NotEqual(key, ShaderPipelineCache.KeyOf(renderingChanged));
        Assert.Equal(key, ShaderPipelineCache.KeyOf(With(description, description.StaticParameters)));
    }

    [Fact]
    public void ComputePipelineKey_IsTheProgramId()
    {
        Assert.Equal(new ComputePipelineKey(5), new ComputePipelineKey(5));
        Assert.NotEqual(new ComputePipelineKey(5), new ComputePipelineKey(6));

        var guest = new PipelineTestGuest();
        var cache = new ShaderPipelineCache(guest.Context, guest.Host, guest.Compiler, guest.Registry);
        var input = ComputeProgram().Input;
        var first = cache.CreateComputePipeline(input, new ShaderProgram(7, 1));
        var again = cache.CreateComputePipeline(input, new ShaderProgram(7, 1));
        var other = cache.CreateComputePipeline(input, new ShaderProgram(8, 2));

        Assert.Equal(first, again);
        Assert.NotEqual(first, other);
        Assert.Equal(2, guest.Host.ComputePipelines.Count);
        Assert.Equal(2, cache.ComputePipelineCount);
    }

    [Fact]
    public void TryCreateComputePipeline_ReportsTheHostCompileAndCachesTheResult()
    {
        var guest = new PipelineTestGuest();
        var cache = new ShaderPipelineCache(guest.Context, guest.Host, guest.Compiler, guest.Registry);
        var input = ComputeProgram().Input;
        var program = new ShaderProgram(7, 1);
        guest.Host.PendingComputeCompiles[program.Id] = 2;

        Assert.False(cache.TryCreateComputePipeline(input, program, out _));
        Assert.False(cache.TryCreateComputePipeline(input, program, out _));
        Assert.Empty(guest.Host.ComputePipelines);
        Assert.Equal(0, cache.ComputePipelineCount);

        Assert.True(cache.TryCreateComputePipeline(input, program, out var ready));
        Assert.Single(guest.Host.ComputePipelines);

        // The cached pipeline is served without asking the host again.
        Assert.True(cache.TryCreateComputePipeline(input, program, out var cached));
        Assert.Equal(ready, cached);
        Assert.Single(guest.Host.ComputePipelines);
        Assert.Equal(1, cache.ComputePipelineCount);
    }

    [Fact]
    public void GraphicsPipelines_AreCachedByTheirKey()
    {
        var guest = new PipelineTestGuest();
        var cache = new ShaderPipelineCache(guest.Context, guest.Host, guest.Compiler, guest.Registry);
        var host = new RecordingRenderHost();
        var executor = new RenderExecutor(host, new CachingProvider(cache));

        executor.DrawAuto(1, Banks(), Auto(3));
        executor.DrawAuto(2, Banks(), Auto(3));
        executor.DrawAuto(3, Banks(PrimitiveTriangleStrip), Auto(4));

        Assert.Equal(2, guest.Host.GraphicsPipelines.Count);
        Assert.Equal(2, cache.GraphicsPipelineCount);
    }

    [Fact]
    public void GraphicsPrograms_UseTheResolvedAttachmentExportMapping()
    {
        const ulong vertexAddress = PipelineTestGuest.MemoryBase + 0x1000;
        const ulong pixelAddress = PipelineTestGuest.MemoryBase + 0x2000;
        var guest = new PipelineTestGuest();
        guest.RegisterProgram(
            vertexAddress,
            PipelineTestGuest.MemoryBase + 0x8000,
            PipelineTestGuest.EndProgram,
            userDataAddress: PipelineTestGuest.MemoryBase + 0x9000);
        guest.RegisterProgram(
            pixelAddress,
            PipelineTestGuest.MemoryBase + 0x8100,
            PipelineTestGuest.EndProgram,
            userDataAddress: PipelineTestGuest.MemoryBase + 0xA000);
        var cache = new ShaderPipelineCache(guest.Context, guest.Host, guest.Compiler, guest.Registry);
        var banks = Banks();
        banks.Shader.Vertex.ExportAddress = vertexAddress;
        banks.Shader.Pixel.Address = pixelAddress;
        banks.Context.ShaderInterface.TargetOutputModes[0] = 4;
        var mappings = Enumerable.Repeat(ColorComponentMap.Identity, PixelInputInfo.TargetCount).ToArray();
        mappings[0] = ColorComponentMap.Argb;

        var programs = cache.GetGraphicsPrograms(
            banks.Shader.Vertex,
            banks.Shader.Pixel,
            banks.Context.ShaderInterface,
            banks.Context,
            banks.UserConfig,
            mappings,
            pixelActive: true,
            depthBound: false);

        Assert.True(programs.Available);
        var request = Assert.Single(
            guest.Compiler.Requests,
            request => request.Stage == ShaderStage.Pixel);
        var output = Assert.Single(request.PixelOutputs);
        Assert.Equal(ColorComponentMap.Argb.Packed, output.ComponentMapping.Packed);
        Assert.Equal(4, output.TargetOutputMode);
        Assert.Equal(ColorComponentMap.Argb, programs.PixelInput.TargetExportMappings[0]);
    }

    [Fact]
    public void GraphicsPrograms_CompactTheHostLocationAcrossUnboundGuestSlots()
    {
        const ulong vertexAddress = PipelineTestGuest.MemoryBase + 0x1000;
        const ulong pixelAddress = PipelineTestGuest.MemoryBase + 0x2000;
        var guest = new PipelineTestGuest();
        guest.RegisterProgram(
            vertexAddress,
            PipelineTestGuest.MemoryBase + 0x8000,
            PipelineTestGuest.EndProgram,
            userDataAddress: PipelineTestGuest.MemoryBase + 0x9000);
        guest.RegisterProgram(
            pixelAddress,
            PipelineTestGuest.MemoryBase + 0x8100,
            PipelineTestGuest.EndProgram,
            userDataAddress: PipelineTestGuest.MemoryBase + 0xA000);
        var cache = new ShaderPipelineCache(guest.Context, guest.Host, guest.Compiler, guest.Registry);
        var banks = Banks();
        banks.Shader.Vertex.ExportAddress = vertexAddress;
        banks.Shader.Pixel.Address = pixelAddress;
        banks.Context.ColorTargets[1] = RegisterWords.Color(SecondColorBase, 64, 64);
        banks.Context.RenderTargetMask = 0xFF;
        var mappings = Enumerable.Repeat(ColorComponentMap.Identity, PixelInputInfo.TargetCount).ToArray();

        var programs = cache.GetGraphicsPrograms(
            banks.Shader.Vertex,
            banks.Shader.Pixel,
            banks.Context.ShaderInterface,
            banks.Context,
            banks.UserConfig,
            mappings,
            boundColorSlots: 0b10,
            pixelActive: true,
            depthBound: false);

        Assert.True(programs.Available);
        var request = Assert.Single(
            guest.Compiler.Requests,
            request => request.Stage == ShaderStage.Pixel);
        var output = Assert.Single(request.PixelOutputs);
        Assert.Equal(1u, output.GuestSlot);
        Assert.Equal(0u, output.HostLocation);
    }

    [Fact]
    public void MeshGraphicsPrograms_SkipFixedFunctionVertexTablesAndKeepClipTransform()
    {
        const ulong entryCode = PipelineTestGuest.MemoryBase + 0x1000;
        const ulong continuationCode = PipelineTestGuest.MemoryBase + 0x2000;
        const ulong entryHeader = PipelineTestGuest.MemoryBase + 0x8000;
        const ulong continuationHeader = PipelineTestGuest.MemoryBase + 0x8100;
        const ulong userData = PipelineTestGuest.MemoryBase + 0x9000;
        const ulong directOffsets = PipelineTestGuest.MemoryBase + 0x9100;
        const ulong semantics = PipelineTestGuest.MemoryBase + 0x9200;
        var guest = new PipelineTestGuest();
        guest.Host.MeshShadersSupported = true;
        guest.RegisterProgram(
            entryCode,
            entryHeader,
            [0xBF800000u, 0xBE802000u],
            userDataAddress: userData,
            inputSemanticsAddress: semantics,
            inputSemanticsCount: 1,
            scratchDwords: 5);
        guest.RegisterProgram(
            continuationCode,
            continuationHeader,
            [0xBF800000u, 0xBF810000u],
            scratchDwords: 13);
        guest.RegisterFusedProgram(entryCode, continuationCode);
        WriteVertexTableMetadata(guest, userData, directOffsets, semantics);

        var cache = new ShaderPipelineCache(guest.Context, guest.Host, guest.Compiler, guest.Registry);
        var banks = Banks(withPixel: false);
        banks.Shader.Vertex.ExportAddress = entryCode;
        banks.Shader.Vertex.GeometryAddress = continuationCode;
        banks.Shader.Vertex.GeometryResource1 = new GeometryResource1 { GeometryVectorComponentCount = 3 };
        banks.Shader.Vertex.GeometryResource2 = new GeometryResource2 { ExportVectorComponentCount = 3 };
        banks.Context.ShaderStages = LegacyFusedGeometryStageMask | 0x0040_0000;
        banks.Context.ShaderInterface.VertexOutputControl = 0x1234;
        banks.Context.ShaderInterface.MaxOutputPerSubgroup = 192;
        banks.Context.ShaderInterface.GeometryMaxVerticesOut = 3;
        banks.Context.ShaderInterface.GeometryOutputPrimitiveType = 2;
        banks.Context.Clip = new ClipControlRegisters { ClipDisable = true };
        banks.Context.ScreenViewport.Viewports[0] = new ViewportRegisters
        {
            XScale = 3,
            YScale = 4,
            XOffset = 5,
            YOffset = 6,
        };
        banks.UserConfig.GeometryEngineControl = new GeometryEngineControlRegisters
        {
            PrimitiveGroupSize = 64,
            VertexGroupSize = 64,
        };

        var programs = cache.GetGraphicsPrograms(
            banks.Shader.Vertex,
            banks.Shader.Pixel,
            banks.Context.ShaderInterface,
            banks.Context,
            banks.UserConfig,
            [],
            pixelActive: false,
            depthBound: false);

        Assert.True(programs.Available);
        Assert.Empty(programs.VertexInput.Buffers);
        Assert.Empty(programs.VertexInput.Attributes);
        Assert.False(programs.VertexInput.FetchEmbedded);
        Assert.Equal(0u, programs.VertexInput.ScratchDwords);
        Assert.Equal(0x1234u, programs.VertexInput.PositionExportControl);
        Assert.Equal(
            new ClipSpaceTransform(true, 3, 4, 5, 6, 8192, 8192),
            programs.VertexInput.ClipSpace);
        Assert.True(programs.VertexInput.Mesh.IsActive);
        Assert.Equal(13u, programs.VertexInput.Mesh.ScratchDwords);
        Assert.Null(programs.PositionStream);
        var meshRequest = Assert.Single(
            guest.Compiler.Requests,
            request => request.Stage == ShaderStage.Mesh);
        Assert.Equal(0x1234u, meshRequest.PositionExportControl);
        Assert.Equal(
            new ShaderClipSpaceTransform(true, 3, 4, 5, 6, 8192, 8192),
            meshRequest.ClipSpace);
    }

    [Fact]
    public void ConventionalVertexProgram_StillRejectsNullDeclaredVertexTables()
    {
        const ulong code = PipelineTestGuest.MemoryBase + 0x1000;
        const ulong header = PipelineTestGuest.MemoryBase + 0x8000;
        const ulong userData = PipelineTestGuest.MemoryBase + 0x9000;
        const ulong directOffsets = PipelineTestGuest.MemoryBase + 0x9100;
        const ulong semantics = PipelineTestGuest.MemoryBase + 0x9200;
        var guest = new PipelineTestGuest();
        guest.RegisterProgram(
            code,
            header,
            PipelineTestGuest.EndProgram,
            userDataAddress: userData,
            inputSemanticsAddress: semantics,
            inputSemanticsCount: 1);
        WriteVertexTableMetadata(guest, userData, directOffsets, semantics);
        var cache = new ShaderPipelineCache(guest.Context, guest.Host, guest.Compiler, guest.Registry);
        var banks = Banks(withPixel: false);
        banks.Shader.Vertex.ExportAddress = code;

        var fatal = Assert.Throws<SchedulerFatalException>(() => cache.GetGraphicsPrograms(
            banks.Shader.Vertex,
            banks.Shader.Pixel,
            banks.Context.ShaderInterface,
            banks.Context,
            banks.UserConfig,
            [],
            pixelActive: false,
            depthBound: false));

        Assert.Contains("vertex table pointer is null", fatal.Message);
    }

    private static void WriteVertexTableMetadata(
        PipelineTestGuest guest,
        ulong userDataAddress,
        ulong directOffsetsAddress,
        ulong semanticsAddress)
    {
        var userData = new byte[0x30];
        BinaryPrimitives.WriteUInt64LittleEndian(userData, directOffsetsAddress);
        BinaryPrimitives.WriteUInt16LittleEndian(userData.AsSpan(0x2C), 11);
        guest.Write(userDataAddress, userData);

        var offsets = new byte[11 * sizeof(ushort)];
        for (var index = 0; index < 11; index++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(offsets.AsSpan(index * sizeof(ushort)), 0xFFFF);
        }
        BinaryPrimitives.WriteUInt16LittleEndian(offsets.AsSpan(8 * sizeof(ushort)), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(offsets.AsSpan(10 * sizeof(ushort)), 2);
        guest.Write(directOffsetsAddress, offsets);
        guest.WriteWords(semanticsAddress, 4u << 16);
    }

    // Programs from the fixtures, pipelines from the real cache over the fake host.
    private sealed class CachingProvider(ShaderPipelineCache cache) : IShaderPipelineProvider
    {
        private readonly GraphicsPrograms _programs = Programs();

        public GraphicsPrograms GetGraphicsPrograms(VertexStageRegisters vertex, PixelStageRegisters pixel, ShaderInterfaceRegisters shaderInterface, ContextRegisters context, UserConfigRegisters userConfig, ReadOnlySpan<ColorComponentMap> targetExportMapping, bool pixelActive, bool depthBound) => _programs;

        public PipelineHandle CreateGraphicsPipeline(ReadOnlySpan<ColorTargetState> colors, in DepthAttachmentState depth, VertexInputInfo vertexInput, PixelInputInfo? pixelInput, ContextRegisters context, in RenderingState rendering, PrimitiveTopology topology, bool primitiveRestartEnabled, bool disableBlending, ShaderProgram vertexProgram, ShaderProgram pixelProgram) =>
            cache.CreateGraphicsPipeline(colors, in depth, vertexInput, pixelInput, context, in rendering, topology, primitiveRestartEnabled, disableBlending, vertexProgram, pixelProgram);

        public ComputeProgram GetComputeProgram(ComputeStageRegisters compute, ShaderInterfaceRegisters shaderInterface, uint dispatchInitiator, uint dimensionX, uint dimensionY, uint dimensionZ) =>
            throw new InvalidOperationException("The caching provider has no compute program.");

        public PipelineHandle CreateComputePipeline(ComputeInputInfo input, ShaderProgram program) => cache.CreateComputePipeline(input, program);
    }

    [Fact]
    public void NoAttachments_TakeTheSampleCountFromTheAntialiasingConfig()
    {
        var banks = Banks();
        banks.Context.AntialiasingConfig.SampleCountLog2 = 2;
        var programs = Programs();
        var rendering = new RenderingState { Samples = 1 };

        var description = ShaderPipelineCache.BuildGraphicsDescription(
            [], default, programs.VertexInput, programs.PixelInput, banks.Context, in rendering, PrimitiveTopology.TriangleList, false, false,
            programs.Vertex, programs.Pixel, SampleCountFlags.Count1Bit | SampleCountFlags.Count4Bit);

        Assert.Equal(4u, description.StaticParameters.Samples);
        Assert.Equal(0u, description.StaticParameters.ColorCount);
    }

    [Theory]
    [InlineData(0x0u, 0x0u)]
    [InlineData(0xFu, 0xFu)]
    public void ColorTarget_TheProgramNeverExports_IsNotWritten(uint exportMasks, uint expectedMask)
    {
        // Astro Bot's depth-only pattern pass keeps a color target bound but exports only to
        // the null target; writing the undefined host output drew a rectangle over the movie.
        var banks = Banks();
        banks.Context.RenderTargetMask = 0xF;
        var programs = Programs(pixelStage: Stage(new ShaderProgramInfo { Stage = ShaderStageKind.Pixel, PixelColorExportMasks = exportMasks }));
        var resolution = new ColorTargetResolution(
            default, 0x1000, 0x10000, new Extent2D(64, 64), 0, 0, 1, ColorComponentMap.Identity, false, false, default);
        ColorTargetState[] colors = [new(in resolution, 0, new SharpEmu.Libs.Gpu.Buffers.ResourceSlotIdentifier(1, 1))];
        var rendering = new RenderingState { Samples = 1, ColorAttachmentCount = 1 };
        rendering.ColorAttachments[0] = new RenderingAttachment(
            default, ImageLayout.ColorAttachmentOptimal, Format.R8G8B8A8Unorm, 0, 0, 0, 0, false, false, false, false, false);

        var description = ShaderPipelineCache.BuildGraphicsDescription(
            colors, default, programs.VertexInput, programs.PixelInput, banks.Context, in rendering, PrimitiveTopology.TriangleList, false, false,
            programs.Vertex, programs.Pixel, SampleCountFlags.Count1Bit);

        Assert.Equal(expectedMask, description.StaticParameters.GetColorMask(0));
    }

    [Fact]
    public void ColorTarget_WithoutAPixelStage_IsNotWritten()
    {
        var banks = Banks();
        banks.Context.RenderTargetMask = 0xF;
        var programs = Programs();
        var resolution = new ColorTargetResolution(
            default, 0x1000, 0x10000, new Extent2D(64, 64), 0, 0, 1, ColorComponentMap.Identity, false, false, default);
        ColorTargetState[] colors = [new(in resolution, 0, new SharpEmu.Libs.Gpu.Buffers.ResourceSlotIdentifier(1, 1))];
        var rendering = new RenderingState { Samples = 1, ColorAttachmentCount = 1 };
        rendering.ColorAttachments[0] = new RenderingAttachment(
            default, ImageLayout.ColorAttachmentOptimal, Format.R8G8B8A8Unorm, 0, 0, 0, 0, false, false, false, false, false);

        var description = ShaderPipelineCache.BuildGraphicsDescription(
            colors, default, programs.VertexInput, null, banks.Context, in rendering, PrimitiveTopology.TriangleList, false, false,
            programs.Vertex, programs.Pixel, SampleCountFlags.Count1Bit);

        Assert.Equal(0u, description.StaticParameters.GetColorMask(0));
    }

    [Fact]
    public void NoAttachments_AtAnUnsupportedSampleCount_AreFatal()
    {
        var banks = Banks();
        banks.Context.AntialiasingConfig.SampleCountLog2 = 3;
        var programs = Programs();
        var rendering = new RenderingState { Samples = 1 };

        var fatal = Assert.Throws<SchedulerFatalException>(() => ShaderPipelineCache.BuildGraphicsDescription(
            [], default, programs.VertexInput, programs.PixelInput, banks.Context, in rendering, PrimitiveTopology.TriangleList, false, false,
            programs.Vertex, programs.Pixel, SampleCountFlags.Count1Bit | SampleCountFlags.Count4Bit));

        Assert.Contains("samples=8", fatal.Message);
    }

    [Theory]
    [InlineData(true, 2, true)]
    [InlineData(false, 2, false)]
    [InlineData(true, 0, false)]
    public void SampleShading_IsDerivedFromThePixelInputsAndTheSampleCount(bool pixelSampleShading, byte sampleCountLog2, bool expected)
    {
        var banks = Banks();
        banks.Context.AntialiasingConfig.SampleCountLog2 = sampleCountLog2;
        var programs = Programs();
        var pixelInput = new PixelInputInfo { InputCount = 1, SampleShading = pixelSampleShading, Stage = programs.PixelInput.Stage };
        var rendering = new RenderingState { Samples = 1 };

        var description = ShaderPipelineCache.BuildGraphicsDescription(
            [], default, programs.VertexInput, pixelInput, banks.Context, in rendering, PrimitiveTopology.TriangleList, false, false,
            programs.Vertex, programs.Pixel, SampleCountFlags.Count1Bit | SampleCountFlags.Count4Bit);

        Assert.Equal(expected, description.StaticParameters.SampleShadingEnable);
    }

    [Fact]
    public void MissingPrograms_AreFatal()
    {
        var banks = Banks();
        var programs = Programs();
        var rendering = new RenderingState { Samples = 1 };

        var noVertex = Assert.Throws<SchedulerFatalException>(() => ShaderPipelineCache.BuildGraphicsDescription(
            [], default, programs.VertexInput, programs.PixelInput, banks.Context, in rendering, PrimitiveTopology.TriangleList, false, false,
            default, programs.Pixel, SampleCountFlags.Count1Bit));
        Assert.Contains("no vertex program", noVertex.Message);

        var noPixel = Assert.Throws<SchedulerFatalException>(() => ShaderPipelineCache.BuildGraphicsDescription(
            [], default, programs.VertexInput, programs.PixelInput, banks.Context, in rendering, PrimitiveTopology.TriangleList, false, false,
            programs.Vertex, default, SampleCountFlags.Count1Bit));
        Assert.Contains("without a pixel program", noPixel.Message);
    }
}
