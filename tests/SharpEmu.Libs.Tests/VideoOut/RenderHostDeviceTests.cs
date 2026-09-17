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
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Gpu.Vulkan;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Silk.NET.Vulkan;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Images.ImageCacheTestSupport;
using ResourceSnapshot = SharpEmu.ShaderCompiler.Resources.ResourceSnapshot;

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

    // One float2 position program and one solid red pixel program over empty resource plans.
    private sealed class FixedProgramProvider(IShaderPipelineHost host, ulong vertexAddress, bool pushData = false) : IShaderPipelineProvider
    {
        private const uint Float2Format = 64;
        private static readonly uint[] UserRegisters = [0, 1];
        private readonly ShaderProgramInfo _vertex = EmptyProgram(ShaderStageKind.Vertex, 1, fetchComponents: 2, userDataRegisters: pushData ? UserRegisters : null, pushDataStart: 2);
        private readonly ShaderProgramInfo _pixel = EmptyProgram(ShaderStageKind.Pixel, 2, userDataRegisters: pushData ? UserRegisters : null);
        private readonly ResourceSnapshot _snapshot = new() { UserData = pushData ? [0x11, 0x22] : [] };
        private ShaderProgram _vertexProgram;
        private ShaderProgram _pixelProgram;

        internal static ShaderProgramInfo EmptyProgram(ShaderStageKind stage, ulong hash, ShaderResourceInfo? info = null, uint fetchComponents = 0, uint[]? userDataRegisters = null, uint pushDataStart = 0)
        {
            info ??= new ShaderResourceInfo();
            var program = new ShaderProgramInfo
            {
                Stage = stage,
                Hash = hash,
                UserDataCount = (uint)(userDataRegisters?.Length ?? 0),
                Resources = new SpecializedResourceInfo { Info = info },
                Bindings = BindingLayout.Allocate(info, userDataRegisters ?? [], usesGlobalDataShare: false, usesFlattenedTable: false, usesShaderBase: false, pushDataStart),
            };
            program.VertexFetchComponents[0] = (byte)fetchComponents;
            return program;
        }

        public bool UsesPushData => _vertex.Bindings!.UsesPushData && _pixel.Bindings!.UsesPushData;

        // Modules are created on the worker with the first draw, like the cache does.
        private void EnsureModules()
        {
            if (_vertexProgram.IsValid)
            {
                return;
            }

            _vertexProgram = new ShaderProgram(1, host.CreateShaderModule(new VulkanCompiledGuestShader(CreatePositionVertexShader()), ShaderStage.Vertex, 1, 1));
            _pixelProgram = new ShaderProgram(2, host.CreateShaderModule(new VulkanCompiledGuestShader(SpirvFixedShaders.CreateSolidFragment(1f, 0f, 0f, 1f)), ShaderStage.Pixel, 2, 2));
        }

        public GraphicsPrograms GetGraphicsPrograms(
            VertexStageRegisters vertex,
            PixelStageRegisters pixel,
            ShaderInterfaceRegisters shaderInterface,
            ContextRegisters context,
            ReadOnlySpan<ColorComponentMap> targetExportMapping,
            bool pixelActive)
        {
            EnsureModules();
            return new()
            {
                Vertex = _vertexProgram,
                Pixel = _pixelProgram,
                VertexInput = new VertexInputInfo
                {
                    Buffers = [new VertexInputBuffer(vertexAddress, VertexStride, VertexCount)],
                    Attributes = [new VertexAttributeResource(new BufferDescriptorWords((uint)vertexAddress, (uint)(vertexAddress >> 32) | (VertexStride << 16), VertexCount, Float2Format << 12), 0, 2, 0, 0, 0, 0)],
                    Stage = new ShaderStageResources(_vertex, _snapshot),
                },
                PixelInput = new PixelInputInfo { InputCount = 0, Stage = new ShaderStageResources(_pixel, _snapshot) },
            };
        }

        public PipelineHandle CreateGraphicsPipeline(
            ReadOnlySpan<ColorTargetState> colors,
            in DepthAttachmentState depth,
            VertexInputInfo vertexInput,
            PixelInputInfo? pixelInput,
            ContextRegisters context,
            in RenderingState rendering,
            PrimitiveTopology topology,
            bool primitiveRestartEnabled,
            bool disableBlending,
            ShaderProgram vertexProgram,
            ShaderProgram pixelProgram) =>
            host.CreateGraphicsPipeline(ShaderPipelineCache.BuildGraphicsDescription(
                colors, in depth, vertexInput, pixelInput, context, in rendering, topology, primitiveRestartEnabled, disableBlending,
                vertexProgram, pixelProgram, host.NoAttachmentSampleCounts));

        public ComputeProgram GetComputeProgram(ComputeStageRegisters compute, ShaderInterfaceRegisters shaderInterface, uint dispatchInitiator, uint dimensionX, uint dimensionY, uint dimensionZ) =>
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
    [InlineData(ShaderStageKind.Vertex)]
    [InlineData(ShaderStageKind.Pixel)]
    [InlineData(ShaderStageKind.Compute)]
    public void PrepareBindings_RejectsDrawImageTypesButKeepsComputeViewChecksFatal(ShaderStageKind stage)
    {
        if (!Ready()) return;
        using var presenter = new PresenterUnderTest(_vulkan!);
        using var fatal = new FatalScope();
        var harness = presenter.Harness;
        var address = harness.MapBacked(0x10000, ReadWrite);
        var info = new ShaderResourceInfo
        {
            Images = [new ImageResource
            {
                Dimension = ImageDimension.Dim1D,
                ResourceClass = global::SharpEmu.ShaderCompiler.Resources.ImageResourceClass.Sampled,
                NumericClass = ImageNumericClass.Float,
                Read = true,
            }],
        };
        var program = FixedProgramProvider.EmptyProgram(stage, 0x1234, info);
        var words = RegisterWords.Texture(address, GuestPixelFormat.Bits8_8_8_8UNorm, 1, 1);
        var snapshot = new ResourceSnapshot { Images = [words] };
        presenter.Run(() =>
        {
            using var preparation = presenter.RenderHost.BeginPreparation();
            var request = ImageRequestBuilders.Texture(words, new ShaderImageShape(false, false, false, false, TextureNumericClass.Float)).Request;
            var description = request.Description;
            description.Type = GuestImageType.Color1D;
            var identifier = harness.Images.InsertImageForTest(description);
            Assert.Equal(identifier, harness.Images.FindImage(ref request));
            var image = harness.Images.GetImage(identifier);
            Assert.Equal(ImageType.Type1D, image.Backing.ImageType);
            Assert.Equal(ImageViewType.Type2D, request.View.Type);
            Assert.False(image.SupportsViewType(request.View));
            if (stage == ShaderStageKind.Compute)
            {
                var prepared = presenter.RenderHost.PrepareBindings(new ShaderStageResources(program, snapshot));
                var failure = Assert.Throws<SchedulerFatalException>(() => presenter.RenderHost.BindResources(prepared));
                Assert.Contains("typeValid=False", failure.Message);
            }
            else
            {
                var failure = Assert.Throws<DrawImageTypeMismatchException>(() =>
                    presenter.RenderHost.PrepareBindings(new ShaderStageResources(program, snapshot)));
                Assert.Contains("imageType=0 viewType=1", failure.Message);
            }
        });
        presenter.Run(presenter.RenderHost.ResetBindings);
        harness.Finish();
        harness.Shutdown();
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
        var info = new ShaderResourceInfo { Buffers = [new BufferResource { Read = true, Written = writable, Formatted = formatted, MaxByteExtent = 0x10000 }] };
        var program = FixedProgramProvider.EmptyProgram(ShaderStageKind.Compute, 3, info);
        var snapshot = new ResourceSnapshot { Buffers = [[(uint)address, (uint)(address >> 32), 0x10000, 0]] };
        presenter.Run(() =>
        {
            Assert.False(image.IsBufferModified);
            using var preparation = presenter.RenderHost.BeginPreparation();
            var prepared = presenter.RenderHost.PrepareBindings(new ShaderStageResources(program, snapshot));
            presenter.RenderHost.BindResources(prepared);
            Assert.Equal(formatted && writable, image.IsBufferModified);
        });
        harness.Finish();
        harness.Shutdown();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClampMappedSize_AcceptsPrivateAndBackedRangesAndStopsAtTheirEnd(bool privateMemory)
    {
        if (!Ready()) return;
        using var presenter = new PresenterUnderTest(_vulkan!);
        using var fatal = new FatalScope();
        var harness = presenter.Harness;
        var address = privateMemory ? harness.MapPrivate(0x10000) : harness.MapBacked(0x10000, ReadWrite);
        presenter.Run(() =>
        {
            Assert.Equal(32UL, presenter.RenderHost.ClampMappedSize(address + 0x1DA0, 32));
            Assert.Equal(16UL, presenter.RenderHost.ClampMappedSize(address + 0xFFF0, 32));
            Assert.Throws<SchedulerFatalException>(() => presenter.RenderHost.ClampMappedSize(address + 0x10000, 32));
            Assert.Throws<SchedulerFatalException>(() => presenter.RenderHost.ClampMappedSize(address, 0));
            Assert.Throws<SchedulerFatalException>(() => presenter.RenderHost.ClampMappedSize(0, 32));
            Assert.Throws<SchedulerFatalException>(() => presenter.RenderHost.ClampMappedSize(ulong.MaxValue - 15, 32));
        });
        harness.Shutdown();
    }

    [Fact]
    public void ClampMappedSize_DoesNotCrossAnUnmappedGap()
    {
        if (!Ready()) return;
        using var presenter = new PresenterUnderTest(_vulkan!);
        var (first, granule, last) = presenter.Harness.MapBackedSandwich();
        presenter.Run(() => Assert.Equal(16UL,
            presenter.RenderHost.ClampMappedSize(first + granule - 16, last - first + 16)));
        presenter.Harness.Shutdown();
    }

    [Fact]
    public void PrepareBindings_PrivateBufferUploadsCurrentBytesAndStillRejectsGpuWrites()
    {
        if (!Ready()) return;
        using var presenter = new PresenterUnderTest(_vulkan!);
        using var fatal = new FatalScope();
        presenter.SetField("_minStorageBufferOffsetAlignment", 256UL);
        presenter.LoadRenderingCommands();
        var harness = presenter.Harness;
        Assert.True(harness.Memory.TryAllocateAtOrAbove(0x2_0000_0000, 0x10000, executable: true, 0x4000, out var allocation));
        harness.Gpu.Register(allocation, 0x10000, ReadWrite | GuestPageProtection.Execute);
        var address = allocation + 0x1DA0;
        var resource = new BufferResource { Read = true, MaxByteExtent = 32 };
        var program = FixedProgramProvider.EmptyProgram(ShaderStageKind.Compute, 3,
            new ShaderResourceInfo { Buffers = [resource] });
        var snapshot = new ResourceSnapshot { Buffers = [[(uint)address, (uint)(address >> 32), 32, 0]] };
        Assert.False(harness.Memory.IsBackedView(address));
        Assert.True(harness.Memory.CanRead(address, 32));
        GuestGpuMemoryHook.Attach(harness.Gpu);
        try
        {
            foreach (byte seed in new byte[] { 17, 91 })
            {
                var expected = Enumerable.Range(0, 32).Select(index => (byte)(seed + index)).ToArray();
                Assert.True(harness.Memory.TryWrite(address, expected));
                var buffer = presenter.Run(() =>
                {
                    using var preparation = presenter.RenderHost.BeginPreparation();
                    var prepared = presenter.RenderHost.PrepareBindings(new ShaderStageResources(program, snapshot));
                    presenter.RenderHost.BindResources(prepared);
                    return harness.Cache.GetBuffer(harness.Cache.FindBuffer(address, 32));
                });
                Assert.Equal(expected, harness.ReadBack(buffer, buffer.Offset(address), 32));
                Assert.Equal(HostPageProtection.ReadExecute, harness.Protection(address));
            }

            resource.Written = true;
            presenter.Run(() =>
            {
                using var preparation = presenter.RenderHost.BeginPreparation();
                var prepared = presenter.RenderHost.PrepareBindings(new ShaderStageResources(program, snapshot));
                Assert.Throws<SchedulerFatalException>(() => presenter.RenderHost.BindResources(prepared));
            });
            Assert.Contains(fatal.Messages, message => message.StartsWith("Could not write the required direct backing", StringComparison.Ordinal));
            Assert.False(harness.Cache.HasGpuDirtyPages(address, 32));
            Assert.Equal(HostPageProtection.ReadExecute, harness.Protection(address));
        }
        finally
        {
            GuestGpuMemoryHook.Attach(null);
        }

        harness.Shutdown();
        Assert.Equal(HostPageProtection.ReadWriteExecute, harness.Protection(address));
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
        var executor = new RenderExecutor(presenter.RenderHost, new FixedProgramProvider((IShaderPipelineHost)presenter.Instance, vertices));
        GuestGpuMemoryHook.Attach(harness.Gpu);
        try
        {
            presenter.Run(() => executor.DrawAuto(1, Banks(firstWords), Draw()));
            // The guarded write marks the pages dirty; the second obtain uploads the new bytes.
            Assert.True(harness.Memory.TryWrite(vertices, Triangle(-1f, -1f, -0.5f, -1f, -1f, -0.5f)));
            presenter.Run(() => executor.DrawAuto(2, Banks(secondWords), Draw()));
            presenter.Run(() => presenter.InvokeMethod("FlushBatchedGuestCommands"));
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
    public void NewPreparation_ReusesRetiredStreamSpaceAfterTheRingFills()
    {
        if (!Ready()) return;
        using var presenter = new PresenterUnderTest(_vulkan);
        presenter.LoadRenderingCommands();
        var harness = presenter.Harness;
        var stream = harness.Cache.GetUtilityBuffer(GpuBufferUsage.Stream);
        var address = harness.MapBacked(0x10000, ReadWrite);
        harness.Write(address, new byte[0x100]);

        for (var preparationIndex = 0; preparationIndex < 3; preparationIndex++)
        {
            presenter.Run(() =>
            {
                Assert.True(stream.TryMap(stream.Size, out _));
                stream.Commit();
            });
            harness.Finish();
            presenter.Run(() =>
            {
                using var preparation = presenter.RenderHost.BeginPreparation();
                var binding = presenter.RenderHost.ObtainBuffer(address, 0x100, false);
                Assert.Equal(stream.Handle.Handle, binding.Handle);
                Assert.Equal(0UL, binding.Offset);
                var transient = presenter.RenderHost.UploadTransient(new byte[0x100], 16);
                Assert.Equal(stream.Handle.Handle, transient.Handle);
            });
            harness.Finish();
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

    // Retained ring bytes force expanded indices into a host buffer; the draw reads it before retirement.
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
        presenter.Run(() =>
        {
            Assert.True(stream.TryMap(stream.Size, out _, 16));
            stream.Commit();
        });
        using var retention = stream.RetainContents();

        var executor = new RenderExecutor(presenter.RenderHost, new FixedProgramProvider((IShaderPipelineHost)presenter.Instance, vertices));
        presenter.Run(() => executor.DrawIndexed(1, Banks(words), new DrawIndexedArguments(0, 0, VertexCount, indices, 2, 1, 0, 0, DrawOffsetSource.Packet)));
        Assert.Equal(0UL, presenter.HostBuffers.CachedBytes);

        presenter.Run(() => presenter.InvokeMethod("FlushBatchedGuestCommands"));
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
        var layouts = presenter.GetField<System.Collections.IDictionary>("_shaderModules");
        using (presenter)
        {
            presenter.LoadRenderingCommands();
            var target = presenter.Harness.MapBacked(0x10000, ReadWrite);
            var vertices = presenter.Harness.MapBacked(0x10000, ReadWrite);
            presenter.Harness.Write(vertices, Triangle(-1f, -1f, 3f, -1f, -1f, 3f));
            var words = RegisterWords.Color(target, Size, Size);
            var executor = new RenderExecutor(presenter.RenderHost,
                new FixedProgramProvider((IShaderPipelineHost)presenter.Instance, vertices));
            presenter.Run(() => executor.DrawAuto(1, Banks(words), Draw()));
            Assert.NotEmpty(pipelines);
            Assert.NotEmpty(layouts);
        }

        Assert.Empty(pipelines);
        Assert.Empty(layouts);
        _vulkan.AssertNoValidationMessages();
    }

    // Both stages push their blocks into the one 128-byte range; the validation layer checks the layout covers them.
    [Fact]
    public void PushConstants_CoverBothStagesOfADrawInOneRange()
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
        var provider = new FixedProgramProvider((IShaderPipelineHost)presenter.Instance, vertices, pushData: true);
        Assert.True(provider.UsesPushData);
        var executor = new RenderExecutor(presenter.RenderHost, provider);
        presenter.Run(() => executor.DrawAuto(1, Banks(words), Draw()));
        presenter.Run(() => presenter.InvokeMethod("FlushBatchedGuestCommands"));
        harness.Finish();
        var pixels = harness.ReadImageBytes(TargetImage(presenter, words));
        Assert.Equal(Red, Pixel(pixels, Size / 2, Size / 2));
        harness.Shutdown();
        _vulkan.AssertNoValidationMessages();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GuestWordReaders_RejectUnmappedMemoryAndClearTheResult(bool cleanOnly)
    {
        if (!Ready()) return;
        using var presenter = new PresenterUnderTest(_vulkan);
        var host = (IShaderPipelineHost)presenter.Instance;
        presenter.Run(() =>
        {
            const ulong unmappedAddress = 0x12340000;
            uint word = uint.MaxValue;
            var success = cleanOnly
                ? host.TryReadCleanGuestWord(unmappedAddress, out word)
                : host.TryReadGuestWord(unmappedAddress, out word);
            Assert.False(success);
            Assert.Equal(0u, word);
        });
    }

    // The plain reader downloads a GPU-written range first; the clean reader refuses it until the GPU is done.
    [Fact]
    public void GuestWordReaders_HonourGpuOwnershipOfTheRange()
    {
        if (!Ready())
        {
            return;
        }

        using var presenter = new PresenterUnderTest(_vulkan);
        presenter.LoadRenderingCommands();
        var harness = presenter.Harness;
        var address = harness.MapBacked(0x10000, ReadWrite);
        harness.Write(address, BitConverter.GetBytes(0xCAFEF00Du));
        var host = (IShaderPipelineHost)presenter.Instance;
        GuestGpuMemoryHook.Attach(harness.Gpu);
        try
        {
            presenter.Run(() =>
            {
                Assert.True(host.TryReadGuestWord(address, out var word));
                Assert.Equal(0xCAFEF00Du, word);
                Assert.True(host.TryReadCleanGuestWord(address, out word));
                Assert.Equal(0xCAFEF00Du, word);

                _ = harness.Cache.ObtainBuffer(address, 0x1000, isWritten: true);
                Assert.False(host.TryReadCleanGuestWord(address, out _));
                Assert.False(host.TryReadCleanGuestWord(address + 0x800, out _));
                Assert.True(host.TryReadCleanGuestWord(address + 0x2000, out _));
                Assert.True(host.TryReadGuestWord(address, out word));
                Assert.Equal(0xCAFEF00Du, word);
            });
        }
        finally
        {
            GuestGpuMemoryHook.Attach(null);
        }

        harness.Finish();
        harness.Shutdown();
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
        var executor = new RenderExecutor(presenter.RenderHost, new FixedProgramProvider((IShaderPipelineHost)presenter.Instance, vertices));
        var rendering = (IRenderingState)presenter.Instance;
        presenter.Run(() =>
        {
            executor.DrawAuto(1, Banks(words), Draw());
            Assert.True(rendering.IsRendering);
            executor.DrawAuto(2, Banks(words), Draw());
            Assert.True(rendering.IsRendering);
            Assert.Equal(1L, presenter.GetField<long>("_renderingScopesBegun"));
            presenter.InvokeMethod("FlushBatchedGuestCommands");
            Assert.False(rendering.IsRendering);
        });
        harness.Finish();
        var pixels = harness.ReadImageBytes(TargetImage(presenter, words));
        Assert.Equal(Red, Pixel(pixels, Size / 2, Size / 2));
        harness.Shutdown();
    }
}
