// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using SharpEmu.HLE.Host;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using SharpEmu.Libs.VideoOut;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu.Vulkan;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Silk.NET.Vulkan;
using ImageResourceClass = SharpEmu.Libs.Gpu.Rendering.ImageResourceClass;
using PlanImageResourceClass = SharpEmu.ShaderCompiler.Resources.ImageResourceClass;
using ResourceSnapshot = SharpEmu.ShaderCompiler.Resources.ResourceSnapshot;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Images.ImageCacheTestSupport;

namespace SharpEmu.Libs.Tests.VideoOut;

// The presenter's image bindings over the store: discovery, acquisition, layouts, per-draw passes and shutdown.
[Collection(SchedulingStateCollection.Name)]
public sealed class PresenterImageBindingTests : IClassFixture<HeadlessVulkanFixture>
{
    private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Type PresenterType = typeof(VulkanVideoPresenter).GetNestedType("Presenter", BindingFlags.NonPublic)!;
    private static readonly Type ColorAttachmentType = typeof(VulkanVideoPresenter).GetNestedType("ColorAttachment", BindingFlags.NonPublic)!;
    private static readonly Type TextureResourceType = typeof(VulkanVideoPresenter).GetNestedType("TextureResource", BindingFlags.NonPublic)!;
    private static readonly ShaderImageShape Sampled2D = new(false, false, false, false, TextureNumericClass.Float);

    private readonly HeadlessVulkan? _vulkan;

    public PresenterImageBindingTests(HeadlessVulkanFixture fixture) => _vulkan = fixture.Vulkan;

    [Theory]
    [InlineData(GuestPixelFormat.Bits16UNorm, Format.D16Unorm)]
    [InlineData(GuestPixelFormat.Bits32Float, Format.D32Sfloat)]
    public void ComparisonBinding_CreatesDepthImageWithoutAnAttachment(GuestPixelFormat guestFormat, Format hostFormat)
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var fatal = new FatalScope();
        using var presenter = new PresenterUnderTest(_vulkan);
        var harness = presenter.Harness;
        var address = harness.MapBacked(0x10000, ReadWrite);
        var sample = guestFormat == GuestPixelFormat.Bits16UNorm
            ? BitConverter.GetBytes((ushort)0x4000)
            : BitConverter.GetBytes(0.25f);
        var guestBytes = new byte[0x10000];
        for (var offset = 0; offset < guestBytes.Length; offset += sample.Length)
            sample.CopyTo(guestBytes, offset);
        harness.Write(address, guestBytes);
        CachedImage sampledImage = null!;
        presenter.Run(() =>
        {
            var resource = new ImageResource
            {
                ResourceClass = PlanImageResourceClass.Sampled,
                NumericClass = ImageNumericClass.Float,
                Dimension = ImageDimension.Dim2D,
                Read = true,
                DepthCompare = true,
            };
            var words = RegisterWords.Texture(address, guestFormat, 48, 24);
            words[3] = (words[3] & ~0xFFFu) | ViewFormatRules.PackDestinationSelect(4, 0, 0, 1);
            var binding = presenter.InvokeMethod("ResolveImageBinding", resource, words, new ShaderProgramInfo(), 0)!;
            var identifier = (ResourceSlotIdentifier)GetFieldValue(binding, "ImageIdentifier");
            var request = (ImageRequest)GetFieldValue(binding, "Request");
            var image = harness.Images.GetImage(identifier);
            sampledImage = image;
            var view = harness.Images.AcquireTextureView(identifier, request);
            Assert.Equal(hostFormat, image.Backing.Format);
            Assert.True(image.Description.IsDepth);
            Assert.NotEqual(0UL, view.Handle);
            Assert.Contains(image.Views, candidate => candidate.Description.Aspect == ImageAspectFlags.DepthBit);
            presenter.RenderHost.ResetBindings();
        });
        var uploaded = harness.ReadImageBytes(sampledImage, ImageAspectFlags.DepthBit);
        Assert.Equal(sample, uploaded.AsSpan(0, sample.Length).ToArray());
        harness.Shutdown();
    }

    [Theory]
    [InlineData(true, 1u, false)]
    [InlineData(true, 2u, true)]
    [InlineData(false, 1u, false)]
    public unsafe void StencilStorage_PublishesRecordedWritesWithoutChangingDepth(bool recordDispatch, uint layers, bool sampledAlias)
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        if (!_vulkan.StorageImageExtendedFormats || !_vulkan.SupportsDynamicRendering)
        {
            Assert.False(ReferenceShaders.Required, "Stencil storage tests need extended storage formats and dynamic rendering.");
            return;
        }
        using var fatal = new FatalScope();
        using var presenter = new PresenterUnderTest(_vulkan);
        presenter.LoadRenderingCommands();
        var harness = presenter.Harness;
        var region = harness.MapBacked(0x100000, ReadWrite);
        var stencilAddress = region + 0x80000;
        CachedImage attachment = null!;
        var workingImages = new List<CachedImage>();
        presenter.Run(() =>
        {
            var shape = new ShaderImageShape(false, true, true, false, TextureNumericClass.Uint);
            var texture = new GuestDrawTexture(stencilAddress, 64, 64, 0, 0, [], false, true,
                Descriptor: RegisterWords.Texture(stencilAddress, GuestPixelFormat.Bits8UInt, 64, 64,
                    type: GuestImageType.Color2DArray, tile: GuestTileMode.Depth, layers: layers, baseArray: layers - 1), Shape: shape);
            // The stencil range was a color image before the depth target took ownership.
            presenter.InvokeMethod("ResolveTexture", texture);
            var target = new GuestDepthTarget(region, region, 64, 64, 3, 0, 1f, false,
                Registers: RegisterWords.Depth(region, 64, 64, stencilBase: stencilAddress) with { DepthView = (layers - 1) << 13 });
            var depth = presenter.InvokeMethod("DiscoverDepthTarget", target)!;
            presenter.InvokeMethod("AcquireDepthAttachment", depth, GuestDepthState.Default);
            attachment = (CachedImage)GetFieldValue(depth, "Image");
            var range = new ImageSubresourceRange(ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit, 0, 1, 0, layers);
            var initial = new ClearDepthStencilValue(0.25f, 0x37);
            attachment.Transition(ImageLayout.TransferDstOptimal, AccessFlags.TransferWriteBit, null, presenter.Command);
            _vulkan.Vk.CmdClearDepthStencilImage(presenter.Command, attachment.Backing.Handle, ImageLayout.TransferDstOptimal, &initial, 1, &range);
            presenter.RenderHost.ResetBindings();

            var storageImage = new ImageResource { ResourceClass = PlanImageResourceClass.Storage, NumericClass = ImageNumericClass.Uint, Dimension = ImageDimension.Dim2DArray, Read = true, Written = true };
            var sampledImage = new ImageResource { ResourceClass = PlanImageResourceClass.Sampled, NumericClass = ImageNumericClass.Uint, Dimension = ImageDimension.Dim2DArray, Read = true };
            var info = new ShaderResourceInfo { Images = sampledAlias ? [sampledImage, storageImage] : [storageImage] };
            var storageBinding = BindingLayout.NativeBindingIndex(ShaderStage.Compute, ImageDescriptorBinding.ForImage(storageImage)!.Value);
            var program = new ShaderProgramInfo
            {
                Stage = ShaderStageKind.Compute,
                Images = sampledAlias
                    ? [new ImageResourceInfo(ImageResourceClass.Sampled, false), new ImageResourceInfo(ImageResourceClass.Storage, true)]
                    : [new ImageResourceInfo(ImageResourceClass.Storage, true)],
                Resources = new SpecializedResourceInfo { Info = info },
                Bindings = BindingLayout.Allocate(info, [], usesGlobalDataShare: false, usesFlattenedTable: false, usesShaderBase: false),
            };
            var words = texture.Descriptor!;
            var stage = new ShaderStageResources(program, new ResourceSnapshot { Images = sampledAlias ? [words, words] : [words] });
            var input = new ComputeInputInfo { ThreadsX = 1, ThreadsY = 1, ThreadsZ = 1, Stage = stage };
            var pipelineHost = (IShaderPipelineHost)presenter.Instance;
            var module = pipelineHost.CreateShaderModule(new VulkanCompiledGuestShader(CreateStencilIncrementShader(storageBinding)), ShaderStage.Compute, 0, 1);
            for (var dispatchIndex = 0; dispatchIndex < 2; dispatchIndex++)
            {
                using (presenter.RenderHost.BeginPreparation())
                {
                    var bindings = presenter.RenderHost.PrepareBindings(stage);
                    presenter.RenderHost.BindResources(bindings);
                    var textures = (Array)bindings.GetType().GetProperty("Textures")!.GetValue(bindings)!;
                    var working = (CachedImage)GetFieldValue(textures.GetValue(textures.Length - 1)!, "CachedImage");
                    workingImages.Add(working);
                    Assert.NotSame(attachment, working);
                    Assert.Equal(Format.R8Uint, working.Backing.Format);
                    Assert.NotEqual(0, (int)(working.Backing.Usage & ImageUsageFlags.StorageBit));
                    Assert.Equal(0, (int)(attachment.Backing.Usage & ImageUsageFlags.StorageBit));
                    var invalidStorage = ImageViewDescription.Default with
                    {
                        Format = Format.R8Uint, Aspect = ImageAspectFlags.StencilBit, Usage = ImageUsageFlags.StorageBit,
                    };
                    Assert.Throws<SchedulerFatalException>(() => attachment.GetOrCreateView(invalidStorage));

                    if (recordDispatch)
                    {
                        var pipeline = pipelineHost.CreateComputePipeline(new ComputePipelineDescription { Input = input, Program = new ShaderProgram(1, module), Stage = program });
                        presenter.RenderHost.CommitBindings(PipelineBindPoint.Compute, pipeline, [bindings]);
                        if (sampledAlias)
                            Assert.Same(working, GetFieldValue(textures.GetValue(0)!, "CachedImage"));
                        presenter.RenderHost.BindPipeline(PipelineBindPoint.Compute, pipeline);
                        presenter.RenderHost.Dispatch(32, 64, 1);
                        presenter.RenderHost.ShaderAccessBarrier();
                    }
                    else
                    {
                        // An abandoned preparation must not publish even if its working image changed.
                        working.Transition(ImageLayout.TransferDstOptimal, AccessFlags.TransferWriteBit, null, presenter.Command);
                        var discarded = new ClearColorValue(0x55u, 0u, 0u, 0u);
                        var colorRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, layers);
                        _vulkan.Vk.CmdClearColorImage(presenter.Command, working.Backing.Handle, ImageLayout.TransferDstOptimal, &discarded, 1, &colorRange);
                    }
                }
                Assert.NotEqual(0UL, workingImages[^1].Backing.Handle.Handle);
                presenter.RenderHost.ResetBindings();
            }
        });

        harness.Finish();
        Assert.All(workingImages, image => Assert.False(image.Backing.Exists));
        var stencil = harness.ReadImageBytes(attachment, ImageAspectFlags.StencilBit);
        for (var layer = 0; layer < layers; layer++)
            for (var row = 0; row < 64; row++)
                for (var column = 0; column < 64; column++)
                    Assert.Equal((byte)(recordDispatch && layer == layers - 1 && column < 32 ? 0x39 : 0x37), stencil[layer * 64 * 64 + row * 64 + column]);
        var depthBytes = harness.ReadImageBytes(attachment, ImageAspectFlags.DepthBit);
        for (var offset = 0; offset < depthBytes.Length; offset += sizeof(float))
            Assert.Equal(0.25f, BitConverter.ToSingle(depthBytes, offset));
        Assert.True(attachment.IsGpuModified);
        Assert.True(harness.ProxyAt(stencilAddress, attachment.Description.Stencil.Size).IsValid);
        harness.Shutdown();
    }

    private static byte[] CreateStencilIncrementShader(uint imageBinding)
    {
        var module = new SpirvModuleBuilder();
        module.AddCapability(SpirvCapability.Shader);
        module.AddCapability(SpirvCapability.StorageImageExtendedFormats);
        var voidType = module.TypeVoid();
        var unsignedType = module.TypeInt(32, false);
        var coordinateType = module.TypeVector(unsignedType, 3);
        var pixelType = module.TypeVector(unsignedType, 4);
        var imageType = module.TypeImage(unsignedType, SpirvImageDim.Dim2D, false, true, false, 2, SpirvImageFormat.R8ui);
        var image = module.AddGlobalVariable(module.TypePointer(SpirvStorageClass.UniformConstant, imageType), SpirvStorageClass.UniformConstant);
        module.AddDecoration(image, SpirvDecoration.DescriptorSet, 0);
        module.AddDecoration(image, SpirvDecoration.Binding, imageBinding);
        var position = module.AddGlobalVariable(module.TypePointer(SpirvStorageClass.Input, coordinateType), SpirvStorageClass.Input);
        module.AddDecoration(position, SpirvDecoration.BuiltIn, (uint)SpirvBuiltIn.GlobalInvocationId);
        var zero = module.Constant(unsignedType, 0);
        var main = module.BeginFunction(voidType, module.TypeFunction(voidType));
        module.AddLabel();
        var coordinates = module.AddInstruction(SpirvOp.Load, coordinateType, position);
        var loadedImage = module.AddInstruction(SpirvOp.Load, imageType, image);
        var pixel = module.AddInstruction(SpirvOp.ImageRead, pixelType, loadedImage, coordinates);
        var previous = module.AddInstruction(SpirvOp.CompositeExtract, unsignedType, pixel, 0);
        var incremented = module.AddInstruction(SpirvOp.IAdd, unsignedType, previous, module.Constant(unsignedType, 1));
        var result = module.AddInstruction(SpirvOp.CompositeConstruct, pixelType, incremented, zero, zero, zero);
        module.AddStatement(SpirvOp.ImageWrite, loadedImage, coordinates, result);
        module.AddStatement(SpirvOp.Return);
        module.EndFunction();
        module.AddEntryPoint(SpirvExecutionModel.GLCompute, main, "main", [image, position]);
        module.AddExecutionMode(main, SpirvExecutionMode.LocalSize, 1, 1, 1);
        return module.Build();
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false, true)]
    [InlineData(false, true, false, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, true, true)]
    public void SampledDepth_PreservesAttachmentLayoutOrRejectsOverlappingWrites(bool depthWrite, bool stencilWrite, bool sampleStencil, bool pendingClear = false)
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var presenter = new PresenterUnderTest(_vulkan);
        var harness = presenter.Harness;
        var region = harness.MapBacked(0x100000, ReadWrite);
        CachedImage? clearedImage = null;
        var target = new GuestDepthTarget(region, region, 64, 64, 3, 0, 1f, false,
            Registers: RegisterWords.Depth(region, 64, 64, stencilBase: region + 0x80000));
        presenter.Run(() =>
        {
            var depth = presenter.InvokeMethod("DiscoverDepthTarget", target)!;
            var state = new GuestDepthState(true, depthWrite, 7, StencilTestEnable: stencilWrite,
                StencilFront: new GuestStencilFaceState(1, 1, 1, 7, 0xFF, 0xFF, 0, 0));
            presenter.InvokeMethod("AcquireDepthAttachment", depth, state);
            if (pendingClear)
            {
                depth.GetType().GetField("ClearDepth")!.SetValue(depth, true);
                depth.GetType().GetField("ClearStencil")!.SetValue(depth, true);
                depth.GetType().GetField("ClearStencilValue")!.SetValue(depth, (byte)0x40);
            }
            var image = (CachedImage)GetFieldValue(depth, "Image");
            var request = (ImageRequest)GetFieldValue(depth, "Request");
            request.Role = ImageRole.Texture;
            request.View = request.View with
            {
                Format = sampleStencil ? Format.R8Uint : Format.R32Sfloat,
                Aspect = sampleStencil ? ImageAspectFlags.StencilBit : ImageAspectFlags.DepthBit,
                Usage = ImageUsageFlags.SampledBit,
            };
            var binding = Activator.CreateInstance(TextureResourceType, nonPublic: true)!;
            TextureResourceType.GetField("CachedImage")!.SetValue(binding, image);
            TextureResourceType.GetField("Request")!.SetValue(binding, request);
            TextureResourceType.GetField("View")!.SetValue(binding, image.GetOrCreateView(request.View));
            var bindings = Array.CreateInstance(TextureResourceType, 1);
            bindings.SetValue(binding, 0);
            if (sampleStencil ? stencilWrite : depthWrite)
            {
                var error = Assert.Throws<InvalidOperationException>(() => presenter.InvokeMethod("RecordDrawTextureTransitions", bindings, depth, state));
                Assert.Contains($"sampled_aspects={(sampleStencil ? ImageAspectFlags.StencilBit : ImageAspectFlags.DepthBit)}", error.Message);
                Assert.Contains("clear_depth=False clear_stencil=False", error.Message);
                Assert.Contains("depth_state=", error.Message);
            }
            else
            {
                presenter.InvokeMethod("RecordDrawTextureTransitions", bindings, depth, state);
                Assert.False((bool)GetFieldValue(depth, "ClearDepth"));
                Assert.False((bool)GetFieldValue(depth, "ClearStencil"));
                Assert.Equal(GetFieldValue(depth, "Layout"), GetFieldValue(binding, "Layout"));
                harness.Scheduler.Finish();
                if (pendingClear)
                    clearedImage = image;
            }
            presenter.InvokeMethod("ResetImageBindings");
        });
        harness.Finish();
        if (clearedImage is not null)
        {
            var depthBytes = harness.ReadImageBytes(clearedImage, ImageAspectFlags.DepthBit);
            for (var offset = 0; offset < depthBytes.Length; offset += sizeof(float))
                Assert.Equal(1f, BitConverter.ToSingle(depthBytes, offset));
            var stencilBytes = harness.ReadImageBytes(clearedImage, ImageAspectFlags.StencilBit);
            Assert.All(stencilBytes.Take(64 * 64), value => Assert.Equal((byte)0x40, value));
        }
        harness.Shutdown();
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public unsafe void SeparateDepthClear_PreservesOtherAspectsAndAllowsLargerColorExtent(bool clearDepth, bool clearStencil)
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var presenter = new PresenterUnderTest(_vulkan);
        var harness = presenter.Harness;
        var region = harness.MapBacked(0x100000, ReadWrite);
        CachedImage depthImage = null!;
        CachedImage colorImage = null!;
        presenter.Run(() =>
        {
            var depthTarget = new GuestDepthTarget(region, region, 64, 64, 3, 0, 0.25f, false,
                Registers: RegisterWords.Depth(region, 64, 64, stencilBase: region + 0x80000));
            var depth = presenter.InvokeMethod("DiscoverDepthTarget", depthTarget)!;
            presenter.InvokeMethod("AcquireDepthAttachment", depth, GuestDepthState.Default);
            depthImage = (CachedImage)GetFieldValue(depth, "Image");
            depthImage.Transition(ImageLayout.TransferDstOptimal, AccessFlags.TransferWriteBit, null, presenter.Command);
            var range = new ImageSubresourceRange(ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit, 0, 1, 0, 1);
            var initial = new ClearDepthStencilValue(0.75f, 0x37);
            harness.Vulkan.Vk.CmdClearDepthStencilImage(presenter.Command, depthImage.Backing.Handle, ImageLayout.TransferDstOptimal, &initial, 1, &range);
            depth.GetType().GetField("ClearDepth")!.SetValue(depth, clearDepth);
            depth.GetType().GetField("ClearStencil")!.SetValue(depth, clearStencil);
            depth.GetType().GetField("ClearStencilValue")!.SetValue(depth, (byte)0x6A);
            presenter.InvokeMethod("RecordSeparateDepthClear", depth);

            var color = presenter.InvokeMethod("DiscoverColorTarget", ColorTarget(region + 0x40000, 128, 128), false, false)!;
            presenter.InvokeMethod("AcquireColorAttachment", color);
            color.GetType().GetField("Clear")!.SetValue(color, true);
            colorImage = (CachedImage)GetFieldValue(color, "Image");
            var extent = new Extent2D(128, 128);
            ClearColorAttachment(presenter, color, extent, 1, new ClearColorValue(1f, 0f, 0f, 1f));
            harness.Scheduler.Finish();
            presenter.InvokeMethod("ResetImageBindings");
        });
        var depthBytes = harness.ReadImageBytes(depthImage, ImageAspectFlags.DepthBit);
        for (var offset = 0; offset < depthBytes.Length; offset += sizeof(float))
            Assert.Equal(clearDepth ? 0.25f : 0.75f, BitConverter.ToSingle(depthBytes, offset));
        var stencilBytes = harness.ReadImageBytes(depthImage, ImageAspectFlags.StencilBit);
        Assert.All(stencilBytes.Take(64 * 64), value => Assert.Equal(clearStencil ? (byte)0x6A : (byte)0x37, value));
        var colorBytes = harness.ReadImageBytes(colorImage);
        Assert.Equal(128 * 128 * 4, colorBytes.Length);
        for (var offset = 0; offset < colorBytes.Length; offset += sizeof(uint))
            Assert.Equal(0xFF0000FFu, BitConverter.ToUInt32(colorBytes, offset));
        harness.Shutdown();
    }

    private static void ClearColorAttachment(PresenterUnderTest presenter, object attachment, Extent2D extent, uint layers, ClearColorValue clear)
    {
        presenter.LoadRenderingCommands();
        var state = new RenderingState
        {
            Width = extent.Width,
            Height = extent.Height,
            Layers = layers,
            Samples = 1,
            ColorAttachmentCount = 1,
        };
        state.ColorAttachments[0] = new RenderingAttachment(
            (ImageView)GetFieldValue(attachment, "View"),
            (ImageLayout)GetFieldValue(attachment, "Layout"),
            ((ImageRequest)GetFieldValue(attachment, "Request")).View.Format,
            clear.Uint32_0, clear.Uint32_1, clear.Uint32_2, clear.Uint32_3,
            true, false, false, false, false);
        presenter.RenderHost.BeginRendering(in state);
        presenter.RenderHost.EndRendering();
    }

    private static object GetFieldValue(object target, string name) => target.GetType().GetField(name, InstanceMembers)!.GetValue(target)!;

    private static GuestRenderTarget ColorTarget(ulong address, uint width = 64, uint height = 64, uint sliceMax = 0, uint sliceStart = 0) =>
        new(address, width, height, 0, 0, Registers: RegisterWords.Color(address, width, height, sliceMax: sliceMax, sliceStart: sliceStart));

    private static GuestDrawTexture Texture(ulong address) =>
        new(address, 64, 64, 0, 0, [], false, false, Descriptor: RegisterWords.Texture(address, GuestPixelFormat.Bits8_8_8_8UNorm, 64, 64), Shape: Sampled2D);

    // Binds one shader image the way a draw does and returns the bound resource.
    private static object AcquireTexture(PresenterUnderTest presenter, GuestDrawTexture texture)
    {
        var resource = presenter.InvokeMethod("ResolveTexture", texture)!;
        var bindings = Array.CreateInstance(TextureResourceType, 1);
        bindings.SetValue(resource, 0);
        presenter.InvokeMethod("AcquireTextureViews", bindings, new List<GuestDrawTexture> { texture });
        return bindings.GetValue(0)!;
    }

    [Theory]
    [InlineData(TextureNumericClass.Float, false)]
    [InlineData(TextureNumericClass.Uint, false)]
    [InlineData(TextureNumericClass.Sint, false)]
    [InlineData(TextureNumericClass.Float, true)]
    [InlineData(TextureNumericClass.Uint, true)]
    [InlineData(TextureNumericClass.Sint, true)]
    public void NullTexture_ResolvesAndAcquiresAView(TextureNumericClass numericClass, bool storage)
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var presenter = new PresenterUnderTest(_vulkan);
        var texture = new GuestDrawTexture(0, 1, 1, 0, 0, [], false, storage,
            Descriptor: [], Shape: new ShaderImageShape(false, false, storage, false, numericClass));

        var resource = presenter.Run(() => AcquireTexture(presenter, texture));
        Assert.NotEqual(0UL, ((ImageView)GetFieldValue(resource, "View")).Handle);
        var resolution = (TextureRequestResolution)GetFieldValue(resource, "Resolution");
        Assert.Equal(resolution.Request.View.Format, resolution.ViewFormat);
        Assert.NotEqual(Format.Undefined, resolution.ViewFormat);
        presenter.Run(() => presenter.InvokeMethod("ResetImageBindings"));
        presenter.Harness.Finish();
        presenter.Harness.Shutdown();
    }

    [Fact]
    public void NullTexture_ForDepthReferenceSampling_IsADepthView()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var presenter = new PresenterUnderTest(_vulkan);
        var texture = new GuestDrawTexture(0, 1, 1, 0, 0, [], false, false,
            Descriptor: [], Shape: new ShaderImageShape(false, false, false, false, TextureNumericClass.Float) with { DepthCompare = true });

        var resource = presenter.Run(() => AcquireTexture(presenter, texture));
        Assert.NotEqual(0UL, ((ImageView)GetFieldValue(resource, "View")).Handle);
        var resolution = (TextureRequestResolution)GetFieldValue(resource, "Resolution");
        Assert.Equal(Format.D32Sfloat, resolution.ViewFormat);
        presenter.Run(() => presenter.InvokeMethod("ResetImageBindings"));
        presenter.Harness.Finish();
        presenter.Harness.Shutdown();
    }

    [Fact]
    public void ColorTarget_IsFoundInTheStoreAndBoundForTheDraw()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var presenter = new PresenterUnderTest(_vulkan);
        var harness = presenter.Harness;
        var address = harness.MapBacked(0x10000, ReadWrite);

        var attachment = presenter.Run(() => presenter.InvokeMethod("DiscoverColorTarget", ColorTarget(address), false, false));
        Assert.NotNull(attachment);
        var imageIdentifier = (ResourceSlotIdentifier)GetFieldValue(attachment, "ImageIdentifier");
        var image = harness.Image(imageIdentifier);
        Assert.True(image.Binding.IsTarget);
        Assert.Equal(address, image.Description.Data.Address);
        Assert.Equal(new Extent3D(64, 64, 1), image.Description.Extent);
        Assert.Equal(Format.R8G8B8A8Unorm, image.Description.PixelFormat);

        presenter.Run(() => presenter.InvokeMethod("AcquireColorAttachment", attachment));
        Assert.NotEqual(0UL, ((ImageView)GetFieldValue(attachment, "View")).Handle);
        Assert.Equal(ImageLayout.ColorAttachmentOptimal, (ImageLayout)GetFieldValue(attachment, "Layout"));
        Assert.False((bool)GetFieldValue(attachment, "Clear"));
        Assert.Same(image, GetFieldValue(attachment, "Image"));

        presenter.Run(() => presenter.InvokeMethod("ResetImageBindings"));
        Assert.False(harness.Image(imageIdentifier).Binding.IsTarget);
        harness.Finish();
        harness.Shutdown();
    }

    [Fact]
    public void TargetSampledByItsOwnDraw_UsesTheGeneralLayout()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var presenter = new PresenterUnderTest(_vulkan);
        var harness = presenter.Harness;
        var address = harness.MapBacked(0x10000, ReadWrite);
        var texture = Texture(address);

        presenter.Run(() =>
        {
            var resource = presenter.InvokeMethod("ResolveTexture", texture)!;
            var attachment = presenter.InvokeMethod("DiscoverColorTarget", ColorTarget(address), false, false)!;
            Assert.Equal(GetFieldValue(resource, "ImageIdentifier"), GetFieldValue(attachment, "ImageIdentifier"));
            var image = harness.Image((ResourceSlotIdentifier)GetFieldValue(resource, "ImageIdentifier"));
            Assert.True(image.Binding.IsBound);
            Assert.True(image.Binding.IsTarget);
            Assert.False(image.Binding.ShaderWrite);

            presenter.InvokeMethod("AcquireColorAttachment", attachment);
            Assert.Equal(ImageLayout.General, (ImageLayout)GetFieldValue(attachment, "Layout"));

            var bindings = Array.CreateInstance(TextureResourceType, 1);
            bindings.SetValue(resource, 0);
            presenter.InvokeMethod("AcquireTextureViews", bindings, new List<GuestDrawTexture> { texture });
            presenter.InvokeMethod("RecordTextureTransitions", bindings);
            var bound = bindings.GetValue(0)!;
            Assert.Equal(ImageLayout.General, (ImageLayout)GetFieldValue(bound, "Layout"));
            Assert.NotEqual(0UL, ((ImageView)GetFieldValue(bound, "View")).Handle);
            Assert.NotEqual(0UL, ((Sampler)GetFieldValue(bound, "Sampler")).Handle);
            Assert.True(image.Uses.Texture);

            presenter.InvokeMethod("ResetImageBindings");
            Assert.False(image.Binding.IsBound);
            Assert.False(image.Binding.IsTarget);
        });
        harness.Finish();
        harness.Shutdown();
    }

    [Fact]
    public void DepthTarget_LayoutFollowsTheWrittenAspects()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var presenter = new PresenterUnderTest(_vulkan);
        var harness = presenter.Harness;
        var region = harness.MapBacked(0x100000, ReadWrite);
        var words = RegisterWords.Depth(region, 64, 64, stencilBase: region + 0x80000);
        var target = new GuestDepthTarget(region, region, 64, 64, 3, 0, 1f, false, Registers: words);

        presenter.Run(() =>
        {
            var depth = presenter.InvokeMethod("DiscoverDepthTarget", target);
            Assert.NotNull(depth);
            var imageIdentifier = (ResourceSlotIdentifier)GetFieldValue(depth, "ImageIdentifier");
            Assert.True(harness.Image(imageIdentifier).Binding.IsTarget);
            Assert.Equal(Format.D32SfloatS8Uint, harness.Image(imageIdentifier).Description.PixelFormat);

            presenter.InvokeMethod("AcquireDepthAttachment", depth, new GuestDepthState(true, true, 7));
            Assert.NotEqual(0UL, ((ImageView)GetFieldValue(depth, "View")).Handle);
            Assert.Equal(ImageLayout.DepthAttachmentStencilReadOnlyOptimal, (ImageLayout)GetFieldValue(depth, "Layout"));
            Assert.False((bool)GetFieldValue(depth, "ClearDepth"));

            presenter.InvokeMethod("AcquireDepthAttachment", depth, new GuestDepthState(true, false, 7));
            Assert.Equal(ImageLayout.DepthStencilReadOnlyOptimal, (ImageLayout)GetFieldValue(depth, "Layout"));

            var stencilWrite = new GuestDepthState(true, true, 7, StencilTestEnable: true, StencilFront: new GuestStencilFaceState(1, 1, 1, 7, 0xFF, 0xFF, 0, 0));
            presenter.InvokeMethod("AcquireDepthAttachment", depth, stencilWrite);
            Assert.Equal(ImageLayout.DepthStencilAttachmentOptimal, (ImageLayout)GetFieldValue(depth, "Layout"));
            presenter.InvokeMethod("ResetImageBindings");
        });
        harness.Finish();
        harness.Shutdown();
    }

    [Fact]
    public void DepthTarget_ConsumesQueuedMetadataClearOnce()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var presenter = new PresenterUnderTest(_vulkan);
        var harness = presenter.Harness;
        var region = harness.MapBacked(0x100000, ReadWrite);
        var metadataAddress = region + 0x80000;
        var words = RegisterWords.Depth(region, 64, 64) with
        {
            ZInfo = 3u | (1u << 29),
            HtileBase = metadataAddress,
        };
        var target = new GuestDepthTarget(region, region, 64, 64, 3, 0, 0f, false,
            HtileAddress: metadataAddress, HtileAcceleration: true, MetadataClear: true, Registers: words);

        presenter.Run(() =>
        {
            var depth = presenter.InvokeMethod("DiscoverDepthTarget", target)!;
            presenter.InvokeMethod("AcquireDepthAttachment", depth, new GuestDepthState(true, false, 7));
            Assert.True((bool)GetFieldValue(depth, "ClearDepth"));
            Assert.Equal(0f, (float)GetFieldValue(depth, "ClearDepthValue"));
            Assert.False((bool)GetFieldValue(depth, "ClearStencil"));
            Assert.False(harness.Images.IsMetadataCleared(metadataAddress, 0));
            presenter.InvokeMethod("ResetImageBindings");

            depth = presenter.InvokeMethod("DiscoverDepthTarget", target with { MetadataClear = false })!;
            presenter.InvokeMethod("AcquireDepthAttachment", depth, new GuestDepthState(true, false, 7));
            Assert.False((bool)GetFieldValue(depth, "ClearDepth"));
            presenter.InvokeMethod("ResetImageBindings");
        });
        harness.Finish();
        harness.Shutdown();
    }

    [Fact]
    public void StorageBinding_SelectsTheInstructionMipAboveTheDescriptorBase()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var presenter = new PresenterUnderTest(_vulkan);
        var harness = presenter.Harness;
        var address = harness.MapBacked(0x10000, ReadWrite);
        var storage = Sampled2D with { Storage = true };
        GuestDrawTexture Storage(uint mip, uint baseLevel = 0) => new(
            address, 64, 64, 0, 0, [], false, true, MipLevel: mip, BaseMipLevel: baseLevel,
            Descriptor: RegisterWords.Texture(address, GuestPixelFormat.Bits8_8_8_8UNorm, 64, 64, baseLevel: baseLevel, lastLevel: 2, maxMip: 2), Shape: storage);

        presenter.Run(() =>
        {
            var level1 = AcquireTexture(presenter, Storage(1));
            var level1View = ((ImageRequest)GetFieldValue(level1, "Request")).View;
            Assert.Equal(1u, level1View.BaseLevel);
            Assert.Equal(1u, level1View.LevelCount);

            var level0 = AcquireTexture(presenter, Storage(0));
            Assert.Equal(0u, ((ImageRequest)GetFieldValue(level0, "Request")).View.BaseLevel);
            Assert.NotEqual(((ImageView)GetFieldValue(level0, "View")).Handle, ((ImageView)GetFieldValue(level1, "View")).Handle);

            var stacked = AcquireTexture(presenter, Storage(1, baseLevel: 1));
            Assert.Equal(2u, ((ImageRequest)GetFieldValue(stacked, "Request")).View.BaseLevel);

            using var fatal = new FatalScope();
            Assert.Throws<SchedulerFatalException>(() => AcquireTexture(presenter, Storage(3)));
            Assert.Contains("mip=3 levels=3", Assert.Single(fatal.Messages));
            presenter.InvokeMethod("ResetImageBindings");
        });
        harness.Finish();
        harness.Shutdown();
    }

    [Fact]
    public void AcquisitionThatEndsTheTick_RecordsIntoTheNewBuffer()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var presenter = new PresenterUnderTest(_vulkan);
        var harness = presenter.Harness;
        const uint width = 2048;
        const uint height = 1024;
        const ulong uploadBytes = width * height * 4;
        var address = harness.MapBacked(uploadBytes, ReadWrite);

        presenter.Run(() =>
        {
            var attachment = presenter.InvokeMethod("DiscoverColorTarget", ColorTarget(address, width, height), false, false)!;
            // Leave less than the upload at the end of the staging ring, owned by the current tick.
            var staging = harness.Cache.GetUtilityBuffer(GpuBufferUsage.Upload);
            Assert.True(staging.TryMap(staging.Size - uploadBytes / 2, out _));
            staging.Commit();
            presenter.InvokeMethod("BeginBatchedGuestCommands");
            var tickBefore = harness.Scheduler.CurrentTick;
            var bufferBefore = harness.Scheduler.Current.Handle;

            presenter.InvokeMethod("AcquireColorAttachment", attachment);

            Assert.True(harness.Scheduler.CurrentTick > tickBefore);
            Assert.NotEqual(bufferBefore, harness.Scheduler.Current.Handle);
            Assert.True((bool)GetFieldValue(presenter.Instance, "_batchOpen"));
            Assert.Equal(harness.Scheduler.Current.Handle, ((CommandBuffer)presenter.InvokeMethod("BeginBatchedGuestCommands")!).Handle);
            Assert.Equal(ImageLayout.ColorAttachmentOptimal, (ImageLayout)GetFieldValue(attachment, "Layout"));
            presenter.InvokeMethod("ResetImageBindings");
        });
        harness.Finish();
        harness.Shutdown();
    }

    [Fact]
    public void LayeredAttachmentClear_ClearsEveryLayer()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var presenter = new PresenterUnderTest(_vulkan);
        var harness = presenter.Harness;
        const int sliceBytes = 64 * 64 * 4;
        var address = harness.MapBacked(0x10000, ReadWrite);
        var pattern = new byte[2 * sliceBytes];
        Array.Fill(pattern, (byte)0xAB);
        harness.Write(address, pattern);
        CachedImage image = null!;

        presenter.Run(() =>
        {
            var attachment = presenter.InvokeMethod("DiscoverColorTarget", ColorTarget(address, sliceMax: 1), false, false)!;
            presenter.InvokeMethod("AcquireColorAttachment", attachment);
            image = harness.Image((ResourceSlotIdentifier)GetFieldValue(attachment, "ImageIdentifier"));
            Assert.Equal(2u, image.Backing.Layers);

            var extent = new Extent2D(64, 64);
            ClearColorAttachment(presenter, attachment, extent, 2, default);
            presenter.InvokeMethod("ResetImageBindings");
            harness.Scheduler.Finish();
        });

        var bytes = harness.ReadImageBytes(image);
        Assert.Equal(2 * sliceBytes, bytes.Length);
        Assert.All(bytes, value => Assert.Equal(0, value));
        harness.Shutdown();
    }

    [Fact]
    public void ColdScheduler_StartsRecordingBeforeTheFirstTargetLookup()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var presenter = new PresenterUnderTest(_vulkan, startScheduler: false);
        var harness = presenter.Harness;
        var address = harness.MapBacked(0x10000, ReadWrite);
        Assert.False(harness.Scheduler.Active);

        presenter.Run(() =>
        {
            var attachment = presenter.InvokeMethod("DiscoverColorTarget", ColorTarget(address), false, false);
            Assert.NotNull(attachment);
            Assert.True(harness.Scheduler.Active);
            presenter.InvokeMethod("AcquireColorAttachment", attachment);
            Assert.NotEqual(0UL, ((ImageView)GetFieldValue(attachment, "View")).Handle);
            presenter.InvokeMethod("ResetImageBindings");
        });
        harness.Finish();
        harness.Shutdown();
    }

    [Fact]
    public void ColdScheduler_StartsRecordingBeforeTheFirstTextureLookup()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var presenter = new PresenterUnderTest(_vulkan, startScheduler: false);
        var harness = presenter.Harness;
        var address = harness.MapBacked(0x10000, ReadWrite);
        Assert.False(harness.Scheduler.Active);

        presenter.Run(() =>
        {
            var bindings = (Array)presenter.InvokeMethod("ResolveDrawTextures", new List<GuestDrawTexture> { Texture(address) })!;
            Assert.True(harness.Scheduler.Active);
            Assert.Equal(ImageLayout.ShaderReadOnlyOptimal, (ImageLayout)GetFieldValue(bindings.GetValue(0)!, "Layout"));
        });
        harness.Finish();
        harness.Shutdown();
    }

    [Fact]
    public void ConsecutiveLayerViews_UseDistinctAttachmentViews()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var presenter = new PresenterUnderTest(_vulkan);
        var harness = presenter.Harness;
        var address = harness.MapBacked(0x10000, ReadWrite);

        presenter.Run(() =>
        {
            ImageView Bind(uint layer)
            {
                var attachment = presenter.InvokeMethod("DiscoverColorTarget", ColorTarget(address, sliceMax: layer, sliceStart: layer), false, false)!;
                presenter.InvokeMethod("AcquireColorAttachment", attachment);
                var view = (ImageView)GetFieldValue(attachment, "View");
                presenter.InvokeMethod("ResetImageBindings");
                return view;
            }

            var layer1 = Bind(1);
            var layer0 = Bind(0);
            var layer0Again = Bind(0);
            Assert.NotEqual(layer1.Handle, layer0.Handle);
            Assert.Equal(layer0.Handle, layer0Again.Handle);
        });
        harness.Finish();
        harness.Shutdown();
    }

    [Fact]
    public void ShutdownScheduler_ShutsTheImageStoreDownBeforeTheBufferStoreAndTheScheduler()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var presenter = new PresenterUnderTest(_vulkan);
        var harness = presenter.Harness;
        var address = harness.MapBacked(0x10000, ReadWrite);
        presenter.Run(() =>
        {
            var attachment = presenter.InvokeMethod("DiscoverColorTarget", ColorTarget(address), false, false)!;
            presenter.InvokeMethod("AcquireColorAttachment", attachment);
            presenter.InvokeMethod("ResetImageBindings");
        });
        harness.Finish();
        Assert.Single(harness.ImagesInRange(address, 4));
        Assert.Equal(HostPageProtection.ReadOnly, harness.Protection(address));

        presenter.Run(() => presenter.InvokeMethod("ShutdownScheduler"));
        harness.MarkShutDown();

        Assert.Empty(harness.ImagesInRange(address, 4));
        Assert.Equal(HostPageProtection.ReadWrite, harness.Protection(address));
        harness.Vulkan.AssertNoValidationMessages();
    }
}
