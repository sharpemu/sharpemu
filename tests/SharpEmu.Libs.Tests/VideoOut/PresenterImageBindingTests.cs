// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using SharpEmu.HLE.Host;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using SharpEmu.Libs.VideoOut;
using Silk.NET.Vulkan;
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
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void OffscreenFailureDescription_AcceptsMissingColorTargets(bool hasColor, bool hasDepth)
    {
        var targets = hasColor ? new[] { ColorTarget(0x10000) } : Array.Empty<GuestRenderTarget>();
        var depth = hasDepth ? new GuestDepthTarget(0x20000, 0x20000, 32, 16, 3, 0, 1f, false) : null;
        var description = (string)PresenterType.GetMethod("DescribeOffscreenTarget", InstanceMembers | BindingFlags.Static)!
            .Invoke(null, new object?[] { targets, depth })!;
        Assert.Contains(hasColor ? "size=64x64" : hasDepth ? "depth_only=true" : "targets=none", description);
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
                var targets = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(ColorAttachmentType))!;
                var pass = ((RenderPass, Framebuffer))presenter.InvokeMethod("CreateRenderPassAndFramebuffer", targets, depth, new Extent2D(64, 64), 1u, 1u)!;
                presenter.SetField("_commandBuffer", presenter.Command);
                presenter.InvokeMethod("BeginTranslatedRenderPass", pass.Item1, pass.Item2, new Extent2D(64, 64), 0, true, 1f, (byte)0, Array.Empty<ClearColorValue>());
                harness.Vulkan.Vk.CmdEndRenderPass(presenter.Command);
                harness.Scheduler.Finish();
                DestroyTransientPass(presenter, pass);
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
            var pass = ((RenderPass, Framebuffer))presenter.InvokeMethod("CreateRenderPassAndFramebuffer", ColorTargets(color), null, extent, 1u, 1u)!;
            presenter.SetField("_commandBuffer", presenter.Command);
            presenter.InvokeMethod("BeginTranslatedRenderPass", pass.Item1, pass.Item2, extent, 1, false, 1f, (byte)0, new[] { new ClearColorValue(1f, 0f, 0f, 1f) });
            harness.Vulkan.Vk.CmdEndRenderPass(presenter.Command);
            harness.Scheduler.Finish();
            DestroyTransientPass(presenter, pass);
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

    // The scheduler drives the presenter's submission bookkeeping and render-pass state, as in production.
    private sealed class SchedulerForwarder : IRenderingState
    {
        public PresenterUnderTest? Target { get; set; }

        public bool IsRendering => Target is { } target && ((IRenderingState)target.Instance).IsRendering;

        public void EndRendering()
        {
            if (Target is { } target)
            {
                ((IRenderingState)target.Instance).EndRendering();
            }
        }

        public void Prepare(SubmitBundle bundle) => Target?.InvokeMethod("PrepareGuestSubmission", bundle);

        public void Complete(ulong tick) => Target?.InvokeMethod("CompleteGuestSubmission", tick);
    }

    // A presenter over the harness caches without a window, a swapchain or a render thread.
    private sealed class PresenterUnderTest : IDisposable
    {
        public PresenterUnderTest(HeadlessVulkan vulkan, bool startScheduler = true)
        {
            var forwarder = new SchedulerForwarder();
            Harness = new CacheHarness(vulkan, hooks: new SchedulerHooks(forwarder, forwarder.Prepare, forwarder.Complete), startScheduler: startScheduler);
            Samplers = new SamplerStore(vulkan.DeviceInfo);
            Instance = RuntimeHelpers.GetUninitializedObject(PresenterType);
            SetField("_vk", vulkan.Vk);
            SetField("_device", vulkan.Device);
            SetField("_deviceInfo", vulkan.DeviceInfo);
            SetField("_scheduler", Harness.Scheduler);
            SetField("_relay", Harness.Worker.Relay);
            SetField("_bufferCache", Harness.Cache);
            SetField("_imageCache", Harness.Images);
            SetField("_samplerStore", Samplers);
            SetField("_trackedImageBindings", new List<ResourceSlotIdentifier>());
            SetField("_submissionContext", new SubmissionContext { QueueName = "presenter.test", SubmissionId = 1 });
            foreach (var name in new[]
            {
                "_batchResources", "_batchRetireBuffers", "_pendingGuestSubmissions", "_lastSubmittedTimelineByGuestQueue",
                "_lastSubmittedGpuLabelDependencyByGuestQueue", "_gpuLabelHostPublications", "_recycledDescriptorPools",
                "_deferredResourceDestroys", "_deferredGuestImageVersionDestroys",
            })
            {
                var field = PresenterType.GetField(name, InstanceMembers)!;
                field.SetValue(Instance, Activator.CreateInstance(field.FieldType, nonPublic: true));
            }

            forwarder.Target = this;
        }

        public CacheHarness Harness { get; }

        public SamplerStore Samplers { get; }

        public object Instance { get; }

        public CommandBuffer Command => new(Harness.Scheduler.Current.Handle);

        public void SetField(string name, object value) => PresenterType.GetField(name, InstanceMembers)!.SetValue(Instance, value);

        public object? InvokeMethod(string name, params object?[] arguments)
        {
            try
            {
                return PresenterType.GetMethod(name, InstanceMembers | BindingFlags.Static)!.Invoke(Instance, arguments);
            }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                throw exception.InnerException;
            }
        }

        public T Run<T>(Func<T> work) => Harness.Worker.Run(work);

        public void Run(Action work) => Harness.Worker.Run(work);

        public void Dispose()
        {
            Run(Samplers.Dispose);
            Harness.Dispose();
        }
    }

    private static object GetFieldValue(object target, string name) => target.GetType().GetField(name, InstanceMembers)!.GetValue(target)!;

    private static GuestRenderTarget ColorTarget(ulong address, uint width = 64, uint height = 64, uint sliceMax = 0, uint sliceStart = 0) =>
        new(address, width, height, 0, 0, Registers: RegisterWords.Color(address, width, height, sliceMax: sliceMax, sliceStart: sliceStart));

    private static GuestDrawTexture Texture(ulong address) =>
        new(address, 64, 64, 0, 0, [], false, false, Descriptor: RegisterWords.Texture(address, GuestPixelFormat.Bits8_8_8_8UNorm, 64, 64), Shape: Sampled2D);

    private static IList ColorTargets(object attachment)
    {
        var targets = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(ColorAttachmentType))!;
        targets.Add(attachment);
        return targets;
    }

    // Binds one shader image the way a draw does and returns the bound resource.
    private static object AcquireTexture(PresenterUnderTest presenter, GuestDrawTexture texture)
    {
        var resource = presenter.InvokeMethod("ResolveTexture", texture)!;
        var bindings = Array.CreateInstance(TextureResourceType, 1);
        bindings.SetValue(resource, 0);
        presenter.InvokeMethod("AcquireTextureViews", bindings, new List<GuestDrawTexture> { texture });
        return bindings.GetValue(0)!;
    }

    private static void DestroyTransientPass(PresenterUnderTest presenter, (RenderPass RenderPass, Framebuffer Framebuffer) pass)
    {
        var resources = Activator.CreateInstance(PresenterType.GetNestedType("TranslatedDrawResources", BindingFlags.NonPublic)!, nonPublic: true)!;
        resources.GetType().GetField("TransientRenderPass")!.SetValue(resources, pass.RenderPass);
        resources.GetType().GetField("TransientFramebuffer")!.SetValue(resources, pass.Framebuffer);
        presenter.InvokeMethod("DestroyTranslatedDrawResources", resources);
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
    public void CacheCollection_UsesSubmissionBoundariesInsteadOfPartialProgress()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var presenter = new PresenterUnderTest(_vulkan);
        var harness = presenter.Harness;
        var address = harness.MapBacked(0x10000, ReadWrite);
        presenter.Run(() =>
        {
            // Check steady reuse after the initial collection-age window.
            for (var warmup = 0; warmup < 20; warmup++)
            {
                presenter.InvokeMethod("RunGuestCacheCollection");
            }

            var resource = AcquireTexture(presenter, Texture(address));
            var imageIdentifier = (ResourceSlotIdentifier)GetFieldValue(resource, "ImageIdentifier");
            presenter.InvokeMethod("ResetImageBindings");
            var initialTick = (ulong)GetFieldValue(harness.Images, "_collectionTick");
            var ordinaryAction = new VulkanOrderedGuestAction(() => { }, "ordinary action");
            for (var retry = 0; retry < 100; retry++)
            {
                presenter.InvokeMethod("ProcessGuestCacheReadbacks");
                presenter.InvokeMethod("PrepareGuestSubmissionCompletion", ordinaryAction);
            }

            Assert.Equal(initialTick, (ulong)GetFieldValue(harness.Images, "_collectionTick"));
            Assert.True(harness.Images.Contains(imageIdentifier));
            var completion = new VulkanOrderedGuestAction(() => { }, "submission end")
            {
                CollectionPending = true,
            };
            presenter.InvokeMethod("PrepareGuestSubmissionCompletion", completion);
            Assert.False(completion.CollectionPending);
            for (var retry = 0; retry < 100; retry++)
            {
                presenter.InvokeMethod("PrepareGuestSubmissionCompletion", completion);
            }

            Assert.Equal(initialTick + 1, (ulong)GetFieldValue(harness.Images, "_collectionTick"));
            for (var frame = 0; frame < 3; frame++)
            {
                _ = AcquireTexture(presenter, Texture(address));
                presenter.InvokeMethod("ResetImageBindings");
                for (var submission = 0; submission < 12; submission++)
                {
                    presenter.InvokeMethod("ProcessGuestCacheReadbacks");
                    presenter.InvokeMethod("ProcessGuestCacheReadbacks");
                    presenter.InvokeMethod("PrepareGuestSubmissionCompletion",
                        new VulkanOrderedGuestAction(() => { }, "submission end") { CollectionPending = true });
                }

                Assert.True(harness.Images.Contains(imageIdentifier));
            }

            Assert.Equal(initialTick + 37, (ulong)GetFieldValue(harness.Images, "_collectionTick"));
        });
        harness.Finish();
        harness.Shutdown();
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
    public void TransientRenderPassAndFramebuffer_AreCreatedPerDrawAndRetiredWithIt()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var presenter = new PresenterUnderTest(_vulkan);
        var harness = presenter.Harness;
        var address = harness.MapBacked(0x10000, ReadWrite);

        presenter.Run(() =>
        {
            var attachment = presenter.InvokeMethod("DiscoverColorTarget", ColorTarget(address), false, false)!;
            presenter.InvokeMethod("AcquireColorAttachment", attachment);
            var targets = ColorTargets(attachment);

            var first = ((RenderPass, Framebuffer))presenter.InvokeMethod("CreateRenderPassAndFramebuffer", targets, null, new Extent2D(64, 64), 1u, 1u)!;
            var second = ((RenderPass, Framebuffer))presenter.InvokeMethod("CreateRenderPassAndFramebuffer", targets, null, new Extent2D(64, 64), 1u, 1u)!;
            Assert.NotEqual(0UL, first.Item1.Handle);
            Assert.NotEqual(0UL, first.Item2.Handle);
            Assert.NotEqual(first.Item2.Handle, second.Item2.Handle);

            DestroyTransientPass(presenter, first);
            DestroyTransientPass(presenter, second);
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
        VulkanVideoPresenter.RequestGuestColorClear(address);
        CachedImage image = null!;

        presenter.Run(() =>
        {
            var attachment = presenter.InvokeMethod("DiscoverColorTarget", ColorTarget(address, sliceMax: 1), false, false)!;
            presenter.InvokeMethod("AcquireColorAttachment", attachment);
            Assert.True((bool)GetFieldValue(attachment, "Clear"));
            image = harness.Image((ResourceSlotIdentifier)GetFieldValue(attachment, "ImageIdentifier"));
            Assert.Equal(2u, image.Backing.Layers);

            var extent = new Extent2D(64, 64);
            var pass = ((RenderPass, Framebuffer))presenter.InvokeMethod("CreateRenderPassAndFramebuffer", ColorTargets(attachment), null, extent, 1u, 2u)!;
            var command = presenter.Command;
            presenter.SetField("_commandBuffer", command);
            presenter.InvokeMethod("BeginTranslatedRenderPass", pass.Item1, pass.Item2, extent, 1, false, 1f, (byte)0, new[] { (ClearColorValue)GetFieldValue(attachment, "ClearValue") });
            harness.Vulkan.Vk.CmdEndRenderPass(command);
            presenter.InvokeMethod("ResetImageBindings");
            harness.Scheduler.Finish();
            DestroyTransientPass(presenter, pass);
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
    public void ConsecutiveLayerViews_DoNotContinueTheOpenPass()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var presenter = new PresenterUnderTest(_vulkan);
        var harness = presenter.Harness;
        var address = harness.MapBacked(0x10000, ReadWrite);

        presenter.Run(() =>
        {
            VulkanRenderPassReuseKey Bind(uint layer)
            {
                var attachment = presenter.InvokeMethod("DiscoverColorTarget", ColorTarget(address, sliceMax: layer, sliceStart: layer), false, false)!;
                presenter.InvokeMethod("AcquireColorAttachment", attachment);
                var key = (VulkanRenderPassReuseKey)presenter.InvokeMethod("CreateRenderPassReuseCandidate", ColorTargets(attachment), null, 64u, 64u, 1u)!;
                presenter.InvokeMethod("ResetImageBindings");
                return key;
            }

            var layer1 = Bind(1);
            var layer0 = Bind(0);
            var layer0Again = Bind(0);
            Assert.Equal(layer1.Image, layer0.Image);
            Assert.NotEqual(layer1.AttachmentViews[0], layer0.AttachmentViews[0]);
            Assert.False(VulkanRenderPassReusePolicy.CanContinue(layer1, layer0, VulkanRenderPassReuseHazard.None));
            Assert.True(VulkanRenderPassReusePolicy.CanContinue(layer0, layer0Again, VulkanRenderPassReuseHazard.None));
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
