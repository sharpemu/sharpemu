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

    // Forwards each pipeline request to the static builder and keeps the description.
    private sealed class DescribingProvider : IShaderPipelineProvider
    {
        public GraphicsPrograms Graphics { get; set; } = Programs();

        public List<GraphicsPipelineDescription> Descriptions { get; } = new();

        public GraphicsPrograms GetGraphicsPrograms(VertexStageRegisters vertex, PixelStageRegisters pixel, ShaderInterfaceRegisters shaderInterface, ContextRegisters context, ReadOnlySpan<ColorComponentMap> targetExportMapping, bool pixelActive) => Graphics;

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

        public GraphicsPrograms GetGraphicsPrograms(VertexStageRegisters vertex, PixelStageRegisters pixel, ShaderInterfaceRegisters shaderInterface, ContextRegisters context, ReadOnlySpan<ColorComponentMap> targetExportMapping, bool pixelActive) => _programs;

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
