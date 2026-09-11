// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.HLE.Host;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Gpu.Vulkan;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using SharpEmu.ShaderCompiler.Vulkan;
using Silk.NET.Vulkan;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Images.ImageCacheTestSupport;

namespace SharpEmu.Libs.Tests.VideoOut;

// The executor over the presenter's render host on a real device: each draw renders the guest bytes it was recorded with.
[Collection(SchedulingStateCollection.Name)]
public sealed unsafe class RenderHostDeviceTests : IClassFixture<HeadlessVulkanFixture>
{
    private const uint Size = 64;
    private const uint VertexStride = 8;
    private const uint VertexCount = 3;
    private const uint Red = 0xFF0000FF;

    private readonly HeadlessVulkan? _vulkan;

    [Fact]
    public void GlobalBarrierEndsDynamicRendering()
    {
        if (!Ready()) return;
        using var presenter = new PresenterUnderTest(_vulkan!);
        presenter.LoadRenderingCommands();
        presenter.Run(() =>
        {
            var state = new RenderingState { Width = 1, Height = 1, Layers = 1, Samples = 1 };
            presenter.RenderHost.BeginRendering(in state);
            ((ICommandStreamHost)presenter.Instance).EmitGlobalBarrier();
            Assert.False(presenter.GetField<bool>("_renderingActive"));
        });
        presenter.Harness.Finish();
        presenter.Harness.Shutdown();
    }

    [Fact]
    public void BeginPreparation_AcquiresMovieFrameOnceAndRetainsItUntilReplacement()
    {
        if (!Ready()) return;
        using var presenter = new PresenterUnderTest(_vulkan!);
        presenter.LoadRenderingCommands();
        var bridgeType = typeof(SharpEmu.Libs.Media.HostMovieBridge);
        var fieldNames = new[] { "_activePath", "_activeInfo", "_frameBuffer", "_frameBufferPresented", "_playback", "_frameSerial" };
        var fields = fieldNames.Select(name => bridgeType.GetField(name,
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!).ToArray();
        var savedValues = fields.Select(field => field.GetValue(null)).ToArray();
        try
        {
            byte[] firstFrame = [10, 20, 30, 255];
            fields[0].SetValue(null, "movie.bk2");
            fields[1].SetValue(null, new SharpEmu.Libs.Media.HostMovieBridge.Bink2MovieInfo(1, 1, 30, 1));
            fields[2].SetValue(null, firstFrame);
            fields[3].SetValue(null, false);
            fields[4].SetValue(null, null);
            fields[5].SetValue(null, 100L);
            presenter.Run(() =>
            {
                using (presenter.RenderHost.BeginPreparation())
                {
                    Assert.Same(firstFrame, presenter.GetField<byte[]>("_hostMovieFramePixels"));
                    Assert.Equal(101L, presenter.GetField<long>("_hostMovieFrameSerial"));
                    Assert.Equal(1u, presenter.GetField<uint>("_hostMovieFrameWidth"));
                }

                using (presenter.RenderHost.BeginPreparation())
                {
                    Assert.Equal(101L, presenter.GetField<long>("_hostMovieFrameSerial"));
                }

                fields[2].SetValue(null, null);
                using (presenter.RenderHost.BeginPreparation())
                {
                    Assert.Same(firstFrame, presenter.GetField<byte[]>("_hostMovieFramePixels"));
                }

                byte[] nextFrame = [40, 50, 60, 255];
                fields[2].SetValue(null, nextFrame);
                fields[3].SetValue(null, false);
                using (presenter.RenderHost.BeginPreparation())
                {
                    Assert.Same(nextFrame, presenter.GetField<byte[]>("_hostMovieFramePixels"));
                    Assert.Equal(102L, presenter.GetField<long>("_hostMovieFrameSerial"));
                }
            });
        }
        finally
        {
            for (var index = 0; index < fields.Length; index++) fields[index].SetValue(null, savedValues[index]);
        }

        presenter.Harness.Finish();
        presenter.Harness.Shutdown();
    }

    public RenderHostDeviceTests(HeadlessVulkanFixture fixture) => _vulkan = fixture.Vulkan;

    // One float2 position program and one solid red pixel program, laid out as the provider lays out a draw.
    private sealed class FixedProgramProvider(AgcExports.IHostPipelineFactory factory, ulong vertexAddress) : IShaderPipelineProvider
    {
        private readonly AgcExports.CompiledStageProgram _vertex = new()
        {
            Shader = new VulkanCompiledGuestShader(CreatePositionVertexShader()),
            Stage = ShaderStageKind.Vertex,
            Hash = 1,
            VertexAttributes = [new AgcExports.VertexAttributeLayout(0, 0, 2, 11, 7, 0, false)],
        };

        private readonly AgcExports.CompiledStageProgram _pixel = new()
        {
            Shader = new VulkanCompiledGuestShader(SpirvFixedShaders.CreateSolidFragment(1f, 0f, 0f, 1f)),
            Stage = ShaderStageKind.Pixel,
            Hash = 2,
        };

        public GraphicsPrograms GetGraphicsPrograms(
            VertexStageRegisters vertex,
            PixelStageRegisters pixel,
            ShaderInterfaceRegisters shaderInterface,
            ContextRegisters context,
            ReadOnlySpan<ColorComponentMap> targetExportMapping,
            bool pixelActive) =>
            new()
            {
                Vertex = new ShaderProgram(1),
                Pixel = new ShaderProgram(2),
                VertexInput = new VertexInputInfo
                {
                    Buffers = [new VertexInputBuffer(vertexAddress, VertexStride, VertexCount)],
                    Stage = new ShaderStageResources(_vertex, new ResourceSnapshot()),
                },
                PixelInput = new PixelInputInfo { InputCount = 0, Stage = new ShaderStageResources(_pixel, new ResourceSnapshot()) },
            };

        public PipelineHandle CreateGraphicsPipeline(
            ReadOnlySpan<ColorTargetState> colors,
            in DepthAttachmentState depth,
            VertexInputInfo vertexInput,
            PixelInputInfo? pixelInput,
            ContextRegisters context,
            in RenderingState rendering,
            PrimitiveTopology topology,
            bool primitiveRestartEnabled,
            ShaderProgram vertexProgram,
            ShaderProgram pixelProgram) =>
            factory.CreateGraphicsPipeline(colors, in depth, vertexInput, pixelInput, context, in rendering, topology, primitiveRestartEnabled, vertexProgram, pixelProgram);

        public ComputeProgram GetComputeProgram(ComputeStageRegisters compute, ShaderInterfaceRegisters shaderInterface, uint dispatchInitiator) =>
            throw new InvalidOperationException("The test provider has no compute program.");

        public PipelineHandle CreateComputePipeline(ComputeInputInfo input, ShaderProgram program) =>
            throw new InvalidOperationException("The test provider has no compute pipeline.");
    }

    // position = (input.xy, 0, 1)
    private static byte[] CreatePositionVertexShader()
    {
        var module = new SpirvModuleBuilder();
        module.AddCapability(SpirvCapability.Shader);
        var voidType = module.TypeVoid();
        var floatType = module.TypeFloat(32);
        var vec2Type = module.TypeVector(floatType, 2);
        var vec4Type = module.TypeVector(floatType, 4);
        var inputPointer = module.TypePointer(SpirvStorageClass.Input, vec2Type);
        var outputPointer = module.TypePointer(SpirvStorageClass.Output, vec4Type);
        var input = module.AddGlobalVariable(inputPointer, SpirvStorageClass.Input);
        module.AddDecoration(input, SpirvDecoration.Location, 0);
        var position = module.AddGlobalVariable(outputPointer, SpirvStorageClass.Output);
        module.AddDecoration(position, SpirvDecoration.BuiltIn, (uint)SpirvBuiltIn.Position);
        var functionType = module.TypeFunction(voidType);
        var main = module.BeginFunction(voidType, functionType);
        module.AddLabel();
        var value = module.AddInstruction(SpirvOp.Load, vec2Type, input);
        var x = module.AddInstruction(SpirvOp.CompositeExtract, floatType, value, 0);
        var y = module.AddInstruction(SpirvOp.CompositeExtract, floatType, value, 1);
        var composed = module.AddInstruction(
            SpirvOp.CompositeConstruct, vec4Type, x, y, module.ConstantFloat(floatType, 0f), module.ConstantFloat(floatType, 1f));
        module.AddStatement(SpirvOp.Store, position, composed);
        module.AddStatement(SpirvOp.Return);
        module.EndFunction();
        module.AddEntryPoint(SpirvExecutionModel.Vertex, main, "main", [input, position]);
        return module.Build();
    }

    private static byte[] Triangle(float x0, float y0, float x1, float y1, float x2, float y2)
    {
        var bytes = new byte[VertexCount * VertexStride];
        Span<float> values = [x0, y0, x1, y1, x2, y2];
        for (var index = 0; index < values.Length; index++)
        {
            BitConverter.TryWriteBytes(bytes.AsSpan(index * sizeof(float)), values[index]);
        }

        return bytes;
    }

    private static RegisterBanks Banks(ColorTargetWords target)
    {
        var banks = new RegisterBanks(static message => new InvalidOperationException(message));
        var context = banks.Context;
        context.ColorTargets[0] = target;
        context.RenderTargetMask = 0xF;
        context.ShaderInterface.ColorShaderMask = 0xF;
        context.ScreenViewport.Viewports[0] = new ViewportRegisters
        {
            XScale = Size / 2f,
            XOffset = Size / 2f,
            YScale = Size / 2f,
            YOffset = Size / 2f,
            ZScale = 0.5f,
            ZOffset = 0.5f,
            MaxDepth = 1,
        };
        banks.Shader.Vertex.ExportAddress = 0x1000;
        banks.Shader.Pixel.Address = 0x2000;
        banks.UserConfig.PrimitiveType = 4;
        return banks;
    }

    private static DrawAutoArguments Draw() => new(0, 0, VertexCount, 1, 0, 0, DrawOffsetSource.Packet);

    private static uint Pixel(byte[] bytes, uint x, uint y) => BitConverter.ToUInt32(bytes, (int)(((y * Size) + x) * 4));

    private static CachedImage TargetImage(PresenterUnderTest presenter, ColorTargetWords words)
    {
        var resolution = ImageRequestBuilders.ColorTarget(words, 0xF, 0, ignoreTargetMask: false)!.Value;
        return presenter.Run(() =>
        {
            var request = resolution.Request;
            return presenter.Harness.Images.GetImage(presenter.Harness.Images.FindImage(ref request));
        });
    }

    [System.Diagnostics.CodeAnalysis.MemberNotNullWhen(true, nameof(_vulkan))]
    private bool Ready()
    {
        if (!GatePrerequisites.Ready(_vulkan))
        {
            return false;
        }

        if (!_vulkan.SupportsDynamicRendering)
        {
            Console.Error.WriteLine("[TEST][SKIP] The device lacks dynamic rendering or the extended dynamic state extensions.");
            return false;
        }

        return true;
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void PrepareBindings_InvalidatesImagesOnlyForFormattedWrites(bool formatted, bool writable)
    {
        if (!Ready()) return;
        using var presenter = new PresenterUnderTest(_vulkan!);
        presenter.SetField("_minStorageBufferOffsetAlignment", 256UL);
        presenter.LoadRenderingCommands();
        var harness = presenter.Harness;
        var address = harness.MapBacked(0x10000, ReadWrite);
        var image = TargetImage(presenter, RegisterWords.Color(address, Size, Size));
        var program = new AgcExports.CompiledStageProgram
        {
            Shader = new VulkanCompiledGuestShader([]),
            Stage = ShaderStageKind.Compute,
            Buffers = [new BufferResourceInfo(true, writable, false, formatted, false, 0x10000, 0)],
            GlobalBuffers = [new GuestMemoryBuffer(address, [], 0, 0x10000, false, writable)],
        };
        presenter.Run(() =>
        {
            Assert.False(image.IsBufferModified);
            using var preparation = presenter.RenderHost.BeginPreparation();
            presenter.RenderHost.PrepareBindings(new ShaderStageResources(program, new ResourceSnapshot()));
            Assert.Equal(formatted && writable, image.IsBufferModified);
        });
        harness.Finish();
        harness.Shutdown();
    }

    [Fact]
    public void TwoDraws_RenderTheVertexVersionEachWasRecordedWith()
    {
        if (!Ready())
        {
            return;
        }

        using var presenter = new PresenterUnderTest(_vulkan);
        presenter.LoadRenderingCommands();
        var harness = presenter.Harness;
        var firstTarget = harness.MapBacked(0x10000, ReadWrite);
        var secondTarget = harness.MapBacked(0x10000, ReadWrite);
        var vertices = harness.MapBacked(0x10000, ReadWrite);
        var firstWords = RegisterWords.Color(firstTarget, Size, Size);
        var secondWords = RegisterWords.Color(secondTarget, Size, Size);
        harness.Write(vertices, Triangle(-1f, -1f, 3f, -1f, -1f, 3f));
        var executor = new RenderExecutor(presenter.RenderHost, new FixedProgramProvider((AgcExports.IHostPipelineFactory)presenter.Instance, vertices));
        GuestGpuMemoryHook.Attach(harness.Gpu);
        try
        {
            presenter.Run(() => executor.DrawAuto(1, Banks(firstWords), Draw()));
            // The guarded write marks the pages dirty; the second obtain uploads the new bytes.
            Assert.True(harness.Memory.TryWrite(vertices, Triangle(-1f, -1f, -0.5f, -1f, -1f, -0.5f)));
            presenter.Run(() => executor.DrawAuto(2, Banks(secondWords), Draw()));
            presenter.Run(() => presenter.InvokeMethod("FlushBatchedGuestCommands", (object?)null));
            harness.Finish();

            var first = harness.ReadImageBytes(TargetImage(presenter, firstWords));
            var second = harness.ReadImageBytes(TargetImage(presenter, secondWords));
            Assert.Equal(Size * Size * 4, (uint)first.Length);
            Assert.All(Enumerable.Range(0, (int)(Size * Size)), index => Assert.Equal(Red, BitConverter.ToUInt32(first, index * 4)));
            Assert.Equal(Red, Pixel(second, 1, 1));
            Assert.Equal(0u, Pixel(second, Size / 2, Size / 2));
            Assert.Equal(0u, Pixel(second, Size - 2, Size - 2));
        }
        finally
        {
            GuestGpuMemoryHook.Attach(null);
        }

        harness.Shutdown();
    }

    [Fact]
    public void UploadTransient_TakesAHostBufferWhenTheRingWouldWrapOverThePreparation()
    {
        if (!Ready())
        {
            return;
        }

        using var presenter = new PresenterUnderTest(_vulkan);
        presenter.LoadRenderingCommands();
        var harness = presenter.Harness;
        var stream = harness.Cache.GetUtilityBuffer(GpuBufferUsage.Stream);
        var host = presenter.RenderHost;
        var chunk = new byte[stream.Size / 4];
        presenter.Run(() =>
        {
            using var preparation = host.BeginPreparation();
            var last = default(BufferBinding);
            for (var index = 0; index < 4; index++)
            {
                last = host.UploadTransient(chunk, 16);
                Assert.Equal(stream.Handle.Handle, last.Handle);
            }

            var overflow = host.UploadTransient(chunk, 16);
            Assert.NotEqual(stream.Handle.Handle, overflow.Handle);
            Assert.NotEqual(0UL, overflow.Handle);
            Assert.Equal(0UL, overflow.Offset);
        });
        harness.Finish();
        harness.Shutdown();
    }

    // A ring that would wrap over the preparation sends the expanded indices to a host buffer; the draw reads it.
    [Fact]
    public void OverflowIndexBuffer_IsConsumedByTheDrawAndRetiredWithIt()
    {
        if (!Ready())
        {
            return;
        }

        using var presenter = new PresenterUnderTest(_vulkan);
        presenter.LoadRenderingCommands();
        var harness = presenter.Harness;
        var target = harness.MapBacked(0x10000, ReadWrite);
        var vertices = harness.MapBacked(0x10000, ReadWrite);
        var indices = harness.MapBacked(0x10000, ReadWrite);
        var words = RegisterWords.Color(target, Size, Size);
        harness.Write(vertices, Triangle(-1f, -1f, 3f, -1f, -1f, 3f));
        harness.Write(indices, [0, 1, 2]);
        var stream = harness.Cache.GetUtilityBuffer(GpuBufferUsage.Stream);
        var chunk = new byte[stream.Size / 4];
        presenter.Run(() =>
        {
            for (var index = 0; index < 4; index++)
            {
                stream.Copy(chunk, 16);
            }
        });

        var executor = new RenderExecutor(presenter.RenderHost, new FixedProgramProvider((AgcExports.IHostPipelineFactory)presenter.Instance, vertices));
        presenter.Run(() => executor.DrawIndexed(1, Banks(words), new DrawIndexedArguments(0, 0, VertexCount, indices, 2, 1, 0, 0, DrawOffsetSource.Packet)));
        Assert.Equal(0UL, presenter.HostBuffers.CachedBytes);

        presenter.Run(() => presenter.InvokeMethod("FlushBatchedGuestCommands", (object?)null));
        harness.Finish();
        presenter.Run(() => presenter.InvokeMethod("WaitForAllGuestSubmissions"));
        Assert.Equal(8UL, presenter.HostBuffers.CachedBytes);
        var pixels = harness.ReadImageBytes(TargetImage(presenter, words));
        Assert.Equal(Red, Pixel(pixels, Size / 2, Size / 2));
        harness.Shutdown();
    }

    [Fact]
    public void PresenterDisposal_ReleasesPipelinesAndLayoutsAfterRecordedDraw()
    {
        if (!Ready()) return;
        var presenter = new PresenterUnderTest(_vulkan);
        var pipelines = presenter.GetField<System.Collections.IDictionary>("_pipelineEntries");
        var layouts = presenter.GetField<System.Collections.IDictionary>("_descriptorLayouts");
        using (presenter)
        {
            presenter.LoadRenderingCommands();
            var target = presenter.Harness.MapBacked(0x10000, ReadWrite);
            var vertices = presenter.Harness.MapBacked(0x10000, ReadWrite);
            presenter.Harness.Write(vertices, Triangle(-1f, -1f, 3f, -1f, -1f, 3f));
            var words = RegisterWords.Color(target, Size, Size);
            var executor = new RenderExecutor(presenter.RenderHost,
                new FixedProgramProvider((AgcExports.IHostPipelineFactory)presenter.Instance, vertices));
            presenter.Run(() => executor.DrawAuto(1, Banks(words), Draw()));
            Assert.NotEmpty(pipelines);
            Assert.NotEmpty(layouts);
        }

        Assert.Empty(pipelines);
        Assert.Empty(layouts);
        _vulkan.AssertNoValidationMessages();
    }

    [Fact]
    public void ConsecutiveDraws_ShareOneRenderingScopeAndCloseItBeforeTheSubmit()
    {
        if (!Ready())
        {
            return;
        }

        using var presenter = new PresenterUnderTest(_vulkan);
        presenter.LoadRenderingCommands();
        var harness = presenter.Harness;
        var target = harness.MapBacked(0x10000, ReadWrite);
        var vertices = harness.MapBacked(0x10000, ReadWrite);
        var words = RegisterWords.Color(target, Size, Size);
        harness.Write(vertices, Triangle(-1f, -1f, 3f, -1f, -1f, 3f));
        var executor = new RenderExecutor(presenter.RenderHost, new FixedProgramProvider((AgcExports.IHostPipelineFactory)presenter.Instance, vertices));
        var rendering = (IRenderingState)presenter.Instance;
        presenter.Run(() =>
        {
            executor.DrawAuto(1, Banks(words), Draw());
            Assert.True(rendering.IsRendering);
            executor.DrawAuto(2, Banks(words), Draw());
            Assert.True(rendering.IsRendering);
            Assert.Equal(1L, presenter.GetField<long>("_renderingScopesBegun"));
            presenter.InvokeMethod("FlushBatchedGuestCommands", (object?)null);
            Assert.False(rendering.IsRendering);
        });
        harness.Finish();
        var pixels = harness.ReadImageBytes(TargetImage(presenter, words));
        Assert.Equal(Red, Pixel(pixels, Size / 2, Size / 2));
        harness.Shutdown();
    }
}
