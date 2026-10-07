// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using Silk.NET.Vulkan;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Rendering.RenderExecutorFixtures;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

// The graphics pipeline description folds the draw's static state byte for byte; the keys fold all of it.
[Collection(SchedulingStateCollection.Name)]
public sealed class ShaderPipelineCacheTests : IDisposable
{
    private const uint Format32x4Float = 77;

    private readonly FatalScope _fatal = new();

    public void Dispose() => _fatal.Dispose();

    [Fact]
    public void IeeeModeChangesEveryStageKey()
    {
        var ordinary = new List<uint>();
        var ieee = new List<uint>();
        StageStaticKey.Build(new VertexInputInfo(), 0, ordinary);
        StageStaticKey.Build(new VertexInputInfo { IeeeMode = true }, 0, ieee);
        Assert.False(ordinary.SequenceEqual(ieee));
        StageStaticKey.Build(new PixelInputInfo(), ordinary);
        StageStaticKey.Build(new PixelInputInfo { IeeeMode = true }, ieee);
        Assert.False(ordinary.SequenceEqual(ieee));
        StageStaticKey.Build(new ComputeInputInfo(), ordinary);
        StageStaticKey.Build(new ComputeInputInfo { IeeeMode = true }, ieee);
        Assert.False(ordinary.SequenceEqual(ieee));
        StageStaticKey.Build(new MeshDrawConfiguration(), 0, ordinary);
        StageStaticKey.Build(new MeshDrawConfiguration
        {
            Geometry = new GuestGeometryConfiguration { IeeeMode = true },
        }, 0, ieee);
        Assert.False(ordinary.SequenceEqual(ieee));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void SecondaryBlendExportDoesNotRequireAnotherAttachment(bool enabled, bool bypass)
    {
        var guest = new PipelineTestGuest();
        var vertexAddress = PipelineTestGuest.MemoryBase + 0x1000;
        var pixelAddress = PipelineTestGuest.MemoryBase + 0x2000;
        guest.RegisterProgram(vertexAddress, PipelineTestGuest.MemoryBase + 0x8000, PipelineTestGuest.EndProgram,
            userDataAddress: PipelineTestGuest.MemoryBase + 0x9000);
        guest.RegisterProgram(pixelAddress, PipelineTestGuest.MemoryBase + 0x8100, PipelineTestGuest.EndProgram);
        var cache = new ShaderPipelineCache(guest.Context, guest.Host, guest.Compiler, guest.Registry);
        var banks = Banks();
        banks.Shader.Vertex.ExportAddress = vertexAddress;
        banks.Shader.Pixel.Address = pixelAddress;
        banks.Context.BlendControls[0] = new BlendRegisters { Enable = enabled, ColorDestinationFactor = 15 };
        if (bypass)
        {
            banks.Context.ColorTargets[0] = banks.Context.ColorTargets[0] with
            {
                Info = banks.Context.ColorTargets[0].Info | (1u << 16),
            };
        }
        var mappings = Enumerable.Repeat(ColorComponentMap.Identity, 8).ToArray();
        GraphicsPrograms Prepare() => cache.GetGraphicsPrograms(banks.Shader.Vertex, banks.Shader.Pixel,
            banks.Context.ShaderInterface, banks.Context, mappings, pixelActive: true, depthBound: false);
        Assert.True(Prepare().Available);
        var outputs = guest.Compiler.Requests[0].PixelOutputs;
        Assert.Equal(enabled && !bypass ? 2 : 1, outputs.Count);
        if (enabled && !bypass)
        {
            Assert.Equal(0u, outputs[1].HostLocation);
            Assert.Equal(1u, outputs[1].BlendSourceIndex);
            Assert.Equal(1u, outputs[1].ExportTarget);
            banks.Context.BlendControls[0].Enable = false;
            Assert.True(Prepare().Available);
            Assert.Single(guest.Compiler.Requests.Last(request => request.Stage == SharpEmu.ShaderCompiler.Resources.ShaderStage.Pixel).PixelOutputs);
        }
    }

    [Fact]
    public void FusedMeshPointerUpdatesWithoutCompilingAnotherProgram()
    {
        var guest = new PipelineTestGuest();
        var address = PipelineTestGuest.MemoryBase + 0x1000;
        var header = PipelineTestGuest.MemoryBase + 0x8000;
        var continuation = address + 0x100;
        guest.RegisterProgram(address, header, [0xBE940400, 0xBE960308, 0xBE970303, 0xBF810000]);
        guest.RegisterProgram(continuation, header + 0x100, PipelineTestGuest.EndProgram);
        var registry = new ShaderHeaderRegistry(guest.Context, _ => header,
            _ => new FusedProgramParts(continuation, header + 0x100));
        guest.Host.MeshShadersSupported = true;
        guest.Host.MeshLimits = new MeshShaderLimits(128, 256, 256, 32768, 65535, 65535, 65535,
            128, 32768, 32768, 32, 32, 128);
        var cache = new ShaderPipelineCache(guest.Context, guest.Host, guest.Compiler, registry);
        var banks = Banks();
        banks.Shader.Vertex.ExportAddress = address;
        banks.Shader.Vertex.GeometryUserDataAddress = 0x2012345678;
        banks.Shader.Vertex.GeometryUserScalars.Set(0, 123, UserScalarKind.Unknown);
        banks.Context.ShaderStages = 0x00400020;
        banks.UserConfig.PrimitiveType = 4;
        banks.Context.ShaderInterface.MaxOutputPerSubgroup = 32;
        banks.Context.ShaderInterface.GeometryMaxVerticesOut = 3;
        banks.Context.ShaderInterface.GeometryOutputPrimitiveType = 2;
        banks.UserConfig.GeometryEngineControl.PrimitiveGroupSize = 8;
        banks.UserConfig.GeometryEngineControl.VertexGroupSize = 24;
        GraphicsPrograms Prepare() => cache.GetMeshGraphicsPrograms(banks.Shader.Vertex, banks.Shader.Pixel,
            banks.Context.ShaderInterface, banks.Context, banks.UserConfig, [], pixelActive: false, depthBound: false);

        var first = Prepare().VertexInput.Stage;
        Assert.Equal(0u, first.Program!.UserDataBase);
        Assert.Equal(new uint[] { 0, 1, 8 }, first.Program.Bindings!.UserDataRegisters);
        Assert.Equal(0x12345678u, first.Resources.UserData[0]);
        Assert.Equal(0x20u, first.Resources.UserData[1]);
        Assert.Equal(123u, first.Resources.UserData[8]);
        banks.Shader.Vertex.GeometryUserDataAddress = 0x3198765432;
        var second = Prepare().VertexInput.Stage;
        Assert.Equal(0x98765432u, second.Resources.UserData[0]);
        Assert.Equal(0x31u, second.Resources.UserData[1]);
        Assert.Equal(1, guest.Compiler.Compilations);
    }

    [Fact]
    public void MeshDescriptorsUseTheMeshPipelineStage()
    {
        Assert.Equal(PipelineStageFlags.MeshShaderBitExt,
            DescriptorWriter.PipelineStageFlag(ShaderStageFlags.MeshBitExt));
        Assert.Equal(PipelineStageFlags.MeshShaderBitExt | PipelineStageFlags.FragmentShaderBit,
            DescriptorWriter.PipelineStageFlag(ShaderStageFlags.MeshBitExt | ShaderStageFlags.FragmentBit));
    }

    [Theory]
    [InlineData(false, "")]
    [InlineData(true, "")]
    [InlineData(false, "workgroup")]
    [InlineData(false, "output")]
    [InlineData(false, "payload-output")]
    [InlineData(false, "exact-output")]
    [InlineData(false, "components")]
    [InlineData(false, "", true)]
    [InlineData(true, "", true)]
    public void MeshProgramChecksDeviceLimitsAndRetainsPixelStage(bool triangleStrip, string limit, bool depthBound = false)
    {
        var guest = new PipelineTestGuest();
        var vertexAddress = PipelineTestGuest.MemoryBase + 0x1000;
        var pixelAddress = PipelineTestGuest.MemoryBase + 0x2000;
        guest.RegisterProgram(vertexAddress, PipelineTestGuest.MemoryBase + 0x8000, PipelineTestGuest.EndProgram);
        guest.RegisterProgram(pixelAddress, PipelineTestGuest.MemoryBase + 0x8100, PipelineTestGuest.EndProgram);
        guest.Host.MeshShadersSupported = true;
        guest.Host.MeshLimits = new MeshShaderLimits(128, 256, 256, 32768, 65535, 65535, 65535,
            128, 32768, 32768, 32, 32, 128);
        guest.Host.MeshLimits = limit switch
        {
            "workgroup" => guest.Host.MeshLimits with { MaxWorkGroupSizeX = 31 },
            "components" => guest.Host.MeshLimits with { MaxOutputComponents = 8 },
            "output" => guest.Host.MeshLimits with { MaxOutputMemoryBytes = 1535 },
            "payload-output" => guest.Host.MeshLimits with { MaxPayloadAndOutputMemoryBytes = 1535 },
            "exact-output" => guest.Host.MeshLimits with { MaxOutputMemoryBytes = 1536, MaxPayloadAndOutputMemoryBytes = 1536 },
            _ => guest.Host.MeshLimits,
        };
        var cache = new ShaderPipelineCache(guest.Context, guest.Host, guest.Compiler, guest.Registry);
        var banks = Banks();
        banks.Shader.Vertex.ExportAddress = vertexAddress;
        banks.Shader.Vertex.GeometryAddress = 0;
        banks.Shader.Pixel.Address = pixelAddress;
        banks.Context.ShaderStages = triangleStrip ? 0x20u : 0x00400020u;
        banks.UserConfig.PrimitiveType = triangleStrip ? 6u : 4u;
        banks.Context.ShaderInterface.MaxOutputPerSubgroup = triangleStrip ? 192u : 32u;
        banks.Context.ShaderInterface.GeometryMaxVerticesOut = 3;
        banks.Context.ShaderInterface.GeometryOutputPrimitiveType = 2;
        banks.UserConfig.GeometryEngineControl.PrimitiveGroupSize = 8;
        banks.UserConfig.GeometryEngineControl.VertexGroupSize = 24;
        var mapping = Enumerable.Repeat(ColorComponentMap.Identity, 8).ToArray();

        GraphicsPrograms Prepare() => cache.GetMeshGraphicsPrograms(banks.Shader.Vertex, banks.Shader.Pixel,
            banks.Context.ShaderInterface, banks.Context, banks.UserConfig, mapping, pixelActive: true, depthBound);
        if (limit is "components")
        {
            var error = Assert.Throws<SchedulerFatalException>(() => Prepare());
            Assert.Contains("device component limit", error.Message);
            return;
        }
        if (limit is "output" or "payload-output")
        {
            var error = Assert.Throws<SchedulerFatalException>(() => Prepare());
            Assert.Contains("mesh output exceeds device memory limits", error.Message);
            return;
        }
        var programs = Prepare();
        if (limit == "workgroup")
        {
            Assert.False(programs.Available);
            Assert.Equal(0, guest.Compiler.Compilations);
            return;
        }

        Assert.True(programs.Available);
        Assert.NotNull(programs.MeshInput);
        Assert.Equal(triangleStrip ? 192u : 32u, programs.MeshInput.Geometry.ThreadsPerGroup);
        Assert.Equal(triangleStrip ? 10u : 24u, programs.MeshInput.Geometry.InputVertexCountPerWorkgroup);
        Assert.Equal(triangleStrip, programs.MeshInput.Geometry.InputTriangleStrip);
        Assert.Equal(ShaderStageKind.Mesh, programs.VertexInput.Stage.Program!.Stage);
        Assert.Equal(ShaderStageKind.Pixel, programs.PixelInput.Stage.Program!.Stage);
        Assert.Equal(2, guest.Compiler.Compilations);
        Assert.Equal(depthBound ? 0 : 1, guest.Compiler.Requests[0].PixelOutputs.Count);
        Assert.Null(programs.SolidClear);
    }

    // Forwards each pipeline request to the static builder and keeps the description.
    private sealed class DescribingProvider : IShaderPipelineProvider
    {
        public GraphicsPrograms Graphics { get; set; } = Programs();

        public List<GraphicsPipelineDescription> Descriptions { get; } = new();

        public GraphicsPrograms GetGraphicsPrograms(VertexStageRegisters vertex, PixelStageRegisters pixel, ShaderInterfaceRegisters shaderInterface, ContextRegisters context, ReadOnlySpan<ColorComponentMap> targetExportMapping, bool pixelActive, bool depthBound) => Graphics;

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

    private static GraphicsPipelineDescription Describe(Action<RegisterBanks>? configure = null, GraphicsPrograms? programs = null, uint primitiveType = PrimitiveTriangleList, bool withDepth = false)
    {
        var host = new RecordingRenderHost();
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
        Assert.False(parameters.WithDepth);
        Assert.False(parameters.StencilTestEnable);
        Assert.Equal(Format.Undefined, description.Rendering.DepthFormat);
        Assert.NotEqual(Format.Undefined, description.Rendering.ColorFormats[0]);
        Assert.Equal(1u, description.Rendering.ColorCount);
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

    [Fact]
    public void RectangleList_DisablesCullingAndKeepsThePatchTopology()
    {
        var description = Describe(banks => banks.Context.RasterMode.CullBack = true, primitiveType: 7);

        Assert.Equal(PrimitiveTopology.PatchList, description.StaticParameters.Topology);
        Assert.False(description.StaticParameters.CullBack);
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
    public void GraphicsPipelineKey_FoldsAll166StaticBytes()
    {
        var description = Describe();
        var key = ShaderPipelineCache.KeyOf(description);
        Assert.Equal(key, ShaderPipelineCache.KeyOf(With(description, PipelineStaticParameters.FromBytes(description.StaticParameters.Bytes))));
        Assert.Equal(166, description.StaticParameters.Bytes.Length);

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

    // Programs from the fixtures, pipelines from the real cache over the fake host.
    private sealed class CachingProvider(ShaderPipelineCache cache) : IShaderPipelineProvider
    {
        private readonly GraphicsPrograms _programs = Programs();

        public GraphicsPrograms GetGraphicsPrograms(VertexStageRegisters vertex, PixelStageRegisters pixel, ShaderInterfaceRegisters shaderInterface, ContextRegisters context, ReadOnlySpan<ColorComponentMap> targetExportMapping, bool pixelActive, bool depthBound) => _programs;

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
