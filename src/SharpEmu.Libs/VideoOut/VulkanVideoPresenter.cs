// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Silk.NET.Core;
using Silk.NET.Core.Native;
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
    GuestGpuCacheOperation Operation,
    Action ApplyHostState,
    string DebugName);

internal readonly record struct VulkanGuestCacheBarrier(
    PipelineStageFlags DestinationStages,
    AccessFlags DestinationAccess);

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
        private const string FullscreenBarycentricVertexSpirv =
            "AwIjBwAAAQALAAgAMgAAAAAAAAARAAIAAQAAAAsABgABAAAAR0xTTC5zdGQuNDUwAAAAAA4AAwAAAAAAAQAAAA8ACAAAAAAABAAAAG1haW4AAAAADQAAABoAAAApAAAAAwADAAIAAADCAQAABQAEAAQAAABtYWluAAAAAAUABgALAAAAZ2xfUGVyVmVydGV4AAAAAAYABgALAAAAAAAAAGdsX1Bvc2l0aW9uAAYABwALAAAAAQAAAGdsX1BvaW50U2l6ZQAAAAAGAAcACwAAAAIAAABnbF9DbGlwRGlzdGFuY2UABgAHAAsAAAADAAAAZ2xfQ3VsbERpc3RhbmNlAAUAAwANAAAAAAAAAAUABgAaAAAAZ2xfVmVydGV4SW5kZXgAAAUABQAdAAAAaW5kZXhhYmxlAAAABQAFACkAAABiYXJ5Y2VudHJpYwAFAAUALwAAAGluZGV4YWJsZQAAAEcAAwALAAAAAgAAAEgABQALAAAAAAAAAAsAAAAAAAAASAAFAAsAAAABAAAACwAAAAEAAABIAAUACwAAAAIAAAALAAAAAwAAAEgABQALAAAAAwAAAAsAAAAEAAAARwAEABoAAAALAAAAKgAAAEcABAApAAAAHgAAAAAAAAATAAIAAgAAACEAAwADAAAAAgAAABYAAwAGAAAAIAAAABcABAAHAAAABgAAAAQAAAAVAAQACAAAACAAAAAAAAAAKwAEAAgAAAAJAAAAAQAAABwABAAKAAAABgAAAAkAAAAeAAYACwAAAAcAAAAGAAAACgAAAAoAAAAgAAQADAAAAAMAAAALAAAAOwAEAAwAAAANAAAAAwAAABUABAAOAAAAIAAAAAEAAAArAAQADgAAAA8AAAAAAAAAFwAEABAAAAAGAAAAAgAAACsABAAIAAAAEQAAAAMAAAAcAAQAEgAAABAAAAARAAAAKwAEAAYAAAATAAAAAACAvywABQAQAAAAFAAAABMAAAATAAAAKwAEAAYAAAAVAAAAAABAQCwABQAQAAAAFgAAABUAAAATAAAALAAFABAAAAAXAAAAEwAAABUAAAAsAAYAEgAAABgAAAAUAAAAFgAAABcAAAAgAAQAGQAAAAEAAAAOAAAAOwAEABkAAAAaAAAAAQAAACAABAAcAAAABwAAABIAAAAgAAQAHgAAAAcAAAAQAAAAKwAEAAYAAAAhAAAAAAAAACsABAAGAAAAIgAAAAAAgD8gAAQAJgAAAAMAAAAHAAAAIAAEACgAAAADAAAAEAAAADsABAAoAAAAKQAAAAMAAAAsAAUAEAAAACoAAAAiAAAAIQAAACwABQAQAAAAKwAAACEAAAAiAAAALAAFABAAAAAsAAAAIQAAACEAAAAsAAYAEgAAAC0AAAAqAAAAKwAAACwAAAA2AAUAAgAAAAQAAAAAAAAAAwAAAPgAAgAFAAAAOwAEABwAAAAdAAAABwAAADsABAAcAAAALwAAAAcAAAA9AAQADgAAABsAAAAaAAAAPgADAB0AAAAYAAAAQQAFAB4AAAAfAAAAHQAAABsAAAA9AAQAEAAAACAAAAAfAAAAUQAFAAYAAAAjAAAAIAAAAAAAAABRAAUABgAAACQAAAAgAAAAAQAAAFAABwAHAAAAJQAAACMAAAAkAAAAIQAAACIAAABBAAUAJgAAACcAAAANAAAADwAAAD4AAwAnAAAAJQAAAD0ABAAOAAAALgAAABoAAAA+AAMALwAAAC0AAABBAAUAHgAAADAAAAAvAAAALgAAAD0ABAAQAAAAMQAAADAAAAA+AAMAKQAAADEAAAD9AAEAOAABAA==";

        private const string FullscreenBarycentricFragmentSpirv =
            "AwIjBwAAAQALAAgAEgAAAAAAAAARAAIAAQAAAAsABgABAAAAR0xTTC5zdGQuNDUwAAAAAA4AAwAAAAAAAQAAAA8ABwAEAAAABAAAAG1haW4AAAAACQAAAAwAAAAQAAMABAAAAAcAAAADAAMAAgAAAMIBAAAFAAQABAAAAG1haW4AAAAABQAFAAkAAABvdXRDb2xvcgAAAAAFAAUADAAAAGJhcnljZW50cmljAEcABAAJAAAAHgAAAAAAAABHAAQADAAAAB4AAAAAAAAAEwACAAIAAAAhAAMAAwAAAAIAAAAWAAMABgAAACAAAAAXAAQABwAAAAYAAAAEAAAAIAAEAAgAAAADAAAABwAAADsABAAIAAAACQAAAAMAAAAXAAQACgAAAAYAAAACAAAAIAAEAAsAAAABAAAACgAAADsABAALAAAADAAAAAEAAAArAAQABgAAAA4AAAAAAAAANgAFAAIAAAAEAAAAAAAAAAMAAAD4AAIABQAAAD0ABAAKAAAADQAAAAwAAABRAAUABgAAAA8AAAANAAAAAAAAAFEABQAGAAAAEAAAAA0AAAABAAAAUAAHAAcAAAARAAAADwAAABAAAAAOAAAADgAAAD4AAwAJAAAAEQAAAP0AAQA4AAEA";

        private long _presentedSequence;
        private long _presentNotTakenLoggedSequence = long.MinValue;
        private bool _vulkanReady;
        private bool _firstFramePresented;
        private bool _firstGuestDrawPresented;
        private bool _splashPresented;
        private static int _guestImageDumpSequence;
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
        private readonly HashSet<ulong> _tracedGuestImageContents = new();
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







        private bool TryExecuteOrderedGuestAction(VulkanOrderedGuestAction work)
        {
            var visible = TryMakeActiveGuestQueueSubmissionsCpuVisible();
            if (!visible)
            {
                RenderPhaseProfile.RecordOrderedAction(work.DebugName, completed: false);
                return false;
            }

            WriteBackAllDirtyGuestBuffers(_activeGuestQueue.Name);
            work.Action();
            RenderPhaseProfile.RecordOrderedAction(work.DebugName, completed: true);
            if (_traceVulkanShaderEnabled)
            {
                TraceVulkanShader(
                    $"vk.ordered_action queue={_activeGuestQueue.Name} " +
                    $"submission={_activeGuestQueue.SubmissionId} " +
                    $"work_sequence={_activeGuestWorkSequence} name='{work.DebugName}'");
            }

            return true;
        }

        private void ExecuteGuestCacheOperation(VulkanGuestCacheOperation work)
        {
            CloseOpenTranslatedRenderPass();
            var commandBuffer = BeginBatchedGuestCommands();
            var plan = VulkanGuestCacheBarrierPlanner.Resolve(work.Operation);
            var barrier = new MemoryBarrier
            {
                SType = StructureType.MemoryBarrier,
                SrcAccessMask = AccessFlags.MemoryWriteBit,
                DstAccessMask = plan.DestinationAccess,
            };
            _vk.CmdPipelineBarrier(
                commandBuffer,
                PipelineStageFlags.AllCommandsBit,
                plan.DestinationStages,
                0,
                1,
                &barrier,
                0,
                null,
                0,
                null);
            work.ApplyHostState();
            if (_traceVulkanShaderEnabled)
            {
                TraceVulkanShader(
                    $"vk.guest_cache_operation queue={_activeGuestQueue.Name} " +
                    $"submission={_activeGuestQueue.SubmissionId} " +
                    $"work_sequence={_activeGuestWorkSequence} " +
                    $"domains={work.Operation.Domains} actions={work.Operation.Actions} " +
                    $"name='{work.DebugName}'");
            }
        }

        private void ExecuteGpuLabelSignal(VulkanGpuLabelSignal work)
        {
            FlushBatchedGuestCommands();
            _lastSubmittedGpuLabelDependencyByGuestQueue.TryGetValue(
                _activeGuestQueue.Name,
                out var dependency);
            _gpuLabelTimelineSignalCount++;
            if (_gpuLabelTimelineSignalCount <= 8 ||
                (_gpuLabelTimelineSignalCount & (_gpuLabelTimelineSignalCount - 1)) == 0)
            {
                Console.Error.WriteLine(
                    $"[LOADER][TRACE] vk.gpu_label_signal " +
                    $"count={_gpuLabelTimelineSignalCount} " +
                    $"queue={_activeGuestQueue.Name} " +
                    $"dependency=g{dependency.GraphicsTimeline}/" +
                    $"c{dependency.ComputeTimeline}");
            }
            work.PublishGpu(dependency);
            if (work.PublishHost is not null)
            {
                RegisterGpuLabelHostPublication(dependency, work.PublishHost);
            }
        }

        private void RegisterGpuLabelHostPublication(
            GuestGpuLabelDependency dependency,
            Action publish) =>
            _gpuLabelHostPublications.Register(dependency, publish);

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

        /// <summary>
        /// Returns a skipped draw's pooled data arrays: draws dropped before
        /// resource creation would otherwise strand their rented buffers.
        /// </summary>
        private static void ReturnPooledGuestData(VulkanTranslatedGuestDraw draw)
        {
            var returned = new HashSet<byte[]>(
                System.Collections.Generic.ReferenceEqualityComparer.Instance);
            foreach (var buffer in draw.GlobalMemoryBuffers)
            {
                if (buffer.Pooled && returned.Add(buffer.Data))
                {
                    GuestDataPool.Shared.Return(buffer.Data);
                }
            }

            foreach (var buffer in draw.VertexBuffers)
            {
                if (buffer.Pooled && returned.Add(buffer.Data))
                {
                    GuestDataPool.Shared.Return(buffer.Data);
                }
            }

            if (draw.IndexBuffer is { Pooled: true } indexBuffer &&
                returned.Add(indexBuffer.Data))
            {
                indexBuffer.TryReturnPooledData();
            }
        }

        private static void ReturnPooledGuestData(VulkanComputeGuestDispatch dispatch)
        {
            var returned = new HashSet<byte[]>(
                System.Collections.Generic.ReferenceEqualityComparer.Instance);
            foreach (var buffer in dispatch.GlobalMemoryBuffers)
            {
                if (buffer.Pooled && returned.Add(buffer.Data))
                {
                    GuestDataPool.Shared.Return(buffer.Data);
                }
            }
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

        private IReadOnlyList<GuestImageResource> GetTraceImages(
            TranslatedDrawResources resources,
            IReadOnlyList<GuestImageResource>? renderTargets = null,
            ulong shaderAddress = 0)
        {
            if (!_traceGuestImagesEnabled &&
                !_traceGuestImageAddressFilterEnabled &&
                GuestImageTraceInterval() is null)
            {
                return Array.Empty<GuestImageResource>();
            }

            var candidates = new HashSet<GuestImageResource>();
            foreach (var renderTarget in renderTargets ?? [])
            {
                candidates.Add(renderTarget);
            }

            foreach (var texture in resources.Textures)
            {
                if ((texture.IsStorage || _traceGuestImageAddressFilterEnabled) &&
                    texture.GuestImage is { } image)
                {
                    candidates.Add(image);
                }
            }

            return candidates
                .Where(image => ShouldTraceGuestImageContents(image, shaderAddress))
                .ToArray();
        }

        private void CreateGuestDrawResources()
        {
            var presentationFormat = PresentationTargetFormat;
            var colorAttachment = new AttachmentDescription
            {
                Format = presentationFormat,
                Samples = SampleCountFlags.Count1Bit,
                LoadOp = AttachmentLoadOp.Clear,
                StoreOp = AttachmentStoreOp.Store,
                StencilLoadOp = AttachmentLoadOp.DontCare,
                StencilStoreOp = AttachmentStoreOp.DontCare,
                InitialLayout = ImageLayout.Undefined,
                FinalLayout = PresentationTargetFinalLayout,
            };
            var colorReference = new AttachmentReference
            {
                Attachment = 0,
                Layout = ImageLayout.ColorAttachmentOptimal,
            };
            var subpass = new SubpassDescription
            {
                PipelineBindPoint = PipelineBindPoint.Graphics,
                ColorAttachmentCount = 1,
                PColorAttachments = &colorReference,
            };
            var dependency = new SubpassDependency
            {
                SrcSubpass = Vk.SubpassExternal,
                DstSubpass = 0,
                SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
                DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
                DstAccessMask = AccessFlags.ColorAttachmentWriteBit,
            };
            var renderPassInfo = new RenderPassCreateInfo
            {
                SType = StructureType.RenderPassCreateInfo,
                AttachmentCount = 1,
                PAttachments = &colorAttachment,
                SubpassCount = 1,
                PSubpasses = &subpass,
                DependencyCount = 1,
                PDependencies = &dependency,
            };
            Check(
                _vk.CreateRenderPass(
                    _device,
                    &renderPassInfo,
                    null,
                    out var swapchainRenderPass),
                "vkCreateRenderPass");
            if (swapchainRenderPass.Handle == 0)
            {
                throw new InvalidOperationException(
                    "vkCreateRenderPass returned a null swapchain render pass");
            }

            _renderPass = swapchainRenderPass;

            _swapchainImageViews = new ImageView[_swapchainImages.Length];
            _framebuffers = new Framebuffer[_swapchainImages.Length];
            if (_hdrOutputActive)
            {
                _presentationImages = new Image[_swapchainImages.Length];
                _presentationImageMemory = new DeviceMemory[_swapchainImages.Length];
                _presentationImageViews = new ImageView[_swapchainImages.Length];
                _presentationSampleViews = new ImageView[_swapchainImages.Length];
            }
            for (var index = 0; index < _swapchainImages.Length; index++)
            {
                var viewInfo = new ImageViewCreateInfo
                {
                    SType = StructureType.ImageViewCreateInfo,
                    Image = _swapchainImages[index],
                    ViewType = ImageViewType.Type2D,
                    Format = _swapchainFormat,
                    Components = new ComponentMapping(
                        ComponentSwizzle.Identity,
                        ComponentSwizzle.Identity,
                        ComponentSwizzle.Identity,
                        ComponentSwizzle.Identity),
                    SubresourceRange = ColorSubresourceRange(),
                };
                Check(
                    _vk.CreateImageView(_device, &viewInfo, null, out _swapchainImageViews[index]),
                    "vkCreateImageView");

                var imageView = _swapchainImageViews[index];
                if (_hdrOutputActive)
                {
                    CreatePresentationImage(index);
                    imageView = _presentationImageViews[index];
                }
                var framebufferInfo = new FramebufferCreateInfo
                {
                    SType = StructureType.FramebufferCreateInfo,
                    RenderPass = swapchainRenderPass,
                    AttachmentCount = 1,
                    PAttachments = &imageView,
                    Width = _extent.Width,
                    Height = _extent.Height,
                    Layers = 1,
                };
                Check(
                    _vk.CreateFramebuffer(_device, &framebufferInfo, null, out _framebuffers[index]),
                    "vkCreateFramebuffer");
            }

            var layoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
            };
            Check(
                _vk.CreatePipelineLayout(_device, &layoutInfo, null, out _pipelineLayout),
                "vkCreatePipelineLayout");
            CreateBarycentricPipeline();
            if (_hdrOutputActive)
            {
                CreateHdrPresentationResources();
            }
        }


        private void CreateBarycentricPipeline()
        {
            var vertexBytes = Convert.FromBase64String(FullscreenBarycentricVertexSpirv);
            var fragmentBytes = Convert.FromBase64String(FullscreenBarycentricFragmentSpirv);
            var vertexModule = CreateShaderModule(vertexBytes);
            var fragmentModule = CreateShaderModule(fragmentBytes);
            var entryPoint = (byte*)SilkMarshal.StringToPtr("main");
            try
            {
                var shaderStages = stackalloc PipelineShaderStageCreateInfo[2];
                shaderStages[0] = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.VertexBit,
                    Module = vertexModule,
                    PName = entryPoint,
                };
                shaderStages[1] = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.FragmentBit,
                    Module = fragmentModule,
                    PName = entryPoint,
                };

                var vertexInput = new PipelineVertexInputStateCreateInfo
                {
                    SType = StructureType.PipelineVertexInputStateCreateInfo,
                };
                var inputAssembly = new PipelineInputAssemblyStateCreateInfo
                {
                    SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                    Topology = PrimitiveTopology.TriangleList,
                };
                var viewport = new Viewport(0, 0, _extent.Width, _extent.Height, 0, 1);
                var scissor = new Rect2D(new Offset2D(0, 0), _extent);
                var viewportState = new PipelineViewportStateCreateInfo
                {
                    SType = StructureType.PipelineViewportStateCreateInfo,
                    ViewportCount = 1,
                    PViewports = &viewport,
                    ScissorCount = 1,
                    PScissors = &scissor,
                };
                var rasterization = new PipelineRasterizationStateCreateInfo
                {
                    SType = StructureType.PipelineRasterizationStateCreateInfo,
                    PolygonMode = PolygonMode.Fill,
                    CullMode = CullModeFlags.None,
                    FrontFace = FrontFace.CounterClockwise,
                    LineWidth = 1,
                };
                var multisample = new PipelineMultisampleStateCreateInfo
                {
                    SType = StructureType.PipelineMultisampleStateCreateInfo,
                    RasterizationSamples = SampleCountFlags.Count1Bit,
                };
                var colorBlendAttachment = new PipelineColorBlendAttachmentState
                {
                    ColorWriteMask =
                        ColorComponentFlags.RBit |
                        ColorComponentFlags.GBit |
                        ColorComponentFlags.BBit |
                        ColorComponentFlags.ABit,
                };
                var colorBlend = new PipelineColorBlendStateCreateInfo
                {
                    SType = StructureType.PipelineColorBlendStateCreateInfo,
                    AttachmentCount = 1,
                    PAttachments = &colorBlendAttachment,
                };
                var pipelineInfo = new GraphicsPipelineCreateInfo
                {
                    SType = StructureType.GraphicsPipelineCreateInfo,
                    StageCount = 2,
                    PStages = shaderStages,
                    PVertexInputState = &vertexInput,
                    PInputAssemblyState = &inputAssembly,
                    PViewportState = &viewportState,
                    PRasterizationState = &rasterization,
                    PMultisampleState = &multisample,
                    PColorBlendState = &colorBlend,
                    Layout = _pipelineLayout,
                    RenderPass = _renderPass,
                    Subpass = 0,
                };
                Check(
                    _vk.CreateGraphicsPipelines(
                        _device,
                        _pipelineCache,
                        1,
                        &pipelineInfo,
                        null,
                        out _barycentricPipeline),
                    "vkCreateGraphicsPipelines");
                MarkPipelineCacheDirty();
            }
            finally
            {
                SilkMarshal.Free((nint)entryPoint);
                _vk.DestroyShaderModule(_device, fragmentModule, null);
                _vk.DestroyShaderModule(_device, vertexModule, null);
            }
        }


        /// <summary>
        /// Creates a draw-local sampled image containing the render target's
        /// value immediately before the draw. RDNA permits a pixel shader to
        /// read an attachment while producing its replacement value, whereas
        /// core Vulkan does not permit the same subresource to be both a
        /// sampled image and a color attachment. Keeping the two images
        /// distinct preserves the guest's read-before-write behavior without
        /// a queue idle or an undefined Vulkan feedback loop.
        /// </summary>
        private TextureResource CreateRenderTargetFeedbackSnapshot(
            GuestDrawTexture texture,
            GuestImageResource source)
        {
            var viewFormat = GetTextureFormat(texture.Format, texture.NumberType);
            if (!IsCompatibleViewFormat(source.Format, viewFormat))
            {
                throw new InvalidOperationException(
                    $"Feedback view format {viewFormat} is incompatible with " +
                    $"render-target format {source.Format}.");
            }

            var key = new VulkanFeedbackSnapshotKey(
                VulkanFeedbackSnapshotKind.Color,
                source.Format,
                viewFormat,
                source.Width,
                source.Height,
                source.MipLevels,
                texture.DstSelect);
            if (TryRentFeedbackSnapshot(key, out var pooled))
            {
                RecordFeedbackSnapshotAcquired(
                    VulkanFeedbackSnapshotKind.Color,
                    pooled.AllocationBytes,
                    allocated: false);
                return new TextureResource
                {
                    Address = texture.Address,
                    Image = pooled.Image,
                    ImageMemory = pooled.Memory,
                    View = pooled.View,
                    Width = source.Width,
                    Height = source.Height,
                    RowLength = source.Width,
                    DstSelect = texture.DstSelect,
                    OwnsStorage = true,
                    FeedbackSnapshotKey = key,
                    FeedbackAllocationBytes = pooled.AllocationBytes,
                    SamplerState = texture.Sampler,
                    FeedbackSource = source,
                };
            }

            var image = default(Image);
            var memory = default(DeviceMemory);
            var view = default(ImageView);
            try
            {
                var imageInfo = new ImageCreateInfo
                {
                    SType = StructureType.ImageCreateInfo,
                    Flags =
                        ImageCreateFlags.CreateMutableFormatBit |
                        ImageCreateFlags.CreateExtendedUsageBit,
                    ImageType = ImageType.Type2D,
                    Format = source.Format,
                    Extent = new Extent3D(source.Width, source.Height, 1),
                    MipLevels = source.MipLevels,
                    ArrayLayers = 1,
                    Samples = SampleCountFlags.Count1Bit,
                    Tiling = ImageTiling.Optimal,
                    Usage =
                        ImageUsageFlags.TransferDstBit |
                        ImageUsageFlags.SampledBit,
                    SharingMode = SharingMode.Exclusive,
                    InitialLayout = ImageLayout.Undefined,
                };
                Check(
                    _vk.CreateImage(_device, &imageInfo, null, out image),
                    "vkCreateImage(render-target feedback snapshot)");
                _vk.GetImageMemoryRequirements(_device, image, out var requirements);
                var memoryInfo = new MemoryAllocateInfo
                {
                    SType = StructureType.MemoryAllocateInfo,
                    AllocationSize = requirements.Size,
                    MemoryTypeIndex = FindMemoryType(
                        requirements.MemoryTypeBits,
                        MemoryPropertyFlags.DeviceLocalBit),
                };
                Check(
                    _vk.AllocateMemory(_device, &memoryInfo, null, out memory),
                    "vkAllocateMemory(render-target feedback snapshot)");
                Check(
                    _vk.BindImageMemory(_device, image, memory, 0),
                    "vkBindImageMemory(render-target feedback snapshot)");

                var viewInfo = new ImageViewCreateInfo
                {
                    SType = StructureType.ImageViewCreateInfo,
                    Image = image,
                    ViewType = ImageViewType.Type2D,
                    Format = viewFormat,
                    Components = ToVkComponentMapping(texture.DstSelect),
                    SubresourceRange = ColorSubresourceRange(0, source.MipLevels),
                };
                Check(
                    _vk.CreateImageView(_device, &viewInfo, null, out view),
                    "vkCreateImageView(render-target feedback snapshot)");

                var debugName =
                    $"SharpEmu feedback 0x{source.Address:X16} " +
                    $"{source.Width}x{source.Height} {source.Format}->{viewFormat}";
                SetDebugName(ObjectType.Image, image.Handle, $"{debugName} image");
                SetDebugName(ObjectType.ImageView, view.Handle, $"{debugName} view");
                TraceVulkanShader(
                    $"vk.feedback_snapshot_create addr=0x{source.Address:X16} " +
                    $"size={source.Width}x{source.Height} mips={source.MipLevels} " +
                    $"image_format={source.Format} view_format={viewFormat} " +
                    $"dst=0x{texture.DstSelect:X3}");
                RecordFeedbackSnapshotAcquired(
                    VulkanFeedbackSnapshotKind.Color,
                    requirements.Size,
                    allocated: true);

                return new TextureResource
                {
                    Address = texture.Address,
                    Image = image,
                    ImageMemory = memory,
                    View = view,
                    Width = source.Width,
                    Height = source.Height,
                    RowLength = source.Width,
                    DstSelect = texture.DstSelect,
                    OwnsStorage = true,
                    FeedbackSnapshotKey = key,
                    FeedbackAllocationBytes = requirements.Size,
                    SamplerState = texture.Sampler,
                    FeedbackSource = source,
                };
            }
            catch
            {
                if (view.Handle != 0)
                {
                    _vk.DestroyImageView(_device, view, null);
                }

                if (image.Handle != 0)
                {
                    _vk.DestroyImage(_device, image, null);
                }

                if (memory.Handle != 0)
                {
                    _vk.FreeMemory(_device, memory, null);
                }

                throw;
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
        private unsafe (Image Image, DeviceMemory Memory) CreateTransferScratchImage(
            Format format,
            uint width,
            uint height)
        {
            var imageInfo = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                ImageType = ImageType.Type2D,
                Format = format,
                Extent = new Extent3D(width, height, 1),
                MipLevels = 1,
                ArrayLayers = 1,
                Samples = SampleCountFlags.Count1Bit,
                Tiling = ImageTiling.Optimal,
                Usage = ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit,
                SharingMode = SharingMode.Exclusive,
                InitialLayout = ImageLayout.Undefined,
            };
            Check(_vk.CreateImage(_device, &imageInfo, null, out var image), "vkCreateImage(format-convert scratch)");
            _vk.GetImageMemoryRequirements(_device, image, out var requirements);
            var allocationInfo = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = requirements.Size,
                MemoryTypeIndex = FindMemoryType(
                    requirements.MemoryTypeBits,
                    MemoryPropertyFlags.DeviceLocalBit),
            };
            Check(
                _vk.AllocateMemory(_device, &allocationInfo, null, out var memory),
                "vkAllocateMemory(format-convert scratch)");
            Check(_vk.BindImageMemory(_device, image, memory, 0), "vkBindImageMemory(format-convert scratch)");
            return (image, memory);
        }

        private unsafe void ConvertGuestImageBytesInPlace(
            GuestImageResource resource,
            Format fromFormat,
            Format toFormat)
        {
            var (oldTyped, oldMemory) = CreateTransferScratchImage(fromFormat, resource.Width, resource.Height);
            var (newTyped, newMemory) = CreateTransferScratchImage(toFormat, resource.Width, resource.Height);
            try
            {
                var commandBuffer = AllocateGuestCommandBuffer();
                var beginInfo = new CommandBufferBeginInfo
                {
                    SType = StructureType.CommandBufferBeginInfo,
                    Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
                };
                Check(
                    _vk.BeginCommandBuffer(commandBuffer, &beginInfo),
                    "vkBeginCommandBuffer(format-convert)");

                var toTransferSrc = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask = AccessFlags.MemoryWriteBit,
                    DstAccessMask = AccessFlags.TransferReadBit,
                    OldLayout = ImageLayout.General,
                    NewLayout = ImageLayout.TransferSrcOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = resource.Image,
                    SubresourceRange = ColorSubresourceRange(0, 1),
                };
                _vk.CmdPipelineBarrier(
                    commandBuffer, PipelineStageFlags.AllCommandsBit, PipelineStageFlags.TransferBit,
                    0, 0, null, 0, null, 1, &toTransferSrc);

                var oldTypedToDst = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask = 0,
                    DstAccessMask = AccessFlags.TransferWriteBit,
                    OldLayout = ImageLayout.Undefined,
                    NewLayout = ImageLayout.TransferDstOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = oldTyped,
                    SubresourceRange = ColorSubresourceRange(0, 1),
                };
                _vk.CmdPipelineBarrier(
                    commandBuffer, PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.TransferBit,
                    0, 0, null, 0, null, 1, &oldTypedToDst);

                var copyRegion = new ImageCopy
                {
                    SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                    SrcOffset = new Offset3D(0, 0, 0),
                    DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                    DstOffset = new Offset3D(0, 0, 0),
                    Extent = new Extent3D(resource.Width, resource.Height, 1),
                };
                _vk.CmdCopyImage(
                    commandBuffer,
                    resource.Image, ImageLayout.TransferSrcOptimal,
                    oldTyped, ImageLayout.TransferDstOptimal,
                    1, &copyRegion);

                var oldTypedToSrc = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask = AccessFlags.TransferWriteBit,
                    DstAccessMask = AccessFlags.TransferReadBit,
                    OldLayout = ImageLayout.TransferDstOptimal,
                    NewLayout = ImageLayout.TransferSrcOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = oldTyped,
                    SubresourceRange = ColorSubresourceRange(0, 1),
                };
                _vk.CmdPipelineBarrier(
                    commandBuffer, PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit,
                    0, 0, null, 0, null, 1, &oldTypedToSrc);

                var newTypedToDst = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask = 0,
                    DstAccessMask = AccessFlags.TransferWriteBit,
                    OldLayout = ImageLayout.Undefined,
                    NewLayout = ImageLayout.TransferDstOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = newTyped,
                    SubresourceRange = ColorSubresourceRange(0, 1),
                };
                _vk.CmdPipelineBarrier(
                    commandBuffer, PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.TransferBit,
                    0, 0, null, 0, null, 1, &newTypedToDst);

                var blitRegion = new ImageBlit
                {
                    SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                    SrcOffsets = new ImageBlit.SrcOffsetsBuffer
                    {
                        Element0 = new Offset3D(0, 0, 0),
                        Element1 = new Offset3D(checked((int)resource.Width), checked((int)resource.Height), 1),
                    },
                    DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                    DstOffsets = new ImageBlit.DstOffsetsBuffer
                    {
                        Element0 = new Offset3D(0, 0, 0),
                        Element1 = new Offset3D(checked((int)resource.Width), checked((int)resource.Height), 1),
                    },
                };
                _vk.CmdBlitImage(
                    commandBuffer,
                    oldTyped, ImageLayout.TransferSrcOptimal,
                    newTyped, ImageLayout.TransferDstOptimal,
                    1, &blitRegion, Filter.Nearest);

                var newTypedToSrc = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask = AccessFlags.TransferWriteBit,
                    DstAccessMask = AccessFlags.TransferReadBit,
                    OldLayout = ImageLayout.TransferDstOptimal,
                    NewLayout = ImageLayout.TransferSrcOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = newTyped,
                    SubresourceRange = ColorSubresourceRange(0, 1),
                };
                _vk.CmdPipelineBarrier(
                    commandBuffer, PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit,
                    0, 0, null, 0, null, 1, &newTypedToSrc);

                var resourceToDst = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask = AccessFlags.TransferReadBit,
                    DstAccessMask = AccessFlags.TransferWriteBit,
                    OldLayout = ImageLayout.TransferSrcOptimal,
                    NewLayout = ImageLayout.TransferDstOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = resource.Image,
                    SubresourceRange = ColorSubresourceRange(0, 1),
                };
                _vk.CmdPipelineBarrier(
                    commandBuffer, PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit,
                    0, 0, null, 0, null, 1, &resourceToDst);

                _vk.CmdCopyImage(
                    commandBuffer,
                    newTyped, ImageLayout.TransferSrcOptimal,
                    resource.Image, ImageLayout.TransferDstOptimal,
                    1, &copyRegion);

                var resourceToGeneral = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask = AccessFlags.TransferWriteBit,
                    DstAccessMask = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit,
                    OldLayout = ImageLayout.TransferDstOptimal,
                    NewLayout = ImageLayout.General,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = resource.Image,
                    SubresourceRange = ColorSubresourceRange(0, 1),
                };
                _vk.CmdPipelineBarrier(
                    commandBuffer, PipelineStageFlags.TransferBit, PipelineStageFlags.AllCommandsBit,
                    0, 0, null, 0, null, 1, &resourceToGeneral);

                Check(_vk.EndCommandBuffer(commandBuffer), "vkEndCommandBuffer(format-convert)");
                SubmitGuestCommandBuffer(commandBuffer, [], []);

                if (_traceGuestImageEvents)
                {
                    Console.Error.WriteLine(
                        "[FORMAT-CONVERT] " +
                        $"source_format={fromFormat} target_format={toFormat} " +
                        $"address=0x{resource.Address:X16} " +
                        "reason=bit-incompatible-view-reinterpret " +
                        $"size={resource.Width}x{resource.Height}");
                }
            }
            finally
            {
                _vk.DestroyImage(_device, oldTyped, null);
                _vk.FreeMemory(_device, oldMemory, null);
                _vk.DestroyImage(_device, newTyped, null);
                _vk.FreeMemory(_device, newMemory, null);
            }
        }

        private static readonly bool _realFormatConversionEnabled = string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_ENABLE_REAL_FORMAT_CONVERSION"),
            "1",
            StringComparison.Ordinal);








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

        private void TraceGuestImageContents(GuestImageResource image)
        {
            var bytesPerPixel = GetReadbackBytesPerPixel(image.Format);
            if (bytesPerPixel == 0)
            {
                Console.Error.WriteLine(
                    "[LOADER][TRACE] " +
                    $"vk.guest_image addr=0x{image.Address:X16} " +
                    $"format={image.Format} readback=unsupported");
                return;
            }

            var byteCount = checked((ulong)image.Width * image.Height * bytesPerPixel);
            var buffer = CreateBuffer(
                byteCount,
                BufferUsageFlags.TransferDstBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                out var memory);
            try
            {
                Check(
                    _vk.ResetCommandBuffer(_commandBuffer, 0),
                    "vkResetCommandBuffer(guest readback)");
                var beginInfo = new CommandBufferBeginInfo
                {
                    SType = StructureType.CommandBufferBeginInfo,
                    Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
                };
                Check(
                    _vk.BeginCommandBuffer(_commandBuffer, &beginInfo),
                    "vkBeginCommandBuffer(guest readback)");

                var toTransfer = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask = AccessFlags.ShaderReadBit,
                    DstAccessMask = AccessFlags.TransferReadBit,
                    OldLayout = ImageLayout.ShaderReadOnlyOptimal,
                    NewLayout = ImageLayout.TransferSrcOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = image.Image,
                    SubresourceRange = ColorSubresourceRange(),
                };
                _vk.CmdPipelineBarrier(
                    _commandBuffer,
                    PipelineStageFlags.FragmentShaderBit |
                    PipelineStageFlags.ComputeShaderBit,
                    PipelineStageFlags.TransferBit,
                    0,
                    0,
                    null,
                    0,
                    null,
                    1,
                    &toTransfer);

                var region = new BufferImageCopy
                {
                    ImageSubresource = new ImageSubresourceLayers
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        LayerCount = 1,
                    },
                    ImageExtent = new Extent3D(image.Width, image.Height, 1),
                };
                _vk.CmdCopyImageToBuffer(
                    _commandBuffer,
                    image.Image,
                    ImageLayout.TransferSrcOptimal,
                    buffer,
                    1,
                    &region);

                var toShaderRead = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask = AccessFlags.TransferReadBit,
                    DstAccessMask = AccessFlags.ShaderReadBit,
                    OldLayout = ImageLayout.TransferSrcOptimal,
                    NewLayout = ImageLayout.ShaderReadOnlyOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = image.Image,
                    SubresourceRange = ColorSubresourceRange(),
                };
                _vk.CmdPipelineBarrier(
                    _commandBuffer,
                    PipelineStageFlags.TransferBit,
                    PipelineStageFlags.FragmentShaderBit |
                    PipelineStageFlags.ComputeShaderBit,
                    0,
                    0,
                    null,
                    0,
                    null,
                    1,
                    &toShaderRead);

                Check(
                    _vk.EndCommandBuffer(_commandBuffer),
                    "vkEndCommandBuffer(guest readback)");
                var commandBuffer = _commandBuffer;
                var submitInfo = new SubmitInfo
                {
                    SType = StructureType.SubmitInfo,
                    CommandBufferCount = 1,
                    PCommandBuffers = &commandBuffer,
                };
                Check(
                    _vk.QueueSubmit(_queue, 1, &submitInfo, default),
                    "vkQueueSubmit(guest readback)");
                Check(
                    _vk.QueueWaitIdle(_queue),
                    "vkQueueWaitIdle(guest readback)");

                void* mapped;
                Check(
                    _vk.MapMemory(_device, memory, 0, byteCount, 0, &mapped),
                    "vkMapMemory(guest readback)");
                try
                {
                    var bytes = new ReadOnlySpan<byte>(mapped, checked((int)byteCount));
                    if (GuestImageTraceInterval() is not null && bytesPerPixel == 4)
                    {
                        long r = 0, g = 0, b = 0, a = 0, samples = 0;
                        for (var offset = 0; offset + 4 <= bytes.Length; offset += 4 * 251)
                        {
                            r += bytes[offset];
                            g += bytes[offset + 1];
                            b += bytes[offset + 2];
                            a += bytes[offset + 3];
                            samples++;
                        }

                        if (samples > 0)
                        {
                            Console.Error.WriteLine(
                                $"[RB] addr=0x{image.Address:X} " +
                                $"mean={r / samples},{g / samples},{b / samples},A{a / samples} " +
                                $"sample_unique={CountSampledUniquePixels(bytes, bytesPerPixel)}");
                        }

                        if (++_intervalReadbackCount % 25 == 0)
                        {
                            DumpGuestImageBytes(image, bytes);
                        }

                        return;
                    }

                    var nonzeroBytes = 0L;
                    ulong hash = 14695981039346656037UL;
                    foreach (var value in bytes)
                    {
                        nonzeroBytes += value == 0 ? 0 : 1;
                        hash = (hash ^ value) * 1099511628211UL;
                    }

                    var nonblackPixels = CountNonblackPixels(
                        bytes,
                        image.Format,
                        bytesPerPixel);
                    var centerOffset = checked(
                        ((int)(image.Height / 2) * (int)image.Width +
                         (int)(image.Width / 2)) *
                        (int)bytesPerPixel);
                    var center = Convert.ToHexString(
                        bytes.Slice(centerOffset, (int)bytesPerPixel));
                    Console.Error.WriteLine(
                        "[LOADER][TRACE] " +
                        $"vk.guest_image addr=0x{image.Address:X16} " +
                        $"size={image.Width}x{image.Height} format={image.Format} " +
                        $"nonzero_bytes={nonzeroBytes}/{byteCount} " +
                        $"nonblack_pixels={nonblackPixels}/{(ulong)image.Width * image.Height} " +
                        $"center={center} sample_unique={CountSampledUniquePixels(bytes, bytesPerPixel)} " +
                        $"hash=0x{hash:X16}");
                    DumpGuestImageBytes(image, bytes);
                }
                finally
                {
                    _vk.UnmapMemory(_device, memory);
                }
            }
            finally
            {
                _vk.DestroyBuffer(_device, buffer, null);
                _vk.FreeMemory(_device, memory, null);
            }
        }

        private static int CountSampledUniquePixels(
            ReadOnlySpan<byte> bytes,
            uint bytesPerPixel)
        {
            if (bytesPerPixel == 0)
            {
                return 0;
            }

            var unique = new HashSet<ulong>();
            var stride = checked((int)bytesPerPixel * 251);
            for (var offset = 0;
                 offset + bytesPerPixel <= bytes.Length;
                 offset += stride)
            {
                ulong hash = 14695981039346656037UL;
                for (var index = 0; index < bytesPerPixel; index++)
                {
                    hash = (hash ^ bytes[offset + index]) * 1099511628211UL;
                }

                unique.Add(hash);
                if (unique.Count > 256)
                {
                    return 257;
                }
            }

            return unique.Count;
        }

        private static void DumpGuestImageBytes(
            GuestImageResource image,
            ReadOnlySpan<byte> bytes)
        {
            var directory =
                Environment.GetEnvironmentVariable("SHARPEMU_GUEST_IMAGE_DUMP_DIR");
            if (string.IsNullOrWhiteSpace(directory))
            {
                return;
            }

            Directory.CreateDirectory(directory);
            var sequence = Interlocked.Increment(ref _guestImageDumpSequence);
            var path = Path.Combine(
                directory,
                $"{sequence:D4}-0x{image.Address:X16}-{image.Width}x{image.Height}-{image.Format}.rgba");
            File.WriteAllBytes(path, bytes.ToArray());
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

        private static uint GetReadbackBytesPerPixel(Format format) =>
            format switch
            {
                Format.R8Unorm or
                Format.R8Uint or
                Format.R8Sint => 1,
                Format.R8G8Unorm or
                Format.R8G8Uint or
                Format.R8G8Sint => 2,
                Format.R32Uint or
                Format.R32Sint or
                Format.R32Sfloat or
                Format.B10G11R11UfloatPack32 or
                Format.R16G16Uint or
                Format.R16G16Sint or
                Format.R16G16Sfloat or
                Format.R8G8B8A8Uint or
                Format.R8G8B8A8Sint or
                Format.R8G8B8A8Unorm or
                Format.R8G8B8A8Srgb or
                Format.B8G8R8A8Unorm or
                Format.B8G8R8A8Srgb or
                Format.A2R10G10B10UnormPack32 or
                Format.A2B10G10R10UnormPack32 => 4,
                Format.R16G16B16A16Uint or
                Format.R16G16B16A16Sint or
                Format.R16G16B16A16Sfloat => 8,
                Format.R32G32Uint or
                Format.R32G32Sint or
                Format.R32G32Sfloat => 8,
                Format.R32G32B32A32Uint or
                Format.R32G32B32A32Sint or
                Format.R32G32B32A32Sfloat => 16,
                _ => 0,
            };

        private static long CountNonblackPixels(
            ReadOnlySpan<byte> bytes,
            Format format,
            uint bytesPerPixel)
        {
            var count = 0L;
            for (var offset = 0; offset < bytes.Length; offset += (int)bytesPerPixel)
            {
                var pixel = bytes.Slice(offset, (int)bytesPerPixel);
                var hasColor = format switch
                {
                    Format.A2R10G10B10UnormPack32 or
                    Format.A2B10G10R10UnormPack32 =>
                        (BitConverter.ToUInt32(pixel) & 0x3FFFFFFFu) != 0,
                    Format.R8G8B8A8Uint or
                    Format.R8G8B8A8Sint or
                    Format.R8G8B8A8Unorm =>
                        pixel[0] != 0 || pixel[1] != 0 || pixel[2] != 0,
                    Format.R16G16B16A16Uint or
                    Format.R16G16B16A16Sint or
                    Format.R16G16B16A16Sfloat =>
                        pixel[..6].IndexOfAnyExcept((byte)0) >= 0,
                    _ => pixel.IndexOfAnyExcept((byte)0) >= 0,
                };
                count += hasColor ? 1 : 0;
            }

            return count;
        }





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


        private string ResolveGuestSubmitContext(
            IReadOnlyList<TranslatedDrawResources> resources)
        {
            var workLabel = !string.IsNullOrEmpty(_activeGuestWorkLabel)
                ? _activeGuestWorkLabel
                : _lastGuestWorkLabel;
            var resourceName = resources.Count > 0
                ? resources[0].DebugName
                : _batchResources.Count > 0
                    ? _batchResources[0].DebugName
                    : string.Empty;
            if (string.IsNullOrEmpty(workLabel))
            {
                return string.IsNullOrEmpty(resourceName)
                    ? string.Empty
                    : $"batch={resourceName}";
            }

            return string.IsNullOrEmpty(resourceName)
                ? workLabel
                : $"{workLabel} batch={resourceName}";
        }

        private static string DescribeGuestWork(
            object work,
            VulkanGuestQueueIdentity queue,
            long sequence)
        {
            var queuePart =
                $"queue={queue.Name} submission={queue.SubmissionId} sequence={sequence}";
            return work switch
            {
                VulkanComputeGuestDispatch compute =>
                    $"compute cs=0x{compute.ShaderAddress:X16} " +
                    $"groups={compute.GroupCountX}x{compute.GroupCountY}x{compute.GroupCountZ} " +
                    $"textures={compute.Textures.Count} " +
                    $"globals={compute.GlobalMemoryBuffers.Count} " +
                    $"writes_global={(compute.WritesGlobalMemory ? 1 : 0)} " +
                    $"indirect={(compute.IsIndirect ? 1 : 0)} " +
                    $"spirv={compute.ComputeSpirv.Length} {queuePart}",
                VulkanOffscreenGuestDraw draw =>
                    $"offscreen vs=0x{draw.ShaderAddress:X16} " +
                    $"mrt={draw.Targets.Count} " +
                    $"textures={draw.Draw.Textures.Count} " +
                    $"vertices={draw.Draw.VertexCount} {queuePart}",
                VulkanOffscreenColorClear clear =>
                    $"offscreen_clear ps=0x{clear.ShaderAddress:X16} " +
                    $"mrt={clear.Targets.Count} " +
                    $"rgba=({clear.Red:0.###},{clear.Green:0.###},{clear.Blue:0.###},{clear.Alpha:0.###}) " +
                    queuePart,
                VulkanGuestImageWrite imageWrite =>
                    $"image_write addr=0x{imageWrite.Address:X16} {queuePart}",
                VulkanOrderedGuestAction action =>
                    $"ordered_action name={action.DebugName} {queuePart}",
                VulkanGuestCacheOperation operation =>
                    $"cache_operation name={operation.DebugName} " +
                    $"domains={operation.Operation.Domains} " +
                    $"actions={operation.Operation.Actions} {queuePart}",
                VulkanOrderedGuestFlip flip =>
                    $"ordered_flip version={flip.Version} " +
                    $"buf={flip.DisplayBufferIndex} addr=0x{flip.Address:X16} {queuePart}",
                VulkanOrderedGuestFlipWait wait =>
                    $"flip_wait version={wait.Version} " +
                    $"buf={wait.DisplayBufferIndex} {queuePart}",
                _ => $"{work.GetType().Name} {queuePart}",
            };
        }

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
