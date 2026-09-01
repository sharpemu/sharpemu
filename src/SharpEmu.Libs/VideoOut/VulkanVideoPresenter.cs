// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Silk.NET.Core;
using System.Collections.Concurrent;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.AvPlayer;
using SharpEmu.Libs.Media;
using SharpEmu.Libs.Gpu;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Vulkan;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using Silk.NET.Vulkan.Extensions.EXT;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

namespace SharpEmu.Libs.VideoOut;

internal readonly record struct VulkanRenderTargetFormat(
    Format Format,
    Gen5PixelOutputKind OutputKind,
    Gen5ColorComponentMapping ExportMapping)
{
    public bool IsInteger => OutputKind is Gen5PixelOutputKind.Uint or Gen5PixelOutputKind.Sint;
}

internal static class VulkanGraphicsSubgroupPolicy
{
    internal static bool Resolve(uint nativeSubgroupSize, string? overrideValue) =>
        overrideValue switch
        {
            "0" => false,
            "1" => true,
            _ => nativeSubgroupSize == 32,
        };
}

internal sealed record VulkanTranslatedGuestDraw(
    byte[] VertexSpirv,
    byte[] PixelSpirv,
    IReadOnlyList<GuestDrawTexture> Textures,
    IReadOnlyList<GuestMemoryBuffer> GlobalMemoryBuffers,
    IReadOnlyList<GuestVertexBuffer> VertexBuffers,
    uint AttributeCount,
    uint VertexCount,
    uint InstanceCount,
    uint PrimitiveType,
    GuestIndexBuffer? IndexBuffer,
    GuestRenderState RenderState,
    int BaseVertex = 0);

internal sealed record VulkanOffscreenGuestDraw(
    VulkanTranslatedGuestDraw Draw,
    IReadOnlyList<GuestRenderTarget> Targets,
    GuestDepthTarget? DepthTarget,
    bool PublishTarget,
    ulong ShaderAddress);

internal sealed record VulkanOffscreenColorClear(
    IReadOnlyList<GuestRenderTarget> Targets,
    float Red,
    float Green,
    float Blue,
    float Alpha,
    ulong ShaderAddress);

internal sealed record VulkanComputeGuestDispatch(
    ulong ShaderAddress,
    byte[] ComputeSpirv,
    IReadOnlyList<GuestDrawTexture> Textures,
    IReadOnlyList<GuestMemoryBuffer> GlobalMemoryBuffers,
    uint GroupCountX,
    uint GroupCountY,
    uint GroupCountZ,
    uint BaseGroupX,
    uint BaseGroupY,
    uint BaseGroupZ,
    uint LocalSizeX,
    uint LocalSizeY,
    uint LocalSizeZ,
    bool IsIndirect,
    bool WritesGlobalMemory,
    uint ThreadCountX = uint.MaxValue,
    uint ThreadCountY = uint.MaxValue,
    uint ThreadCountZ = uint.MaxValue);

internal sealed record VulkanOrderedGuestAction(
    Action Action,
    string DebugName);

internal sealed record VulkanGuestCacheOperation(
    IReadOnlyList<GuestGpuCacheOperation> Operations,
    Action ApplyHostState,
    string DebugName);

internal readonly record struct VulkanGuestCacheBarrier(
    PipelineStageFlags DestinationStages,
    AccessFlags DestinationAccess);

internal enum VulkanGuestCacheResourceKind
{
    Buffer,
    DepthImage,
    ImageWithoutTrackedLayout,
}

internal readonly record struct VulkanGuestCacheResourceRange(
    ulong BaseAddress,
    ulong SizeBytes,
    VulkanGuestCacheResourceKind Kind);

internal readonly record struct VulkanGuestCacheResourcePlan(
    bool UseGlobalBarrier,
    int MatchedResourceCount);

internal static class VulkanGuestCacheBarrierPlanner
{
    private const GuestGpuCacheDomain ShaderDomains =
        GuestGpuCacheDomain.Instruction |
        GuestGpuCacheDomain.Scalar |
        GuestGpuCacheDomain.Vector |
        GuestGpuCacheDomain.ShaderL1 |
        GuestGpuCacheDomain.ShaderL2;

    public static VulkanGuestCacheBarrier Resolve(GuestGpuCacheOperation operation)
    {
        var hasNonShaderDomain = (operation.Domains & ~ShaderDomains) != 0;
        return hasNonShaderDomain || operation.Domains == GuestGpuCacheDomain.None
            ? new VulkanGuestCacheBarrier(
                PipelineStageFlags.AllCommandsBit,
                AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit)
            : new VulkanGuestCacheBarrier(
                PipelineStageFlags.AllGraphicsBit | PipelineStageFlags.ComputeShaderBit,
                AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit);
    }

    public static VulkanGuestCacheResourcePlan ResolveResources(
        GuestGpuCacheOperation operation,
        IReadOnlyList<VulkanGuestCacheResourceRange> resources)
    {
        if (operation.CoversAllMemory ||
            operation.SizeBytes == 0 ||
            operation.SizeBytes == ulong.MaxValue)
        {
            return new VulkanGuestCacheResourcePlan(
                UseGlobalBarrier: true,
                MatchedResourceCount: 0);
        }

        var matches = 0;
        foreach (var resource in resources)
        {
            if (!RangesOverlap(
                    operation.BaseAddress,
                    operation.SizeBytes,
                    resource.BaseAddress,
                    resource.SizeBytes))
            {
                continue;
            }

            matches++;
            if (resource.Kind ==
                VulkanGuestCacheResourceKind.ImageWithoutTrackedLayout)
            {
                return new VulkanGuestCacheResourcePlan(
                    UseGlobalBarrier: true,
                    MatchedResourceCount: matches);
            }
        }

        return new VulkanGuestCacheResourcePlan(
            UseGlobalBarrier: matches == 0,
            MatchedResourceCount: matches);
    }

    internal static bool RangesOverlap(
        ulong leftBase,
        ulong leftSize,
        ulong rightBase,
        ulong rightSize)
    {
        if (leftSize == 0 || rightSize == 0)
        {
            return false;
        }

        var leftEnd = SaturatingEnd(leftBase, leftSize);
        var rightEnd = SaturatingEnd(rightBase, rightSize);
        return leftBase < rightEnd && rightBase < leftEnd;
    }

    internal static ulong SaturatingEnd(ulong address, ulong size) =>
        address > ulong.MaxValue - size ? ulong.MaxValue : address + size;
}

internal sealed record VulkanGpuLabelSignal(
    Action<GuestGpuLabelDependency> PublishGpu,
    Action? PublishHost,
    string DebugName);

internal static class VulkanVertexBindingPlanner
{
    public static int BuildUniqueSourceIndices(
        ReadOnlySpan<ulong> handles,
        ReadOnlySpan<bool> perInstance,
        Span<int> sourceIndices)
    {
        if (handles.Length != perInstance.Length || sourceIndices.Length < handles.Length)
        {
            throw new ArgumentException("Vertex binding spans must have compatible lengths.");
        }

        var bindingCount = 0;
        for (var sourceIndex = 0; sourceIndex < handles.Length; sourceIndex++)
        {
            var found = false;
            for (var bindingIndex = 0; bindingIndex < bindingCount; bindingIndex++)
            {
                var previousSourceIndex = sourceIndices[bindingIndex];
                if (handles[previousSourceIndex] == handles[sourceIndex] &&
                    perInstance[previousSourceIndex] == perInstance[sourceIndex])
                {
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                sourceIndices[bindingCount++] = sourceIndex;
            }
        }

        return bindingCount;
    }
}

internal readonly record struct VulkanGuestQueueIdentity(
    string Name,
    ulong SubmissionId)
{
    public static VulkanGuestQueueIdentity Default { get; } = new("host.default", 0);
}

internal static unsafe partial class VulkanVideoPresenter
{
    private static int _nativeSubgroupSize;

    internal static bool GraphicsSubgroupOperationsEnabled =>
        VulkanGraphicsSubgroupPolicy.Resolve(
            unchecked((uint)Volatile.Read(ref _nativeSubgroupSize)),
            Environment.GetEnvironmentVariable("SHARPEMU_GRAPHICS_SUBGROUPS"));

    private static void SetNativeSubgroupSize(uint subgroupSize) =>
        Volatile.Write(ref _nativeSubgroupSize, checked((int)subgroupSize));

    // Standalone launches use a desktop-sized SDL surface unless configured.
    internal const uint Gen5DepthTileMode = 24;
    internal const uint Gen5TextureType2D = 9;
    internal const uint Gen5TextureType3D = 10;

    internal static bool IsGuestTexture3D(uint type) =>
        type == Gen5TextureType3D;

    internal static uint GetGuestTextureDepth(uint type, uint depth) =>
        IsGuestTexture3D(type) ? Math.Max(depth, 1u) : 1u;

    internal static ImageType GetGuestTextureImageType(uint type) =>
        IsGuestTexture3D(type) ? ImageType.Type3D : ImageType.Type2D;

    internal static ImageViewType GetGuestTextureViewType(
        uint type,
        bool arrayedView = false) =>
        IsGuestTexture3D(type)
            ? ImageViewType.Type3D
            : arrayedView
                ? ImageViewType.Type2DArray
                : ImageViewType.Type2D;

    // Vulkan's portable upper bound for minStorageBufferOffsetAlignment is
    // 256 bytes. Using that fixed power of two (instead of racing the render
    // thread's physical-device query) gives shader translation and descriptor
    // creation one stable aliasing contract on every conformant device.
    internal const ulong GuestStorageBufferOffsetAlignment = 256;
    // The pending queue and per-render drain budget bound how much guest GPU
    // work can be buffered ahead of the presenter. Draws are batched into
    // shared command buffers, so draining a large batch per render tick is
    // cheap; small caps here throttle games that issue more than a handful
    // of draws per frame to a fraction of the display rate. The pending cap
    // stays tighter than the drain budget because queued draws pin their
    // pooled guest-data arrays until the render thread uploads them.
    // The Cocoa event loop must stay responsive while guest work is pending,
    // but Windows and Linux render on a dedicated host thread. Keeping the
    // macOS item limit everywhere throttles draw-heavy games well below their
    // display rate before the byte budget is remotely close to full.
    private static readonly int _maxPendingGuestWorkItems =
        int.TryParse(
            Environment.GetEnvironmentVariable("SHARPEMU_PENDING_GUEST_WORK_ITEMS"),
            out var pendingGuestWorkItems) && pendingGuestWorkItems > 0
            ? pendingGuestWorkItems
            : OperatingSystem.IsMacOS() ? 64 : 512;
    private const ulong MaximumCachedHostBufferBytes = 128UL * 1024 * 1024;
    // A count-only queue bound is not a memory bound: one compute dispatch can
    // carry dozens of full-resolution texture snapshots.  At 4K, 64 queued
    // dispatches retained more than 12 GiB of managed byte arrays before the
    // render thread could upload them.  Keep the count cap for small work and
    // independently apply backpressure to the actual retained payload.
    private static readonly ulong _maxPendingGuestWorkBytes =
        (ulong.TryParse(
             Environment.GetEnvironmentVariable("SHARPEMU_PENDING_GUEST_WORK_MB"),
             out var pendingGuestWorkMb) && pendingGuestWorkMb > 0
            ? pendingGuestWorkMb
            : 256UL) * 1024UL * 1024UL;
    private static readonly int _maxGuestWorkPerRender =
        int.TryParse(
            Environment.GetEnvironmentVariable("SHARPEMU_MAX_GUEST_WORK_PER_RENDER"),
            out var guestWorkPerRender) && guestWorkPerRender > 0
            ? guestWorkPerRender
            : OperatingSystem.IsMacOS() ? 256 : 1024;
    // On macOS the whole window loop — including Render() and its guest-work
    // drain — runs on the process main thread, so draining a large backlog of
    // slow guest work (heavy compute) blocks the Cocoa event pump and marks the
    // window "Not Responding" while starving the swapchain present. Cap the
    // wall-clock time spent draining per Render() call; leftover work stays
    // queued for the next frame. SHARPEMU_RENDER_WORK_BUDGET_MS overrides
    // (0 disables the cap); default 12ms keeps the macOS window interactive at
    // ~60Hz. Windows and Linux use a dedicated render thread, so they drain
    // without a time budget by default.
    private static readonly long _renderWorkBudgetTicks =
        (long.TryParse(
             Environment.GetEnvironmentVariable("SHARPEMU_RENDER_WORK_BUDGET_MS"),
             out var renderBudgetMs) && renderBudgetMs >= 0
            ? renderBudgetMs
            : OperatingSystem.IsMacOS() ? 12L : 0L) *
        System.Diagnostics.Stopwatch.Frequency / 1000L;
    private static readonly int _guestWorkFollowupWaitMs =
        int.TryParse(
            Environment.GetEnvironmentVariable("SHARPEMU_RENDER_FOLLOWUP_WAIT_MS"),
            out var followupWaitMs) && followupWaitMs >= 0
            ? followupWaitMs
            : 2;
    private static readonly long _guestWorkFollowupBudgetTicks =
        (long.TryParse(
             Environment.GetEnvironmentVariable("SHARPEMU_RENDER_FOLLOWUP_BUDGET_MS"),
             out var followupBudgetMs) && followupBudgetMs >= 0
            ? followupBudgetMs
            : 24L) *
        System.Diagnostics.Stopwatch.Frequency / 1000L;
    // Max time the main-thread Render() will block waiting for a frame slot's
    // GPU fence before skipping the frame and returning to the event pump.
    // Prevents the window freezing behind a slow-compute GPU backlog.
    // SHARPEMU_FRAME_WAIT_BUDGET_MS overrides; default 8ms on macOS. The
    // dedicated Windows/Linux render thread may wait for its frame slot so it
    // does not drop guest-work drain opportunities under normal GPU load.
    private static readonly ulong _frameSlotWaitBudgetNs =
        ulong.TryParse(
            Environment.GetEnvironmentVariable("SHARPEMU_FRAME_WAIT_BUDGET_MS"),
            out var frameWaitMs) && frameWaitMs > 0
            ? frameWaitMs * 1_000_000UL
            : OperatingSystem.IsMacOS() ? 8_000_000UL : ulong.MaxValue;
    // Cap the guest-submission fence wait so a GPU submission whose fence never
    // signals (a mistranslated compute shader that hangs the Metal queue) cannot
    // freeze the render thread forever and starve the swapchain present.
    // SHARPEMU_FENCE_WAIT_TIMEOUT_MS overrides; default 3s.
    private static readonly ulong _guestFenceWaitTimeoutNs =
        ulong.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_FENCE_WAIT_TIMEOUT_MS"), out var fenceMs) && fenceMs > 0
            ? fenceMs * 1_000_000UL
            : 3_000_000_000UL;
    // When making room in the in-flight submission queue from the macOS MAIN
    // thread (Render() -> guest-work drain), block only this long per attempt
    // instead of the full fence timeout. If a slow/capped compute submission
    // isn't done yet, proceed anyway: the in-flight cap is soft, the fence and
    // command-buffer pools are dynamic so a brief overshoot is safe, and the
    // queue drains as GPU completions land on later frames. This keeps the
    // window responsive (event pump runs) under a heavy compute backlog instead
    // of the main thread sitting in vkWaitForFences for up to 3s per chunk.
    // SHARPEMU_SUBMISSION_CAPACITY_WAIT_MS overrides; default 100ms; 0 restores
    // the full blocking wait.
    private static readonly ulong _submissionCapacityWaitNs =
        ulong.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_SUBMISSION_CAPACITY_WAIT_MS"), out var capMs)
            ? capMs * 1_000_000UL
            : 100_000_000UL;
    private static readonly HashSet<string> _tracedFenceTimeouts = new();
    private static long _guestQueueBackpressureTraceCount;
    private static long _orderedActionFenceWaitTraceCount;
    private static long _guestQueueStarvationTraceCount;
    private static long _guestQueueStarvationLastQueued = -1;
    // Zero-payload sync (ordered actions / flip markers) may exceed the
    // payload item cap without hard-blocking producers; byte budget still
    // bounds fat compute/draw snapshots. Override with
    // SHARPEMU_PENDING_GUEST_SYNC_ITEMS (default 8x payload item cap).
    private static readonly int _maxPendingGuestSyncItems =
        int.TryParse(
            Environment.GetEnvironmentVariable("SHARPEMU_PENDING_GUEST_SYNC_ITEMS"),
            out var pendingGuestSyncItems) && pendingGuestSyncItems > 0
            ? pendingGuestSyncItems
            : Math.Max(_maxPendingGuestWorkItems * 8, 4096);
    private static int _pendingPayloadGuestWorkCount;
    private static int _pendingSyncGuestWorkCount;
    // Diagnostic: skip every compute dispatch (mistranslated compute shaders
    // run long / GPU-hang and starve the present). Isolates whether the
    // geometry+composite path renders on its own.
    private static readonly bool _skipAllCompute =
        Environment.GetEnvironmentVariable("SHARPEMU_SKIP_ALL_COMPUTE") == "1";
    // Use a second queue from the graphics queue family. Use one queue with
    // RenderDoc because a capture changes the timing of cross-queue work.
    // Set SHARPEMU_RENDERDOC_SINGLE_QUEUE=0 to test a multi-queue capture.
    private static readonly bool _renderDocSingleQueue =
        RenderDocCapture.IsAvailable &&
        !string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_RENDERDOC_SINGLE_QUEUE"),
            "0",
            StringComparison.Ordinal);
    private static readonly bool _useDedicatedComputeQueueRequested =
        !_renderDocSingleQueue &&
        !string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_DEDICATED_COMPUTE_QUEUE"),
            "0",
            StringComparison.Ordinal);
    private static readonly bool _gpuLabelTimelineRequested =
        IsGpuLabelTimelineRequested(
            Environment.GetEnvironmentVariable("SHARPEMU_GPU_LABEL_TIMELINE"));
    private static readonly bool _gpuLabelVirtualWritesEnabled =
        _gpuLabelTimelineRequested &&
        !string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_GPU_LABEL_VIRTUAL_WRITES"),
            "0",
            StringComparison.Ordinal);
    private static bool _gpuLabelTimelineAvailable;

    internal static bool IsGpuLabelTimelineRequested(string? setting) =>
        !string.Equals(setting, "0", StringComparison.Ordinal);

    internal static GuestGpuLabelDependency ResolveGpuLabelSubmissionDependency(
        GuestGpuLabelDependency requiredDependency,
        GuestGpuLabelDependency priorQueueDependency) =>
        requiredDependency.Merge(priorQueueDependency);
    // Diagnostic: skip compute dispatches whose GroupCountZ is at least this,
    // to isolate a specific tall dispatch (e.g. Demon's Souls' 27x15x72 froxel
    // shader that hangs the Metal queue) without needing its ASLR-varying
    // address. 0 disables.
    private static readonly uint _skipTallComputeZ =
        uint.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_SKIP_TALL_COMPUTE_Z"), out var z)
            ? z
            : 0;
    private const uint GuestPrimitiveRectList = AgcPrimitiveHelpers.PrimitiveRectListLegacy;
    private const uint GuestPrimitiveRectListNgg = AgcPrimitiveHelpers.PrimitiveRectList;

    internal static int ScorePhysicalDevice(
        PhysicalDeviceProperties properties,
        string name,
        string? deviceOverride)
    {
        if (!string.IsNullOrWhiteSpace(deviceOverride))
        {
            return name.Contains(deviceOverride, StringComparison.OrdinalIgnoreCase) ? 1000 : -1000;
        }

        var score = properties.DeviceType switch
        {
            PhysicalDeviceType.DiscreteGpu => 300,
            PhysicalDeviceType.VirtualGpu => 100,
            PhysicalDeviceType.IntegratedGpu => 50,
            PhysicalDeviceType.Cpu => 20,
            _ => 10,
        };

        if (properties.VendorID == NvidiaVendorId)
        {
            score += 500;
        }

        score -= ComputeDevicePenalty(properties, OperatingSystem.IsWindows());

        return score;
    }

    internal static int ComputeDevicePenalty(PhysicalDeviceProperties properties, bool isWindows)
    {
        var penalty = 0;

        if (isWindows &&
            properties.DeviceType == PhysicalDeviceType.IntegratedGpu &&
            properties.VendorID == AmdVendorId)
        {
            penalty += LastResortPenalty;
        }

        return penalty;
    }

    internal static bool RequiresRealFormatConversion(Format from, Format to)
    {
        static bool Is10Bit(Format f) =>
            f is Format.A2R10G10B10UnormPack32 or Format.A2B10G10R10UnormPack32;
        return (from == Format.R8G8B8A8Unorm && Is10Bit(to)) ||
               (Is10Bit(from) && to == Format.R8G8B8A8Unorm);
    }

    private sealed partial class Presenter : IDisposable
    {

        private long _presentedSequence;
        private long _presentNotTakenLoggedSequence = long.MinValue;
        private bool _vulkanReady;
        private bool _firstFramePresented;
        private bool _firstGuestDrawPresented;
        private bool _splashPresented;
        private bool _deviceLost;
        private bool _deviceLostLogged;
        // Last guest work the render thread entered; included in device-lost
        // reports so QueueSubmit faults name the offending dispatch/draw.
        private string _activeGuestWorkLabel = string.Empty;
        // Survives the per-work finally clear: batched submits often flush
        // after the label is reset (queue switch / end-of-drain).
        private string _lastGuestWorkLabel = string.Empty;
        private string _lastSubmitDebugName = string.Empty;
        private int _directPresentationCount;
        private readonly Dictionary<ulong, long> _presentedGuestImageTraceCounts = new();
        private readonly Dictionary<long, GuestImageResource> _guestImageVersions = new();
        private readonly HashSet<(ulong Address, uint Width, uint Height, Format Format)> _tracedTextureCacheHits = new();
        private readonly HashSet<(ulong Address, int ActualSize, ulong ExpectedSize, Format Format)>
            _rejectedGuestImageUploads = new();
        private readonly Dictionary<ulong, int> _tracedGuestWriteCounts = new();
        private readonly Dictionary<int, int> _pixelSpirvWriteCounts = new();

        private sealed class TranslatedDrawResources
        {
            public string DebugName = "SharpEmu translated";
            public PipelineLayout PipelineLayout;
            public Pipeline Pipeline;
            public bool PipelineCached;
            public bool DescriptorLayoutCached;
            public DescriptorSetLayout DescriptorSetLayout;
            public DescriptorPool DescriptorPool;
            public DescriptorSet DescriptorSet;
            public TextureResource[] Textures = [];
            public List<(VkBuffer Buffer, DeviceMemory Memory)> DeferredTextureStagingBuffers { get; } = [];
            public GlobalBufferResource[] GlobalMemoryBuffers = [];
            public VertexBufferResource[] VertexBuffers = [];
            public VkBuffer IndexBuffer;
            public DeviceMemory IndexMemory;
            public bool Index32Bit;
            public uint VertexCount = 3;
            public uint InstanceCount = 1;
            public int BaseVertex;
            public PrimitiveTopology Topology = PrimitiveTopology.TriangleList;
            public GuestBlendState[] Blends = [GuestBlendState.Default];
            public GuestBlendConstant BlendConstant;
            // Vulkan format of this draw's color target. Needed to suppress
            // blending on formats Metal cannot blend (integer / 32-bit float),
            // which otherwise makes vkCreateGraphicsPipelines fail and can
            // trip a Metal validation assertion.
            public Format[] TargetFormats = [Format.Undefined];
            public GuestRect? Scissor;
            public GuestViewport? Viewport;
            public GuestRasterState Raster = GuestRasterState.Default;
            public GuestDepthState Depth = GuestDepthState.Default;
            public bool HasDepthAttachment;
            public Format DepthAttachmentFormat = Format.Undefined;
            // Layout keys are needed twice per draw (pipeline lookup and
            // descriptor-layout lookup); cache the built strings.
            public string? ResourceLayoutKey;
            public string? VertexLayoutKey;
            public RenderPass TransientRenderPass;
            public Framebuffer TransientFramebuffer;
        }

        private const ulong SwapchainAcquireTimeoutNs = 250_000_000;

        public Presenter(uint width, uint height)
        {
            _hostBufferPool = new VulkanHostBufferPool(
                MaximumCachedHostBufferBytes,
                DestroyHostBufferAllocation);
            if (_retireCachedTextureStaging)
            {
                Console.Error.WriteLine(
                    "[LOADER][INFO] Vulkan cached texture staging retirement enabled.");
            }
            _window = new SdlHostWindow(
                VideoOutExports.GetWindowTitle(),
                _videoOptions,
                SdlGraphicsApi.Vulkan);
        }

        public void Run()
        {
            _window.Run(
                Initialize,
                Render,
                () =>
                {
                    Console.Error.WriteLine(
                        $"[LOADER][WARN] Vulkan VideoOut window closing; " +
                        $"requested={Volatile.Read(ref _presenterCloseRequested)} " +
                        $"deviceLost={_deviceLost}");
                    VideoOutExports.NotifyPresentationWindowClosed();
                    DisposeVulkan();
                },
                WaitForRenderWork);
        }

        public void Dispose()
        {
            DisposeVulkan();
            _window.Dispose();
        }

        private static bool AnyTargetAddressMatches(
            IReadOnlyList<GuestImageResource>? targets,
            string environmentVariable)
        {
            if (targets is null)
            {
                return false;
            }

            foreach (var target in targets)
            {
                if (AddressListContains(environmentVariable, target.Address))
                {
                    return true;
                }
            }

            return false;
        }

        [ThreadStatic]
        private static string? _pendingShaderModuleDumpPath;

        private static byte[]? TryReadGuestTexturePixels(GuestDrawTexture texture)
        {
            var memory = _guestMemory;
            if (memory is null || texture.Address == 0)
            {
                return null;
            }

            var width = Math.Max(texture.Width, 1);
            var height = Math.Max(texture.Height, 1);
            var rowLength = texture.TileMode == 0
                ? Math.Max(texture.Pitch, width)
                : width;
            var depth = GetGuestTextureDepth(texture.Type, texture.Depth);
            var byteCount = GetTextureByteCount(texture.Format, rowLength, height, depth);
            if (byteCount == 0 || byteCount > int.MaxValue)
            {
                return null;
            }

            var pixels = new byte[(int)byteCount];
            return memory.TryRead(texture.Address, pixels) ? pixels : null;
        }

        private void ProcessDeferredTextureDestroys()
        {
            while (_deferredTextureDestroys.TryPeek(out var entry) &&
                   entry.RetireTimeline <= _completedTimeline)
            {
                _deferredTextureDestroys.Dequeue();
                DestroyCachedTextureResource(entry.Texture);
            }

            while (_deferredResourceDestroys.TryPeek(out var resourceEntry) &&
                   resourceEntry.RetireTimeline <= _completedTimeline)
            {
                _deferredResourceDestroys.Dequeue();
                DestroyTranslatedDrawResources(resourceEntry.Resources);
            }

            while (_deferredGuestImageVersionDestroys.TryPeek(out var imageEntry) &&
                   imageEntry.RetireTimeline <= _completedTimeline)
            {
                _deferredGuestImageVersionDestroys.Dequeue();
                DestroyGuestImage(imageEntry.Image);
                FlipProgressTracker.RecordFlip(imageEntry.Image.FlipVersion);
                TraceVulkanShader(
                    $"vk.flip_retired version={imageEntry.Image.FlipVersion} " +
                    $"timeline={imageEntry.RetireTimeline} reason=presentation-dropped");
            }

            while (_deferredGuestImageVariantDestroys.TryPeek(out var variantEntry) &&
                   variantEntry.RetireTimeline <= _completedTimeline)
            {
                _deferredGuestImageVariantDestroys.Dequeue();
                DestroyGuestImage(variantEntry.Image);
                _guestImageVariantDeferredDestroyCount++;
            }
        }

        private static void WriteUInt16(byte[] output, int offset, ushort value)
        {
            output[offset + 0] = (byte)value;
            output[offset + 1] = (byte)(value >> 8);
        }

        private static void WriteUInt32(byte[] output, int offset, uint value)
        {
            output[offset + 0] = (byte)value;
            output[offset + 1] = (byte)(value >> 8);
            output[offset + 2] = (byte)(value >> 16);
            output[offset + 3] = (byte)(value >> 24);
        }

        private static void WriteInt32(byte[] output, int offset, int value) =>
            WriteUInt32(output, offset, unchecked((uint)value));

        private VkBuffer CreateBuffer(
            ulong size,
            BufferUsageFlags usage,
            MemoryPropertyFlags memoryFlags,
            out DeviceMemory memory,
            MemoryPropertyFlags preferredMemoryFlags = 0)
        {
            var bufferInfo = new BufferCreateInfo
            {
                SType = StructureType.BufferCreateInfo,
                Size = size,
                Usage = usage,
                SharingMode = SharingMode.Exclusive,
            };
            Check(_vk.CreateBuffer(_device, &bufferInfo, null, out var buffer), "vkCreateBuffer");

            _vk.GetBufferMemoryRequirements(_device, buffer, out var requirements);
            var memoryInfo = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = requirements.Size,
                MemoryTypeIndex = FindMemoryType(
                    requirements.MemoryTypeBits,
                    memoryFlags,
                    preferredMemoryFlags),
            };
            Check(_vk.AllocateMemory(_device, &memoryInfo, null, out memory), "vkAllocateMemory");
            Check(_vk.BindBufferMemory(_device, buffer, memory, 0), "vkBindBufferMemory");
            return buffer;
        }

        private uint FindMemoryType(
            uint typeBits,
            MemoryPropertyFlags requiredFlags,
            MemoryPropertyFlags preferredFlags = 0)
        {
            _vk.GetPhysicalDeviceMemoryProperties(_physicalDevice, out var properties);
            var memoryTypes = &properties.MemoryTypes.Element0;

            for (var pass = preferredFlags != 0 ? 0 : 1; pass < 2; pass++)
            {
                var wanted = pass == 0 ? requiredFlags | preferredFlags : requiredFlags;
                for (uint index = 0; index < properties.MemoryTypeCount; index++)
                {
                    if ((typeBits & (1u << (int)index)) != 0 &&
                        (memoryTypes[index].PropertyFlags & wanted) == wanted)
                    {
                        return index;
                    }
                }
            }

            throw new InvalidOperationException("No compatible Vulkan host-visible memory type was found.");
        }

        private static uint ClampMipLevels(
            uint width,
            uint height,
            uint depth,
            uint requestedMipLevels)
        {
            var largestDimension = Math.Max(Math.Max(width, height), depth);
            uint maximumMipLevels = 1;
            while (largestDimension > 1)
            {
                largestDimension >>= 1;
                maximumMipLevels++;
            }

            return Math.Min(Math.Max(requestedMipLevels, 1u), maximumMipLevels);
        }

        private static uint GetMipDimension(uint dimension, uint mipLevel) =>
            mipLevel >= 32
                ? 1
                : Math.Max(dimension >> (int)mipLevel, 1u);

        private void ReleaseUnsubmittedPresentationResources(
            int frameSlot,
            TranslatedDrawResources? translatedResources,
            bool ownsPresentedGuestImageVersion,
            GuestImageResource? presentedGuestImage)
        {
            if (translatedResources is not null)
            {
                DestroyTranslatedDrawResources(translatedResources);
            }

            if (!ownsPresentedGuestImageVersion || presentedGuestImage is null ||
                _frameGuestImageVersions.Length <= frameSlot ||
                !ReferenceEquals(_frameGuestImageVersions[frameSlot], presentedGuestImage))
            {
                return;
            }

            _frameGuestImageVersions[frameSlot] = null;
            DestroyGuestImage(presentedGuestImage);
        }

        // Metal cannot blend into integer render targets or 32-bit-per-channel
        // float targets (unsupported on Apple-family GPUs). Enabling blend on
        // one makes vkCreateGraphicsPipelines fail with ErrorInitializationFailed
        // (and trips a Metal "not blendable" validation assertion), so the draw
        // is silently dropped. Force blend off for those; blending on an integer
        // target is meaningless on real hardware anyway.
        private static bool IsBlendableFormat(Format format) =>
            format switch
            {
                Format.R8Uint or Format.R8Sint or
                Format.R8G8B8A8Uint or Format.R8G8B8A8Sint or
                Format.R16G16Uint or Format.R16G16Sint or
                Format.R16G16B16A16Uint or Format.R16G16B16A16Sint or
                Format.R32Uint or Format.R32Sint or
                Format.R32G32Uint or Format.R32G32Sint or
                Format.R32G32B32A32Uint or Format.R32G32B32A32Sint or
                Format.R32Sfloat or
                Format.R32G32Sfloat or
                Format.R32G32B32A32Sfloat => false,
                _ => true,
            };

        private static ImageSubresourceRange ColorSubresourceRange(
            uint baseMipLevel = 0,
            uint levelCount = 1,
            uint layerCount = 1) =>
            new()
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = baseMipLevel,
                LevelCount = levelCount,
                LayerCount = layerCount,
            };

        private static void TraceVulkanShader(string message)
        {
            if (!_traceVulkanShaderEnabled)
            {
                return;
            }

            Console.Error.WriteLine($"[LOADER][TRACE] {message}");
        }
    }
}
