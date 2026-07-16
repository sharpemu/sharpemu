// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Maths;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Bink;
using Silk.NET.Input;
using SharpEmu.Libs.Gpu;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Vulkan;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Windowing;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

namespace SharpEmu.Libs.VideoOut;

internal readonly record struct VulkanRenderTargetFormat(
    Format Format,
    Gen5PixelOutputKind OutputKind)
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
    GuestRenderState RenderState);

internal sealed record VulkanOffscreenGuestDraw(
    VulkanTranslatedGuestDraw Draw,
    IReadOnlyList<GuestRenderTarget> Targets,
    GuestDepthTarget? DepthTarget,
    bool PublishTarget,
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

internal sealed record VulkanOrderedGuestFlip(
    long Version,
    int VideoOutHandle,
    int DisplayBufferIndex,
    ulong Address,
    uint Width,
    uint Height,
    uint PitchInPixel);

internal sealed record VulkanOrderedGuestFlipWait(
    long Version,
    int VideoOutHandle,
    int DisplayBufferIndex);

internal readonly record struct VulkanGuestQueueIdentity(
    string Name,
    ulong SubmissionId)
{
    public static VulkanGuestQueueIdentity Default { get; } = new("host.default", 0);
}

internal static unsafe class VulkanVideoPresenter
{
    private const uint DefaultWindowWidth = 1280;
    private const uint DefaultWindowHeight = 720;
    // Vulkan's portable upper bound for minStorageBufferOffsetAlignment is
    // 256 bytes. Using that fixed power of two (instead of racing the render
    // thread's physical-device query) gives shader translation and descriptor
    // creation one stable aliasing contract on every conformant device.
    internal const ulong GuestStorageBufferOffsetAlignment = 256;
    // Guest draw snapshots churn through a small set of 128 KiB-16 MiB size
    // classes thousands of times per second. The process-wide shared pool
    // trims and repartitions those large arrays aggressively under GC load,
    // causing hundreds of MiB/s of replacement byte[] allocations. Keep a
    // bounded, non-shared pool for AGC-to-presenter ownership transfers.
    internal static System.Buffers.ArrayPool<byte> GuestDataPool { get; } =
        System.Buffers.ArrayPool<byte>.Create(
            maxArrayLength: 16 * 1024 * 1024,
            maxArraysPerBucket: 96);
    // The pending queue and per-render drain budget bound how much guest GPU
    // work can be buffered ahead of the presenter. Draws are batched into
    // shared command buffers, so draining a large batch per render tick is
    // cheap; small caps here throttle games that issue more than a handful
    // of draws per frame to a fraction of the display rate. The pending cap
    // stays tighter than the drain budget because queued draws pin their
    // pooled guest-data arrays until the render thread uploads them.
    private const int MaxPendingGuestWork = 64;
    private const ulong MaximumCachedHostBufferBytes = 128UL * 1024 * 1024;
    // A captured 4K flip can consume tens of MiB of device-local memory.
    // Retain only a short presentation queue while always preserving the
    // newest generation; older immutable versions are retired immediately.
    private const int MaxPendingGuestFlipVersions = 4;
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
    private const int MaxGuestWorkPerRender = 256;
    // On macOS the whole window loop — including Render() and its guest-work
    // drain — runs on the process main thread, so draining a large backlog of
    // slow guest work (heavy compute) blocks the Cocoa event pump and marks the
    // window "Not Responding" while starving the swapchain present. Cap the
    // wall-clock time spent draining per Render() call; leftover work stays
    // queued for the next frame. SHARPEMU_RENDER_WORK_BUDGET_MS overrides
    // (0 disables the cap); default 12ms keeps the window interactive at ~60Hz.
    private static readonly long _renderWorkBudgetTicks =
        (long.TryParse(
             Environment.GetEnvironmentVariable("SHARPEMU_RENDER_WORK_BUDGET_MS"),
             out var renderBudgetMs) && renderBudgetMs >= 0
            ? renderBudgetMs
            : 12L) * System.Diagnostics.Stopwatch.Frequency / 1000L;
    // Max time the main-thread Render() will block waiting for a frame slot's
    // GPU fence before skipping the frame and returning to the event pump.
    // Prevents the window freezing behind a slow-compute GPU backlog.
    // SHARPEMU_FRAME_WAIT_BUDGET_MS overrides; default 8ms.
    private static readonly ulong _frameSlotWaitBudgetNs =
        (ulong.TryParse(
             Environment.GetEnvironmentVariable("SHARPEMU_FRAME_WAIT_BUDGET_MS"),
             out var frameWaitMs) && frameWaitMs > 0
            ? frameWaitMs
            : 8UL) * 1_000_000UL;
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
    // Diagnostic: skip every compute dispatch (mistranslated compute shaders
    // run long / GPU-hang and starve the present). Isolates whether the
    // geometry+composite path renders on its own.
    private static readonly bool _skipAllCompute =
        Environment.GetEnvironmentVariable("SHARPEMU_SKIP_ALL_COMPUTE") == "1";
    // Diagnostic: skip compute dispatches whose GroupCountZ is at least this,
    // to isolate a specific tall dispatch (e.g. Demon's Souls' 27x15x72 froxel
    // shader that hangs the Metal queue) without needing its ASLR-varying
    // address. 0 disables.
    private static readonly uint _skipTallComputeZ =
        uint.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_SKIP_TALL_COMPUTE_Z"), out var z)
            ? z
            : 0;
    private const uint GuestPrimitiveRectList = 0x11;

    private static readonly object _gate = new();
    private readonly record struct PendingGuestWork(
        object Work,
        ulong PayloadBytes,
        long Sequence,
        long RequiredSequence,
        long EnqueuedTicks,
        VulkanGuestQueueIdentity Queue);

    // PS5 exposes independent graphics and asynchronous-compute queues. A
    // single host FIFO adds dependencies that do not exist in the guest: one
    // slow ACB dispatch can delay a graphics clear until the CPU has reused
    // that heap. Keep FIFO order within each logical guest queue and schedule
    // ready queues round-robin. Explicit WAIT_REG_MEM packets remain the only
    // mechanism that orders one logical queue behind another.
    private static readonly Dictionary<string, LinkedList<PendingGuestWork>>
        _pendingGuestWorkByQueue = new(StringComparer.Ordinal);
    private static readonly List<string> _pendingGuestQueueSchedule = [];
    private static int _pendingGuestQueueCursor;
    private static int _pendingGuestWorkCount;
    private static ulong _pendingGuestWorkBytes;
    // A flip names an image that was rendered earlier in the command stream.
    // Keep a small FIFO of those flips instead of replacing an incomplete one
    // with the next frame: the guest can enqueue the next frame before the
    // render thread reaches the previous image, which otherwise starves
    // presentation indefinitely.
    private static readonly Queue<Presentation> _pendingGuestImagePresentations = new();
    private static readonly Dictionary<ulong, long> _guestImageWorkSequences = new();
    private static readonly Dictionary<ulong, uint> _availableGuestImages = new();
    private static readonly Dictionary<(int Handle, int BufferIndex), long>
        _lastOrderedGuestFlipVersions = new();
    private static long _orderedGuestFlipVersionSequence;
    // Storage-image initialization is copied only by the first queued writer.
    // Later dispatches targeting the same image must not each retain another
    // multi-megabyte guest-memory snapshot while waiting for that first writer
    // to reach the presenter.  Reference counts let failed/completed work
    // retire its reservation without leaving a permanent false cache hit.
    private readonly record struct PendingGuestImageUpload(int Count, long OwnerSequence);
    private static readonly Dictionary<(ulong Address, uint Format), PendingGuestImageUpload>
        _pendingGuestImageUploads = new();
    private static readonly Dictionary<ulong, byte[]> _pendingGuestImageInitialData = new();
    private static readonly Dictionary<ulong, (uint Width, uint Height, ulong ByteCount)>
        _guestImageExtents = new();
    private static readonly bool _traceGuestImageEvents =
        string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_TRACE_DRAWS"),
            "1",
            StringComparison.Ordinal) ||
        string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_TRACE_GUEST_IMAGE_EVENTS"),
            "1",
            StringComparison.Ordinal);
    private static readonly bool _traceGuestWorkCompletion =
        string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_TRACE_GUEST_WORK_COMPLETION"),
            "1",
            StringComparison.Ordinal);
    private static readonly HashSet<(ulong Address, uint Width, uint Height)>
        _tracedGuestImageSubmissions = [];
    private static Thread? _thread;
    private static Presentation? _latestPresentation;
    private static byte[]? _copyFragmentSpirv;
    private static uint _windowWidth;
    private static uint _windowHeight;
    private static bool _closed;
    private static bool _presenterCloseRequested;
    private const string DebugUtilsExtensionName = "VK_EXT_debug_utils";
    private const uint NvidiaVendorId = 0x10DE;
    private const string PortabilityEnumerationExtensionName = "VK_KHR_portability_enumeration";
    private const string PortabilitySubsetExtensionName = "VK_KHR_portability_subset";
    private const int GlfwPlatformHint = 0x00050003;
    private const int GlfwPlatformWin32 = 0x00060001;
    private const int GlfwPlatformCocoa = 0x00060002;
    private const int GlfwPlatformWayland = 0x00060003;
    private const int GlfwPlatformX11 = 0x00060004;
    private const int GlfwPlatformNull = 0x00060005;
    private static bool _splashHidden;
    private static long _enqueuedGuestWorkSequence;
    // Largest contiguous completed sequence, retained for compact diagnostics.
    // Per-queue scheduling can complete a later global id first, so correctness
    // checks use IsGuestWorkCompletedLocked rather than numeric <= comparisons.
    private static long _completedGuestWorkSequence;
    private static readonly HashSet<long> _completedGuestWorkOutOfOrder = [];
    private static readonly Dictionary<string, long> _lastEnqueuedGuestWorkByQueue =
        new(StringComparer.Ordinal);
    private static long _executingGuestWorkSequence;
    [ThreadStatic]
    private static VulkanGuestQueueIdentity? _submittingGuestQueue;
    [ThreadStatic]
    private static bool _enqueueAsImmediateQueueFollowup;
    [ThreadStatic]
    private static LinkedListNode<PendingGuestWork>? _immediateFollowupTail;

    private sealed class GuestQueueScope : IDisposable
    {
        private readonly VulkanGuestQueueIdentity? _previous;
        private bool _disposed;

        public GuestQueueScope(VulkanGuestQueueIdentity queue)
        {
            _previous = _submittingGuestQueue;
            _submittingGuestQueue = queue;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _submittingGuestQueue = _previous;
        }
    }

    public static IDisposable EnterGuestQueue(
        string queueName,
        ulong submissionId) =>
        new GuestQueueScope(new VulkanGuestQueueIdentity(
            string.IsNullOrWhiteSpace(queueName) ? "guest.unknown" : queueName,
            submissionId));

    private static long CurrentSubmittingQueueTailLocked()
    {
        var queue = _submittingGuestQueue;
        return queue is { } identity &&
            _lastEnqueuedGuestWorkByQueue.TryGetValue(identity.Name, out var tail)
                ? tail
                : 0;
    }

    private static bool ShouldTraceGuestImageSubmissionsForDiagnostics()
    {
        return string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_TRACE_GUEST_IMAGES"),
            "1",
            StringComparison.Ordinal);
    }

    private static bool ShouldSamplePresentedGuestImageForDiagnostics(long frame)
    {
        var mode = Environment.GetEnvironmentVariable("SHARPEMU_TRACE_GUEST_IMAGES");
        if (string.Equals(mode, "present", StringComparison.OrdinalIgnoreCase))
        {
            // A 4K Vulkan readback is deliberately synchronous and can take
            // several seconds on Linux.  The lightweight "present" mode only
            // needs one proof that the final image is non-black.
            return frame == 1;
        }

        return string.Equals(mode, "1", StringComparison.Ordinal) &&
               (frame is 1 or 30 or 120 || frame % 600 == 0);
    }

    public static void EnsureStarted(uint width, uint height)
    {
        if (width == 0 || height == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_closed || _thread is not null)
            {
                return;
            }
        }

        var hasSplash = PngSplashLoader.TryLoad(
            out var splashPixels,
            out var splashWidth,
            out var splashHeight);
        lock (_gate)
        {
            if (_closed || _thread is not null)
            {
                return;
            }

            _windowWidth = width;
            _windowHeight = height;
            _latestPresentation ??= _splashHidden
                ? new Presentation(
                    CreateBlackFrame(width, height),
                    width,
                    height,
                    1,
                    GuestDrawKind.None,
                    TranslatedDraw: null,
                    RequiredGuestWorkSequence: 0,
                    IsSplash: false)
                : hasSplash
                ? new Presentation(
                    splashPixels,
                    splashWidth,
                    splashHeight,
                    1,
                    GuestDrawKind.None,
                    TranslatedDraw: null,
                    RequiredGuestWorkSequence: 0,
                    IsSplash: true)
                : new Presentation(
                    null,
                    width,
                    height,
                    0,
                    GuestDrawKind.None,
                    TranslatedDraw: null,
                    RequiredGuestWorkSequence: 0,
                    IsSplash: false);
            StartPresenterLocked();
        }
    }

    public static void HideSplashScreen()
    {
        lock (_gate)
        {
            _splashHidden = true;
            if (_closed || _latestPresentation is not { IsSplash: true } latest)
            {
                return;
            }

            var sequence = latest.Sequence + 1;
            _latestPresentation = new Presentation(
                CreateBlackFrame(latest.Width, latest.Height),
                latest.Width,
                latest.Height,
                sequence,
                GuestDrawKind.None,
                TranslatedDraw: null,
                RequiredGuestWorkSequence: 0,
                IsSplash: false);
            Console.Error.WriteLine("[LOADER][INFO] Vulkan VideoOut hid splash");
        }
    }

    public static void Submit(byte[] bgraFrame, uint width, uint height)
    {
        if (bgraFrame.Length != checked((int)(width * height * 4)))
        {
            return;
        }

        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            var sequence = (_latestPresentation?.Sequence ?? 0) + 1;
            _latestPresentation = new Presentation(
                bgraFrame,
                width,
                height,
                sequence,
                GuestDrawKind.None,
                TranslatedDraw: null,
                RequiredGuestWorkSequence: 0,
                IsSplash: false);
            if (_thread is not null)
            {
                return;
            }

            _windowWidth = width;
            _windowHeight = height;
            StartPresenterLocked();
        }
    }

    public static void SubmitGuestDraw(GuestDrawKind drawKind, uint width, uint height)
    {
        if (drawKind == GuestDrawKind.None || width == 0 || height == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_closed ||
                _latestPresentation is { Pixels: null } latest &&
                latest.DrawKind == drawKind &&
                latest.Width == width &&
                latest.Height == height)
            {
                return;
            }

            var sequence = (_latestPresentation?.Sequence ?? 0) + 1;
            _latestPresentation = new Presentation(
                null,
                width,
                height,
                sequence,
                drawKind,
                TranslatedDraw: null,
                RequiredGuestWorkSequence: CurrentSubmittingQueueTailLocked(),
                IsSplash: false);
            if (_thread is not null)
            {
                return;
            }

            _windowWidth = width;
            _windowHeight = height;
            StartPresenterLocked();
        }
    }

    public static void SubmitTranslatedDraw(
        byte[] pixelSpirv,
        IReadOnlyList<GuestDrawTexture> textures,
        IReadOnlyList<GuestMemoryBuffer> globalMemoryBuffers,
        uint width,
        uint height,
        uint attributeCount,
        byte[]? vertexSpirv = null,
        uint vertexCount = 3,
        uint instanceCount = 1,
        uint primitiveType = 4,
        GuestIndexBuffer? indexBuffer = null,
        IReadOnlyList<GuestVertexBuffer>? vertexBuffers = null,
        GuestRenderState? renderState = null)
    {
        if (pixelSpirv.Length == 0 || width == 0 || height == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            var sequence = (_latestPresentation?.Sequence ?? 0) + 1;
            _latestPresentation = new Presentation(
                null,
                width,
                height,
                sequence,
                GuestDrawKind.None,
                new VulkanTranslatedGuestDraw(
                    vertexSpirv ?? [],
                    pixelSpirv,
                    textures.ToArray(),
                    globalMemoryBuffers.ToArray(),
                    vertexBuffers?.ToArray() ?? [],
                    attributeCount,
                    vertexCount,
                    instanceCount,
                    primitiveType,
                    indexBuffer,
                    renderState ?? GuestRenderState.Default),
                RequiredGuestWorkSequence: CurrentSubmittingQueueTailLocked(),
                IsSplash: false);
            if (_thread is not null)
            {
                return;
            }

            _windowWidth = width;
            _windowHeight = height;
            StartPresenterLocked();
        }
    }

    public static void SubmitOffscreenTranslatedDraw(
        byte[] pixelSpirv,
        IReadOnlyList<GuestDrawTexture> textures,
        IReadOnlyList<GuestMemoryBuffer> globalMemoryBuffers,
        uint attributeCount,
        GuestRenderTarget target,
        byte[]? vertexSpirv = null,
        uint vertexCount = 3,
        uint instanceCount = 1,
        uint primitiveType = 4,
        GuestIndexBuffer? indexBuffer = null,
        IReadOnlyList<GuestVertexBuffer>? vertexBuffers = null,
        GuestRenderState? renderState = null,
        GuestDepthTarget? depthTarget = null,
        ulong shaderAddress = 0)
    {
        SubmitOffscreenTranslatedDraw(
            pixelSpirv,
            textures,
            globalMemoryBuffers,
            attributeCount,
            [target],
            vertexSpirv,
            vertexCount,
            instanceCount,
            primitiveType,
            indexBuffer,
            vertexBuffers,
            renderState,
            depthTarget,
            shaderAddress);
    }

    // Manual scans (targets are <= 8) so the per-draw validation does not
    // allocate LINQ iterators/closures or a Distinct HashSet.
    private static bool AnyRenderTargetInvalid(IReadOnlyList<GuestRenderTarget> targets)
    {
        foreach (var target in targets)
        {
            if (target.Address == 0 || target.Width == 0 || target.Height == 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool RenderTargetsMismatchedOrAliased(IReadOnlyList<GuestRenderTarget> targets, GuestRenderTarget first)
    {
        for (var i = 0; i < targets.Count; i++)
        {
            if (targets[i].Width != first.Width || targets[i].Height != first.Height)
            {
                return true;
            }

            for (var j = i + 1; j < targets.Count; j++)
            {
                if (targets[i].Address == targets[j].Address)
                {
                    return true;
                }
            }
        }

        return false;
    }

    public static void SubmitOffscreenTranslatedDraw(
        byte[] pixelSpirv,
        IReadOnlyList<GuestDrawTexture> textures,
        IReadOnlyList<GuestMemoryBuffer> globalMemoryBuffers,
        uint attributeCount,
        IReadOnlyList<GuestRenderTarget> targets,
        byte[]? vertexSpirv = null,
        uint vertexCount = 3,
        uint instanceCount = 1,
        uint primitiveType = 4,
        GuestIndexBuffer? indexBuffer = null,
        IReadOnlyList<GuestVertexBuffer>? vertexBuffers = null,
        GuestRenderState? renderState = null,
        GuestDepthTarget? depthTarget = null,
        ulong shaderAddress = 0)
    {
        if (pixelSpirv.Length == 0 ||
            targets.Count == 0 ||
            targets.Count > 8 ||
            AnyRenderTargetInvalid(targets))
        {
            return;
        }

        var firstTarget = targets[0];
        if (RenderTargetsMismatchedOrAliased(targets, firstTarget))
        {
            Console.Error.WriteLine(
                "[LOADER][WARN] Vulkan skipped MRT draw with mismatched dimensions or aliased targets.");
            return;
        }

        if (ShouldTraceGuestImageSubmissionsForDiagnostics())
        {
            Console.Error.WriteLine(
                $"[LOADER][TRACE] vk.submit_call kind=SubmitOffscreenTranslatedDraw " +
                $"targets={targets.Count} first=0x{firstTarget.Address:X16} " +
                $"{firstTarget.Width}x{firstTarget.Height} textures={textures.Count}");
        }

        var effectiveRenderState = renderState ?? GuestRenderState.Default;
        if (effectiveRenderState.Blends.Count == 1 && targets.Count > 1)
        {
            var broadcastBlends = new GuestBlendState[targets.Count];
            Array.Fill(broadcastBlends, effectiveRenderState.Blends[0]);
            effectiveRenderState = effectiveRenderState with
            {
                Blends = broadcastBlends,
            };
        }
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            foreach (var target in targets)
            {
                var guestTextureFormat = GetGuestTextureFormat(
                    target.Format,
                    target.NumberType);
                if (guestTextureFormat != 0)
                {
                    _availableGuestImages[target.Address] = guestTextureFormat;
                }
            }

            var workSequence = EnqueueGuestWorkLocked(
                new VulkanOffscreenGuestDraw(
                    new VulkanTranslatedGuestDraw(
                        vertexSpirv ?? [],
                        pixelSpirv,
                        textures.ToArray(),
                        globalMemoryBuffers.ToArray(),
                        vertexBuffers?.ToArray() ?? [],
                        attributeCount,
                        vertexCount,
                        instanceCount,
                        primitiveType,
                        indexBuffer,
                        effectiveRenderState),
                    targets.ToArray(),
                    depthTarget,
                    PublishTarget: true,
                    shaderAddress));
            foreach (var target in targets)
            {
                _guestImageWorkSequences[target.Address] = workSequence;
            }
        }
    }

    public static void SubmitDepthOnlyTranslatedDraw(
        byte[] pixelSpirv,
        IReadOnlyList<GuestDrawTexture> textures,
        IReadOnlyList<GuestMemoryBuffer> globalMemoryBuffers,
        uint attributeCount,
        GuestDepthTarget depthTarget,
        byte[]? vertexSpirv = null,
        uint vertexCount = 3,
        uint instanceCount = 1,
        uint primitiveType = 4,
        GuestIndexBuffer? indexBuffer = null,
        IReadOnlyList<GuestVertexBuffer>? vertexBuffers = null,
        GuestRenderState? renderState = null,
        ulong shaderAddress = 0)
    {
        if (pixelSpirv.Length == 0 ||
            depthTarget.Address == 0 ||
            depthTarget.Width == 0 ||
            depthTarget.Height == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            EnqueueGuestWorkLocked(
                new VulkanOffscreenGuestDraw(
                    new VulkanTranslatedGuestDraw(
                        vertexSpirv ?? [],
                        pixelSpirv,
                        textures.ToArray(),
                        globalMemoryBuffers.ToArray(),
                        vertexBuffers?.ToArray() ?? [],
                        attributeCount,
                        vertexCount,
                        instanceCount,
                        primitiveType,
                        indexBuffer,
                        renderState ?? GuestRenderState.Default),
                    [new GuestRenderTarget(
                        Address: 0,
                        depthTarget.Width,
                        depthTarget.Height,
                        Format: 10,
                        NumberType: 0)],
                    depthTarget,
                    PublishTarget: false,
                    shaderAddress));
        }
    }

    private sealed record VulkanGuestImageWrite(
        ulong Address,
        byte[]? Pixels,
        uint FillValue);

    /// <summary>
    /// Reports the extent of a live guest image so DMA writes to its backing
    /// memory can be mirrored into the Vulkan image (PS5 render targets alias
    /// guest memory, so CP DMA fills/copies are visible to later GPU reads).
    /// </summary>
    internal static bool TryGetGuestImageExtent(
        ulong address,
        out uint width,
        out uint height,
        out ulong byteCount)
    {
        lock (_gate)
        {
            if (_guestImageExtents.TryGetValue(address, out var extent))
            {
                (width, height, byteCount) = extent;
                return true;
            }
        }

        width = 0;
        height = 0;
        byteCount = 0;
        return false;
    }

    internal static void SubmitGuestImageFill(ulong address, uint fillValue)
    {
        lock (_gate)
        {
            if (_closed || !_guestImageExtents.ContainsKey(address))
            {
                return;
            }

            _guestImageWorkSequences[address] = EnqueueGuestWorkLocked(
                new VulkanGuestImageWrite(address, null, fillValue));
        }
    }

    internal static void SubmitGuestImageWrite(ulong address, byte[] pixels)
    {
        lock (_gate)
        {
            if (_closed || !_guestImageExtents.ContainsKey(address))
            {
                return;
            }

            _guestImageWorkSequences[address] = EnqueueGuestWorkLocked(
                new VulkanGuestImageWrite(address, pixels, 0));
        }
    }

    private static long _perfDrawCount;
    private static long _perfDrawTicks;
    private static long _perfPipelineCreations;
    private static long _perfSpirvCompilations;

    internal static (long Draws, double DrawMs, long Pipelines, long SpirvCompilations)
        ReadAndResetPerfCounters()
    {
        var draws = Interlocked.Exchange(ref _perfDrawCount, 0);
        var ticks = Interlocked.Exchange(ref _perfDrawTicks, 0);
        var pipelines = Interlocked.Exchange(ref _perfPipelineCreations, 0);
        var spirv = Interlocked.Exchange(ref _perfSpirvCompilations, 0);
        return (draws, ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency, pipelines, spirv);
    }

    internal static void CountSpirvCompilation() =>
        Interlocked.Increment(ref _perfSpirvCompilations);

    internal static IReadOnlyList<(ulong Address, uint Width, uint Height, ulong ByteCount)> GetGuestImageExtents()
    {
        lock (_gate)
        {
            return _guestImageExtents
                .Select(entry => (
                    entry.Key,
                    entry.Value.Width,
                    entry.Value.Height,
                    entry.Value.ByteCount))
                .ToArray();
        }
    }

    public static void SubmitStorageTranslatedDraw(
        byte[] pixelSpirv,
        IReadOnlyList<GuestDrawTexture> textures,
        IReadOnlyList<GuestMemoryBuffer> globalMemoryBuffers,
        uint attributeCount,
        uint width,
        uint height,
        ulong shaderAddress = 0)
    {
        if (pixelSpirv.Length == 0 ||
            width == 0 ||
            height == 0 ||
            textures.All(texture => !texture.IsStorage))
        {
            return;
        }

        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            EnqueueGuestWorkLocked(
                new VulkanOffscreenGuestDraw(
                    new VulkanTranslatedGuestDraw(
                        [],
                        pixelSpirv,
                        textures.ToArray(),
                        globalMemoryBuffers.ToArray(),
                        [],
                        attributeCount,
                        3,
                        1,
                        4,
                        null,
                        GuestRenderState.Default),
                    [new GuestRenderTarget(
                        Address: 0,
                        width,
                        height,
                        Format: 12,
                        NumberType: 7)],
                    DepthTarget: null,
                    PublishTarget: false,
                    shaderAddress));
        }
    }

    public static long SubmitComputeDispatch(
        ulong shaderAddress,
        byte[] computeSpirv,
        IReadOnlyList<GuestDrawTexture> textures,
        IReadOnlyList<GuestMemoryBuffer> globalMemoryBuffers,
        uint groupCountX,
        uint groupCountY,
        uint groupCountZ,
        uint baseGroupX,
        uint baseGroupY,
        uint baseGroupZ,
        uint localSizeX,
        uint localSizeY,
        uint localSizeZ,
        bool isIndirect,
        bool writesGlobalMemory,
        uint threadCountX = uint.MaxValue,
        uint threadCountY = uint.MaxValue,
        uint threadCountZ = uint.MaxValue)
    {
        if (computeSpirv.Length == 0 ||
            groupCountX == 0 ||
            groupCountY == 0 ||
            groupCountZ == 0 ||
            textures.All(texture => !texture.IsStorage) &&
            !writesGlobalMemory)
        {
            return 0;
        }

        long workSequence;
        lock (_gate)
        {
            if (_closed)
            {
                return 0;
            }

            workSequence = EnqueueGuestWorkLocked(
                new VulkanComputeGuestDispatch(
                    shaderAddress,
                    computeSpirv,
                    textures.ToArray(),
                    globalMemoryBuffers.ToArray(),
                    groupCountX,
                    groupCountY,
                    groupCountZ,
                    baseGroupX,
                    baseGroupY,
                    baseGroupZ,
                    localSizeX,
                    localSizeY,
                    localSizeZ,
                    isIndirect,
                    writesGlobalMemory,
                    threadCountX,
                    threadCountY,
                    threadCountZ));
            foreach (var key in GetStorageImageUploadKeys(textures))
            {
                _pendingGuestImageUploads[key] =
                    _pendingGuestImageUploads.TryGetValue(key, out var pendingUpload)
                        ? pendingUpload with { Count = checked(pendingUpload.Count + 1) }
                        : new PendingGuestImageUpload(1, workSequence);
            }

            foreach (var texture in textures)
            {
                if (texture.IsStorage && texture.Address != 0)
                {
                    _guestImageWorkSequences[texture.Address] = workSequence;
                }
            }
        }

        return workSequence;
    }

    /// <summary>
    /// Enqueues a CPU-visible PM4 side effect behind all GPU work submitted
    /// before it. The render thread flushes its open batch and waits for the
    /// corresponding guest fences before invoking the action.
    /// </summary>
    public static long SubmitOrderedGuestAction(Action action, string debugName)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_gate)
        {
            return _closed || _thread is null
                ? 0
                : EnqueueGuestWorkLocked(new VulkanOrderedGuestAction(action, debugName));
        }
    }

    /// <summary>
    /// Sequence currently being executed by the single guest-work consumer.
    /// Intended only for address-filtered lifetime diagnostics emitted from a
    /// guest-work callback before <see cref="CompleteGuestWork"/> advances it.
    /// </summary>
    public static long CurrentGuestWorkSequenceForDiagnostics =>
        Volatile.Read(ref _executingGuestWorkSequence);

    private static bool IsGuestWorkCompletedLocked(long sequence) =>
        sequence <= 0 ||
        sequence <= _completedGuestWorkSequence ||
        _completedGuestWorkOutOfOrder.Contains(sequence);

    public static bool WaitForGuestWork(
        long workSequence,
        int timeoutMilliseconds = System.Threading.Timeout.Infinite)
    {
        if (workSequence <= 0)
        {
            return false;
        }

        var waitIndefinitely = timeoutMilliseconds == System.Threading.Timeout.Infinite;
        var deadline = waitIndefinitely
            ? long.MaxValue
            : Environment.TickCount64 + Math.Max(timeoutMilliseconds, 1);
        lock (_gate)
        {
            if (_traceGuestWorkCompletion)
            {
                Console.Error.WriteLine(
                    $"[LOADER][TRACE] vk.guest_work_wait_enter sequence={workSequence} " +
                    $"contiguous_completed={_completedGuestWorkSequence} " +
                    $"out_of_order={_completedGuestWorkOutOfOrder.Count}");
            }
            while (!_closed && !IsGuestWorkCompletedLocked(workSequence))
            {
                if (!waitIndefinitely)
                {
                    var remaining = deadline - Environment.TickCount64;
                    if (remaining <= 0)
                    {
                        Console.Error.WriteLine(
                            $"[LOADER][WARN] Vulkan guest work wait timed out " +
                            $"sequence={workSequence} contiguous_completed={_completedGuestWorkSequence} " +
                            $"out_of_order={_completedGuestWorkOutOfOrder.Count}");
                        return false;
                    }

                    System.Threading.Monitor.Wait(
                        _gate,
                        checked((int)Math.Min(remaining, 1_000)));
                    continue;
                }

                // CPU-visible GPU writes are ordering points in the guest
                // command stream. First-use shader compilation can take more
                // than a minute on MoltenVK; timing out would let the guest
                // consume stale zero-filled buffers and permanently corrupt
                // the frame. Closing the presenter pulses this monitor, so an
                // unbounded correctness wait remains interruptible.
                System.Threading.Monitor.Wait(_gate, 1_000);
            }

            var completed = IsGuestWorkCompletedLocked(workSequence);
            if (_traceGuestWorkCompletion)
            {
                Console.Error.WriteLine(
                    $"[LOADER][TRACE] vk.guest_work_wait_exit sequence={workSequence} " +
                    $"completed={completed} contiguous_completed={_completedGuestWorkSequence} " +
                    $"out_of_order={_completedGuestWorkOutOfOrder.Count}");
            }
            return completed;
        }
    }

    public static bool TrySubmitGuestImage(
        ulong address,
        uint width,
        uint height,
        uint pitchInPixel)
    {
        var traceSubmission = false;
        lock (_gate)
        {
            if (_closed ||
                !_availableGuestImages.ContainsKey(address))
            {
                return false;
            }

            traceSubmission =
                _tracedGuestImageSubmissions.Add((address, width, height));
            var sequence = (_latestPresentation?.Sequence ?? 0) + 1;
            var requiredWorkSequence = _guestImageWorkSequences.TryGetValue(
                address,
                out var imageWorkSequence)
                ? imageWorkSequence
                : _completedGuestWorkSequence;
            var presentation = new Presentation(
                null,
                width,
                height,
                sequence,
                GuestDrawKind.None,
                TranslatedDraw: null,
                // Wait only for the work that last wrote this image, not for
                // every later command the guest has already queued. Requiring
                // the global tail makes a fast guest permanently outrun the
                // renderer and turns every flip into a dropped black frame.
                RequiredGuestWorkSequence: requiredWorkSequence,
                IsSplash: false,
                GuestImageAddress: address);
            _latestPresentation = presentation;
            _pendingGuestImagePresentations.Enqueue(presentation);
            while (_pendingGuestImagePresentations.Count > MaxPendingGuestWork)
            {
                _pendingGuestImagePresentations.Dequeue();
            }
        }

        if (traceSubmission)
        {
            var effectivePitch = pitchInPixel == 0 ? width : pitchInPixel;
            Console.Error.WriteLine(
                $"[LOADER][TRACE] vk.submit_guest_image addr=0x{address:X16} " +
                $"size={width}x{height} pitch={effectivePitch}");
        }

        return true;
    }

    /// <summary>
    /// Enqueues an AGC flip at its exact position in the logical guest queue.
    /// The presenter captures the named image into an immutable Vulkan image
    /// before it executes later work from the same queue. Presentation then
    /// consumes that captured generation rather than the mutable render target.
    /// </summary>
    public static bool TrySubmitOrderedGuestImageFlip(
        int videoOutHandle,
        int displayBufferIndex,
        ulong address,
        uint width,
        uint height,
        uint pitchInPixel)
    {
        lock (_gate)
        {
            if (_closed ||
                _thread is null ||
                !_availableGuestImages.ContainsKey(address))
            {
                return false;
            }

            var version = ++_orderedGuestFlipVersionSequence;
            _lastOrderedGuestFlipVersions[(videoOutHandle, displayBufferIndex)] = version;
            return EnqueueGuestWorkLocked(
                new VulkanOrderedGuestFlip(
                    version,
                    videoOutHandle,
                    displayBufferIndex,
                    address,
                    width,
                    height,
                    pitchInPixel)) > 0;
        }
    }

    /// <summary>
    /// Preserves sceAgcDcbWaitUntilSafeForRendering in queue order. Because an
    /// ordered flip first copies the mutable render target into an immutable
    /// generation on the same Vulkan queue, reaching this marker proves later
    /// rendering cannot change the frame selected by that flip. No CPU wait or
    /// event-loop stall is required.
    /// </summary>
    public static long SubmitOrderedGuestFlipWait(
        int videoOutHandle,
        int displayBufferIndex)
    {
        lock (_gate)
        {
            var version = _lastOrderedGuestFlipVersions.TryGetValue(
                (videoOutHandle, displayBufferIndex),
                out var lastVersion)
                    ? lastVersion
                    : 0;
            return _closed || _thread is null
                ? 0
                : EnqueueGuestWorkLocked(
                    new VulkanOrderedGuestFlipWait(
                        version,
                        videoOutHandle,
                        displayBufferIndex));
        }
    }

    /// <summary>
    /// On PS5 a render target aliases guest memory, so CPU-prefilled pixels are
    /// visible before the first draw. Our Vulkan images start undefined, so the
    /// first draw into a new address must seed the image from guest memory.
    /// </summary>
    internal static bool GuestImageWantsInitialData(ulong address)
    {
        if (address == 0)
        {
            return false;
        }

        lock (_gate)
        {
            return !_availableGuestImages.ContainsKey(address) &&
                !_pendingGuestImageInitialData.ContainsKey(address);
        }
    }

    internal static void ProvideGuestImageInitialData(ulong address, byte[] rgbaPixels)
    {
        lock (_gate)
        {
            _pendingGuestImageInitialData[address] = rgbaPixels;
        }
    }

    private static byte[]? TakeGuestImageInitialData(ulong address)
    {
        lock (_gate)
        {
            if (!_pendingGuestImageInitialData.TryGetValue(address, out var data))
            {
                return null;
            }

            _pendingGuestImageInitialData.Remove(address);
            return data;
        }
    }

    // Mirror of the render thread's texture-cache identities, readable from
    // the guest submit thread. The AGC translator used to allocate and copy
    // every referenced texture's texels out of guest memory on every draw,
    // only for the presenter to discard the bytes on a cache hit — for a
    // scene sampling large textures this was by far the dominant CPU cost
    // (gigabytes/second of allocation, page faults and GC pressure).
    // ConcurrentDictionary keyed set: reads happen per texture per draw on
    // the guest submit thread and must not contend with the render thread's
    // mutations.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<
        TextureContentIdentity, byte> _cachedTextureIdentities = new();

    internal readonly record struct TextureContentIdentity(
        ulong Address,
        uint Width,
        uint Height,
        uint Format,
        uint NumberType,
        uint DstSelect,
        uint TileMode,
        uint Pitch,
        GuestSampler Sampler);

    // Guest memory handle for render-thread self-healing: when a draw whose
    // texel copy was skipped misses the texture cache (eviction, cache
    // clear, or any other race), the presenter re-reads the texels itself
    // instead of showing a fallback pattern.
    private static volatile SharpEmu.HLE.ICpuMemory? _guestMemory;

    internal static void AttachGuestMemory(SharpEmu.HLE.ICpuMemory memory) =>
        _guestMemory = memory;

    internal static bool IsTextureContentCached(in TextureContentIdentity identity) =>
        _cachedTextureIdentities.ContainsKey(identity);

    private static void MarkTextureContentCached(in TextureContentIdentity identity) =>
        _cachedTextureIdentities.TryAdd(identity, 0);

    private static void UnmarkTextureContentCached(in TextureContentIdentity identity) =>
        _cachedTextureIdentities.TryRemove(identity, out _);

    private static void ClearCachedTextureIdentities() =>
        _cachedTextureIdentities.Clear();

    internal static bool IsGuestImageAvailable(
        ulong address,
        uint format,
        uint numberType)
    {
        var guestFormat = GetGuestTextureFormat(format, numberType);
        if (address == 0 || guestFormat == 0)
        {
            return false;
        }

        lock (_gate)
        {
            return _availableGuestImages.TryGetValue(address, out var availableFormat) &&
                availableFormat == guestFormat;
        }
    }

    // Display buffers registered through sceVideoOutRegisterBuffers remain
    // valid flip targets even before AGC has rendered into them.
    internal static void RegisterKnownDisplayBuffer(ulong address, uint guestFormat)
    {
        if (address == 0 || guestFormat == 0)
        {
            return;
        }

        lock (_gate)
        {
            _availableGuestImages[address] = guestFormat;
        }
    }

    internal static bool IsGpuGuestImageAvailable(
        ulong address,
        uint format,
        uint numberType) =>
        IsGuestImageAvailable(address, format, numberType);

    /// <summary>
    /// Returns whether a storage image already exists on the presenter or an
    /// earlier queued dispatch owns its one-time guest-memory initialization.
    /// This is intentionally separate from <see cref="IsGuestImageAvailable"/>:
    /// a pending image may skip a duplicate upload but is not yet safe for a
    /// flip/presentation lookup.
    /// </summary>
    internal static bool IsGuestImageUploadKnown(
        ulong address,
        uint format,
        uint numberType)
    {
        var guestFormat = GetGuestTextureFormat(format, numberType);
        if (address == 0 || guestFormat == 0)
        {
            return false;
        }

        lock (_gate)
        {
            return _availableGuestImages.TryGetValue(address, out var availableFormat) &&
                    availableFormat == guestFormat ||
                _pendingGuestImageUploads.ContainsKey((address, guestFormat));
        }
    }

    public static bool TrySubmitGuestImageBlit(
        ulong sourceAddress,
        uint sourceWidth,
        uint sourceHeight,
        uint sourceFormat,
        uint sourceNumberType,
        ulong destinationAddress,
        uint destinationWidth,
        uint destinationHeight,
        uint destinationFormat,
        uint destinationNumberType)
    {
        if (sourceAddress == 0 ||
            destinationAddress == 0 ||
            sourceWidth == 0 ||
            sourceHeight == 0 ||
            destinationWidth == 0 ||
            destinationHeight == 0 ||
            !TryGetCopyFragmentShader(out var fragmentSpirv))
        {
            return false;
        }

        lock (_gate)
        {
            if (_closed ||
                !_availableGuestImages.ContainsKey(sourceAddress) ||
                GetGuestTextureFormat(destinationFormat, 0) == 0)
            {
                return false;
            }
        }

        SubmitOffscreenTranslatedDraw(
            fragmentSpirv,
            [
                new GuestDrawTexture(
                    sourceAddress,
                    sourceWidth,
                    sourceHeight,
                    sourceFormat,
                    sourceNumberType,
                    [],
                    IsFallback: false,
                    IsStorage: false),
            ],
            [],
            attributeCount: 1,
            new GuestRenderTarget(
                destinationAddress,
                destinationWidth,
                destinationHeight,
                destinationFormat,
                destinationNumberType));
        return true;
    }

    private static bool TryGetCopyFragmentShader(out byte[] spirv)
    {
        lock (_gate)
        {
            if (_copyFragmentSpirv is not null)
            {
                spirv = _copyFragmentSpirv;
                return true;
            }
        }

        spirv = SpirvFixedShaders.CreateCopyFragment();

        lock (_gate)
        {
            _copyFragmentSpirv ??= spirv;
            spirv = _copyFragmentSpirv;
        }

        return true;
    }

    private static uint GetGuestTextureFormat(uint format, uint numberType) =>
        IsKnownGuestTextureFormat(format)
            ? 0x8000_0000u | ((format & 0x1FFu) << 8) | (numberType & 0xFFu)
            : 0;

    internal static bool TryDecodeRenderTargetFormat(
        uint dataFormat,
        uint numberType,
        out VulkanRenderTargetFormat result)
    {
        var format = (dataFormat, numberType) switch
        {
            (4, 4) => Format.R32Uint,
            (4, 5) => Format.R32Sint,
            (4, 7) => Format.R32Sfloat,
            (5, 4) => Format.R16G16Uint,
            (5, 5) => Format.R16G16Sint,
            (5, 7) => Format.R16G16Sfloat,
            (6, 7) or (7, 7) => Format.B10G11R11UfloatPack32,
            (9, _) => Format.A2R10G10B10UnormPack32,
            (10, 4) => Format.R8G8B8A8Uint,
            (10, 5) => Format.R8G8B8A8Sint,
            (10, 9) => Format.R8G8B8A8Srgb,
            (10, _) => Format.R8G8B8A8Unorm,
            (11, 7) => Format.R32G32Sfloat,
            (12, 4) => Format.R16G16B16A16Uint,
            (12, 5) => Format.R16G16B16A16Sint,
            (12, 7) => Format.R16G16B16A16Sfloat,
            (13, 7) or (14, 7) => Format.R32G32B32A32Sfloat,
            (20, 0) => Format.R32Uint,
            (29, 0) or (4, 0) => Format.R32Sfloat,
            (1, 0) or (36, 0) => Format.R8Unorm,
            (49, 0) => Format.R8Uint,
            (3, 0) => Format.R8G8Unorm,
            (5, 0) => Format.R16G16Unorm,
            (7, 0) => Format.B10G11R11UfloatPack32,
            (12, 0) => Format.R16G16B16A16Unorm,
            (13, 0) or (14, 0) => Format.R32G32B32A32Sfloat,
            (22, 0) or (71, 0) => Format.R16G16B16A16Sfloat,
            (56, 0) or (62, 0) or (64, 0) => Format.R8G8B8A8Unorm,
            (75, 0) => Format.R32G32Sfloat,
            _ => Format.Undefined,
        };

        if (format == Format.Undefined)
        {
            result = default;
            return false;
        }

        var outputKind = format switch
        {
            Format.R8Uint or Format.R32Uint or Format.R16G16Uint or
                Format.R8G8B8A8Uint or Format.R16G16B16A16Uint => Gen5PixelOutputKind.Uint,
            Format.R32Sint or Format.R16G16Sint or Format.R8G8B8A8Sint or
                Format.R16G16B16A16Sint => Gen5PixelOutputKind.Sint,
            _ => Gen5PixelOutputKind.Float,
        };
        result = new VulkanRenderTargetFormat(format, outputKind);
        return true;
    }

    private static bool IsKnownGuestTextureFormat(uint format) =>
        format is >= 1 and <= 19 or 34 or >= 169 and <= 182;

    private static byte[] CreateBlackFrame(uint width, uint height)
    {
        if (width == 0 || height == 0 || width > 8192 || height > 8192)
        {
            width = 1;
            height = 1;
        }

        var pixels = GC.AllocateUninitializedArray<byte>(checked((int)(width * height * 4)));
        pixels.AsSpan().Clear();
        for (var offset = 3; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = 0xFF;
        }

        return pixels;
    }

    private static void StartPresenterLocked()
    {
        if (HostMainThread.IsAvailable)
        {
            // AppKit (and therefore GLFW) traps when touched off the process
            // main thread on macOS, so hand the whole window loop to the
            // main-thread pump the CLI parked for us. _thread only marks the
            // presenter as running; Run() clears it on exit either way.
            _thread = Thread.CurrentThread;
            HostMainThread.SetShutdownRequestHandler(RequestClose);
            HostMainThread.Post(Run);
            return;
        }

        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "SharpEmu Vulkan VideoOut",
        };
        _thread.Start();
    }

    /// <summary>
    /// Asks a running presenter to close its window; used at emulator
    /// shutdown so a main-thread-hosted window loop returns to the pump.
    /// </summary>
    public static void RequestClose()
    {
        Volatile.Write(ref _presenterCloseRequested, true);
    }

    /// <summary>
    /// GLFW resolves Vulkan with dlopen("libvulkan.1.dylib"), which cannot
    /// find the app-local MoltenVK on macOS (Homebrew's Vulkan libraries are
    /// arm64-only and this is an x86-64 process). GLFW 3.4 accepts the
    /// loader entry point directly instead, so hand it MoltenVK's
    /// vkGetInstanceProcAddr before any window exists.
    /// </summary>
    private static unsafe void InitializeMacVulkanLoader()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        try
        {
            nint vulkan = 0;
            foreach (var candidate in new[]
            {
                Path.Combine(AppContext.BaseDirectory, "libvulkan.1.dylib"),
                Path.Combine(AppContext.BaseDirectory, "libMoltenVK.dylib"),
                "libvulkan.1.dylib",
                "libMoltenVK.dylib",
            })
            {
                if (System.Runtime.InteropServices.NativeLibrary.TryLoad(candidate, out vulkan))
                {
                    break;
                }
            }

            if (vulkan == 0 ||
                !System.Runtime.InteropServices.NativeLibrary.TryGetExport(
                    vulkan, "vkGetInstanceProcAddr", out var procAddr))
            {
                Console.Error.WriteLine(
                    "[LOADER][WARN] No Vulkan loader for GLFW; place a universal libMoltenVK.dylib " +
                    "next to SharpEmu as libvulkan.1.dylib.");
                return;
            }

            var glfw = System.Runtime.InteropServices.NativeLibrary.Load(
                Path.Combine(AppContext.BaseDirectory, "libglfw.3.dylib"));
            var initVulkanLoader = (delegate* unmanaged<nint, void>)
                System.Runtime.InteropServices.NativeLibrary.GetExport(glfw, "glfwInitVulkanLoader");
            initVulkanLoader(procAddr);
            Console.Error.WriteLine("[LOADER][INFO] GLFW Vulkan loader wired to MoltenVK.");
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[LOADER][WARN] GLFW Vulkan loader setup failed: {exception.Message}");
        }
    }

    private static unsafe void PreferX11OnLinuxWayland()
    {
        if (!OperatingSystem.IsLinux() ||
            string.Equals(
                Environment.GetEnvironmentVariable("SHARPEMU_ENABLE_WAYLAND"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
        {
            return;
        }

        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
        {
            Console.Error.WriteLine(
                "[LOADER][WARN] Wayland session without an X server (DISPLAY unset); " +
                "cannot steer GLFW to XWayland. Set SHARPEMU_ENABLE_WAYLAND=1 to use native Wayland.");
            return;
        }

        if (!TryLoadGlfw(out var glfw))
        {
            return;
        }

        try
        {
            var initHint = (delegate* unmanaged<int, int, void>)
                System.Runtime.InteropServices.NativeLibrary.GetExport(glfw, "glfwInitHint");
            initHint(GlfwPlatformHint, GlfwPlatformX11);
            Console.Error.WriteLine(
                "[LOADER][INFO] Wayland session detected; requested GLFW X11/XWayland backend.");
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"[LOADER][WARN] Could not set GLFW X11 platform hint: {exception.Message}");
        }
    }

    private static unsafe void LogGlfwPlatformInUse()
    {
        if (OperatingSystem.IsWindows() || !TryLoadGlfw(out var glfw))
        {
            return;
        }

        try
        {
            var getPlatform = (delegate* unmanaged<int>)
                System.Runtime.InteropServices.NativeLibrary.GetExport(glfw, "glfwGetPlatform");
            var platform = getPlatform();
            var label = platform switch
            {
                GlfwPlatformWin32 => "Win32",
                GlfwPlatformCocoa => "Cocoa",
                GlfwPlatformWayland => "Wayland",
                GlfwPlatformX11 => "X11",
                GlfwPlatformNull => "Null",
                _ => $"0x{platform:X}",
            };
            Console.Error.WriteLine($"[LOADER][INFO] GLFW windowing platform in use: {label}");
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[LOADER][WARN] Could not query GLFW platform: {exception.Message}");
        }
    }

    private static bool TryLoadGlfw(out nint handle)
    {
        var name = OperatingSystem.IsMacOS() ? "libglfw.3.dylib" : "libglfw.so.3";
        return System.Runtime.InteropServices.NativeLibrary.TryLoad(
            Path.Combine(AppContext.BaseDirectory, name), out handle) ||
            System.Runtime.InteropServices.NativeLibrary.TryLoad(name, out handle);
    }

    private static void Run()
    {
        uint width;
        uint height;
        lock (_gate)
        {
            width = _windowWidth == 0 ? _latestPresentation?.Width ?? 1280 : _windowWidth;
            height = _windowHeight == 0 ? _latestPresentation?.Height ?? 720 : _windowHeight;
        }

        PreferX11OnLinuxWayland();
        InitializeMacVulkanLoader();

        try
        {
            using var presenter = new Presenter(width, height);
            presenter.Run();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[LOADER][ERROR] Vulkan VideoOut presenter failed: {exception}");
        }
        finally
        {
            lock (_gate)
            {
                _closed = true;
                _thread = null;
                System.Threading.Monitor.PulseAll(_gate);
            }
        }
    }

    private static bool TryTakePresentation(long presentedSequence, out Presentation presentation)
    {
        lock (_gate)
        {
            // Guest flips are retained in submission order. The renderer is
            // deliberately allowed to lag a frame or two behind the guest
            // while it drains expensive work, so use the first completed flip
            // rather than repeatedly asking only for the newest one.
            while (_pendingGuestImagePresentations.Count > 0 &&
                   _pendingGuestImagePresentations.Peek().Sequence <= presentedSequence)
            {
                _pendingGuestImagePresentations.Dequeue();
            }

            if (_pendingGuestImagePresentations.Count > 0)
            {
                var pending = _pendingGuestImagePresentations.Peek();
                if (IsGuestWorkCompletedLocked(pending.RequiredGuestWorkSequence))
                {
                    presentation = _pendingGuestImagePresentations.Dequeue();
                    TryReplaceWithBinkFrame(ref presentation);
                    return true;
                }

                presentation = default;
                return false;
            }

            if (_latestPresentation is not { } latest ||
                latest.Sequence == presentedSequence ||
                !IsGuestWorkCompletedLocked(latest.RequiredGuestWorkSequence))
            {
                if (_latestPresentation is { } rej &&
                    rej.GuestImageAddress != 0 &&
					rej.Sequence != presentedSequence &&
                    _tracedGuestImagePresentRejections.Add(rej.Sequence))
                {
                    var reason = rej.Sequence == presentedSequence
                        ? "already-presented(seq==presented)"
                        : !IsGuestWorkCompletedLocked(rej.RequiredGuestWorkSequence)
                            ? $"work-not-done(req={rej.RequiredGuestWorkSequence}>" +
                              $"contiguous_done={_completedGuestWorkSequence})"
                            : "unknown";
                    Console.Error.WriteLine(
                        $"[LOADER][WARN] vk.guest_present_rejected addr=0x{rej.GuestImageAddress:X16} " +
                        $"seq={rej.Sequence} presentedSeq={presentedSequence} reason={reason}");
                }

                presentation = default;
                return false;
            }

            presentation = latest;
            TryReplaceWithBinkFrame(ref presentation);
            return true;
        }
    }

    private static void TryReplaceWithBinkFrame(ref Presentation presentation)
    {
        if (!Bink2MovieBridge.TryDecodeNextFrame(out var pixels, out var width, out var height))
        {
            return;
        }

        presentation = new Presentation(
            pixels,
            width,
            height,
            presentation.Sequence,
            GuestDrawKind.None,
            TranslatedDraw: null,
            presentation.RequiredGuestWorkSequence,
            IsSplash: false);
    }

    private static readonly HashSet<long> _tracedGuestImagePresentRejections = new();

	private static bool HasPendingGuestPresentation(long presentedSequence)
	{
		lock (_gate)
		{
			return _pendingGuestImagePresentations.Count > 0 ||
				_latestPresentation is { } latest && latest.Sequence > presentedSequence;
		}
	}

    private static long EnqueueGuestWorkLocked(object work)
    {
        var payloadBytes = GetGuestWorkPayloadBytes(work);
        var backpressureLogged = false;
        // Work executed by the render-thread consumer can enqueue an ordered
        // same-queue completion marker. Blocking that consumer on the normal
        // producer backpressure limit deadlocks a full queue: no other thread
        // can drain an item to make room for the marker. The consumer has
        // already removed the current item, and each immediate follow-up is
        // bounded by that item, so admitting it cannot cause unbounded growth.
        while (!_enqueueAsImmediateQueueFollowup &&
               !_closed &&
               _thread is not null &&
               (_pendingGuestWorkCount >= MaxPendingGuestWork ||
                // Always admit one item when no payload is outstanding, even
                // when that single item exceeds the configured budget. This
                // avoids an impossible wait while still bounding the normal
                // multi-item backlog.
                (_pendingGuestWorkBytes != 0 &&
                 payloadBytes > _maxPendingGuestWorkBytes -
                     Math.Min(_pendingGuestWorkBytes, _maxPendingGuestWorkBytes))))
        {
            if (!backpressureLogged)
            {
                backpressureLogged = true;
                var traceCount = Interlocked.Increment(
                    ref _guestQueueBackpressureTraceCount);
                if (traceCount <= 16 || (traceCount & (traceCount - 1)) == 0)
                {
                    Console.Error.WriteLine(
                        $"[LOADER][TRACE] vk.guest_queue_backpressure " +
                        $"count={traceCount} " +
                        $"queued={_pendingGuestWorkCount} " +
                        $"logical_queues={_pendingGuestWorkByQueue.Count} " +
                        $"retained_mb={_pendingGuestWorkBytes / (1024 * 1024)} " +
                        $"incoming_mb={payloadBytes / (1024 * 1024)} " +
                        $"budget_mb={_maxPendingGuestWorkBytes / (1024 * 1024)} " +
                        $"work={work.GetType().Name}" +
                        GetGuestWorkPayloadBreakdown(work));
                }
            }

            System.Threading.Monitor.Wait(_gate);
        }

        if (_closed)
        {
            return 0;
        }

        var queue = _submittingGuestQueue ?? VulkanGuestQueueIdentity.Default;
        var sequence = ++_enqueuedGuestWorkSequence;
        _lastEnqueuedGuestWorkByQueue[queue.Name] = sequence;
        var requiredSequence = GetGuestWorkDependencyLocked(work);
        if (!_pendingGuestWorkByQueue.TryGetValue(queue.Name, out var pendingQueue))
        {
            pendingQueue = new LinkedList<PendingGuestWork>();
            _pendingGuestWorkByQueue.Add(queue.Name, pendingQueue);
            _pendingGuestQueueSchedule.Add(queue.Name);
        }

        var pending = new PendingGuestWork(
            work,
            payloadBytes,
            sequence,
            requiredSequence,
            System.Diagnostics.Stopwatch.GetTimestamp(),
            queue);
        if (_traceGuestWorkCompletion)
        {
            Console.Error.WriteLine(
                $"[LOADER][TRACE] vk.guest_work_enqueue sequence={sequence} " +
                $"required={requiredSequence} queue={queue.Name} " +
                $"immediate={_enqueueAsImmediateQueueFollowup} " +
                $"work={work.GetType().Name}");
        }
        if (_enqueueAsImmediateQueueFollowup &&
            _immediateFollowupTail is { List: not null } tail &&
            ReferenceEquals(tail.List, pendingQueue))
        {
            _immediateFollowupTail = pendingQueue.AddAfter(tail, pending);
        }
        else if (_enqueueAsImmediateQueueFollowup)
        {
            _immediateFollowupTail = pendingQueue.AddFirst(pending);
        }
        else
        {
        pendingQueue.AddLast(pending);
        }
        RecordGuestImageWritersLocked(work, sequence);
        _pendingGuestWorkCount++;
        _pendingGuestWorkBytes = SaturatingAdd(_pendingGuestWorkBytes, payloadBytes);
        return sequence;
    }

    private static long GetGuestWorkDependencyLocked(object work)
    {
        IReadOnlyList<GuestDrawTexture> textures = work switch
        {
            VulkanOffscreenGuestDraw draw => draw.Draw.Textures,
            VulkanComputeGuestDispatch compute => compute.Textures,
            _ => Array.Empty<GuestDrawTexture>(),
        };
        var required = 0L;
        foreach (var texture in textures)
        {
            if (!texture.IsStorage ||
                texture.Address == 0 ||
                texture.RgbaPixels.Length != 0)
            {
                continue;
            }

            var format = GetGuestTextureFormat(texture.Format, texture.NumberType);
            if (_pendingGuestImageUploads.TryGetValue(
                    (texture.Address, format),
                    out var pendingUpload))
            {
                required = Math.Max(required, pendingUpload.OwnerSequence);
            }
        }

        return required;
    }

    private static void RecordGuestImageWritersLocked(object work, long sequence)
    {
        static IEnumerable<ulong> StorageAddresses(
            IReadOnlyList<GuestDrawTexture> textures) =>
            textures
                .Where(static texture => texture.IsStorage && texture.Address != 0)
                .Select(static texture => texture.Address);

        IEnumerable<ulong> addresses = work switch
        {
            VulkanOffscreenGuestDraw draw =>
                (draw.PublishTarget
                    ? draw.Targets
                        .Where(static target => target.Address != 0)
                        .Select(static target => target.Address)
                    : Enumerable.Empty<ulong>())
                .Concat(StorageAddresses(draw.Draw.Textures)),
            VulkanComputeGuestDispatch compute => StorageAddresses(compute.Textures),
            VulkanGuestImageWrite imageWrite when imageWrite.Address != 0 =>
                new[] { imageWrite.Address },
            _ => Array.Empty<ulong>(),
        };
        foreach (var address in addresses.Distinct())
        {
            _guestImageWorkSequences[address] = sequence;
        }
    }

    private static bool TryTakeGuestWork(out PendingGuestWork work)
    {
        lock (_gate)
        {
            var queuesToProbe = _pendingGuestQueueSchedule.Count;
            while (_pendingGuestQueueSchedule.Count > 0 && queuesToProbe > 0)
            {
                if (_pendingGuestQueueCursor >= _pendingGuestQueueSchedule.Count)
                {
                    _pendingGuestQueueCursor = 0;
                }

                var queueName = _pendingGuestQueueSchedule[_pendingGuestQueueCursor];
                if (!_pendingGuestWorkByQueue.TryGetValue(queueName, out var queue) ||
                    queue.First is not { } first)
                {
                    _pendingGuestWorkByQueue.Remove(queueName);
                    _pendingGuestQueueSchedule.RemoveAt(_pendingGuestQueueCursor);
                    queuesToProbe = Math.Min(
                        queuesToProbe,
                        _pendingGuestQueueSchedule.Count);
                    continue;
                }

                work = first.Value;
                if (!IsGuestWorkCompletedLocked(work.RequiredSequence))
                {
                    _pendingGuestQueueCursor =
                        (_pendingGuestQueueCursor + 1) % _pendingGuestQueueSchedule.Count;
                    queuesToProbe--;
                    continue;
                }

                queue.RemoveFirst();
                _pendingGuestWorkCount--;
                if (queue.Count == 0)
                {
                    _pendingGuestWorkByQueue.Remove(queueName);
                    _pendingGuestQueueSchedule.RemoveAt(_pendingGuestQueueCursor);
                }
                else
                {
                    _pendingGuestQueueCursor =
                        (_pendingGuestQueueCursor + 1) % _pendingGuestQueueSchedule.Count;
                }

                return true;
            }

            work = default;
            return false;
        }
    }

    private static void CompleteGuestWork(in PendingGuestWork pending)
    {
        SharpEmu.HLE.GuestImageWriteTracker.FlushPendingDiagnostics();
        lock (_gate)
        {
            if (_traceGuestWorkCompletion)
            {
                Console.Error.WriteLine(
                    $"[LOADER][TRACE] vk.guest_work_complete_enter " +
                    $"sequence={pending.Sequence} work={pending.Work.GetType().Name} " +
                    $"contiguous_completed={_completedGuestWorkSequence} " +
                    $"out_of_order={_completedGuestWorkOutOfOrder.Count}");
            }
            _pendingGuestWorkBytes = pending.PayloadBytes >= _pendingGuestWorkBytes
                ? 0
                : _pendingGuestWorkBytes - pending.PayloadBytes;
            ReleasePendingGuestImageUploadsLocked(pending.Work);
            if (pending.Sequence == _completedGuestWorkSequence + 1)
            {
                _completedGuestWorkSequence = pending.Sequence;
                while (_completedGuestWorkOutOfOrder.Remove(
                           _completedGuestWorkSequence + 1))
                {
                    _completedGuestWorkSequence++;
                }
            }
            else if (pending.Sequence > _completedGuestWorkSequence)
            {
                // Debug.Assert calls are compiled out of Release builds, so
                // never put the state mutation inside its argument. Doing so
                // discarded every out-of-order completion in normal runs and
                // left the first immediate follow-up as a permanent sequence
                // hole, blocking all later CPU-visible GPU waits.
                var added = _completedGuestWorkOutOfOrder.Add(pending.Sequence);
                System.Diagnostics.Debug.Assert(
                    added,
                    "A guest work sequence must complete exactly once.");
            }
            System.Threading.Monitor.PulseAll(_gate);
            if (_traceGuestWorkCompletion)
            {
                Console.Error.WriteLine(
                    $"[LOADER][TRACE] vk.guest_work_complete_exit " +
                    $"sequence={pending.Sequence} contiguous_completed={_completedGuestWorkSequence} " +
                    $"out_of_order={_completedGuestWorkOutOfOrder.Count}");
            }
        }
    }

    private static ulong GetGuestWorkPayloadBytes(object work) => work switch
    {
        VulkanComputeGuestDispatch compute => SaturatingAdd(
            GetTexturePayloadBytes(compute.Textures),
            GetGlobalBufferPayloadBytes(compute.GlobalMemoryBuffers)),
        VulkanOffscreenGuestDraw offscreen => GetDrawPayloadBytes(offscreen.Draw),
        VulkanGuestImageWrite { Pixels: { } pixels } => (ulong)pixels.LongLength,
        _ => 0,
    };

    private static string GetGuestWorkPayloadBreakdown(object work)
    {
        static ulong SumTextures(IReadOnlyList<GuestDrawTexture> textures) =>
            GetTexturePayloadBytes(textures) / (1024 * 1024);
        static ulong SumGlobals(IReadOnlyList<GuestMemoryBuffer> buffers) =>
            GetGlobalBufferPayloadBytes(buffers) / (1024 * 1024);

        return work switch
        {
            VulkanOffscreenGuestDraw offscreen =>
                $" textures_mb={SumTextures(offscreen.Draw.Textures)}" +
                $" globals_mb={SumGlobals(offscreen.Draw.GlobalMemoryBuffers)}" +
                $" vertex_mb={offscreen.Draw.VertexBuffers.Aggregate(0UL, static (sum, buffer) => SaturatingAdd(sum, (ulong)buffer.Data.LongLength)) / (1024 * 1024)}" +
                $" index_mb={(ulong)(offscreen.Draw.IndexBuffer?.Data.LongLength ?? 0) / (1024 * 1024)}" +
                $" vertex_lengths=[{string.Join(',', offscreen.Draw.VertexBuffers.Select(static buffer => $"{buffer.Length}/{buffer.Data.LongLength}:s{buffer.Stride}:o{buffer.OffsetBytes}"))}]" +
                $" global_lengths=[{string.Join(',', offscreen.Draw.GlobalMemoryBuffers.Select(static buffer => buffer.Length))}]",
            VulkanComputeGuestDispatch compute =>
                $" textures_mb={SumTextures(compute.Textures)}" +
                $" globals_mb={SumGlobals(compute.GlobalMemoryBuffers)}" +
                $" global_lengths=[{string.Join(',', compute.GlobalMemoryBuffers.Select(static buffer => buffer.Length))}]",
            _ => string.Empty,
        };
    }

    private static ulong GetDrawPayloadBytes(VulkanTranslatedGuestDraw draw)
    {
        var bytes = GetTexturePayloadBytes(draw.Textures);
        bytes = SaturatingAdd(bytes, GetGlobalBufferPayloadBytes(draw.GlobalMemoryBuffers));
        var uniqueVertexData = new HashSet<byte[]>(
            System.Collections.Generic.ReferenceEqualityComparer.Instance);
        foreach (var vertex in draw.VertexBuffers)
        {
            if (uniqueVertexData.Add(vertex.Data))
            {
                bytes = SaturatingAdd(bytes, (ulong)vertex.Data.LongLength);
            }
        }

        if (draw.IndexBuffer is { } index)
        {
            bytes = SaturatingAdd(bytes, (ulong)index.Data.LongLength);
        }

        return bytes;
    }

    private static ulong GetTexturePayloadBytes(
        IReadOnlyList<GuestDrawTexture> textures)
    {
        var bytes = 0UL;
        foreach (var texture in textures)
        {
            bytes = SaturatingAdd(bytes, (ulong)texture.RgbaPixels.LongLength);
        }

        return bytes;
    }

    private static ulong GetGlobalBufferPayloadBytes(
        IReadOnlyList<GuestMemoryBuffer> buffers)
    {
        var bytes = 0UL;
        foreach (var buffer in buffers)
        {
            bytes = SaturatingAdd(bytes, (ulong)buffer.Data.LongLength);
        }

        return bytes;
    }

    private static ulong SaturatingAdd(ulong left, ulong right) =>
        ulong.MaxValue - left < right ? ulong.MaxValue : left + right;

    private static void ReleasePendingGuestImageUploadsLocked(object work)
    {
        if (work is not VulkanComputeGuestDispatch compute)
        {
            return;
        }

        foreach (var key in GetStorageImageUploadKeys(compute.Textures))
        {
            if (!_pendingGuestImageUploads.TryGetValue(key, out var pendingUpload))
            {
                continue;
            }

            if (pendingUpload.Count <= 1)
            {
                _pendingGuestImageUploads.Remove(key);
            }
            else
            {
                _pendingGuestImageUploads[key] = pendingUpload with
                {
                    Count = pendingUpload.Count - 1,
                };
            }
        }
    }

    private static HashSet<(ulong Address, uint Format)> GetStorageImageUploadKeys(
        IReadOnlyList<GuestDrawTexture> textures)
    {
        var keys = new HashSet<(ulong Address, uint Format)>();
        foreach (var texture in textures)
        {
            if (!texture.IsStorage || texture.Address == 0)
            {
                continue;
            }

            var format = GetGuestTextureFormat(texture.Format, texture.NumberType);
            if (format != 0)
            {
                keys.Add((texture.Address, format));
            }
        }

        return keys;
    }

    internal static bool ShouldAttachGuestDepth(
        GuestDepthTarget? target,
        GuestDepthState state) =>
        target is not null &&
        (state.TestEnable || state.WriteEnable || state.ClearEnable);

    private readonly record struct Presentation(
        byte[]? Pixels,
        uint Width,
        uint Height,
        long Sequence,
        GuestDrawKind DrawKind,
        VulkanTranslatedGuestDraw? TranslatedDraw,
        long RequiredGuestWorkSequence,
        bool IsSplash,
        ulong GuestImageAddress = 0,
        long GuestImageVersion = 0);

    private sealed class Presenter : IDisposable
    {
        private const string FullscreenBarycentricVertexSpirv =
            "AwIjBwAAAQALAAgAMgAAAAAAAAARAAIAAQAAAAsABgABAAAAR0xTTC5zdGQuNDUwAAAAAA4AAwAAAAAAAQAAAA8ACAAAAAAABAAAAG1haW4AAAAADQAAABoAAAApAAAAAwADAAIAAADCAQAABQAEAAQAAABtYWluAAAAAAUABgALAAAAZ2xfUGVyVmVydGV4AAAAAAYABgALAAAAAAAAAGdsX1Bvc2l0aW9uAAYABwALAAAAAQAAAGdsX1BvaW50U2l6ZQAAAAAGAAcACwAAAAIAAABnbF9DbGlwRGlzdGFuY2UABgAHAAsAAAADAAAAZ2xfQ3VsbERpc3RhbmNlAAUAAwANAAAAAAAAAAUABgAaAAAAZ2xfVmVydGV4SW5kZXgAAAUABQAdAAAAaW5kZXhhYmxlAAAABQAFACkAAABiYXJ5Y2VudHJpYwAFAAUALwAAAGluZGV4YWJsZQAAAEcAAwALAAAAAgAAAEgABQALAAAAAAAAAAsAAAAAAAAASAAFAAsAAAABAAAACwAAAAEAAABIAAUACwAAAAIAAAALAAAAAwAAAEgABQALAAAAAwAAAAsAAAAEAAAARwAEABoAAAALAAAAKgAAAEcABAApAAAAHgAAAAAAAAATAAIAAgAAACEAAwADAAAAAgAAABYAAwAGAAAAIAAAABcABAAHAAAABgAAAAQAAAAVAAQACAAAACAAAAAAAAAAKwAEAAgAAAAJAAAAAQAAABwABAAKAAAABgAAAAkAAAAeAAYACwAAAAcAAAAGAAAACgAAAAoAAAAgAAQADAAAAAMAAAALAAAAOwAEAAwAAAANAAAAAwAAABUABAAOAAAAIAAAAAEAAAArAAQADgAAAA8AAAAAAAAAFwAEABAAAAAGAAAAAgAAACsABAAIAAAAEQAAAAMAAAAcAAQAEgAAABAAAAARAAAAKwAEAAYAAAATAAAAAACAvywABQAQAAAAFAAAABMAAAATAAAAKwAEAAYAAAAVAAAAAABAQCwABQAQAAAAFgAAABUAAAATAAAALAAFABAAAAAXAAAAEwAAABUAAAAsAAYAEgAAABgAAAAUAAAAFgAAABcAAAAgAAQAGQAAAAEAAAAOAAAAOwAEABkAAAAaAAAAAQAAACAABAAcAAAABwAAABIAAAAgAAQAHgAAAAcAAAAQAAAAKwAEAAYAAAAhAAAAAAAAACsABAAGAAAAIgAAAAAAgD8gAAQAJgAAAAMAAAAHAAAAIAAEACgAAAADAAAAEAAAADsABAAoAAAAKQAAAAMAAAAsAAUAEAAAACoAAAAiAAAAIQAAACwABQAQAAAAKwAAACEAAAAiAAAALAAFABAAAAAsAAAAIQAAACEAAAAsAAYAEgAAAC0AAAAqAAAAKwAAACwAAAA2AAUAAgAAAAQAAAAAAAAAAwAAAPgAAgAFAAAAOwAEABwAAAAdAAAABwAAADsABAAcAAAALwAAAAcAAAA9AAQADgAAABsAAAAaAAAAPgADAB0AAAAYAAAAQQAFAB4AAAAfAAAAHQAAABsAAAA9AAQAEAAAACAAAAAfAAAAUQAFAAYAAAAjAAAAIAAAAAAAAABRAAUABgAAACQAAAAgAAAAAQAAAFAABwAHAAAAJQAAACMAAAAkAAAAIQAAACIAAABBAAUAJgAAACcAAAANAAAADwAAAD4AAwAnAAAAJQAAAD0ABAAOAAAALgAAABoAAAA+AAMALwAAAC0AAABBAAUAHgAAADAAAAAvAAAALgAAAD0ABAAQAAAAMQAAADAAAAA+AAMAKQAAADEAAAD9AAEAOAABAA==";

        private const string FullscreenBarycentricFragmentSpirv =
            "AwIjBwAAAQALAAgAEgAAAAAAAAARAAIAAQAAAAsABgABAAAAR0xTTC5zdGQuNDUwAAAAAA4AAwAAAAAAAQAAAA8ABwAEAAAABAAAAG1haW4AAAAACQAAAAwAAAAQAAMABAAAAAcAAAADAAMAAgAAAMIBAAAFAAQABAAAAG1haW4AAAAABQAFAAkAAABvdXRDb2xvcgAAAAAFAAUADAAAAGJhcnljZW50cmljAEcABAAJAAAAHgAAAAAAAABHAAQADAAAAB4AAAAAAAAAEwACAAIAAAAhAAMAAwAAAAIAAAAWAAMABgAAACAAAAAXAAQABwAAAAYAAAAEAAAAIAAEAAgAAAADAAAABwAAADsABAAIAAAACQAAAAMAAAAXAAQACgAAAAYAAAACAAAAIAAEAAsAAAABAAAACgAAADsABAALAAAADAAAAAEAAAArAAQABgAAAA4AAAAAAAAANgAFAAIAAAAEAAAAAAAAAAMAAAD4AAIABQAAAD0ABAAKAAAADQAAAAwAAABRAAUABgAAAA8AAAANAAAAAAAAAFEABQAGAAAAEAAAAA0AAAABAAAAUAAHAAcAAAARAAAADwAAABAAAAAOAAAADgAAAD4AAwAJAAAAEQAAAP0AAQA4AAEA";

        private readonly IWindow _window;
        private const int MaxInFlightGuestSubmissions = 8;
        private Vk _vk = null!;
        private KhrSurface _surfaceApi = null!;
        private KhrSwapchain _swapchainApi = null!;
        private delegate* unmanaged<Device, DebugUtilsObjectNameInfoEXT*, Result> _setDebugUtilsObjectName;
        private delegate* unmanaged<CommandBuffer, DebugUtilsLabelEXT*, void> _cmdBeginDebugUtilsLabel;
        private delegate* unmanaged<CommandBuffer, void> _cmdEndDebugUtilsLabel;
        private Instance _instance;
        private SurfaceKHR _surface;
        private DebugUtilsMessengerEXT _debugMessenger;
        private ExtDebugUtils? _debugUtils;
        private PhysicalDevice _physicalDevice;
        private uint _maxComputeWorkGroupCountX;
        private uint _maxComputeWorkGroupCountY;
        private uint _maxComputeWorkGroupCountZ;
        private uint _maxComputeWorkGroupSizeX;
        private uint _maxComputeWorkGroupSizeY;
        private uint _maxComputeWorkGroupSizeZ;
        private uint _maxComputeWorkGroupInvocations;
        private ulong _minStorageBufferOffsetAlignment = 1;
        private bool _supportsIndependentBlend;
        private uint _maxColorAttachments;
        private Device _device;
        private PipelineCache _pipelineCache;
        private string? _pipelineCachePath;
        private bool _pipelineCacheDirty;
        private long _lastPipelineCacheSaveTick;
        private Queue _queue;
        private uint _queueFamilyIndex;
        private SwapchainKHR _swapchain;
        private Image[] _swapchainImages = [];
        private ImageView[] _swapchainImageViews = [];
        private Framebuffer[] _framebuffers = [];
        private bool[] _imageInitialized = [];
        private Format _swapchainFormat;
        private Extent2D _extent;
        private RenderPass _renderPass;
        private PipelineLayout _pipelineLayout;
        private Pipeline _barycentricPipeline;
        private CommandPool _commandPool;
        private CommandBuffer _commandBuffer;
        private CommandBuffer _presentationCommandBuffer;
        // Presentation runs with multiple frames in flight: each frame slot
        // owns a command buffer, an acquire semaphore and a fence, so the CPU
        // can record frame N+1 while the GPU still executes frame N. The
        // per-present vkQueueWaitIdle this replaces made CPU and GPU costs
        // strictly additive. Render-finished semaphores are per swapchain
        // image because the presentation engine may still wait on them after
        // the frame fence has signaled.
        private const int MaxFramesInFlight = 2;
        private CommandBuffer[] _frameCommandBuffers = [];
        private VkSemaphore[] _frameImageAvailable = [];
        private VkSemaphore[] _renderFinishedPerImage = [];
        private Fence[] _frameFences = [];
        private bool[] _frameFencePending = [];
        private ulong[] _frameTimelines = [];
        private TranslatedDrawResources?[] _frameTranslatedResources = [];
        private GuestImageResource?[] _frameGuestImageVersions = [];
        private int _currentFrameSlot;
        // Monotonic submission/completion counters across every queue submit
        // (guest batches, compute chunks and presents). Fences on a single
        // queue signal in submission order, so "timeline <= completed" means
        // the GPU is done with everything submitted up to that point; this
        // lets evicted resources be destroyed without a queue drain.
        private ulong _submitTimeline;
        private ulong _completedTimeline;
        private readonly Queue<(TextureResource Texture, ulong RetireTimeline)>
            _deferredTextureDestroys = new();
        private readonly Queue<(TranslatedDrawResources Resources, ulong RetireTimeline)>
            _deferredResourceDestroys = new();
        private readonly Queue<(GuestImageResource Image, ulong RetireTimeline)>
            _deferredGuestImageVersionDestroys = new();
        private readonly Stack<Fence> _recycledGuestFences = new();
        private readonly Stack<CommandBuffer> _recycledGuestCommandBuffers = new();
        private readonly List<(VkBuffer Buffer, DeviceMemory Memory)> _batchRetireBuffers = new();
        private const int MaxRecycledGuestFences = 32;
        private const int MaxRecycledGuestCommandBuffers = 32;
        private VkBuffer _stagingBuffer;
        private DeviceMemory _stagingMemory;
        private ulong _stagingSize;
        // Perf overlay: CPU-rasterized panel copied through per-slot staging
        // buffers into one image, then blitted onto the swapchain.
        private Image _overlayImage;
        private DeviceMemory _overlayImageMemory;
        private bool _overlayImageInitialized;
        private VkBuffer[] _overlayStagingBuffers = [];
        private DeviceMemory[] _overlayStagingMemory = [];
        private nint[] _overlayStagingMapped = [];
        private long _presentedSequence;
        private long _presentNotTakenLoggedSequence = long.MinValue;
        private bool _vulkanReady;
        private bool _firstFramePresented;
        private bool _firstGuestDrawPresented;
        private bool _splashPresented;
        private bool _swapchainRecreateDeferred;
        private bool _tracedPresentedSwapchain;
        private bool _swapchainReadbackPending;
        private long _presentedSwapchainCount;
        private static int _guestImageDumpSequence;
        private readonly System.Collections.Concurrent.ConcurrentQueue<GuestImageResource> _pendingAliasImageDumps = new();
        private bool _deviceLost;
        private bool _deviceLostLogged;
        private int _directPresentationCount;
        private readonly Dictionary<ulong, long> _presentedGuestImageTraceCounts = new();
        private readonly Dictionary<ulong, GuestImageResource> _guestImages = new();
        private readonly record struct GuestImageVariantKey(
            ulong Address,
            uint Width,
            uint Height,
            uint MipLevels,
            uint GuestFormat,
            Format Format);

        // A single guest allocation may be rebound through several RT descriptors.
        // Keep the inactive Vulkan images instead of destroying their contents each
        // time the guest switches size or format at the same address.
        private readonly Dictionary<GuestImageVariantKey, GuestImageResource>
            _guestImageVariants = new();
        private readonly Dictionary<long, GuestImageResource> _guestImageVersions = new();
        private readonly HashSet<long> _capturedGuestFlipVersions = [];
        private readonly record struct GuestDepthKey(
            ulong Address,
            ulong ReadAddress,
            uint Width,
            uint Height,
            uint GuestFormat,
            uint SwizzleMode);

        private readonly Dictionary<GuestDepthKey, GuestDepthResource> _guestDepthImages = new();
        private readonly Dictionary<GuestDepthKey, ulong> _depthOnlyColorAddresses = new();
        private ulong _nextDepthOnlyColorAddress = 0xFFFF_FF00_0000_0000UL;
        private readonly HashSet<(ulong Address, uint Width, uint Height, Format Format)> _tracedTextureCacheHits = new();
        private readonly HashSet<(ulong Address, uint Width, uint Height, uint DstSelect)> _tracedDepthTextureAliases = new();
        private readonly HashSet<(ulong Address, uint Width, uint Height)> _tracedDepthExtentFallbacks = new();
        private readonly HashSet<(ulong Address, uint Width, uint Height, Format Format)> _tracedTextureUploads = new();
        private readonly HashSet<(ulong Address, uint Width, uint Height, uint Format)> _dumpedTextures = new();
        private readonly HashSet<(ulong Address, uint Width, uint Height, uint Format)> _tracedTextureUploadContents = new();
        private readonly HashSet<(ulong Address, int Size)> _tracedGlobalBuffers = new();
        private readonly HashSet<(ulong Address, ulong Size)> _tracedGlobalWritebacks = new();
        private readonly HashSet<(ulong Shader, uint X, uint Y, uint Z, string Reason)>
            _rejectedComputeDispatches = new();
        private int _tracedSmallGlobalWritebackEvents;
        private int _tracedLargeGlobalWritebackEvents;
        private readonly HashSet<ulong> _tracedGuestImageContents = new();
        private readonly Dictionary<ulong, int> _tracedGuestWriteCounts = new();
        private readonly Dictionary<int, int> _pixelSpirvWriteCounts = new();
        private int _tracedVertexBufferCount;
        private bool _tracedTitleDraw;
        // Compute translation can produce an equivalent new byte array on a
        // later submit. Reference identity turns that into an expensive new
        // MoltenVK pipeline compilation every frame, so key the cache by the
        // program content and descriptor-layout shape instead.
        private readonly Dictionary<ComputePipelineKey, Pipeline> _computePipelines = new();
        private readonly Dictionary<GraphicsPipelineKey, Pipeline> _graphicsPipelines = new();
        private readonly Dictionary<GuestSampler, Sampler> _samplers = new();
        private readonly Dictionary<byte[], string> _shaderDigests =
            new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<DescriptorLayoutKey, DescriptorLayoutBundle>
            _descriptorLayouts = new();
        private readonly VulkanHostBufferPool _hostBufferPool;
        private readonly List<GuestBufferAllocation> _guestBufferAllocations = [];
        private readonly Queue<PendingGuestSubmission> _pendingGuestSubmissions = new();
        private readonly Dictionary<string, ulong> _lastSubmittedTimelineByGuestQueue =
            new(StringComparer.Ordinal);
        private readonly Stack<DescriptorPool> _recycledDescriptorPools = new();
        private VulkanGuestQueueIdentity _activeGuestQueue =
            VulkanGuestQueueIdentity.Default;
        private long _activeGuestWorkSequence;

        private readonly record struct GraphicsPipelineKey(
            string VertexShader,
            string FragmentShader,
            string RenderTargetLayout,
            bool HasDepthAttachment,
            PrimitiveTopology Topology,
            string BlendLayout,
            string ResourceLayout,
            string VertexLayout,
            GuestRasterState Raster,
            GuestDepthState Depth);

        private readonly record struct DescriptorLayoutKey(
            ShaderStageFlags Stages,
            string Resources);

        private readonly record struct ComputePipelineKey(
            string ShaderDigest,
            string Resources);

        private sealed record DescriptorLayoutBundle(
            DescriptorSetLayout DescriptorSetLayout,
            PipelineLayout PipelineLayout);

        private readonly record struct DirtyGuestBufferRange(ulong Offset, ulong Length);

        private sealed class GuestBufferAllocation
        {
            public string QueueName = VulkanGuestQueueIdentity.Default.Name;
            public ulong BaseAddress;
            public ulong Size;
            public VkBuffer Buffer;
            public DeviceMemory Memory;
            public nint Mapped;
            public byte[] Shadow = [];
            public ulong LastUseTimeline;
            public List<DirtyGuestBufferRange> DirtyRanges { get; } = [];
        }

        private const string SharedReadOnlyGuestBufferQueue = "shared.readonly";

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
            public GlobalBufferResource[] GlobalMemoryBuffers = [];
            public VertexBufferResource[] VertexBuffers = [];
            public VkBuffer IndexBuffer;
            public DeviceMemory IndexMemory;
            public bool Index32Bit;
            public uint VertexCount = 3;
            public uint InstanceCount = 1;
            public PrimitiveTopology Topology = PrimitiveTopology.TriangleList;
            public GuestBlendState[] Blends = [GuestBlendState.Default];
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

        private sealed class TextureResource
        {
            public ulong Address;
            public VkBuffer StagingBuffer;
            public DeviceMemory StagingMemory;
            public Image Image;
            public DeviceMemory ImageMemory;
            public ImageView View;
            public uint Width;
            public uint Height;
            public uint RowLength;
            public uint DstSelect;
            public bool NeedsUpload;
            public bool OwnsStorage;
            public bool IsStorage;
            public bool Cached;
            public ulong CpuContentFingerprint;
            public bool UpdatesCpuContent;
            public GuestSampler SamplerState;
            public Sampler Sampler;
            public GuestImageResource? GuestImage;
            public GuestDepthResource? GuestDepth;
            // A sampled render-target alias cannot remain bound to the same
            // image while that image is a color attachment. The per-draw
            // snapshot uses this source to copy the target's pre-draw contents
            // before the render pass begins. The snapshot itself is owned by
            // this TextureResource and retires with the draw fence.
            public GuestImageResource? FeedbackSource;
            public GuestDepthResource? DepthFeedbackSource;
        }

        private sealed class GlobalBufferResource
        {
            public ulong BaseAddress;
            public bool Writable;
            public bool WriteBackToGuest;
            public VkBuffer Buffer;
            public DeviceMemory Memory;
            public nint Mapped;
            // DescriptorOffset/Size include the shader-visible byte bias.
            public ulong Offset;
            public ulong Size;
            // GuestOffset/Size identify only the original guest resource and
            // are used for dirty writeback; descriptor padding must not be
            // published over unrelated guest bytes.
            public ulong GuestOffset;
            public ulong GuestSize;
            public GuestBufferAllocation? Allocation;
        }

        private sealed class VertexBufferResource
        {
            public VkBuffer Buffer;
            public DeviceMemory Memory;
            public bool OwnsBuffer;
            public ulong Size;
            public uint Location;
            public uint ComponentCount;
            public uint DataFormat;
            public uint NumberFormat;
            public uint Stride;
            public uint OffsetBytes;
        }

        private const Format DepthFormat = Format.D32Sfloat;

        private sealed class GuestDepthResource
        {
            public GuestDepthKey Key;
            public ulong Address;
            public ulong ReadAddress;
            public ulong WriteAddress;
            public uint Width;
            public uint Height;
            public uint GuestFormat;
            public uint SwizzleMode;
            public Image Image;
            public DeviceMemory Memory;
            public ImageView View;
            public Dictionary<uint, ImageView> SampleViews { get; } = new();
            public bool Initialized;
            public ImageLayout Layout = ImageLayout.Undefined;
            public float GuestClearDepth = 1f;
            public float ClearDepth = 1f;
            public string InitializationSource = "none";
        }

        private sealed class DepthFramebufferResource
        {
            public required GuestDepthResource Depth;
            public RenderPass LoadRenderPass;
            public RenderPass ColorClearRenderPass;
            public RenderPass DepthClearRenderPass;
            public RenderPass BothClearRenderPass;
            public Framebuffer Framebuffer;
        }

        private sealed class GuestImageResource
        {
            public ulong Address;
            public long FlipVersion;
            public uint Width;
            public uint Height;
            public uint MipLevels;
            public uint GuestFormat;
            public Format Format;
            public Image Image;
            public DeviceMemory Memory;
            public ImageView View;
            public ImageView[] MipViews = [];
            public Dictionary<(Format Format, uint MipLevel, uint LevelCount, uint DstSelect), ImageView> FormatViews { get; } = new();
            public RenderPass RenderPass;
            public RenderPass InitialRenderPass;
            public Framebuffer Framebuffer;
            public Dictionary<GuestDepthKey, DepthFramebufferResource> DepthFramebuffers { get; } = new();
            public bool Initialized;
            public bool InitialUploadPending;
            public bool IsCpuBacked;
            public ulong CpuContentFingerprint;
        }

        private sealed record PendingGuestSubmission(
            Fence Fence,
            CommandBuffer CommandBuffer,
            IReadOnlyList<TranslatedDrawResources> Resources,
            IReadOnlyList<GuestImageResource> TraceImages,
            IReadOnlyList<(VkBuffer Buffer, DeviceMemory Memory)> RetireBuffers,
            ulong Timeline,
            string DebugName,
            VulkanGuestQueueIdentity Queue,
            long WorkSequence);

        public Presenter(uint width, uint height)
        {
            _hostBufferPool = new VulkanHostBufferPool(
                MaximumCachedHostBufferBytes,
                DestroyHostBufferAllocation);
            var options = WindowOptions.DefaultVulkan;
            options.Size = new Vector2D<int>((int)DefaultWindowWidth, (int)DefaultWindowHeight);
            options.Title = VideoOutExports.GetWindowTitle();
            options.WindowBorder = WindowBorder.Fixed;
            options.VSync = true;
            options.FramesPerSecond = 60;
            options.UpdatesPerSecond = 60;
            _window = Window.Create(options);
            _window.Load += Initialize;
            _window.Render += Render;
            _window.Closing += () =>
            {
                Console.Error.WriteLine(
                    $"[LOADER][WARN] Vulkan VideoOut window closing; " +
                    $"requested={Volatile.Read(ref _presenterCloseRequested)} " +
                    $"deviceLost={_deviceLost}");
                VideoOutExports.NotifyPresentationWindowClosed();
                DisposeVulkan();
            };
        }

        public void Run() => _window.Run();

        public void Dispose()
        {
            DisposeVulkan();
            try
            {
                _window.Dispose();
            }
            catch (InvalidOperationException exception)
                when (exception.Message.Contains("render loop", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine(
                    $"[LOADER][WARN] Vulkan VideoOut window dispose skipped during render loop: {exception.Message}");
            }
        }

        private void Initialize()
        {
            LogGlfwPlatformInUse();
            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    Pad.HostWindowInput.Attach(_window.CreateInput());
                    Console.Error.WriteLine("[LOADER][INFO] Window keyboard input attached for pad emulation.");
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine($"[LOADER][WARN] Window keyboard input unavailable: {exception.Message}");
                }
            }

            if (PngSplashLoader.TryLoadIcon(out var iconPixels, out var iconWidth, out var iconHeight))
            {
                var icon = new RawImage((int)iconWidth, (int)iconHeight, iconPixels);
                _window.SetWindowIcon(ref icon);
            }

            WaitForRenderDocAttachIfRequested();
            _vk = Vk.GetApi();
            CreateInstance();
            CreateSurface();
            SelectPhysicalDevice();
            CreateDevice();
            CreatePipelineCache();
            CreateSwapchain();
            CreateCommandResources();
            CreateGuestDrawResources();
            _vulkanReady = true;
            Console.Error.WriteLine(
                $"[LOADER][INFO] Vulkan VideoOut ready: {_extent.Width}x{_extent.Height}, format={_swapchainFormat}");
        }

        private static void WaitForRenderDocAttachIfRequested()
        {
            var value = Environment.GetEnvironmentVariable("SHARPEMU_RENDERDOC_WAIT");
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            if (string.Equals(value, "enter", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine(
                    $"[LOADER][INFO] Waiting for RenderDoc attach before Vulkan init. pid={Environment.ProcessId}. Press Enter to continue.");
                _ = Console.ReadLine();
                return;
            }

            var seconds = 15;
            if (int.TryParse(value, out var parsedSeconds))
            {
                seconds = Math.Clamp(parsedSeconds, 1, 300);
            }

            Console.Error.WriteLine(
                $"[LOADER][INFO] Waiting {seconds}s for RenderDoc attach before Vulkan init. pid={Environment.ProcessId}");
            Thread.Sleep(TimeSpan.FromSeconds(seconds));
        }

        private bool IsInstanceExtensionAvailable(string extensionName)
        {
            uint extensionCount = 0;
            if (_vk.EnumerateInstanceExtensionProperties((byte*)null, &extensionCount, null) != Result.Success ||
                extensionCount == 0)
            {
                return false;
            }

            var properties = new ExtensionProperties[extensionCount];
            fixed (ExtensionProperties* propertyPointer = properties)
            {
                if (_vk.EnumerateInstanceExtensionProperties(
                        (byte*)null,
                        &extensionCount,
                        propertyPointer) != Result.Success)
                {
                    return false;
                }

                var expected = Encoding.UTF8.GetBytes(extensionName);
                for (var index = 0; index < extensionCount; index++)
                {
                    if (Utf8NullTerminatedEquals(propertyPointer[index].ExtensionName, expected))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool Utf8NullTerminatedEquals(byte* actual, ReadOnlySpan<byte> expected)
        {
            for (var index = 0; index < expected.Length; index++)
            {
                if (actual[index] != expected[index])
                {
                    return false;
                }
            }

            return actual[expected.Length] == 0;
        }

        private void LoadDebugUtilsCommands()
        {
            if (!_vulkanDebugUtilsEnabled)
            {
                return;
            }

            var setObjectName = _vk.GetDeviceProcAddr(_device, "vkSetDebugUtilsObjectNameEXT");
            var beginLabel = _vk.GetDeviceProcAddr(_device, "vkCmdBeginDebugUtilsLabelEXT");
            var endLabel = _vk.GetDeviceProcAddr(_device, "vkCmdEndDebugUtilsLabelEXT");
            _setDebugUtilsObjectName =
                (delegate* unmanaged<Device, DebugUtilsObjectNameInfoEXT*, Result>)
                setObjectName.Handle;
            _cmdBeginDebugUtilsLabel =
                (delegate* unmanaged<CommandBuffer, DebugUtilsLabelEXT*, void>)
                beginLabel.Handle;
            _cmdEndDebugUtilsLabel =
                (delegate* unmanaged<CommandBuffer, void>)
                endLabel.Handle;

            if (_setDebugUtilsObjectName is not null)
            {
                Console.Error.WriteLine("[LOADER][INFO] Vulkan debug labels enabled.");
            }
        }

        private void SetDebugName(ObjectType objectType, ulong objectHandle, string name)
        {
            if (_setDebugUtilsObjectName is null ||
                _device.Handle == 0 ||
                objectHandle == 0)
            {
                return;
            }

            var bytes = NullTerminatedUtf8(name);
            fixed (byte* namePointer = bytes)
            {
                var info = new DebugUtilsObjectNameInfoEXT
                {
                    SType = StructureType.DebugUtilsObjectNameInfoExt,
                    ObjectType = objectType,
                    ObjectHandle = objectHandle,
                    PObjectName = namePointer,
                };
                _ = _setDebugUtilsObjectName(_device, &info);
            }
        }

        private void BeginDebugLabel(CommandBuffer commandBuffer, string name)
        {
            if (_cmdBeginDebugUtilsLabel is null ||
                commandBuffer.Handle == 0)
            {
                return;
            }

            var bytes = NullTerminatedUtf8(name);
            fixed (byte* namePointer = bytes)
            {
                var label = new DebugUtilsLabelEXT
                {
                    SType = StructureType.DebugUtilsLabelExt,
                    PLabelName = namePointer,
                };
                label.Color[0] = 0.20f;
                label.Color[1] = 0.60f;
                label.Color[2] = 1.00f;
                label.Color[3] = 1.00f;
                _cmdBeginDebugUtilsLabel(commandBuffer, &label);
            }
        }

        private void EndDebugLabel(CommandBuffer commandBuffer)
        {
            if (_cmdEndDebugUtilsLabel is not null &&
                commandBuffer.Handle != 0)
            {
                _cmdEndDebugUtilsLabel(commandBuffer);
            }
        }

        private static byte[] NullTerminatedUtf8(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            Array.Resize(ref bytes, bytes.Length + 1);
            return bytes;
        }

        private static string BuildComputeDebugName(VulkanComputeGuestDispatch dispatch)
        {
            var storage = dispatch.Textures.FirstOrDefault(texture => texture.IsStorage && texture.Address != 0);
            return storage is null
                ? $"SharpEmu compute cs=0x{dispatch.ShaderAddress:X16} " +
                  $"{dispatch.GroupCountX}x{dispatch.GroupCountY}x{dispatch.GroupCountZ}"
                : $"SharpEmu compute cs=0x{dispatch.ShaderAddress:X16} " +
                  $"storage=0x{storage.Address:X16} " +
                  $"{storage.Width}x{storage.Height} fmt{storage.Format} " +
                  $"{dispatch.GroupCountX}x{dispatch.GroupCountY}x{dispatch.GroupCountZ}";
        }

        private static string GuestImageDebugName(GuestRenderTarget target, Format format) =>
            $"SharpEmu guest 0x{target.Address:X16} {target.Width}x{target.Height} " +
            $"fmt{target.Format}/{format}";

        private static string TextureDebugName(GuestDrawTexture texture, Format format) =>
            $"SharpEmu texture 0x{texture.Address:X16} {texture.Width}x{texture.Height} " +
            $"fmt{texture.Format}/{format}";

        private static bool IsTitleDraw(IReadOnlyList<GuestVertexBuffer> vertexBuffers)
        {
            foreach (var buffer in vertexBuffers)
            {
                if (buffer.Location == 0 &&
                    buffer.ComponentCount == 4 &&
                    buffer.DataFormat == 10 &&
                    buffer.NumberFormat == 0 &&
                    buffer.Stride == 16 &&
                    buffer.OffsetBytes == 12 &&
                    buffer.Length == 67568)
                {
                    return true;
                }
            }

            return false;
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

        private void CreateInstance()
        {
            var applicationName = (byte*)SilkMarshal.StringToPtr("SharpEmu");
            byte* validationLayerName = null;

            try
            {
                var applicationInfo = new ApplicationInfo
                {
                    SType = StructureType.ApplicationInfo,
                    PApplicationName = applicationName,
                    ApplicationVersion = Vk.MakeVersion(0, 0, 1),
                    PEngineName = applicationName,
                    EngineVersion = Vk.MakeVersion(0, 0, 1),
                    ApiVersion = Vk.Version12,
                };

                var extensions = _window.VkSurface!.GetRequiredExtensions(out var extensionCount);
                byte* debugUtilsExtension = null;
                byte* portabilityExtension = null;
                var instanceCreateFlags = InstanceCreateFlags.None;
                var enabledExtensionCount = (int)extensionCount;
                var enabledExtensions = stackalloc byte*[(int)extensionCount + 2];
                for (var index = 0; index < (int)extensionCount; index++)
                {
                    enabledExtensions[index] = extensions[index];
                }

                if (_vulkanDebugUtilsEnabled &&
                    IsInstanceExtensionAvailable(DebugUtilsExtensionName))
                {
                    debugUtilsExtension = (byte*)SilkMarshal.StringToPtr(DebugUtilsExtensionName);
                    enabledExtensions[enabledExtensionCount++] = debugUtilsExtension;
                }

                if (IsInstanceExtensionAvailable(PortabilityEnumerationExtensionName))
                {
                    // MoltenVK is a portability (non-conformant) implementation;
                    // without this flag + extension the loader hides it.
                    portabilityExtension = (byte*)SilkMarshal.StringToPtr(PortabilityEnumerationExtensionName);
                    enabledExtensions[enabledExtensionCount++] = portabilityExtension;
                    instanceCreateFlags |= InstanceCreateFlags.EnumeratePortabilityBitKhr;
                }

                if (_vulkanValidationEnabled &&
                    IsInstanceLayerAvailable("VK_LAYER_KHRONOS_validation"))
                {
                    validationLayerName = (byte*)SilkMarshal.StringToPtr("VK_LAYER_KHRONOS_validation");
                }
                else if (_vulkanValidationEnabled)
                {
                    Console.Error.WriteLine("[LOADER][WARN] SHARPEMU_VK_VALIDATION=1 but VK_LAYER_KHRONOS_validation not found (Vulkan SDK installed?).");
                }

                var layers = stackalloc byte*[1];
                if (validationLayerName is not null)
                {
                    layers[0] = validationLayerName;
                }

                var createInfo = new InstanceCreateInfo
                {
                    SType = StructureType.InstanceCreateInfo,
                    Flags = instanceCreateFlags,
                    PApplicationInfo = &applicationInfo,
                    EnabledExtensionCount = (uint)enabledExtensionCount,
                    PpEnabledExtensionNames = enabledExtensions,
                    EnabledLayerCount = validationLayerName is not null ? 1u : 0u,
                    PpEnabledLayerNames = validationLayerName is not null ? layers : null,
                };

                try
                {
                    Check(_vk.CreateInstance(&createInfo, null, out _instance), "vkCreateInstance");
                    if (!_vk.TryGetInstanceExtension(_instance, out _surfaceApi))
                    {
                        throw new InvalidOperationException("VK_KHR_surface is unavailable.");
                    }

                    if (validationLayerName is not null && _vk.TryGetInstanceExtension(_instance, out ExtDebugUtils debugUtils))
                    {
                        _debugUtils = debugUtils;
                        RegisterDebugMessenger(debugUtils);
                        Console.Error.WriteLine("[LOADER][INFO] Vulkan Validation Layers active (SHARPEMU_VK_VALIDATION=1).");
                    }
                }
                finally
                {
                    if (debugUtilsExtension is not null)
                    {
                        SilkMarshal.Free((nint)debugUtilsExtension);
                    }
                    if (portabilityExtension is not null)
                    {
                        SilkMarshal.Free((nint)portabilityExtension);
                    }
                }
            }
            finally
            {
                SilkMarshal.Free((nint)applicationName);
                if (validationLayerName is not null)
                {
                    SilkMarshal.Free((nint)validationLayerName);
                }
            }
        }

        private bool IsDeviceExtensionAvailable(string extensionName)
        {
            uint extensionCount = 0;
            if (_vk.EnumerateDeviceExtensionProperties(_physicalDevice, (byte*)null, &extensionCount, null) != Result.Success ||
                extensionCount == 0)
            {
                return false;
            }

            var properties = new ExtensionProperties[extensionCount];
            fixed (ExtensionProperties* propertyPointer = properties)
            {
                if (_vk.EnumerateDeviceExtensionProperties(
                        _physicalDevice,
                        (byte*)null,
                        &extensionCount,
                        propertyPointer) != Result.Success)
                {
                    return false;
                }

                var expected = Encoding.UTF8.GetBytes(extensionName);
                for (var index = 0; index < extensionCount; index++)
                {
                    if (Utf8NullTerminatedEquals(propertyPointer[index].ExtensionName, expected))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private bool IsInstanceLayerAvailable(string layerName)
        {
            uint layerCount = 0;
            if (_vk.EnumerateInstanceLayerProperties(&layerCount, null) != Result.Success || layerCount == 0)
            {
                return false;
            }

            var properties = new LayerProperties[layerCount];
            fixed (LayerProperties* propertyPointer = properties)
            {
                if (_vk.EnumerateInstanceLayerProperties(&layerCount, propertyPointer) != Result.Success)
                {
                    return false;
                }

                var expected = Encoding.UTF8.GetBytes(layerName);
                for (var index = 0; index < layerCount; index++)
                {
                    if (Utf8NullTerminatedEquals(propertyPointer[index].LayerName, expected))
                    {
                        return true;
                    }
                }
            }

            return false;
        }
        private void RegisterDebugMessenger(ExtDebugUtils debugUtils)
        {
            var messengerInfo = new DebugUtilsMessengerCreateInfoEXT
            {
                SType = StructureType.DebugUtilsMessengerCreateInfoExt,
                MessageSeverity = DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt
                                  | DebugUtilsMessageSeverityFlagsEXT.WarningBitExt,
                MessageType = DebugUtilsMessageTypeFlagsEXT.ValidationBitExt
                              | DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt
                              | DebugUtilsMessageTypeFlagsEXT.GeneralBitExt,
                PfnUserCallback = new PfnDebugUtilsMessengerCallbackEXT(DebugCallback),
            };

            Check(debugUtils.CreateDebugUtilsMessenger(_instance, &messengerInfo, null, out _debugMessenger),
                "vkCreateDebugUtilsMessengerEXT");
        }

        private static unsafe uint DebugCallback(
            DebugUtilsMessageSeverityFlagsEXT severity,
            DebugUtilsMessageTypeFlagsEXT type,
            DebugUtilsMessengerCallbackDataEXT* callbackData,
            void* userData)
        {
            var message = SilkMarshal.PtrToString((nint)callbackData->PMessage);
            var prefix = severity switch
            {
                DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt => "[VULKAN][ERROR]",
                DebugUtilsMessageSeverityFlagsEXT.WarningBitExt => "[VULKAN][WARN]",
                _ => "[VULKAN][INFO]",
            };
            Console.Error.WriteLine($"{prefix} {message}");
            return Vk.False;
        }
        private void CreateSurface()
        {
            var instanceHandle = new VkHandle(_instance.Handle);
            var surfaceHandle = _window.VkSurface!.Create<AllocationCallbacks>(instanceHandle, null);
            _surface = new SurfaceKHR(surfaceHandle.Handle);
        }

        private void SelectPhysicalDevice()
        {
            uint deviceCount = 0;
            Check(_vk.EnumeratePhysicalDevices(_instance, &deviceCount, null), "vkEnumeratePhysicalDevices");
            if (deviceCount == 0)
            {
                throw new InvalidOperationException("No Vulkan physical device was found.");
            }

            var devices = new PhysicalDevice[deviceCount];
            fixed (PhysicalDevice* devicePointer = devices)
            {
                Check(_vk.EnumeratePhysicalDevices(_instance, &deviceCount, devicePointer), "vkEnumeratePhysicalDevices");
            }

            var deviceOverride = Environment.GetEnvironmentVariable("SHARPEMU_VK_DEVICE");
            var bestScore = int.MinValue;
            var found = false;
            foreach (var device in devices)
            {
                uint queueCount = 0;
                _vk.GetPhysicalDeviceQueueFamilyProperties(device, &queueCount, null);
                var queues = new QueueFamilyProperties[queueCount];
                fixed (QueueFamilyProperties* queuePointer = queues)
                {
                    _vk.GetPhysicalDeviceQueueFamilyProperties(device, &queueCount, queuePointer);
                }

                for (uint index = 0; index < queueCount; index++)
                {
                    var supportsGraphics = (queues[index].QueueFlags & QueueFlags.GraphicsBit) != 0;
                    _surfaceApi.GetPhysicalDeviceSurfaceSupport(device, index, _surface, out var supportsPresent);
                    if (!supportsGraphics || !supportsPresent)
                    {
                        continue;
                    }

                    _vk.GetPhysicalDeviceProperties(device, out var properties);
                    var name = SilkMarshal.PtrToString((nint)properties.DeviceName) ?? string.Empty;
                    var score = ScorePhysicalDevice(properties, name, deviceOverride);
                    Console.Error.WriteLine(
                        $"[LOADER][INFO] Vulkan candidate: {name} ({properties.DeviceType}) score={score}");
                    if (score > bestScore)
                    {
                        bestScore = score;
                        _physicalDevice = device;
                        _queueFamilyIndex = index;
                        found = true;
                    }

                    break;
                }
            }

            if (!found)
            {
                throw new InvalidOperationException("No Vulkan graphics/present queue was found.");
            }

            LoadComputeDeviceLimits();
            _vk.GetPhysicalDeviceProperties(_physicalDevice, out var selected);
            _maxColorAttachments = selected.Limits.MaxColorAttachments;
            var selectedName = SilkMarshal.PtrToString((nint)selected.DeviceName) ?? "unknown";
            Console.Error.WriteLine(
                $"[LOADER][INFO] Vulkan device: {selectedName} ({selected.DeviceType})");
            VideoOutExports.SetSelectedGpuName(selectedName);
            _window.Title = VideoOutExports.GetWindowTitle();
        }

        private static int ScorePhysicalDevice(
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
                PhysicalDeviceType.Cpu => 50,
                PhysicalDeviceType.IntegratedGpu => -100,
                _ => 10,
            };

            if (properties.VendorID == NvidiaVendorId)
            {
                score += 500;
            }

            return score;
        }

        private void LoadComputeDeviceLimits()
        {
            _vk.GetPhysicalDeviceProperties(_physicalDevice, out var properties);
            var subgroupSizeControl = new PhysicalDeviceSubgroupSizeControlProperties
            {
                SType = StructureType.PhysicalDeviceSubgroupSizeControlProperties,
            };
            var subgroup = new PhysicalDeviceSubgroupProperties
            {
                SType = StructureType.PhysicalDeviceSubgroupProperties,
                PNext = &subgroupSizeControl,
            };
            var properties2 = new PhysicalDeviceProperties2
            {
                SType = StructureType.PhysicalDeviceProperties2,
                PNext = &subgroup,
            };
            _vk.GetPhysicalDeviceProperties2(_physicalDevice, &properties2);
            _maxComputeWorkGroupCountX = properties.Limits.MaxComputeWorkGroupCount[0];
            _maxComputeWorkGroupCountY = properties.Limits.MaxComputeWorkGroupCount[1];
            _maxComputeWorkGroupCountZ = properties.Limits.MaxComputeWorkGroupCount[2];
            _maxComputeWorkGroupSizeX = properties.Limits.MaxComputeWorkGroupSize[0];
            _maxComputeWorkGroupSizeY = properties.Limits.MaxComputeWorkGroupSize[1];
            _maxComputeWorkGroupSizeZ = properties.Limits.MaxComputeWorkGroupSize[2];
            _maxComputeWorkGroupInvocations = properties.Limits.MaxComputeWorkGroupInvocations;
            _minStorageBufferOffsetAlignment = Math.Max(
                properties.Limits.MinStorageBufferOffsetAlignment,
                1UL);
            if (GuestStorageBufferOffsetAlignment %
                _minStorageBufferOffsetAlignment != 0)
            {
                throw new InvalidOperationException(
                    $"Vulkan storage-buffer alignment " +
                    $"{_minStorageBufferOffsetAlignment} is not compatible with " +
                    $"the portable alias alignment " +
                    $"{GuestStorageBufferOffsetAlignment}");
            }
            Console.Error.WriteLine(
                $"[LOADER][INFO] Vulkan compute limits groups=" +
                $"{_maxComputeWorkGroupCountX}x{_maxComputeWorkGroupCountY}x{_maxComputeWorkGroupCountZ} " +
                $"local={_maxComputeWorkGroupSizeX}x{_maxComputeWorkGroupSizeY}x" +
                $"{_maxComputeWorkGroupSizeZ} invocations={_maxComputeWorkGroupInvocations} " +
                $"storage_alignment={_minStorageBufferOffsetAlignment}");
            Console.Error.WriteLine(
                $"[LOADER][INFO] Vulkan subgroup default={subgroup.SubgroupSize} " +
                $"stages={subgroup.SupportedStages} ops={subgroup.SupportedOperations} " +
                $"size_control={subgroupSizeControl.MinSubgroupSize}-" +
                $"{subgroupSizeControl.MaxSubgroupSize} " +
                $"required_stages={subgroupSizeControl.RequiredSubgroupSizeStages} " +
                $"max_compute_subgroups=" +
                $"{subgroupSizeControl.MaxComputeWorkgroupSubgroups}");
        }

        private void CreateDevice()
        {
            var priority = 1.0f;
            var queueInfo = new DeviceQueueCreateInfo
            {
                SType = StructureType.DeviceQueueCreateInfo,
                QueueFamilyIndex = _queueFamilyIndex,
                QueueCount = 1,
                PQueuePriorities = &priority,
            };
            _vk.GetPhysicalDeviceFeatures(_physicalDevice, out var supportedFeatures);
            _supportsIndependentBlend = supportedFeatures.IndependentBlend;
            var enabledFeatures = new PhysicalDeviceFeatures
            {
                IndependentBlend = supportedFeatures.IndependentBlend,
                VertexPipelineStoresAndAtomics = supportedFeatures.VertexPipelineStoresAndAtomics,
                FragmentStoresAndAtomics = supportedFeatures.FragmentStoresAndAtomics,
                ShaderInt64 = supportedFeatures.ShaderInt64,
                ShaderImageGatherExtended = supportedFeatures.ShaderImageGatherExtended,
                ShaderStorageImageExtendedFormats = supportedFeatures.ShaderStorageImageExtendedFormats,
                ShaderStorageImageReadWithoutFormat = supportedFeatures.ShaderStorageImageReadWithoutFormat,
                ShaderStorageImageWriteWithoutFormat = supportedFeatures.ShaderStorageImageWriteWithoutFormat,
                RobustBufferAccess = supportedFeatures.RobustBufferAccess,
            };

            if (!supportedFeatures.RobustBufferAccess)
            {
                Console.Error.WriteLine(
                    "[LOADER][WARN] GPU does not support robustBufferAccess " +
                    "translated shaders performing out-of-bounds buffer access may cause device loss.");
            }

            if (!supportedFeatures.ShaderInt64)
            {
                Console.Error.WriteLine(
                    "[LOADER][WARN] GPU does not support shaderInt64 " +
                    "translated shaders using 64-bit integers will fail.");
            }

            if (!supportedFeatures.VertexPipelineStoresAndAtomics || !supportedFeatures.FragmentStoresAndAtomics)
            {
                Console.Error.WriteLine(
                    "[LOADER][WARN] GPU does not support vertexPipelineStoresAndAtomics/fragmentStoresAndAtomics " +
                    "translated shaders using storage buffers in vertex/fragment stages may fail.");
            }

            if (!supportedFeatures.ShaderImageGatherExtended)
            {
                Console.Error.WriteLine(
                    "[LOADER][WARN] GPU does not support shaderImageGatherExtended " +
                    "translated shaders using image gather with offsets/LOD/bias will fail.");
            }

            if (!supportedFeatures.ShaderStorageImageReadWithoutFormat ||
                !supportedFeatures.ShaderStorageImageWriteWithoutFormat)
            {
                Console.Error.WriteLine(
                    "[LOADER][WARN] GPU does not support shaderStorageImage(Read|Write)WithoutFormat " +
                    "translated shaders using unformatted storage image load/store will fail.");
            }

            var maintenance8Features = new PhysicalDeviceMaintenance8FeaturesKHR
            {
                SType = StructureType.PhysicalDeviceMaintenance8FeaturesKhr,
            };
            var robustness2Features = new PhysicalDeviceRobustness2FeaturesEXT
            {
                SType = StructureType.PhysicalDeviceRobustness2FeaturesExt,
                PNext = &maintenance8Features,
            };
            var featuresQuery = new PhysicalDeviceFeatures2
            {
                SType = StructureType.PhysicalDeviceFeatures2,
                PNext = &robustness2Features,
            };
            _vk.GetPhysicalDeviceFeatures2(_physicalDevice, &featuresQuery);
            var supportsMaintenance8 = maintenance8Features.Maintenance8;
            var supportsRobustBufferAccess2 = robustness2Features.RobustBufferAccess2;
            var supportsRobustImageAccess2 = robustness2Features.RobustImageAccess2;
            var supportsNullDescriptor = robustness2Features.NullDescriptor;
            var supportsRobustness2 = supportsRobustImageAccess2 || supportsNullDescriptor;
            if (!supportsMaintenance8)
            {
                Console.Error.WriteLine(
                    "[LOADER][WARN] GPU does not support VK_KHR_maintenance8 " +
                    "translated shaders using a dynamic texel offset on non-gather image samples will fail.");
            }

            if (!supportsRobustImageAccess2)
            {
                Console.Error.WriteLine(
                    "[LOADER][WARN] GPU does not support VK_EXT_robustness2 robustImageAccess2 " +
                    "translated shaders performing out-of-bounds image access may cause device loss.");
            }

            var swapchainExtension = (byte*)SilkMarshal.StringToPtr("VK_KHR_swapchain");
            var maintenance8Extension = (byte*)SilkMarshal.StringToPtr("VK_KHR_maintenance8");
            var robustness2Extension = (byte*)SilkMarshal.StringToPtr("VK_EXT_robustness2");
            var portabilitySubsetExtension = (byte*)SilkMarshal.StringToPtr(PortabilitySubsetExtensionName);
            try
            {
                var extensions = stackalloc byte*[4];
                var extensionCount = 0u;
                extensions[extensionCount++] = swapchainExtension;
                if (supportsMaintenance8)
                {
                    extensions[extensionCount++] = maintenance8Extension;
                }

                if (supportsRobustness2)
                {
                    extensions[extensionCount++] = robustness2Extension;
                }

                if (IsDeviceExtensionAvailable(PortabilitySubsetExtensionName))
                {
                    // The spec requires enabling this when the (MoltenVK)
                    // device advertises it.
                    extensions[extensionCount++] = portabilitySubsetExtension;
                }

                maintenance8Features.Maintenance8 = supportsMaintenance8;
                maintenance8Features.PNext = null;
                robustness2Features.RobustBufferAccess2 =
                    supportsRobustBufferAccess2 && supportedFeatures.RobustBufferAccess;
                robustness2Features.RobustImageAccess2 = supportsRobustImageAccess2;
                robustness2Features.NullDescriptor = supportsNullDescriptor;
                robustness2Features.PNext = supportsMaintenance8 ? &maintenance8Features : null;
                var features2 = new PhysicalDeviceFeatures2
                {
                    SType = StructureType.PhysicalDeviceFeatures2,
                    PNext = supportsRobustness2
                        ? &robustness2Features
                        : (supportsMaintenance8 ? &maintenance8Features : null),
                    Features = enabledFeatures,
                };
                var createInfo = new DeviceCreateInfo
                {
                    SType = StructureType.DeviceCreateInfo,
                    PNext = &features2,
                    QueueCreateInfoCount = 1,
                    PQueueCreateInfos = &queueInfo,
                    EnabledExtensionCount = extensionCount,
                    PpEnabledExtensionNames = extensions,
                };

                Check(_vk.CreateDevice(_physicalDevice, &createInfo, null, out _device), "vkCreateDevice");
            }
            finally
            {
                SilkMarshal.Free((nint)swapchainExtension);
                SilkMarshal.Free((nint)maintenance8Extension);
                SilkMarshal.Free((nint)robustness2Extension);
                SilkMarshal.Free((nint)portabilitySubsetExtension);
            }

            _vk.GetDeviceQueue(_device, _queueFamilyIndex, 0, out _queue);
            LoadDebugUtilsCommands();
            if (!_vk.TryGetDeviceExtension(_instance, _device, out _swapchainApi))
            {
                throw new InvalidOperationException("VK_KHR_swapchain is unavailable.");
            }
        }

        private void CreatePipelineCache()
        {
            var cacheMode = Environment.GetEnvironmentVariable("SHARPEMU_VK_PIPELINE_CACHE");
            // Vulkan cache blobs carry the implementation's compatibility
            // header and are rejected/rebuilt below when the device or driver
            // changes. MoltenVK compilation of a large translated shader can
            // take ten seconds, so discarding a valid cache at every launch is
            // much more harmful than using Vulkan's normal persistence path.
            // Keep an explicit opt-out for diagnostics and read-only systems.
            var persistentCacheEnabled =
                !string.Equals(cacheMode, "0", StringComparison.Ordinal);
            _pipelineCachePath = persistentCacheEnabled ? GetPipelineCachePath() : null;
            byte[] initialData = [];
            try
            {
                if (_pipelineCachePath is not null && File.Exists(_pipelineCachePath))
                {
                    initialData = File.ReadAllBytes(_pipelineCachePath);
                }
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(
                    $"[LOADER][WARN] Vulkan pipeline cache read failed: {exception.Message}");
            }

            var result = TryCreatePipelineCache(initialData, out _pipelineCache);
            if (result != Result.Success && initialData.Length != 0)
            {
                Console.Error.WriteLine(
                    $"[LOADER][WARN] Vulkan pipeline cache rejected ({result}); rebuilding it.");
                result = TryCreatePipelineCache([], out _pipelineCache);
            }

            if (result != Result.Success)
            {
                _pipelineCache = default;
                _pipelineCachePath = null;
                Console.Error.WriteLine(
                    $"[LOADER][WARN] Vulkan pipeline cache unavailable: {result}");
                return;
            }

            SetDebugName(
                ObjectType.PipelineCache,
                _pipelineCache.Handle,
                _pipelineCachePath is null
                    ? "SharpEmu in-memory pipeline cache"
                    : "SharpEmu persistent pipeline cache");
            _lastPipelineCacheSaveTick = Environment.TickCount64;
            if (_pipelineCachePath is null)
            {
                Console.Error.WriteLine(
                    "[LOADER][INFO] Vulkan pipeline cache ready: memory-only " +
                    "(persistence disabled with SHARPEMU_VK_PIPELINE_CACHE=0).");
            }
            else
            {
                Console.Error.WriteLine(
                    $"[LOADER][INFO] Vulkan pipeline cache ready: path={_pipelineCachePath} initial={initialData.Length} bytes");
            }
        }

        private Result TryCreatePipelineCache(byte[] initialData, out PipelineCache pipelineCache)
        {
            fixed (byte* initialDataPointer = initialData)
            {
                var createInfo = new PipelineCacheCreateInfo
                {
                    SType = StructureType.PipelineCacheCreateInfo,
                    InitialDataSize = (nuint)initialData.Length,
                    PInitialData = initialData.Length == 0 ? null : initialDataPointer,
                };
                return _vk.CreatePipelineCache(
                    _device,
                    &createInfo,
                    null,
                    out pipelineCache);
            }
        }

        private static string GetPipelineCachePath()
        {
            var configured = Environment.GetEnvironmentVariable("SHARPEMU_VK_PIPELINE_CACHE_PATH");
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return Path.GetFullPath(
                    Environment.ExpandEnvironmentVariables(configured));
            }

            var root = OperatingSystem.IsMacOS()
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Library",
                    "Caches")
                : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(root, "SharpEmu", "vulkan-pipeline-cache.bin");
        }

        private void MarkPipelineCacheDirty()
        {
            if (_pipelineCache.Handle == 0)
            {
                return;
            }

            _pipelineCacheDirty = true;
            // Exporting MoltenVK's cache can itself serialize the compiler.
            // Gameplay may discover dozens of expensive pipelines in one
            // frame, so saving after every slow creation compounds a warm-up
            // hitch into a multi-minute stall. Coalesce all creations into one
            // periodic snapshot; shutdown still forces a final save.
            if (Environment.TickCount64 - _lastPipelineCacheSaveTick >= 30_000)
            {
                SavePipelineCache(force: false);
            }
        }

        private void SavePipelineCache(bool force)
        {
            if (_pipelineCache.Handle == 0 || string.IsNullOrWhiteSpace(_pipelineCachePath))
            {
                return;
            }

            if (!force && !_pipelineCacheDirty)
            {
                return;
            }

            try
            {
                nuint size = 0;
                var result = _vk.GetPipelineCacheData(
                    _device,
                    _pipelineCache,
                    &size,
                    null);
                if (result != Result.Success || size == 0 || size > 256u * 1024u * 1024u)
                {
                    Console.Error.WriteLine(
                        $"[LOADER][WARN] Vulkan pipeline cache query failed: result={result} size={size}");
                    return;
                }

                var data = new byte[checked((int)size)];
                fixed (byte* dataPointer = data)
                {
                    result = _vk.GetPipelineCacheData(
                        _device,
                        _pipelineCache,
                        &size,
                        dataPointer);
                }

                if (result != Result.Success)
                {
                    Console.Error.WriteLine(
                        $"[LOADER][WARN] Vulkan pipeline cache export failed: {result}");
                    return;
                }

                if (size != (nuint)data.Length)
                {
                    Array.Resize(ref data, checked((int)size));
                }

                var directory = Path.GetDirectoryName(_pipelineCachePath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var temporaryPath = _pipelineCachePath + $".{Environment.ProcessId}.tmp";
                File.WriteAllBytes(temporaryPath, data);
                File.Move(temporaryPath, _pipelineCachePath, overwrite: true);
                _pipelineCacheDirty = false;
                _lastPipelineCacheSaveTick = Environment.TickCount64;
                Console.Error.WriteLine(
                    $"[LOADER][INFO] Vulkan pipeline cache saved: path={_pipelineCachePath} bytes={data.Length}");
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(
                    $"[LOADER][WARN] Vulkan pipeline cache save failed: {exception.Message}");
            }
        }

        private void CreateSwapchain()
        {
            Check(
                _surfaceApi.GetPhysicalDeviceSurfaceCapabilities(_physicalDevice, _surface, out var capabilities),
                "vkGetPhysicalDeviceSurfaceCapabilitiesKHR");

            uint formatCount = 0;
            Check(
                _surfaceApi.GetPhysicalDeviceSurfaceFormats(_physicalDevice, _surface, &formatCount, null),
                "vkGetPhysicalDeviceSurfaceFormatsKHR");
            var formats = new SurfaceFormatKHR[formatCount];
            fixed (SurfaceFormatKHR* formatPointer = formats)
            {
                Check(
                    _surfaceApi.GetPhysicalDeviceSurfaceFormats(_physicalDevice, _surface, &formatCount, formatPointer),
                    "vkGetPhysicalDeviceSurfaceFormatsKHR");
            }

            var surfaceFormat = ChooseSurfaceFormat(formats);
            _swapchainFormat = surfaceFormat.Format;
            _extent = ChooseExtent(capabilities);
            var presentMode = ChoosePresentMode();
            var imageCount = capabilities.MinImageCount + 1;
            if (capabilities.MaxImageCount != 0)
            {
                imageCount = Math.Min(imageCount, capabilities.MaxImageCount);
            }

            var compositeAlpha = ChooseCompositeAlpha(capabilities.SupportedCompositeAlpha);
            var createInfo = new SwapchainCreateInfoKHR
            {
                SType = StructureType.SwapchainCreateInfoKhr,
                Surface = _surface,
                MinImageCount = imageCount,
                ImageFormat = surfaceFormat.Format,
                ImageColorSpace = surfaceFormat.ColorSpace,
                ImageExtent = _extent,
                ImageArrayLayers = 1,
                ImageUsage =
                    ImageUsageFlags.TransferDstBit |
                    ImageUsageFlags.TransferSrcBit |
                    ImageUsageFlags.ColorAttachmentBit,
                ImageSharingMode = SharingMode.Exclusive,
                PreTransform = capabilities.CurrentTransform,
                CompositeAlpha = compositeAlpha,
                PresentMode = presentMode,
                Clipped = true,
            };

            Check(_swapchainApi.CreateSwapchain(_device, &createInfo, null, out _swapchain), "vkCreateSwapchainKHR");

            uint swapchainImageCount = 0;
            Check(
                _swapchainApi.GetSwapchainImages(_device, _swapchain, &swapchainImageCount, null),
                "vkGetSwapchainImagesKHR");
            _swapchainImages = new Image[swapchainImageCount];
            fixed (Image* imagePointer = _swapchainImages)
            {
                Check(
                    _swapchainApi.GetSwapchainImages(_device, _swapchain, &swapchainImageCount, imagePointer),
                    "vkGetSwapchainImagesKHR");
            }

            _imageInitialized = new bool[swapchainImageCount];
        }

        private PresentModeKHR ChoosePresentMode()
        {
            // MAILBOX never blocks vkQueuePresentKHR on vblank, so a slow
            // frame does not quantize the frame rate down to 30/20 fps the
            // way FIFO does; the guest side is already paced by PaceFlip.
            // FIFO is the only mode guaranteed by the spec and remains the
            // fallback (MoltenVK typically exposes FIFO + IMMEDIATE only).
            uint modeCount = 0;
            if (_surfaceApi.GetPhysicalDeviceSurfacePresentModes(
                    _physicalDevice,
                    _surface,
                    &modeCount,
                    null) != Result.Success ||
                modeCount == 0)
            {
                return PresentModeKHR.FifoKhr;
            }

            var modes = stackalloc PresentModeKHR[(int)modeCount];
            if (_surfaceApi.GetPhysicalDeviceSurfacePresentModes(
                    _physicalDevice,
                    _surface,
                    &modeCount,
                    modes) != Result.Success)
            {
                return PresentModeKHR.FifoKhr;
            }

            for (var index = 0u; index < modeCount; index++)
            {
                if (modes[index] == PresentModeKHR.MailboxKhr)
                {
                    return PresentModeKHR.MailboxKhr;
                }
            }

            return PresentModeKHR.FifoKhr;
        }

        private void CreateCommandResources()
        {
            var poolInfo = new CommandPoolCreateInfo
            {
                SType = StructureType.CommandPoolCreateInfo,
                Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
                QueueFamilyIndex = _queueFamilyIndex,
            };
            Check(_vk.CreateCommandPool(_device, &poolInfo, null, out _commandPool), "vkCreateCommandPool");

            var allocateInfo = new CommandBufferAllocateInfo
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = _commandPool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = MaxFramesInFlight,
            };
            _frameCommandBuffers = new CommandBuffer[MaxFramesInFlight];
            fixed (CommandBuffer* frameCommandBuffers = _frameCommandBuffers)
            {
                Check(
                    _vk.AllocateCommandBuffers(_device, &allocateInfo, frameCommandBuffers),
                    "vkAllocateCommandBuffers");
            }

            var semaphoreInfo = new SemaphoreCreateInfo
            {
                SType = StructureType.SemaphoreCreateInfo,
            };
            var fenceInfo = new FenceCreateInfo
            {
                SType = StructureType.FenceCreateInfo,
            };
            _frameImageAvailable = new VkSemaphore[MaxFramesInFlight];
            _frameFences = new Fence[MaxFramesInFlight];
            _frameFencePending = new bool[MaxFramesInFlight];
            _frameTimelines = new ulong[MaxFramesInFlight];
            _frameTranslatedResources = new TranslatedDrawResources?[MaxFramesInFlight];
            _frameGuestImageVersions = new GuestImageResource?[MaxFramesInFlight];
            for (var slot = 0; slot < MaxFramesInFlight; slot++)
            {
                Check(
                    _vk.CreateSemaphore(_device, &semaphoreInfo, null, out _frameImageAvailable[slot]),
                    "vkCreateSemaphore");
                Check(
                    _vk.CreateFence(_device, &fenceInfo, null, out _frameFences[slot]),
                    "vkCreateFence(frame)");
            }

            _renderFinishedPerImage = new VkSemaphore[_swapchainImages.Length];
            for (var image = 0; image < _renderFinishedPerImage.Length; image++)
            {
                Check(
                    _vk.CreateSemaphore(_device, &semaphoreInfo, null, out _renderFinishedPerImage[image]),
                    "vkCreateSemaphore");
            }

            _currentFrameSlot = 0;
            _commandBuffer = _frameCommandBuffers[0];
            _presentationCommandBuffer = _commandBuffer;

            CreateStagingBuffer((ulong)_extent.Width * _extent.Height * 4);
            CreateOverlayResources();
        }

        private void CreateOverlayResources()
        {
            const ulong overlayBytes = PerfOverlay.PanelWidth * PerfOverlay.PanelHeight * 4;
            var imageInfo = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                ImageType = ImageType.Type2D,
                Format = Format.B8G8R8A8Unorm,
                Extent = new Extent3D(PerfOverlay.PanelWidth, PerfOverlay.PanelHeight, 1),
                MipLevels = 1,
                ArrayLayers = 1,
                Samples = SampleCountFlags.Count1Bit,
                Tiling = ImageTiling.Optimal,
                Usage = ImageUsageFlags.TransferDstBit | ImageUsageFlags.TransferSrcBit,
                SharingMode = SharingMode.Exclusive,
                InitialLayout = ImageLayout.Undefined,
            };
            Check(_vk.CreateImage(_device, &imageInfo, null, out _overlayImage), "vkCreateImage(overlay)");
            _vk.GetImageMemoryRequirements(_device, _overlayImage, out var requirements);
            var memoryInfo = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = requirements.Size,
                MemoryTypeIndex = FindMemoryType(
                    requirements.MemoryTypeBits,
                    MemoryPropertyFlags.DeviceLocalBit),
            };
            Check(
                _vk.AllocateMemory(_device, &memoryInfo, null, out _overlayImageMemory),
                "vkAllocateMemory(overlay)");
            Check(
                _vk.BindImageMemory(_device, _overlayImage, _overlayImageMemory, 0),
                "vkBindImageMemory(overlay)");
            _overlayImageInitialized = false;

            _overlayStagingBuffers = new VkBuffer[MaxFramesInFlight];
            _overlayStagingMemory = new DeviceMemory[MaxFramesInFlight];
            _overlayStagingMapped = new nint[MaxFramesInFlight];
            for (var slot = 0; slot < MaxFramesInFlight; slot++)
            {
                _overlayStagingBuffers[slot] = CreateBuffer(
                    overlayBytes,
                    BufferUsageFlags.TransferSrcBit,
                    MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                    out _overlayStagingMemory[slot]);
                void* mapped;
                Check(
                    _vk.MapMemory(_device, _overlayStagingMemory[slot], 0, overlayBytes, 0, &mapped),
                    "vkMapMemory(overlay staging)");
                _overlayStagingMapped[slot] = (nint)mapped;
            }
        }

        private void RecordOverlayBlit(uint imageIndex, int frameSlot)
        {
            if (_overlayImage.Handle == 0 || _overlayStagingMapped.Length <= frameSlot)
            {
                return;
            }

            int pendingWork;
            lock (_gate)
            {
                pendingWork = _pendingGuestWorkCount;
            }

            var pixels = new Span<byte>(
                (void*)_overlayStagingMapped[frameSlot],
                PerfOverlay.PanelWidth * PerfOverlay.PanelHeight * 4);
            PerfOverlay.Fill(pixels, pendingWork, _pendingGuestSubmissions.Count);

            var toTransferDst = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = _overlayImageInitialized ? AccessFlags.TransferReadBit : 0,
                DstAccessMask = AccessFlags.TransferWriteBit,
                OldLayout = _overlayImageInitialized
                    ? ImageLayout.TransferSrcOptimal
                    : ImageLayout.Undefined,
                NewLayout = ImageLayout.TransferDstOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = _overlayImage,
                SubresourceRange = ColorSubresourceRange(),
            };
            _vk.CmdPipelineBarrier(
                _commandBuffer,
                PipelineStageFlags.TransferBit,
                PipelineStageFlags.TransferBit,
                0, 0, null, 0, null, 1, &toTransferDst);

            var copyRegion = new BufferImageCopy
            {
                ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                ImageExtent = new Extent3D(PerfOverlay.PanelWidth, PerfOverlay.PanelHeight, 1),
            };
            _vk.CmdCopyBufferToImage(
                _commandBuffer,
                _overlayStagingBuffers[frameSlot],
                _overlayImage,
                ImageLayout.TransferDstOptimal,
                1,
                &copyRegion);

            var toTransferSrc = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = AccessFlags.TransferWriteBit,
                DstAccessMask = AccessFlags.TransferReadBit,
                OldLayout = ImageLayout.TransferDstOptimal,
                NewLayout = ImageLayout.TransferSrcOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = _overlayImage,
                SubresourceRange = ColorSubresourceRange(),
            };
            var swapchainToDst = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = 0,
                DstAccessMask = AccessFlags.TransferWriteBit,
                OldLayout = ImageLayout.PresentSrcKhr,
                NewLayout = ImageLayout.TransferDstOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = _swapchainImages[imageIndex],
                SubresourceRange = ColorSubresourceRange(),
            };
            var preBlitBarriers = stackalloc ImageMemoryBarrier[2] { toTransferSrc, swapchainToDst };
            _vk.CmdPipelineBarrier(
                _commandBuffer,
                PipelineStageFlags.TransferBit | PipelineStageFlags.ColorAttachmentOutputBit,
                PipelineStageFlags.TransferBit,
                0, 0, null, 0, null, 2, preBlitBarriers);

            const int margin = 12;
            var panelWidth = (int)Math.Min(PerfOverlay.PanelWidth, _extent.Width - margin);
            var panelHeight = (int)Math.Min(PerfOverlay.PanelHeight, _extent.Height - margin);
            // Source and destination are both B8G8R8A8 and the panel is not
            // scaled. MoltenVK has corrupted pixels outside the blit region
            // for this transfer-on-swapchain path (horizontal red/yellow
            // scanlines across the entire window). An exact image copy has
            // the required semantics and avoids the driver's blit conversion
            // path altogether.
            var copy = new ImageCopy
            {
                SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                SrcOffset = new Offset3D(0, 0, 0),
                DstOffset = new Offset3D(margin, margin, 0),
                Extent = new Extent3D((uint)panelWidth, (uint)panelHeight, 1),
            };
            _vk.CmdCopyImage(
                _commandBuffer,
                _overlayImage,
                ImageLayout.TransferSrcOptimal,
                _swapchainImages[imageIndex],
                ImageLayout.TransferDstOptimal,
                1,
                &copy);

            var swapchainToPresent = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = AccessFlags.TransferWriteBit,
                DstAccessMask = 0,
                OldLayout = ImageLayout.TransferDstOptimal,
                NewLayout = ImageLayout.PresentSrcKhr,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = _swapchainImages[imageIndex],
                SubresourceRange = ColorSubresourceRange(),
            };
            _vk.CmdPipelineBarrier(
                _commandBuffer,
                PipelineStageFlags.TransferBit,
                PipelineStageFlags.BottomOfPipeBit,
                0, 0, null, 0, null, 1, &swapchainToPresent);
            _overlayImageInitialized = true;
        }

        private CommandBuffer AllocateGuestCommandBuffer()
        {
            // The pool has ResetCommandBufferBit, so vkBeginCommandBuffer
            // implicitly resets recycled buffers.
            if (_recycledGuestCommandBuffers.TryPop(out var recycled))
            {
                return recycled;
            }

            var allocateInfo = new CommandBufferAllocateInfo
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = _commandPool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = 1,
            };
            CommandBuffer commandBuffer;
            Check(
                _vk.AllocateCommandBuffers(
                    _device,
                    &allocateInfo,
                    out commandBuffer),
                "vkAllocateCommandBuffers(guest)");
            return commandBuffer;
        }

        private Fence AcquireGuestFence()
        {
            // Recycled fences were reset when they were collected.
            if (_recycledGuestFences.TryPop(out var recycled))
            {
                return recycled;
            }

            var fenceInfo = new FenceCreateInfo
            {
                SType = StructureType.FenceCreateInfo,
            };
            Fence fence;
            Check(
                _vk.CreateFence(_device, &fenceInfo, null, out fence),
                "vkCreateFence(guest)");
            return fence;
        }

        private void ReleaseGuestCommandBuffer(CommandBuffer commandBuffer)
        {
            if (_recycledGuestCommandBuffers.Count < MaxRecycledGuestCommandBuffers)
            {
                _recycledGuestCommandBuffers.Push(commandBuffer);
                return;
            }

            _vk.FreeCommandBuffers(_device, _commandPool, 1, &commandBuffer);
        }

        private void ReleaseGuestFence(Fence fence, bool needsReset)
        {
            if (_recycledGuestFences.Count < MaxRecycledGuestFences)
            {
                if (needsReset)
                {
                    Check(_vk.ResetFences(_device, 1, &fence), "vkResetFences(guest)");
                }

                _recycledGuestFences.Push(fence);
                return;
            }

            _vk.DestroyFence(_device, fence, null);
        }

        // Translated draws are recorded into a shared command buffer and
        // submitted once per drained work batch: on MoltenVK every
        // vkQueueSubmit is a Metal command-buffer commit (~0.8ms), which used
        // to be paid per draw and dominated the frame time.
        private CommandBuffer _batchCommandBuffer;
        private bool _batchOpen;
        private int _batchDrawCount;
        private readonly List<TranslatedDrawResources> _batchResources = new();
        private readonly List<GuestImageResource> _batchTraceImages = new();

        // Consecutive draws into the same target stay inside one render pass:
        // on MoltenVK every render pass is a Metal render encoder, and one
        // encoder per draw was the dominant per-draw fixed cost after submit
        // batching. The pass closes when the target changes, when a draw
        // needs transfer/storage work outside a pass, or when the batch
        // flushes.
        private GuestImageResource? _openPassTarget;

        private void CloseOpenTranslatedRenderPass()
        {
            if (_openPassTarget is not { } target)
            {
                return;
            }

            _openPassTarget = null;
            _vk.CmdEndRenderPass(_batchCommandBuffer);
            var toShaderRead = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = AccessFlags.ColorAttachmentWriteBit,
                DstAccessMask = AccessFlags.ShaderReadBit,
                OldLayout = ImageLayout.ColorAttachmentOptimal,
                NewLayout = ImageLayout.ShaderReadOnlyOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = target.Image,
                SubresourceRange = ColorSubresourceRange(),
            };
            _vk.CmdPipelineBarrier(
                _batchCommandBuffer,
                PipelineStageFlags.ColorAttachmentOutputBit,
                PipelineStageFlags.FragmentShaderBit,
                0,
                0,
                null,
                0,
                null,
                1,
                &toShaderRead);
        }

        private CommandBuffer BeginBatchedGuestCommands()
        {
            if (_batchOpen)
            {
                return _batchCommandBuffer;
            }

            _batchCommandBuffer = AllocateGuestCommandBuffer();
            var beginInfo = new CommandBufferBeginInfo
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
            };
            Check(
                _vk.BeginCommandBuffer(_batchCommandBuffer, &beginInfo),
                "vkBeginCommandBuffer(batch)");
            _batchOpen = true;
            _batchDrawCount = 0;
            return _batchCommandBuffer;
        }

        private void FlushBatchedGuestCommands()
        {
            if (!_batchOpen)
            {
                return;
            }

            CloseOpenTranslatedRenderPass();
            _batchOpen = false;
            try
            {
                Check(_vk.EndCommandBuffer(_batchCommandBuffer), "vkEndCommandBuffer(batch)");
                SubmitGuestCommandBuffer(
                    _batchCommandBuffer,
                    _batchResources.ToArray(),
                    _batchTraceImages.ToArray(),
                    _batchRetireBuffers.Count > 0 ? _batchRetireBuffers.ToArray() : []);
            }
            catch
            {
                // The batch never reached the queue: release everything it
                // owned here so the stale lists cannot ride into the next
                // batch's submission.
                foreach (var resources in _batchResources)
                {
                    DestroyTranslatedDrawResources(resources);
                }

                foreach (var (buffer, memory) in _batchRetireBuffers)
                {
                    _vk.DestroyBuffer(_device, buffer, null);
                    _vk.FreeMemory(_device, memory, null);
                }

                ReleaseGuestCommandBuffer(_batchCommandBuffer);
                throw;
            }
            finally
            {
                _batchResources.Clear();
                _batchTraceImages.Clear();
                _batchRetireBuffers.Clear();
                _batchCommandBuffer = default;
            }
        }

        private void SubmitGuestCommandBuffer(
            CommandBuffer commandBuffer,
            IReadOnlyList<TranslatedDrawResources> resources,
            IReadOnlyList<GuestImageResource> traceImages,
            IReadOnlyList<(VkBuffer Buffer, DeviceMemory Memory)>? retireBuffers = null,
            IReadOnlyList<TranslatedDrawResources>? referencedResources = null)
        {
            var fence = AcquireGuestFence();
            try
            {
                var submitInfo = new SubmitInfo
                {
                    SType = StructureType.SubmitInfo,
                    CommandBufferCount = 1,
                    PCommandBuffers = &commandBuffer,
                };
                Check(
                    _vk.QueueSubmit(_queue, 1, &submitInfo, fence),
                    "vkQueueSubmit(guest)");
            }
            catch
            {
                ReleaseGuestFence(fence, needsReset: false);
                throw;
            }

            _submitTimeline++;
            foreach (var referenced in referencedResources ?? resources)
            {
                foreach (var globalBuffer in referenced.GlobalMemoryBuffers)
                {
                    if (globalBuffer.Allocation is not { } allocation)
                    {
                        continue;
                    }

                    allocation.LastUseTimeline = Math.Max(
                        allocation.LastUseTimeline,
                        _submitTimeline);
                    if (globalBuffer.Writable && globalBuffer.WriteBackToGuest)
                    {
                        MarkGuestBufferDirty(
                            allocation,
                            globalBuffer.GuestOffset,
                            globalBuffer.GuestSize);
                    }
                }
            }

            _pendingGuestSubmissions.Enqueue(
                new PendingGuestSubmission(
                    fence,
                    commandBuffer,
                    resources,
                    traceImages,
                    retireBuffers ?? [],
                    _submitTimeline,
                    resources.Count > 0 ? resources[0].DebugName : "batch",
                    _activeGuestQueue,
                    _activeGuestWorkSequence));
            _lastSubmittedTimelineByGuestQueue[_activeGuestQueue.Name] =
                _submitTimeline;
        }

        private void TransitionNewGuestImageToSampled(Image image, uint mipLevels)
        {
            var commandBuffer = AllocateGuestCommandBuffer();
            var beginInfo = new CommandBufferBeginInfo
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
            };
            Check(
                _vk.BeginCommandBuffer(commandBuffer, &beginInfo),
                "vkBeginCommandBuffer(guest image init)");
            var barrier = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = 0,
                DstAccessMask = AccessFlags.ShaderReadBit,
                OldLayout = ImageLayout.Undefined,
                NewLayout = ImageLayout.ShaderReadOnlyOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = image,
                SubresourceRange = ColorSubresourceRange(0, mipLevels),
            };
            _vk.CmdPipelineBarrier(
                commandBuffer,
                PipelineStageFlags.TopOfPipeBit,
                PipelineStageFlags.AllCommandsBit,
                0,
                0,
                null,
                0,
                null,
                1,
                &barrier);
            Check(
                _vk.EndCommandBuffer(commandBuffer),
                "vkEndCommandBuffer(guest image init)");
            // Same-queue submission order makes the transition visible to any
            // later use of the image; no CPU-side wait is needed.
            SubmitGuestCommandBuffer(commandBuffer, [], []);
        }

        private void EnsureGuestSubmissionCapacity()
        {
            CollectCompletedGuestSubmissions(waitForOldest: false);
            if (_pendingGuestSubmissions.Count >= MaxInFlightGuestSubmissions)
            {
                // Bounded wait so the macOS main thread returns to its event
                // pump promptly under a slow-compute backlog; if the oldest
                // isn't done yet we proceed (soft cap, dynamic pools).
                CollectCompletedGuestSubmissions(
                    waitForOldest: true,
                    maxWaitNs: _submissionCapacityWaitNs == 0
                        ? _guestFenceWaitTimeoutNs
                        : _submissionCapacityWaitNs);
            }
        }

        private void WaitForAllGuestSubmissions()
        {
            while (_pendingGuestSubmissions.Count != 0)
            {
                CollectCompletedGuestSubmissions(waitForOldest: true);
            }
        }

        private void CollectCompletedGuestSubmissions(bool waitForOldest, ulong maxWaitNs = 0)
        {
            if (waitForOldest && _pendingGuestSubmissions.TryPeek(out var oldest))
            {
                var fence = oldest.Fence;
                // maxWaitNs==0 => the full "is this submission hung" timeout,
                // which also emits the one-shot hang warning below. A shorter
                // capacity-probe wait (maxWaitNs>0) must NOT report a hang: the
                // submission is still tracked and will be collected once the GPU
                // finishes it on a later frame.
                var isProbeWait = maxWaitNs != 0 && maxWaitNs < _guestFenceWaitTimeoutNs;
                var waitNs = maxWaitNs != 0 ? maxWaitNs : _guestFenceWaitTimeoutNs;
                var result = _vk.WaitForFences(
                    _device,
                    1,
                    &fence,
                    true,
                    waitNs);
                if (result == Result.Timeout)
                {
                    // A GPU submission whose fence never signals (typically a
                    // mistranslated compute shader that hangs the Metal queue)
                    // would otherwise block the render thread forever, starving
                    // the swapchain present (black screen). Log the culprit and
                    // continue so at least the last good frame can be shown.
                    if (!isProbeWait && _tracedFenceTimeouts.Add(oldest.DebugName))
                    {
                        Console.Error.WriteLine(
                            $"[LOADER][WARN] vk.fence_wait_timeout submission='{oldest.DebugName}' " +
                            $"— GPU work not completing after {_guestFenceWaitTimeoutNs / 1_000_000}ms; " +
                            "render thread continuing (present not blocked).");
                    }

                    return;
                }

                if (result == Result.ErrorDeviceLost)
                {
                    _deviceLost = true;
                }
                else
                {
                    Check(result, $"vkWaitForFences(guest: {oldest.DebugName})");
                }
            }

            while (_pendingGuestSubmissions.TryPeek(out var submission))
            {
                var status = _vk.GetFenceStatus(_device, submission.Fence);
                if (status == Result.NotReady && !_deviceLost)
                {
                    break;
                }

                if (status == Result.ErrorDeviceLost)
                {
                    // Pending fences never signal on a lost device; retire the
                    // submission anyway so teardown and back-pressure survive.
                    _deviceLost = true;
                }
                else if (status != Result.NotReady)
                {
                    Check(status, $"vkGetFenceStatus(guest: {submission.DebugName})");
                }

                _pendingGuestSubmissions.Dequeue();

                if (!_deviceLost)
                {
                    foreach (var image in submission.TraceImages)
                    {
                        TraceGuestImageContents(image);
                    }
                }

                foreach (var resources in submission.Resources)
                {
                    DestroyTranslatedDrawResources(resources);
                }

                foreach (var (buffer, memory) in submission.RetireBuffers)
                {
                    _vk.DestroyBuffer(_device, buffer, null);
                    _vk.FreeMemory(_device, memory, null);
                }

                ReleaseGuestCommandBuffer(submission.CommandBuffer);
                ReleaseGuestFence(submission.Fence, needsReset: true);
                if (submission.Timeline > _completedTimeline)
                {
                    _completedTimeline = submission.Timeline;
                }
            }

            ProcessDeferredTextureDestroys();
        }

        private void WaitForAllGuestSubmissionsForCpuVisibility()
        {
            FlushBatchedGuestCommands();
            while (_pendingGuestSubmissions.TryPeek(out var oldest))
            {
                var fence = oldest.Fence;
                Check(
                    _vk.WaitForFences(_device, 1, &fence, true, ulong.MaxValue),
                    $"vkWaitForFences(cpu visibility: {oldest.DebugName})");
                CollectCompletedGuestSubmissions(waitForOldest: false);
            }
        }

        private void WaitForActiveGuestQueueSubmissionsForCpuVisibility()
        {
            FlushBatchedGuestCommands();
            if (!_lastSubmittedTimelineByGuestQueue.TryGetValue(
                    _activeGuestQueue.Name,
                    out var targetTimeline) ||
                targetTimeline <= _completedTimeline)
            {
                return;
            }

            PendingGuestSubmission? target = null;
            foreach (var submission in _pendingGuestSubmissions)
            {
                if (submission.Timeline == targetTimeline)
                {
                    target = submission;
                    break;
                }
            }

            if (target is null)
            {
                throw new InvalidOperationException(
                    $"Guest queue '{_activeGuestQueue.Name}' lost pending timeline " +
                    $"{targetTimeline} (completed={_completedTimeline}).");
            }

            var waitStart = System.Diagnostics.Stopwatch.GetTimestamp();
            var fence = target.Fence;
            Check(
                _vk.WaitForFences(_device, 1, &fence, true, ulong.MaxValue),
                $"vkWaitForFences(queue visibility: {_activeGuestQueue.Name})");
            CollectCompletedGuestSubmissions(waitForOldest: false);
            var waitedMs = (System.Diagnostics.Stopwatch.GetTimestamp() - waitStart) *
                1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (_traceVulkanShaderEnabled)
            {
                TraceVulkanShader(
                    $"vk.queue_visibility queue={_activeGuestQueue.Name} " +
                    $"submission={_activeGuestQueue.SubmissionId} " +
                    $"target_timeline={targetTimeline} completed_timeline={_completedTimeline} " +
                    $"waited_ms={waitedMs:F3}");
            }
        }

        private void ExecuteOrderedGuestAction(VulkanOrderedGuestAction work)
        {
            WaitForActiveGuestQueueSubmissionsForCpuVisibility();
            WriteBackAllDirtyGuestBuffers(_activeGuestQueue.Name);
            work.Action();
            if (_traceVulkanShaderEnabled)
            {
                TraceVulkanShader(
                    $"vk.ordered_action queue={_activeGuestQueue.Name} " +
                    $"submission={_activeGuestQueue.SubmissionId} " +
                    $"work_sequence={_activeGuestWorkSequence} name='{work.DebugName}'");
            }
        }

        private void ExecuteOrderedGuestFlip(VulkanOrderedGuestFlip work)
        {
            FlushBatchedGuestCommands();
            _guestImages.TryGetValue(work.Address, out var source);
            if (_deviceLost ||
                source is null ||
                !source.Initialized)
            {
                Console.Error.WriteLine(
                    $"[LOADER][WARN] vk.flip_capture_failed version={work.Version} " +
                    $"queue={_activeGuestQueue.Name} addr=0x{work.Address:X16} " +
                    $"found={(source is not null)} initialized={(source?.Initialized ?? false)}");
                return;
            }

            EnsureGuestSubmissionCapacity();
            var snapshot = CreateGuestFlipSnapshot(source, work.Version);
            var commandBuffer = AllocateGuestCommandBuffer();
            var submitted = false;
            try
            {
                var beginInfo = new CommandBufferBeginInfo
                {
                    SType = StructureType.CommandBufferBeginInfo,
                    Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
                };
                Check(
                    _vk.BeginCommandBuffer(commandBuffer, &beginInfo),
                    "vkBeginCommandBuffer(flip capture)");

                var barriers = stackalloc ImageMemoryBarrier[2];
                barriers[0] = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask = AccessFlags.ShaderReadBit |
                                    AccessFlags.ShaderWriteBit |
                                    AccessFlags.ColorAttachmentWriteBit |
                                    AccessFlags.TransferWriteBit,
                    DstAccessMask = AccessFlags.TransferReadBit,
                    OldLayout = ImageLayout.ShaderReadOnlyOptimal,
                    NewLayout = ImageLayout.TransferSrcOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = source.Image,
                    SubresourceRange = ColorSubresourceRange(),
                };
                barriers[1] = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask = 0,
                    DstAccessMask = AccessFlags.TransferWriteBit,
                    OldLayout = ImageLayout.Undefined,
                    NewLayout = ImageLayout.TransferDstOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = snapshot.Image,
                    SubresourceRange = ColorSubresourceRange(),
                };
                _vk.CmdPipelineBarrier(
                    commandBuffer,
                    PipelineStageFlags.AllCommandsBit,
                    PipelineStageFlags.TransferBit,
                    0,
                    0,
                    null,
                    0,
                    null,
                    2,
                    barriers);

                var copy = new ImageCopy
                {
                    SrcSubresource = new ImageSubresourceLayers(
                        ImageAspectFlags.ColorBit, 0, 0, 1),
                    DstSubresource = new ImageSubresourceLayers(
                        ImageAspectFlags.ColorBit, 0, 0, 1),
                    Extent = new Extent3D(source.Width, source.Height, 1),
                };
                _vk.CmdCopyImage(
                    commandBuffer,
                    source.Image,
                    ImageLayout.TransferSrcOptimal,
                    snapshot.Image,
                    ImageLayout.TransferDstOptimal,
                    1,
                    &copy);

                barriers[0] = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask = AccessFlags.TransferReadBit,
                    DstAccessMask = AccessFlags.ShaderReadBit |
                                    AccessFlags.ShaderWriteBit |
                                    AccessFlags.ColorAttachmentWriteBit,
                    OldLayout = ImageLayout.TransferSrcOptimal,
                    NewLayout = ImageLayout.ShaderReadOnlyOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = source.Image,
                    SubresourceRange = ColorSubresourceRange(),
                };
                barriers[1] = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask = AccessFlags.TransferWriteBit,
                    DstAccessMask = AccessFlags.ShaderReadBit,
                    OldLayout = ImageLayout.TransferDstOptimal,
                    NewLayout = ImageLayout.ShaderReadOnlyOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = snapshot.Image,
                    SubresourceRange = ColorSubresourceRange(),
                };
                _vk.CmdPipelineBarrier(
                    commandBuffer,
                    PipelineStageFlags.TransferBit,
                    PipelineStageFlags.AllCommandsBit,
                    0,
                    0,
                    null,
                    0,
                    null,
                    2,
                    barriers);

                Check(
                    _vk.EndCommandBuffer(commandBuffer),
                    "vkEndCommandBuffer(flip capture)");
                SubmitGuestCommandBuffer(commandBuffer, [], []);
                submitted = true;
                snapshot.Initialized = true;
                _guestImageVersions.Add(work.Version, snapshot);
                _capturedGuestFlipVersions.Add(work.Version);

                lock (_gate)
                {
                    var sequence = (_latestPresentation?.Sequence ?? 0) + 1;
                    var presentation = new Presentation(
                        null,
                        work.Width,
                        work.Height,
                        sequence,
                        GuestDrawKind.None,
                        TranslatedDraw: null,
                        RequiredGuestWorkSequence: _activeGuestWorkSequence,
                        IsSplash: false,
                        GuestImageAddress: work.Address,
                        GuestImageVersion: work.Version);
                    _latestPresentation = presentation;
                    _pendingGuestImagePresentations.Enqueue(presentation);
                    while (_pendingGuestImagePresentations.Count > MaxPendingGuestFlipVersions)
                    {
                        _pendingGuestImagePresentations.Dequeue();
                    }
                }

                CollectAbandonedGuestImageVersions();

                var effectivePitch = work.PitchInPixel == 0
                    ? work.Width
                    : work.PitchInPixel;
                TraceVulkanShader(
                    $"vk.flip_capture version={work.Version} " +
                    $"queue={_activeGuestQueue.Name} submission={_activeGuestQueue.SubmissionId} " +
                    $"work_sequence={_activeGuestWorkSequence} addr=0x{work.Address:X16} " +
                    $"size={work.Width}x{work.Height} pitch={effectivePitch}");
            }
            finally
            {
                if (!submitted)
                {
                    ReleaseGuestCommandBuffer(commandBuffer);
                    DestroyGuestImage(snapshot);
                }
            }
        }

        private void ExecuteOrderedGuestFlipWait(VulkanOrderedGuestFlipWait work)
        {
            var captured = work.Version != 0 &&
                _capturedGuestFlipVersions.Contains(work.Version);
            TraceVulkanShader(
                $"vk.flip_wait_safe version={work.Version} " +
                $"queue={_activeGuestQueue.Name} submission={_activeGuestQueue.SubmissionId} " +
                $"handle={work.VideoOutHandle} index={work.DisplayBufferIndex} " +
                $"capture_complete={(captured ? 1 : 0)}");
            // Demon's Souls executes wait-safe markers before their flip capture;
            // an assert here would fail-fast the process, so warn once instead.
            // Dedup on a flag, not the (per-frame-unique) version, to bound growth.
            if (work.Version != 0 && !captured && !_loggedFlipWaitOrderViolation)
            {
                _loggedFlipWaitOrderViolation = true;
                Console.Error.WriteLine(
                    $"[LOADER][WARN] vk.flip_wait_order version={work.Version} " +
                    "executed before its flip capture; continuing.");
            }
        }

        private bool _loggedFlipWaitOrderViolation;

        private GuestImageResource CreateGuestFlipSnapshot(
            GuestImageResource source,
            long version)
        {
            var imageInfo = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                ImageType = ImageType.Type2D,
                Format = source.Format,
                Extent = new Extent3D(source.Width, source.Height, 1),
                MipLevels = 1,
                ArrayLayers = 1,
                Samples = SampleCountFlags.Count1Bit,
                Tiling = ImageTiling.Optimal,
                Usage = ImageUsageFlags.TransferSrcBit |
                        ImageUsageFlags.TransferDstBit |
                        ImageUsageFlags.SampledBit,
                SharingMode = SharingMode.Exclusive,
                InitialLayout = ImageLayout.Undefined,
            };
            Check(
                _vk.CreateImage(_device, &imageInfo, null, out var image),
                "vkCreateImage(flip snapshot)");
            _vk.GetImageMemoryRequirements(_device, image, out var requirements);
            var allocationInfo = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = requirements.Size,
                MemoryTypeIndex = FindMemoryType(
                    requirements.MemoryTypeBits,
                    MemoryPropertyFlags.DeviceLocalBit),
            };
            DeviceMemory memory = default;
            try
            {
                Check(
                    _vk.AllocateMemory(_device, &allocationInfo, null, out memory),
                    "vkAllocateMemory(flip snapshot)");
                Check(
                    _vk.BindImageMemory(_device, image, memory, 0),
                    "vkBindImageMemory(flip snapshot)");
            }
            catch
            {
                if (memory.Handle != 0)
                {
                    _vk.FreeMemory(_device, memory, null);
                }
                _vk.DestroyImage(_device, image, null);
                throw;
            }

            SetDebugName(
                ObjectType.Image,
                image.Handle,
                $"guest flip v{version} source 0x{source.Address:X16}");
            return new GuestImageResource
            {
                Address = source.Address,
                FlipVersion = version,
                Width = source.Width,
                Height = source.Height,
                MipLevels = 1,
                GuestFormat = source.GuestFormat,
                Format = source.Format,
                Image = image,
                Memory = memory,
            };
        }

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
            var byteCount = GetTextureByteCount(texture.Format, rowLength, height);
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
            foreach (var buffer in draw.GlobalMemoryBuffers)
            {
                if (buffer.Pooled)
                {
                    GuestDataPool.Return(buffer.Data);
                }
            }

            foreach (var buffer in draw.VertexBuffers)
            {
                if (buffer.Pooled)
                {
                    GuestDataPool.Return(buffer.Data);
                }
            }

            if (draw.IndexBuffer is { Pooled: true } indexBuffer)
            {
                GuestDataPool.Return(indexBuffer.Data);
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
                TraceVulkanShader(
                    $"vk.flip_retired version={imageEntry.Image.FlipVersion} " +
                    $"timeline={imageEntry.RetireTimeline} reason=presentation-dropped");
            }
        }

        private void WaitFrameSlot(int slot) => TryWaitFrameSlot(slot, ulong.MaxValue);

        // Returns false when the slot's fence is still unsignaled after
        // timeoutNs (the GPU is behind, e.g. a slow-compute backlog). Callers
        // on the macOS main thread must NOT wait forever here or the Cocoa
        // event pump stalls and the window goes "Not Responding" (F1 overlay /
        // close stop working). A bounded wait lets Render() skip the frame and
        // return to the pump; the fence still signals later and the frame is
        // retried.
        private bool TryWaitFrameSlot(int slot, ulong timeoutNs)
        {
            if (_frameFencePending.Length <= slot || !_frameFencePending[slot])
            {
                if (_frameGuestImageVersions.Length > slot &&
                    _frameGuestImageVersions[slot] is { } unsubmittedVersion)
                {
                    _frameGuestImageVersions[slot] = null;
                    _capturedGuestFlipVersions.Remove(unsubmittedVersion.FlipVersion);
                    DestroyGuestImage(unsubmittedVersion);
                    TraceVulkanShader(
                        $"vk.flip_retired version={unsubmittedVersion.FlipVersion} " +
                        $"frame_slot={slot} reason=frame-not-submitted");
                }
                return true;
            }

            var fence = _frameFences[slot];
            var waitResult = _vk.WaitForFences(_device, 1, &fence, true, timeoutNs);
            if (waitResult == Result.Timeout)
            {
                return false;
            }

            Check(waitResult, "vkWaitForFences(frame)");
            Check(_vk.ResetFences(_device, 1, &fence), "vkResetFences(frame)");
            _frameFencePending[slot] = false;
            if (_frameTimelines[slot] > _completedTimeline)
            {
                _completedTimeline = _frameTimelines[slot];
            }

            if (_frameTranslatedResources[slot] is { } translated)
            {
                _frameTranslatedResources[slot] = null;
                DestroyTranslatedDrawResources(translated);
            }

            if (_frameGuestImageVersions[slot] is { } guestImageVersion)
            {
                _frameGuestImageVersions[slot] = null;
                _capturedGuestFlipVersions.Remove(guestImageVersion.FlipVersion);
                DestroyGuestImage(guestImageVersion);
                TraceVulkanShader(
                    $"vk.flip_retired version={guestImageVersion.FlipVersion} " +
                    $"frame_slot={slot} timeline={_frameTimelines[slot]}");
            }

            ProcessDeferredTextureDestroys();
            return true;
        }

        private void WaitAllFrameSlots()
        {
            for (var slot = 0; slot < _frameFencePending.Length; slot++)
            {
                WaitFrameSlot(slot);
            }
        }

        private void CollectAbandonedGuestImageVersions()
        {
            if (_guestImageVersions.Count == 0)
            {
                return;
            }

            HashSet<long> referencedVersions;
            lock (_gate)
            {
                referencedVersions = _pendingGuestImagePresentations
                    .Select(static presentation => presentation.GuestImageVersion)
                    .Where(static version => version != 0)
                    .ToHashSet();
                if (_latestPresentation is { GuestImageVersion: not 0 } latest)
                {
                    referencedVersions.Add(latest.GuestImageVersion);
                }
            }

            foreach (var entry in _guestImageVersions.ToArray())
            {
                if (referencedVersions.Contains(entry.Key))
                {
                    continue;
                }

                _guestImageVersions.Remove(entry.Key);
                _capturedGuestFlipVersions.Remove(entry.Key);
                _deferredGuestImageVersionDestroys.Enqueue(
                    (entry.Value, _submitTimeline));
                TraceVulkanShader(
                    $"vk.flip_retire_deferred version={entry.Key} " +
                    $"timeline={_submitTimeline} reason=presentation-dropped");
            }
        }

        // Used on teardown paths that already drained the queue (device
        // wait-idle): releases per-slot state without touching fences that
        // were never submitted.
        private void DrainFrameSlots()
        {
            WaitAllFrameSlots();
            _completedTimeline = _submitTimeline;
            ProcessDeferredTextureDestroys();
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
            var colorAttachment = new AttachmentDescription
            {
                Format = _swapchainFormat,
                Samples = SampleCountFlags.Count1Bit,
                LoadOp = AttachmentLoadOp.Clear,
                StoreOp = AttachmentStoreOp.Store,
                StencilLoadOp = AttachmentLoadOp.DontCare,
                StencilStoreOp = AttachmentStoreOp.DontCare,
                InitialLayout = ImageLayout.Undefined,
                FinalLayout = ImageLayout.PresentSrcKhr,
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
            Check(_vk.CreateRenderPass(_device, &renderPassInfo, null, out _renderPass), "vkCreateRenderPass");

            _swapchainImageViews = new ImageView[_swapchainImages.Length];
            _framebuffers = new Framebuffer[_swapchainImages.Length];
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
                var framebufferInfo = new FramebufferCreateInfo
                {
                    SType = StructureType.FramebufferCreateInfo,
                    RenderPass = _renderPass,
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

        private ShaderModule CreateShaderModule(byte[] code)
        {
            fixed (byte* codePointer = code)
            {
                var createInfo = new ShaderModuleCreateInfo
                {
                    SType = StructureType.ShaderModuleCreateInfo,
                    CodeSize = (nuint)code.Length,
                    PCode = (uint*)codePointer,
                };
                Check(
                    _vk.CreateShaderModule(_device, &createInfo, null, out var module),
                    "vkCreateShaderModule");
                return module;
            }
        }

        private TranslatedDrawResources CreateTranslatedDrawResources(
            VulkanTranslatedGuestDraw draw,
            RenderPass renderPass,
            IReadOnlyList<Format> renderTargetFormats,
            Extent2D extent,
            IReadOnlyList<GuestImageResource>? feedbackTargets = null,
            bool hasDepthAttachment = false,
            GuestDepthResource? feedbackDepth = null)
        {
            var isTitleDraw = IsTitleDraw(draw.VertexBuffers);
            var forceFullscreenVertex = _forceFullscreenPipeline ||
                _forceFullscreenVertex ||
                isTitleDraw && _forceTitleFullscreenVertex ||
                AnyTargetAddressMatches(
                    feedbackTargets,
                    "SHARPEMU_FORCE_FULLSCREEN_VERTEX_TARGETS");
            var forceRasterState = _forceFullscreenPipeline ||
                _forceDefaultRasterState ||
                isTitleDraw && _forceTitleDefaultRasterState ||
                AnyTargetAddressMatches(
                    feedbackTargets,
                    "SHARPEMU_FORCE_DEFAULT_RASTER_STATE_TARGETS");
            var forceTitleSolidFragment =
                _forceTitleSolidFragment &&
                isTitleDraw;
            var forceSolidFragment = forceTitleSolidFragment ||
                _forceFullscreenPipeline ||
                _forceSolidFragment ||
                AnyTargetAddressMatches(
                    feedbackTargets,
                    "SHARPEMU_FORCE_SOLID_FRAGMENT_TARGETS");
            var attributeFragmentLocation =
                _forceAttributeFragmentLocation.GetValueOrDefault();
            var forceAttributeFragment =
                _forceAttributeFragmentLocation.HasValue &&
                AnyTargetAddressMatches(
                    feedbackTargets,
                    "SHARPEMU_FORCE_ATTRIBUTE_FRAGMENT_TARGETS");
            var vertexSpirv = forceFullscreenVertex
                ? SpirvFixedShaders.CreateFullscreenVertex(0)
                : draw.VertexSpirv;
            var fragmentSpirv = forceSolidFragment
                ? SpirvFixedShaders.CreateSolidFragment(1f, 0f, 1f, 1f)
                : forceAttributeFragment
                    ? SpirvFixedShaders.CreateAttributeFragment(attributeFragmentLocation)
                : draw.PixelSpirv;
            if (forceSolidFragment && !string.IsNullOrWhiteSpace(_fixedFragmentDumpPath))
            {
                File.WriteAllBytes(_fixedFragmentDumpPath, fragmentSpirv);
            }
            if (draw.RenderState.Blends.Count != renderTargetFormats.Count)
            {
                throw new InvalidOperationException(
                    "color attachment formats and blend states must have matching counts");
            }
            if (vertexSpirv.Length == 0 &&
                !TryCompileFullscreenVertexShader(
                    draw.AttributeCount,
                    out vertexSpirv,
                    out var vertexError))
            {
                throw new InvalidOperationException($"translated vertex shader failed: {vertexError}");
            }

            var resources = new TranslatedDrawResources
            {
                DebugName = "SharpEmu draw",
                Textures = new TextureResource[draw.Textures.Count],
                GlobalMemoryBuffers =
                    new GlobalBufferResource[draw.GlobalMemoryBuffers.Count],
                VertexBuffers = new VertexBufferResource[draw.VertexBuffers.Count],
                VertexCount = GetDrawVertexCount(draw.PrimitiveType, draw.VertexCount, draw.IndexBuffer),
                InstanceCount = Math.Max(draw.InstanceCount, 1),
                Topology = GetPrimitiveTopology(draw.PrimitiveType),
                Blends = draw.RenderState.Blends.ToArray(),
                Scissor = draw.RenderState.Scissor,
                Viewport = draw.RenderState.Viewport,
                Raster = draw.RenderState.Raster,
                Depth = draw.RenderState.Depth,
                HasDepthAttachment = hasDepthAttachment,
                TargetFormats = renderTargetFormats.ToArray(),
            };
            if (forceFullscreenVertex)
            {
                resources.VertexCount = 3;
                resources.InstanceCount = 1;
                resources.Topology = PrimitiveTopology.TriangleList;
            }
            if (forceRasterState)
            {
                resources.Blends = Enumerable.Repeat(
                    GuestBlendState.Default,
                    renderTargetFormats.Count).ToArray();
                resources.Scissor = null;
                resources.Viewport = null;
                resources.Raster = GuestRasterState.Default;
                resources.Depth = GuestDepthState.Default;
            }
            if (isTitleDraw && _forceTitleDefaultBlend)
            {
                resources.Blends = Enumerable.Repeat(
                    GuestBlendState.Default,
                    renderTargetFormats.Count).ToArray();
            }
            if (isTitleDraw && _forceTitleDefaultViewportScissor)
            {
                resources.Scissor = null;
                resources.Viewport = null;
            }
            if (isTitleDraw && _forceTitleDisableCull)
            {
                resources.Raster = resources.Raster with
                {
                    CullFront = false,
                    CullBack = false,
                };
            }
            if (isTitleDraw && _forceTitleDisableDepth)
            {
                resources.Depth = GuestDepthState.Default;
            }
            if (isTitleDraw && _traceTitleState)
            {
                Console.Error.WriteLine(
                    $"[LOADER][TRACE] vk.title_state " +
                    $"viewport={resources.Viewport} scissor={resources.Scissor} " +
                    $"raster={resources.Raster} depth={resources.Depth} " +
                    $"blends=[{string.Join(',', resources.Blends)}]");
            }

            try
            {
                foreach (var texture in draw.Textures)
                {
                    // Skip address-0 storage bindings here: the real resolution
                    // path uses a scratch image for those, but this warm-up pass
                    // called ResolveStorageGuestImage directly, which throws on
                    // address 0 and dropped the whole draw (Demon's Souls G-buffer
                    // normals/IDs passes -> lighting had no input -> black).
                    if (texture.IsStorage && texture.Address != 0)
                    {
                        _ = ResolveStorageGuestImage(texture);
                    }
                }

                for (var index = 0; index < draw.Textures.Count; index++)
                {
                    var texture = draw.Textures[index];
                    var resolved = ResolveTextureResource(texture);
                    var feedbackTarget = !texture.IsStorage
                        ? feedbackTargets?.FirstOrDefault(target =>
                            ReferenceEquals(resolved.GuestImage, target))
                        : null;
                    // ResolveTextureResource may deliberately decline an
                    // address alias when the descriptor is incompatible with
                    // the render-target image. Only snapshot an alias which
                    // actually resolved to the target; a separately uploaded
                    // texture has no Vulkan attachment feedback hazard.
                    resources.Textures[index] =
                        feedbackDepth is not null &&
                        !texture.IsStorage &&
                        ReferenceEquals(resolved.GuestDepth, feedbackDepth)
                            ? CreateDepthFeedbackSnapshot(texture, feedbackDepth)
                            :
                        feedbackTarget is not null &&
                        !texture.IsStorage &&
                        ReferenceEquals(resolved.GuestImage, feedbackTarget)
                            ? CreateRenderTargetFeedbackSnapshot(texture, feedbackTarget)
                            : resolved;
                }

                PrepareGuestBufferAllocations(draw.GlobalMemoryBuffers);
                for (var index = 0; index < draw.GlobalMemoryBuffers.Count; index++)
                {
                    resources.GlobalMemoryBuffers[index] =
                        CreateGlobalBufferResource(draw.GlobalMemoryBuffers[index]);
                }

                var sharedVertexResources = new Dictionary<
                    byte[], VertexBufferResource>(
                    System.Collections.Generic.ReferenceEqualityComparer.Instance);
                for (var index = 0; index < draw.VertexBuffers.Count; index++)
                {
                    var guestVertex = draw.VertexBuffers[index];
                    if (sharedVertexResources.TryGetValue(
                            guestVertex.Data,
                            out var sharedVertex))
                    {
                        resources.VertexBuffers[index] =
                            CreateVertexBufferAlias(sharedVertex, guestVertex);
                    }
                    else
                    {
                        var vertexResource =
                            CreateVertexBufferResource(guestVertex);
                        resources.VertexBuffers[index] = vertexResource;
                        sharedVertexResources.Add(guestVertex.Data, vertexResource);
                    }
                }

                if (draw.IndexBuffer is { Length: > 0 } indexBuffer)
                {
                    resources.IndexBuffer = CreateHostBuffer(
                        indexBuffer.Data.AsSpan(0, indexBuffer.Length),
                        BufferUsageFlags.IndexBufferBit,
                        out resources.IndexMemory,
                        out _);
                    resources.Index32Bit = indexBuffer.Is32Bit;
                    if (indexBuffer.Pooled)
                    {
                        GuestDataPool.Return(indexBuffer.Data);
                    }
                }

                CreateTranslatedDescriptorResources(
                    resources,
                    ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit);
                CreateTranslatedPipeline(
                    resources,
                    vertexSpirv,
                    fragmentSpirv,
                    renderPass,
                    renderTargetFormats,
                    extent);
                return resources;
            }
            catch
            {
                DestroyTranslatedDrawResources(resources);
                throw;
            }
            finally
            {
                var returnedVertexData = new HashSet<byte[]>(
                    System.Collections.Generic.ReferenceEqualityComparer.Instance);
                foreach (var vertex in draw.VertexBuffers)
                {
                    if (vertex.Pooled && returnedVertexData.Add(vertex.Data))
                    {
                        GuestDataPool.Return(vertex.Data);
                    }
                }
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private TranslatedDrawResources CreateComputeDispatchResources(
            VulkanComputeGuestDispatch dispatch)
        {
            var traceResources = dispatch.Textures.Count >= 8;
            if (traceResources)
            {
                TraceVulkanShader(
                    $"vk.compute_resources begin groups={dispatch.GroupCountX}x" +
                    $"{dispatch.GroupCountY}x{dispatch.GroupCountZ} textures={dispatch.Textures.Count}");
            }

            var resources = new TranslatedDrawResources
            {
                DebugName = BuildComputeDebugName(dispatch),
                Textures = new TextureResource[dispatch.Textures.Count],
                GlobalMemoryBuffers =
                    new GlobalBufferResource[dispatch.GlobalMemoryBuffers.Count],
            };

            try
            {
                for (var index = 0; index < dispatch.Textures.Count; index++)
                {
                    var texture = dispatch.Textures[index];
                    // Address-zero storage descriptors are valid scratch bindings.
                    // ResolveTextureResource creates their transient image below;
                    // pre-resolving them as guest-backed images throws and drops
                    // the entire compute dispatch before that path can run.
                    if (texture.IsStorage && texture.Address != 0)
                    {
                        if (traceResources)
                        {
                            TraceVulkanShader(
                                $"vk.compute_resources storage[{index}] begin " +
                                $"addr=0x{texture.Address:X16} fmt={texture.Format} " +
                                $"size={texture.Width}x{texture.Height} " +
                                $"view_mips={texture.BaseMipLevel}+{texture.MipLevels} " +
                                $"resource_mips={texture.ResourceMipLevels} " +
                                $"relative_level={texture.MipLevel}");
                        }

                        _ = ResolveStorageGuestImage(texture);
                        if (traceResources)
                        {
                            TraceVulkanShader($"vk.compute_resources storage[{index}] ready");
                        }
                    }
                }

                if (traceResources)
                {
                    TraceVulkanShader("vk.compute_resources resolve begin");
                }

                for (var index = 0; index < dispatch.Textures.Count; index++)
                {
                    resources.Textures[index] =
                        ResolveTextureResource(dispatch.Textures[index]);
                }

                if (traceResources)
                {
                    TraceVulkanShader("vk.compute_resources resolve ready");
                }

                PrepareGuestBufferAllocations(dispatch.GlobalMemoryBuffers);
                for (var index = 0; index < dispatch.GlobalMemoryBuffers.Count; index++)
                {
                    resources.GlobalMemoryBuffers[index] =
                        CreateGlobalBufferResource(dispatch.GlobalMemoryBuffers[index]);
                }

                if (traceResources)
                {
                    TraceVulkanShader("vk.compute_resources descriptors begin");
                }

                CreateTranslatedDescriptorResources(resources, ShaderStageFlags.ComputeBit);
                if (traceResources)
                {
                    TraceVulkanShader("vk.compute_resources descriptors ready");
                }

                if (traceResources)
                {
                    TraceVulkanShader(
                        $"vk.compute_resources pipeline begin " +
                        $"cs=0x{dispatch.ShaderAddress:X16} " +
                        $"spirv={dispatch.ComputeSpirv.Length} " +
                        $"textures={resources.Textures.Length} " +
                        $"globals={resources.GlobalMemoryBuffers.Length}");
                }

                CreateComputePipeline(resources, dispatch.ComputeSpirv);
                if (traceResources)
                {
                    TraceVulkanShader("vk.compute_resources pipeline ready");
                }

                return resources;
            }
            catch
            {
                DestroyTranslatedDrawResources(resources);
                throw;
            }
        }

        private static bool TryCompileFullscreenVertexShader(
            uint attributeCount,
            out byte[] spirv,
            out string error)
        {
            spirv = [];
            error = string.Empty;
            if (attributeCount > 32)
            {
                error = $"too many interpolated attributes: {attributeCount}";
                return false;
            }

            spirv = SpirvFixedShaders.CreateFullscreenVertex(attributeCount);
            return true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void CreateTranslatedDescriptorResources(
            TranslatedDrawResources resources,
            ShaderStageFlags stageFlags)
        {
            var textureCount = resources.Textures.Length;
            var sampledImageCount = resources.Textures.Count(texture => !texture.IsStorage);
            var storageImageCount = textureCount - sampledImageCount;
            var globalBufferCount = resources.GlobalMemoryBuffers.Length;
            var bindingCount = textureCount + (globalBufferCount == 0 ? 0 : 1);
            var layout = GetOrCreateDescriptorLayout(resources, stageFlags, bindingCount);
            resources.DescriptorSetLayout = layout.DescriptorSetLayout;
            resources.PipelineLayout = layout.PipelineLayout;
            resources.DescriptorLayoutCached = true;
            if (bindingCount == 0)
            {
                return;
            }

            var setLayout = layout.DescriptorSetLayout;

            var poolSizes = new DescriptorPoolSize[
                (sampledImageCount == 0 ? 0 : 1) +
                (storageImageCount == 0 ? 0 : 1) +
                (globalBufferCount == 0 ? 0 : 1)];
            var poolSizeIndex = 0;
            if (sampledImageCount != 0)
            {
                poolSizes[poolSizeIndex++] = new DescriptorPoolSize
                {
                    Type = DescriptorType.CombinedImageSampler,
                    DescriptorCount = (uint)sampledImageCount,
                };
            }

            if (storageImageCount != 0)
            {
                poolSizes[poolSizeIndex++] = new DescriptorPoolSize
                {
                    Type = DescriptorType.StorageImage,
                    DescriptorCount = (uint)storageImageCount,
                };
            }

            if (globalBufferCount != 0)
            {
                poolSizes[poolSizeIndex] = new DescriptorPoolSize
                {
                    Type = DescriptorType.StorageBuffer,
                    DescriptorCount = (uint)globalBufferCount,
                };
            }

            if (_recycledDescriptorPools.TryPop(out var recycledPool))
            {
                Check(
                    _vk.ResetDescriptorPool(_device, recycledPool, 0),
                    "vkResetDescriptorPool");
                resources.DescriptorPool = recycledPool;
            }
            else
            {
                // Generously sized so any draw's set fits, making the pool
                // recyclable regardless of the draw's binding mix. AAA titles
                // (e.g. Demon's Souls) bind well over 32 textures in a single
                // descriptor set, so the sampled-image budget in particular
                // must be large enough to avoid a per-draw dynamic fallback.
                var genericPoolSizes = stackalloc DescriptorPoolSize[3];
                genericPoolSizes[0] = new DescriptorPoolSize
                {
                    Type = DescriptorType.CombinedImageSampler,
                    DescriptorCount = 256,
                };
                genericPoolSizes[1] = new DescriptorPoolSize
                {
                    Type = DescriptorType.StorageImage,
                    DescriptorCount = 64,
                };
                genericPoolSizes[2] = new DescriptorPoolSize
                {
                    Type = DescriptorType.StorageBuffer,
                    DescriptorCount = 64,
                };
                var poolInfo = new DescriptorPoolCreateInfo
                {
                    SType = StructureType.DescriptorPoolCreateInfo,
                    MaxSets = 1,
                    PoolSizeCount = 3,
                    PPoolSizes = genericPoolSizes,
                };
                DescriptorPool descriptorPool;
                Check(
                    _vk.CreateDescriptorPool(
                        _device,
                        &poolInfo,
                        null,
                        out descriptorPool),
                    "vkCreateDescriptorPool");
                resources.DescriptorPool = descriptorPool;
            }

            var allocateInfo = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = resources.DescriptorPool,
                DescriptorSetCount = 1,
                PSetLayouts = &setLayout,
            };
            DescriptorSet descriptorSet;
            Check(
                _vk.AllocateDescriptorSets(_device, &allocateInfo, out descriptorSet),
                "vkAllocateDescriptorSets");
            resources.DescriptorSet = descriptorSet;

            var imageInfos = new DescriptorImageInfo[textureCount];
            var bufferInfos = new DescriptorBufferInfo[globalBufferCount];
            var writes = new WriteDescriptorSet[bindingCount];
            fixed (DescriptorImageInfo* imageInfoPointer = imageInfos)
            fixed (DescriptorBufferInfo* bufferInfoPointer = bufferInfos)
            fixed (WriteDescriptorSet* writePointer = writes)
            {
                var writeIndex = 0;
                if (globalBufferCount != 0)
                {
                    for (var index = 0; index < globalBufferCount; index++)
                    {
                        bufferInfoPointer[index] = new DescriptorBufferInfo
                        {
                            Buffer = resources.GlobalMemoryBuffers[index].Buffer,
                            Offset = resources.GlobalMemoryBuffers[index].Offset,
                            Range = resources.GlobalMemoryBuffers[index].Size,
                        };
                    }

                    writePointer[writeIndex++] = new WriteDescriptorSet
                    {
                        SType = StructureType.WriteDescriptorSet,
                        DstSet = resources.DescriptorSet,
                        DstBinding = 0,
                        DescriptorCount = (uint)globalBufferCount,
                        DescriptorType = DescriptorType.StorageBuffer,
                        PBufferInfo = bufferInfoPointer,
                    };
                }

                for (var index = 0; index < textureCount; index++)
                {
                    var isStorage = resources.Textures[index].IsStorage;
                    if (!isStorage &&
                        resources.Textures[index].Sampler.Handle == 0)
                    {
                        resources.Textures[index].Sampler =
                            CreateSampler(resources.Textures[index].SamplerState);
                    }

                    imageInfoPointer[index] = new DescriptorImageInfo
                    {
                        Sampler = isStorage ? default : resources.Textures[index].Sampler,
                        ImageView = resources.Textures[index].View,
                        ImageLayout = isStorage ||
                            resources.Textures[index].GuestImage is { } guestImage &&
                            resources.Textures.Any(
                                texture =>
                                    texture.IsStorage &&
                                    texture.GuestImage == guestImage)
                                ? ImageLayout.General
                                : ImageLayout.ShaderReadOnlyOptimal,
                    };
                    writePointer[writeIndex++] = new WriteDescriptorSet
                    {
                        SType = StructureType.WriteDescriptorSet,
                        DstSet = resources.DescriptorSet,
                        DstBinding = (uint)(index + 1),
                        DescriptorCount = 1,
                        DescriptorType = isStorage
                            ? DescriptorType.StorageImage
                            : DescriptorType.CombinedImageSampler,
                        PImageInfo = &imageInfoPointer[index],
                    };
                }

                _vk.UpdateDescriptorSets(
                    _device,
                    (uint)bindingCount,
                    writePointer,
                    0,
                    null);
            }
        }

        private void CreateTranslatedPipeline(
            TranslatedDrawResources resources,
            byte[] vertexSpirv,
            byte[] fragmentSpirv,
            RenderPass renderPass,
            IReadOnlyList<Format> renderTargetFormats,
            Extent2D extent)
        {
            var pipelineKey = new GraphicsPipelineKey(
                GetShaderDigest(vertexSpirv),
                GetShaderDigest(fragmentSpirv),
                string.Join(',', renderTargetFormats.Select(format => (uint)format)),
                resources.HasDepthAttachment,
                resources.Topology,
                string.Join(';', resources.Blends.Select(blend =>
                    $"{(blend.Enable ? 1 : 0)}:{blend.ColorSrcFactor}:{blend.ColorDstFactor}:" +
                    $"{blend.ColorFunc}:{blend.AlphaSrcFactor}:{blend.AlphaDstFactor}:" +
                    $"{blend.AlphaFunc}:{(blend.SeparateAlphaBlend ? 1 : 0)}:{blend.WriteMask}")),
                GetResourceLayoutKey(resources),
                GetVertexLayoutKey(resources),
                resources.Raster,
                resources.HasDepthAttachment ? resources.Depth : GuestDepthState.Default);
            if (_graphicsPipelines.TryGetValue(pipelineKey, out var cachedPipeline))
            {
                resources.Pipeline = cachedPipeline;
                resources.PipelineCached = true;
                return;
            }

            var vertexModule = CreateShaderModule(vertexSpirv);
            var fragmentModule = CreateShaderModule(fragmentSpirv);
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

                var vertexBindingDescriptions =
                    new VertexInputBindingDescription[resources.VertexBuffers.Length];
                var vertexAttributeDescriptions =
                    new VertexInputAttributeDescription[resources.VertexBuffers.Length];
                for (var index = 0; index < resources.VertexBuffers.Length; index++)
                {
                    var vertexBuffer = resources.VertexBuffers[index];
                    vertexBindingDescriptions[index] = new VertexInputBindingDescription
                    {
                        Binding = (uint)index,
                        Stride = vertexBuffer.Stride == 0
                            ? Math.Max(vertexBuffer.ComponentCount, 1) * sizeof(float)
                            : vertexBuffer.Stride,
                        InputRate = VertexInputRate.Vertex,
                    };
                    vertexAttributeDescriptions[index] = new VertexInputAttributeDescription
                    {
                        Location = vertexBuffer.Location,
                        Binding = (uint)index,
                        Format = ToVkVertexFormat(
                            vertexBuffer.DataFormat,
                            vertexBuffer.NumberFormat,
                            vertexBuffer.ComponentCount),
                        Offset = 0,
                    };
                }

                fixed (VertexInputBindingDescription* vertexBindingPointerBase = vertexBindingDescriptions)
                fixed (VertexInputAttributeDescription* vertexAttributePointerBase = vertexAttributeDescriptions)
                {
                    var vertexInput = new PipelineVertexInputStateCreateInfo
                    {
                        SType = StructureType.PipelineVertexInputStateCreateInfo,
                        VertexBindingDescriptionCount = (uint)vertexBindingDescriptions.Length,
                        PVertexBindingDescriptions = vertexBindingDescriptions.Length == 0
                            ? null
                            : vertexBindingPointerBase,
                        VertexAttributeDescriptionCount = (uint)vertexAttributeDescriptions.Length,
                        PVertexAttributeDescriptions = vertexAttributeDescriptions.Length == 0
                            ? null
                            : vertexAttributePointerBase,
                    };
                    var inputAssembly = new PipelineInputAssemblyStateCreateInfo
                    {
                        SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                        Topology = resources.Topology,
                        // Metal always applies primitive restart to strip/fan
                        // topologies with the max index as the cut value, so
                        // match that here. Enabling it for lists is what makes
                        // MoltenVK warn ("Metal does not support disabling
                        // primitive restart"); lists never carry a restart
                        // index, so leaving it off for them is both correct
                        // and warning-free.
                        PrimitiveRestartEnable = RequiresPrimitiveRestart(resources.Topology),
                    };
                    var viewport = new Viewport(0, 0, extent.Width, extent.Height, 0, 1);
                    var scissor = new Rect2D(new Offset2D(0, 0), extent);
                    var viewportState = new PipelineViewportStateCreateInfo
                    {
                        SType = StructureType.PipelineViewportStateCreateInfo,
                        ViewportCount = 1,
                        PViewports = &viewport,
                        ScissorCount = 1,
                        PScissors = &scissor,
                    };
                    var raster = resources.Raster;
                    var cullMode = CullModeFlags.None;
                    if (raster.CullFront)
                    {
                        cullMode |= CullModeFlags.FrontBit;
                    }

                    if (raster.CullBack)
                    {
                        cullMode |= CullModeFlags.BackBit;
                    }

                    var rasterization = new PipelineRasterizationStateCreateInfo
                    {
                        SType = StructureType.PipelineRasterizationStateCreateInfo,
                        // Wireframe (PolygonMode.Line) needs the fillModeNonSolid
                        // device feature and is effectively unused by shipping
                        // titles, so fall back to a solid fill.
                        PolygonMode = PolygonMode.Fill,
                        CullMode = cullMode,
                        FrontFace = raster.FrontFaceClockwise
                            ? FrontFace.Clockwise
                            : FrontFace.CounterClockwise,
                        LineWidth = 1,
                    };
                    var multisample = new PipelineMultisampleStateCreateInfo
                    {
                        SType = StructureType.PipelineMultisampleStateCreateInfo,
                        RasterizationSamples = SampleCountFlags.Count1Bit,
                    };
                    var colorBlendAttachments = stackalloc PipelineColorBlendAttachmentState[resources.Blends.Length];
                    for (var index = 0; index < resources.Blends.Length; index++)
                    {
                        var blend = resources.Blends[index];
                        colorBlendAttachments[index] = new PipelineColorBlendAttachmentState
                        {
                            BlendEnable = blend.Enable &&
                                IsBlendableFormat(renderTargetFormats[index]),
                            SrcColorBlendFactor = ToVkBlendFactor(blend.ColorSrcFactor),
                            DstColorBlendFactor = ToVkBlendFactor(blend.ColorDstFactor),
                            ColorBlendOp = ToVkBlendOp(blend.ColorFunc),
                            SrcAlphaBlendFactor = blend.SeparateAlphaBlend
                                ? ToVkBlendFactor(blend.AlphaSrcFactor)
                                : ToVkBlendFactor(blend.ColorSrcFactor),
                            DstAlphaBlendFactor = blend.SeparateAlphaBlend
                                ? ToVkBlendFactor(blend.AlphaDstFactor)
                                : ToVkBlendFactor(blend.ColorDstFactor),
                            AlphaBlendOp = blend.SeparateAlphaBlend
                                ? ToVkBlendOp(blend.AlphaFunc)
                                : ToVkBlendOp(blend.ColorFunc),
                            ColorWriteMask = ToVkColorWriteMask(blend.WriteMask),
                        };
                    }
                    var colorBlend = new PipelineColorBlendStateCreateInfo
                    {
                        SType = StructureType.PipelineColorBlendStateCreateInfo,
                        AttachmentCount = (uint)resources.Blends.Length,
                        PAttachments = colorBlendAttachments,
                    };
                    var dynamicStateValues = stackalloc DynamicState[2];
                    dynamicStateValues[0] = DynamicState.Viewport;
                    dynamicStateValues[1] = DynamicState.Scissor;
                    var dynamicState = new PipelineDynamicStateCreateInfo
                    {
                        SType = StructureType.PipelineDynamicStateCreateInfo,
                        DynamicStateCount = 2,
                        PDynamicStates = dynamicStateValues,
                    };
                    var depth = resources.Depth;
                    var depthStencil = new PipelineDepthStencilStateCreateInfo
                    {
                        SType = StructureType.PipelineDepthStencilStateCreateInfo,
                        DepthTestEnable = depth.TestEnable,
                        DepthWriteEnable = depth.WriteEnable,
                        DepthCompareOp = ToVkCompareOp(depth.CompareOp),
                        DepthBoundsTestEnable = false,
                        StencilTestEnable = false,
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
                        PDepthStencilState = resources.HasDepthAttachment ? &depthStencil : null,
                        PDynamicState = &dynamicState,
                        Layout = resources.PipelineLayout,
                        RenderPass = renderPass,
                        Subpass = 0,
                    };
                    Pipeline pipeline;
                    Check(
                        _vk.CreateGraphicsPipelines(
                            _device,
                            _pipelineCache,
                            1,
                            &pipelineInfo,
                        null,
                        out pipeline),
                    "vkCreateGraphicsPipelines(translated)");
                    MarkPipelineCacheDirty();
                    resources.Pipeline = pipeline;
                    resources.PipelineCached = true;
                    _graphicsPipelines.Add(pipelineKey, pipeline);
                    Interlocked.Increment(ref _perfPipelineCreations);
                    SetDebugName(
                        ObjectType.Pipeline,
                        pipeline.Handle,
                        $"SharpEmu graphics ps={fragmentSpirv.Length}b attrs={resources.Textures.Length}");
                }
            }
            finally
            {
                SilkMarshal.Free((nint)entryPoint);
                _vk.DestroyShaderModule(_device, fragmentModule, null);
                _vk.DestroyShaderModule(_device, vertexModule, null);
            }
        }

        private DescriptorLayoutBundle GetOrCreateDescriptorLayout(
            TranslatedDrawResources resources,
            ShaderStageFlags stageFlags,
            int bindingCount)
        {
            var key = new DescriptorLayoutKey(stageFlags, GetResourceLayoutKey(resources));
            if (_descriptorLayouts.TryGetValue(key, out var cached))
            {
                return cached;
            }

            DescriptorSetLayout descriptorSetLayout = default;
            if (bindingCount != 0)
            {
                var bindings = new DescriptorSetLayoutBinding[bindingCount];
                var bindingOffset = 0;
                if (resources.GlobalMemoryBuffers.Length != 0)
                {
                    bindings[bindingOffset++] = new DescriptorSetLayoutBinding
                    {
                        Binding = 0,
                        DescriptorType = DescriptorType.StorageBuffer,
                        DescriptorCount = (uint)resources.GlobalMemoryBuffers.Length,
                        StageFlags = stageFlags,
                    };
                }

                for (var index = 0; index < resources.Textures.Length; index++)
                {
                    bindings[bindingOffset + index] = new DescriptorSetLayoutBinding
                    {
                        Binding = (uint)(index + 1),
                        DescriptorType = resources.Textures[index].IsStorage
                            ? DescriptorType.StorageImage
                            : DescriptorType.CombinedImageSampler,
                        DescriptorCount = 1,
                        StageFlags = stageFlags,
                    };
                }

                fixed (DescriptorSetLayoutBinding* bindingPointer = bindings)
                {
                    var descriptorInfo = new DescriptorSetLayoutCreateInfo
                    {
                        SType = StructureType.DescriptorSetLayoutCreateInfo,
                        BindingCount = (uint)bindings.Length,
                        PBindings = bindingPointer,
                    };
                    Check(
                        _vk.CreateDescriptorSetLayout(
                            _device,
                            &descriptorInfo,
                            null,
                            out descriptorSetLayout),
                        "vkCreateDescriptorSetLayout");
                }
            }

            var pipelineInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
            };
            if (descriptorSetLayout.Handle != 0)
            {
                pipelineInfo.SetLayoutCount = 1;
                pipelineInfo.PSetLayouts = &descriptorSetLayout;
            }

            var computePushConstantRange = new PushConstantRange
            {
                StageFlags = ShaderStageFlags.ComputeBit,
                Offset = 0,
                Size = 3 * sizeof(uint),
            };
            if ((stageFlags & ShaderStageFlags.ComputeBit) != 0)
            {
                pipelineInfo.PushConstantRangeCount = 1;
                pipelineInfo.PPushConstantRanges = &computePushConstantRange;
            }

            PipelineLayout pipelineLayout;
            Check(
                _vk.CreatePipelineLayout(
                    _device,
                    &pipelineInfo,
                    null,
                    out pipelineLayout),
                "vkCreatePipelineLayout");
            var created = new DescriptorLayoutBundle(descriptorSetLayout, pipelineLayout);
            _descriptorLayouts.Add(key, created);
            return created;
        }

        private string GetShaderDigest(byte[] spirv)
        {
            if (_shaderDigests.TryGetValue(spirv, out var digest))
            {
                return digest;
            }

            digest = Convert.ToHexString(SHA256.HashData(spirv));
            _shaderDigests.Add(spirv, digest);
            return digest;
        }

        private static string GetResourceLayoutKey(TranslatedDrawResources resources) =>
            resources.ResourceLayoutKey ??= BuildResourceLayoutKey(resources);

        private static string GetVertexLayoutKey(TranslatedDrawResources resources) =>
            resources.VertexLayoutKey ??= BuildVertexLayoutKey(resources);

        private static string BuildResourceLayoutKey(TranslatedDrawResources resources)
        {
            var key = new StringBuilder();
            key.Append(resources.GlobalMemoryBuffers.Length).Append(':');
            foreach (var texture in resources.Textures)
            {
                key.Append(texture.IsStorage ? 'S' : 'T');
            }

            return key.ToString();
        }

        private static string BuildVertexLayoutKey(TranslatedDrawResources resources)
        {
            var key = new StringBuilder();
            foreach (var buffer in resources.VertexBuffers)
            {
                key.Append(buffer.Location).Append(',')
                    .Append(buffer.ComponentCount).Append(',')
                    .Append(buffer.DataFormat).Append(',')
                    .Append(buffer.NumberFormat).Append(',')
                    .Append(buffer.Stride == 0
                        ? Math.Max(buffer.ComponentCount, 1) * sizeof(float)
                        : buffer.Stride)
                    .Append(';');
            }

            return key.ToString();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void CreateComputePipeline(
            TranslatedDrawResources resources,
            byte[] computeSpirv)
        {
            var pipelineKey = new ComputePipelineKey(
                GetShaderDigest(computeSpirv),
                GetResourceLayoutKey(resources));
            if (_computePipelines.TryGetValue(pipelineKey, out var cachedPipeline))
            {
                resources.Pipeline = cachedPipeline;
                resources.PipelineCached = true;
                return;
            }

            var computeModule = CreateShaderModule(computeSpirv);
            var entryPoint = (byte*)SilkMarshal.StringToPtr("main");
            try
            {
                var stage = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.ComputeBit,
                    Module = computeModule,
                    PName = entryPoint,
                };
                var pipelineInfo = new ComputePipelineCreateInfo
                {
                    SType = StructureType.ComputePipelineCreateInfo,
                    Flags = PipelineCreateFlags.CreateDispatchBaseBit,
                    Stage = stage,
                    Layout = resources.PipelineLayout,
                };
                Pipeline pipeline;
                Check(
                    _vk.CreateComputePipelines(
                        _device,
                        _pipelineCache,
                        1,
                        &pipelineInfo,
                        null,
                        out pipeline),
                    "vkCreateComputePipelines(translated)");
                MarkPipelineCacheDirty();
                resources.Pipeline = pipeline;
                resources.PipelineCached = true;
                SetDebugName(
                    ObjectType.Pipeline,
                    pipeline.Handle,
                    $"SharpEmu compute cs={computeSpirv.Length}b");
                _computePipelines.Add(pipelineKey, pipeline);
            }
            finally
            {
                SilkMarshal.Free((nint)entryPoint);
                _vk.DestroyShaderModule(_device, computeModule, null);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private TextureResource ResolveTextureResource(GuestDrawTexture texture)
        {
            if (texture.IsStorage)
            {
                return ResolveStorageImageResource(texture);
            }

            if (texture.Address != 0 &&
                TryResolveGuestDepthTexture(texture, out var depthTexture))
            {
                return depthTexture;
            }

            var vkFormat = GetTextureFormat(texture.Format, texture.NumberType);
            if (texture.Address != 0 &&
                TryResolveGuestImageAlias(texture, vkFormat, out var guestImage) &&
                TryGetOrCreateGuestImageView(
                    guestImage,
                    vkFormat,
                    mipLevel: texture.BaseMipLevel,
                    levelCount: texture.MipLevels,
                    dstSelect: texture.DstSelect,
                    out var view))
            {
                if (ShouldTraceVulkanResources() &&
                    _tracedTextureCacheHits.Add(
                        (texture.Address, texture.Width, texture.Height, vkFormat)))
                {
                    Console.Error.WriteLine(
                        $"[LOADER][TRACE] vk.texture_cache_hit addr=0x{texture.Address:X16} " +
                        $"size={texture.Width}x{texture.Height} " +
                        $"image_format={guestImage.Format} view_format={vkFormat}");
                }

                if (guestImage.Width != texture.Width ||
                    guestImage.Height != texture.Height)
                {
                    TraceVulkanShader(
                        $"vk.texture_cache_alias addr=0x{texture.Address:X16} " +
                        $"texture={texture.Width}x{texture.Height} " +
                        $"image={guestImage.Width}x{guestImage.Height} " +
                        $"tile={texture.TileMode} format={vkFormat}");
                }

                if (string.Equals(
                        Environment.GetEnvironmentVariable("SHARPEMU_TRACE_GUEST_IMAGES"),
                        "alias",
                        StringComparison.OrdinalIgnoreCase) &&
                    _tracedGuestImageContents.Add(guestImage.Address))
                {
                    // Deferred: reading back here would clobber the command
                    // buffer mid-recording; drained after the next present.
                    _pendingAliasImageDumps.Enqueue(guestImage);
                }

                if (TryCreateCpuTextureRefreshResource(
                        texture,
                        guestImage,
                        view,
                        out var refreshResource))
                {
                    return refreshResource;
                }

                return new TextureResource
                {
                    Address = texture.Address,
                    Image = guestImage.Image,
                    View = view,
                    Width = guestImage.Width,
                    Height = guestImage.Height,
                    RowLength = guestImage.Width,
                    DstSelect = texture.DstSelect,
                    SamplerState = texture.Sampler,
                    GuestImage = guestImage,
                };
            }

            if (ShouldTraceVulkanResources() && texture.Address != 0)
            {
                if (_guestImages.TryGetValue(texture.Address, out var missImage))
                {
                    Console.Error.WriteLine(
                        $"[LOADER][TRACE] vk.alias_miss addr=0x{texture.Address:X16} " +
                        $"reason={(IsCompatibleGuestImageAlias(texture, missImage) ? "format" : "size")} " +
                        $"tex={texture.Width}x{texture.Height}/f{texture.Format}/n{texture.NumberType}/vk{vkFormat} " +
                        $"img={missImage.Width}x{missImage.Height}/imgfmt{missImage.Format} " +
                        $"init={missImage.Initialized}");
                }
                else
                {
                    Console.Error.WriteLine(
                        $"[LOADER][TRACE] vk.alias_miss addr=0x{texture.Address:X16} " +
                        $"reason=absent tex={texture.Width}x{texture.Height}/f{texture.Format}/n{texture.NumberType}");
                }
            }

            return GetOrCreateCachedTextureResource(texture);
        }

        private bool TryCreateCpuTextureRefreshResource(
            GuestDrawTexture texture,
            GuestImageResource guestImage,
            ImageView view,
            out TextureResource resource)
        {
            resource = default!;
            if (!guestImage.IsCpuBacked ||
                guestImage.Width != texture.Width ||
                guestImage.Height != texture.Height ||
                guestImage.MipLevels != 1 ||
                texture.RgbaPixels.Length == 0)
            {
                return false;
            }

            var rowLength = texture.TileMode == 0
                ? Math.Max(texture.Pitch, texture.Width)
                : texture.Width;
            var expectedSize = GetTextureByteCount(texture.Format, rowLength, texture.Height);
            if (expectedSize == 0 || expectedSize > int.MaxValue)
            {
                return false;
            }

            var pixels = texture.RgbaPixels.Length == (int)expectedSize
                ? texture.RgbaPixels
                : CreateFallbackTexturePixels(texture.Format, rowLength, texture.Height, expectedSize);
            var fingerprint = ComputeTextureContentFingerprint(pixels);
            if ((guestImage.Initialized || guestImage.InitialUploadPending) &&
                guestImage.CpuContentFingerprint == fingerprint)
            {
                return false;
            }

            var uploadPixels = texture.Format == 13
                ? ExpandRgb32Pixels(pixels)
                : pixels;
            var debugName = TextureDebugName(texture, guestImage.Format);
            var (stagingBuffer, stagingMemory) = CreateTextureStagingBuffer(
                uploadPixels,
                $"{debugName} refresh staging");
            TraceVulkanShader(
                $"vk.texture_refresh addr=0x{texture.Address:X16} " +
                $"size={texture.Width}x{texture.Height} bytes={uploadPixels.Length}");
            resource = new TextureResource
            {
                Address = texture.Address,
                StagingBuffer = stagingBuffer,
                StagingMemory = stagingMemory,
                Image = guestImage.Image,
                View = view,
                Width = guestImage.Width,
                Height = guestImage.Height,
                RowLength = rowLength,
                DstSelect = texture.DstSelect,
                NeedsUpload = true,
                SamplerState = texture.Sampler,
                GuestImage = guestImage,
                CpuContentFingerprint = fingerprint,
                UpdatesCpuContent = true,
            };
            return true;
        }

        private bool TryResolveGuestImageAlias(
            GuestDrawTexture texture,
            Format viewFormat,
            out GuestImageResource guestImage)
        {
            GuestImageResource? best = null;
            var bestScore = int.MinValue;
            var guestFormat = GetGuestTextureFormat(texture.Format, texture.NumberType);

            void Consider(GuestImageResource candidate, bool isActive)
            {
                if (!IsUsableGuestImageAlias(texture, viewFormat, candidate))
                {
                    return;
                }

                // Prefer an exact descriptor match. Initialization is important,
                // but it must not make a differently sized alias outrank the image
                // that the texture descriptor actually names.
                var score = 0;
                if (candidate.Width == texture.Width &&
                    candidate.Height == texture.Height)
                {
                    score += 32;
                }
                if (candidate.Format == viewFormat)
                {
                    score += 16;
                }
                if (candidate.GuestFormat == guestFormat)
                {
                    score += 8;
                }
                if (candidate.Initialized)
                {
                    score += 4;
                }
                if (candidate.MipLevels == texture.ResourceMipLevels)
                {
                    score += 2;
                }
                if (isActive)
                {
                    score += 1;
                }

                if (score > bestScore)
                {
                    best = candidate;
                    bestScore = score;
                }
            }

            if (_guestImages.TryGetValue(texture.Address, out var active))
            {
                Consider(active, isActive: true);
            }

            foreach (var (key, candidate) in _guestImageVariants)
            {
                if (key.Address == texture.Address)
                {
                    Consider(candidate, isActive: false);
                }
            }

            if (best is not null)
            {
                guestImage = best;
                if (ShouldTraceVulkanResources())
                {
                    Console.Error.WriteLine(
                        $"[LOADER][TRACE] vk.texture_variant_hit " +
                        $"addr=0x{texture.Address:X16} " +
                        $"tex={texture.Width}x{texture.Height}/{viewFormat} " +
                        $"image={best.Width}x{best.Height}/{best.Format} " +
                        $"initialized={best.Initialized}");
                }
                return true;
            }

            guestImage = null!;
            return false;
        }

        private static bool IsUsableGuestImageAlias(
            GuestDrawTexture texture,
            Format viewFormat,
            GuestImageResource guestImage) =>
            texture.BaseMipLevel < guestImage.MipLevels &&
            IsCompatibleGuestImageAlias(texture, guestImage) &&
            IsCompatibleViewFormat(guestImage.Format, viewFormat);

        private bool TryResolveGuestDepthTexture(
            GuestDrawTexture texture,
            out TextureResource resource)
        {
            foreach (var depth in _guestDepthImages.Values)
            {
                if (texture.Address != depth.Address &&
                    texture.Address != depth.ReadAddress &&
                    texture.Address != depth.WriteAddress)
                {
                    continue;
                }

                if (texture.Width > depth.Width || texture.Height > depth.Height)
                {
                    continue;
                }

                if (!depth.SampleViews.TryGetValue(texture.DstSelect, out var view))
                {
                    var viewInfo = new ImageViewCreateInfo
                    {
                        SType = StructureType.ImageViewCreateInfo,
                        Image = depth.Image,
                        ViewType = ImageViewType.Type2D,
                        Format = DepthFormat,
                        Components = ToVkComponentMapping(texture.DstSelect),
                        SubresourceRange = new ImageSubresourceRange(
                            ImageAspectFlags.DepthBit,
                            0,
                            1,
                            0,
                            1),
                    };
                    Check(
                        _vk.CreateImageView(_device, &viewInfo, null, out view),
                        "vkCreateImageView(depth sample)");
                    SetDebugName(
                        ObjectType.ImageView,
                        view.Handle,
                        $"SharpEmu guest depth sample 0x{depth.Address:X16} " +
                        $"dst=0x{texture.DstSelect:X3}");
                    depth.SampleViews.Add(texture.DstSelect, view);
                }

                if (_tracedDepthTextureAliases.Add(
                        (texture.Address, texture.Width, texture.Height, texture.DstSelect)))
                {
                    TraceVulkanShader(
                        $"vk.depth_texture_alias addr=0x{texture.Address:X16} " +
                        $"depth=0x{depth.Address:X16} " +
                        $"texture={texture.Width}x{texture.Height} " +
                        $"surface={depth.Width}x{depth.Height} dst=0x{texture.DstSelect:X3}");
                }
                resource = new TextureResource
                {
                    Address = texture.Address,
                    Image = depth.Image,
                    View = view,
                    Width = depth.Width,
                    Height = depth.Height,
                    RowLength = depth.Width,
                    DstSelect = texture.DstSelect,
                    SamplerState = texture.Sampler,
                    GuestDepth = depth,
                };
                return true;
            }

            resource = null!;
            return false;
        }

        private readonly Dictionary<TextureContentIdentity, TextureResource> _textureCache = new();

        /// <summary>
        /// Guest textures are static assets in the common case, but every draw
        /// used to restage and reupload them (a fresh image, device memory and
        /// staging buffer per draw). Cache the uploaded resource per descriptor
        /// identity and invalidate through the CPU write tracker, so animated
        /// or streamed texture memory still refreshes.
        /// </summary>
        private TextureResource GetOrCreateCachedTextureResource(GuestDrawTexture texture)
        {
            if (texture.Address == 0)
            {
                return CreateTextureResource(texture);
            }

            var key = new TextureContentIdentity(
                texture.Address,
                texture.Width,
                texture.Height,
                texture.Format,
                texture.NumberType,
                texture.DstSelect,
                texture.TileMode,
                texture.Pitch,
                texture.Sampler);
            if (_textureCache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            // Empty pixels mean the submit thread skipped the guest-memory
            // copy because this identity was marked cached; a miss here is
            // an invalidation race (eviction, cache clear). Self-heal by
            // reading the texels directly rather than rendering a fallback.
            if (texture.RgbaPixels.Length == 0)
            {
                var refreshed = TryReadGuestTexturePixels(texture);
                if (refreshed is null)
                {
                    return CreateTextureResource(texture);
                }

                texture = texture with { RgbaPixels = refreshed };
            }

            var resource = CreateTextureResource(texture);
            if (resource.OwnsStorage)
            {
                resource.Cached = true;
                _textureCache[key] = resource;
                MarkTextureContentCached(key);
                SharpEmu.HLE.GuestImageWriteTracker.Track(
                    texture.Address,
                    (ulong)texture.RgbaPixels.Length,
                    CurrentGuestWorkSequenceForDiagnostics,
                    "vulkan.texture-cache");
            }

            return resource;
        }

        private void EvictDirtyCachedTextures()
        {
            if (_textureCache.Count == 0)
            {
                return;
            }

            List<TextureContentIdentity>? evicted = null;
            foreach (var entry in _textureCache)
            {
                if (SharpEmu.HLE.GuestImageWriteTracker.ConsumeDirty(entry.Key.Address))
                {
                    (evicted ??= []).Add(entry.Key);
                }
            }

            if (evicted is null && _textureCache.Count <= 2048)
            {
                return;
            }

            // Destruction is deferred until every submission that may still
            // reference the texture has completed (fences signal in queue
            // order), so eviction never has to drain the GPU. An open batch
            // is flushed first so the retire timeline exactly covers every
            // recorded reference (nothing may guess which submission lands
            // next on the shared queue).
            if (_batchOpen)
            {
                FlushBatchedGuestCommands();
            }

            var retireTimeline = _submitTimeline;
            if (_textureCache.Count > 2048)
            {
                foreach (var entry in _textureCache)
                {
                    _deferredTextureDestroys.Enqueue((entry.Value, retireTimeline));
                }

                _textureCache.Clear();
                ClearCachedTextureIdentities();
                return;
            }

            foreach (var key in evicted!)
            {
                if (_textureCache.Remove(key, out var resource))
                {
                    UnmarkTextureContentCached(key);
                    _deferredTextureDestroys.Enqueue((resource, retireTimeline));
                    SharpEmu.HLE.GuestImageWriteTracker.Rearm(key.Address);
                }
            }
        }

        private void DestroyCachedTextureResource(TextureResource texture)
        {
            texture.Cached = false;
            if (texture.View.Handle != 0)
            {
                _vk.DestroyImageView(_device, texture.View, null);
            }

            if (texture.Image.Handle != 0 && texture.GuestImage is null)
            {
                _vk.DestroyImage(_device, texture.Image, null);
                if (texture.ImageMemory.Handle != 0)
                {
                    _vk.FreeMemory(_device, texture.ImageMemory, null);
                }
            }

            if (texture.StagingBuffer.Handle != 0)
            {
                _vk.DestroyBuffer(_device, texture.StagingBuffer, null);
            }

            if (texture.StagingMemory.Handle != 0)
            {
                _vk.FreeMemory(_device, texture.StagingMemory, null);
            }
        }

        private static bool IsCompatibleGuestImageAlias(
            GuestDrawTexture texture,
            GuestImageResource guestImage)
        {
            if (guestImage.Width == texture.Width &&
                guestImage.Height == texture.Height)
            {
                return true;
            }

            if (texture.TileMode == 0 ||
                texture.Width == 0 ||
                texture.Height == 0)
            {
                return false;
            }

            return texture.Width <= guestImage.Width &&
                texture.Height <= guestImage.Height;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private TextureResource ResolveStorageImageResource(GuestDrawTexture texture)
        {
            if (texture.Address == 0)
            {
                return CreateStorageScratchResource(texture);
            }

            var guestImage = ResolveStorageGuestImage(texture);
            var vkFormat = GetTextureFormat(texture.Format, texture.NumberType);
            var selectedMipLevel = GetStorageMipLevel(texture);
            var view = GetOrCreateGuestImageView(
                guestImage,
                vkFormat,
                selectedMipLevel,
                levelCount: 1);
            var resource = new TextureResource
            {
                Address = texture.Address,
                Image = guestImage.Image,
                View = view,
                Width = guestImage.Width,
                Height = guestImage.Height,
                RowLength = guestImage.Width,
                DstSelect = texture.DstSelect,
                IsStorage = true,
                SamplerState = texture.Sampler,
                GuestImage = guestImage,
            };

            if (!guestImage.Initialized &&
                !guestImage.InitialUploadPending &&
                texture.MipLevel == 0)
            {
                var expectedSize = GetTextureByteCount(
                    texture.Format,
                    texture.Width,
                    texture.Height);
                if ((ulong)texture.RgbaPixels.Length == expectedSize &&
                    texture.RgbaPixels.AsSpan().IndexOfAnyExcept((byte)0) >= 0)
                {
                    var uploadPixels = texture.Format == 13
                        ? ExpandRgb32Pixels(texture.RgbaPixels)
                        : texture.RgbaPixels;
                    var uploadSize = (ulong)uploadPixels.Length;
                    resource.StagingBuffer = CreateBuffer(
                        uploadSize,
                        BufferUsageFlags.TransferSrcBit,
                        MemoryPropertyFlags.HostVisibleBit |
                        MemoryPropertyFlags.HostCoherentBit,
                        out resource.StagingMemory);

                    void* mapped;
                    Check(
                        _vk.MapMemory(
                            _device,
                            resource.StagingMemory,
                            0,
                            uploadSize,
                            0,
                            &mapped),
                        "vkMapMemory(storage texture)");
                    fixed (byte* source = uploadPixels)
                    {
                        System.Buffer.MemoryCopy(
                            source,
                            mapped,
                            uploadPixels.Length,
                            uploadPixels.Length);
                    }

                    _vk.UnmapMemory(_device, resource.StagingMemory);
                    resource.NeedsUpload = true;
                    guestImage.InitialUploadPending = true;
                    TraceVulkanShader(
                        $"vk.storage_upload addr=0x{texture.Address:X16} " +
                        $"size={texture.Width}x{texture.Height} " +
                        $"logical_bytes={expectedSize} upload_bytes={uploadSize}");
                }
            }

            return resource;
        }

        private TextureResource CreateStorageScratchResource(GuestDrawTexture texture)
        {
            var width = Math.Max(texture.Width, 1);
            var height = Math.Max(texture.Height, 1);
            var vkFormat = GetTextureFormat(texture.Format, texture.NumberType);
            var imageInfo = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                ImageType = ImageType.Type2D,
                Format = vkFormat,
                Extent = new Extent3D(width, height, 1),
                MipLevels = 1,
                ArrayLayers = 1,
                Samples = SampleCountFlags.Count1Bit,
                Tiling = ImageTiling.Optimal,
                Usage =
                    ImageUsageFlags.SampledBit |
                    ImageUsageFlags.StorageBit |
                    ImageUsageFlags.TransferSrcBit |
                    ImageUsageFlags.TransferDstBit,
                SharingMode = SharingMode.Exclusive,
                InitialLayout = ImageLayout.Undefined,
            };
            Check(
                _vk.CreateImage(_device, &imageInfo, null, out var image),
                "vkCreateImage(storage scratch)");
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
                "vkAllocateMemory(storage scratch)");
            Check(
                _vk.BindImageMemory(_device, image, memory, 0),
                "vkBindImageMemory(storage scratch)");

            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = image,
                ViewType = ImageViewType.Type2D,
                Format = vkFormat,
                Components = new ComponentMapping(
                    ComponentSwizzle.Identity,
                    ComponentSwizzle.Identity,
                    ComponentSwizzle.Identity,
                    ComponentSwizzle.Identity),
                SubresourceRange = ColorSubresourceRange(),
            };
            Check(
                _vk.CreateImageView(_device, &viewInfo, null, out var view),
                "vkCreateImageView(storage scratch)");
            SetDebugName(ObjectType.Image, image.Handle, $"SharpEmu scratch storage {width}x{height} {vkFormat}");
            SetDebugName(ObjectType.ImageView, view.Handle, $"SharpEmu scratch storage {width}x{height} {vkFormat} view");

            var guestImage = new GuestImageResource
            {
                Address = 0,
                Width = width,
                Height = height,
                MipLevels = 1,
                GuestFormat = GetGuestTextureFormat(texture.Format, texture.NumberType),
                Format = vkFormat,
                Image = image,
                Memory = memory,
                View = view,
            };

            return new TextureResource
            {
                Address = 0,
                Image = image,
                ImageMemory = memory,
                View = view,
                Width = width,
                Height = height,
                RowLength = width,
                DstSelect = texture.DstSelect,
                OwnsStorage = true,
                IsStorage = true,
                SamplerState = texture.Sampler,
                GuestImage = guestImage,
            };
        }

        private GuestImageResource ResolveStorageGuestImage(GuestDrawTexture texture)
        {
            if (texture.Address == 0)
            {
                throw new InvalidOperationException("Storage image has no guest address.");
            }

            var format = GetTextureFormat(texture.Format, texture.NumberType);
            var guestImage = GetOrCreateGuestImage(
                new GuestRenderTarget(
                    texture.Address,
                    texture.Width,
                    texture.Height,
                    texture.Format,
                    texture.NumberType,
                    texture.ResourceMipLevels),
                format);
            var selectedMipLevel = GetStorageMipLevel(texture);
            if (selectedMipLevel >= guestImage.MipLevels)
            {
                throw new InvalidOperationException(
                    $"Storage mip {selectedMipLevel} (base {texture.BaseMipLevel} + relative " +
                    $"{texture.MipLevel}) exceeds image mip count {guestImage.MipLevels}.");
            }

            return guestImage;
        }

        private static uint GetStorageMipLevel(GuestDrawTexture texture)
        {
            // IMAGE_STORE targets BASE_LEVEL and IMAGE_STORE_MIP's operand is
            // expressed in resource-view space, so Vulkan's absolute image
            // subresource is descriptor base plus the instruction-relative
            // mip. Sampled views achieve the same mapping through their view
            // base in ResolveTextureResource.
            var selectedMipLevel = (ulong)texture.BaseMipLevel + texture.MipLevel;
            if (selectedMipLevel > uint.MaxValue)
            {
                throw new InvalidOperationException(
                    $"Storage mip overflow (base {texture.BaseMipLevel} + relative {texture.MipLevel}).");
            }

            return (uint)selectedMipLevel;
        }

        private TextureResource CreateTextureResource(GuestDrawTexture texture)
        {
            var width = Math.Max(texture.Width, 1);
            var height = Math.Max(texture.Height, 1);
            var rowLength = texture.TileMode == 0
                ? Math.Max(texture.Pitch, width)
                : width;
            var vkFormat = GetTextureFormat(texture.Format, texture.NumberType);

            var expectedSize = GetTextureByteCount(texture.Format, rowLength, height);
            if (ShouldTraceVulkanResources() &&
                _tracedTextureUploads.Add((texture.Address, width, height, vkFormat)))
            {
                Console.Error.WriteLine(
                    $"[LOADER][TRACE] vk.texture addr=0x{texture.Address:X16} " +
                    $"fmt={texture.Format} num={texture.NumberType} vk={vkFormat} " +
                    $"size={width}x{height} row={rowLength} tile={texture.TileMode} " +
                    $"dst=0x{texture.DstSelect:X3} " +
                    $"bytes={texture.RgbaPixels.Length} expected={expectedSize}");
            }
            var pixels = texture.RgbaPixels.Length == (int)expectedSize
                ? texture.RgbaPixels
                : CreateFallbackTexturePixels(texture.Format, rowLength, height, expectedSize);
            if (AddressListContains("SHARPEMU_FORCE_WHITE_TEXTURE_TARGETS", texture.Address))
            {
                pixels = pixels.ToArray();
                pixels.AsSpan().Fill(0xFF);
                Console.Error.WriteLine(
                    $"[LOADER][TRACE] vk.texture_force_white addr=0x{texture.Address:X16} " +
                    $"size={width}x{height} bytes={pixels.Length}");
            }
            DumpTextureUpload(texture, pixels, rowLength, width, height);
            TraceTextureUploadContents(texture, pixels, rowLength, width, height, vkFormat);
            var uploadPixels = texture.Format == 13
                ? ExpandRgb32Pixels(pixels)
                : pixels;
            var contentFingerprint = ComputeTextureContentFingerprint(pixels);

            var (stagingBuffer, stagingMemory) = CreateTextureStagingBuffer(
                uploadPixels,
                $"{TextureDebugName(texture, vkFormat)} staging");

            var supportsAttachmentUsage = !IsBlockCompressedFormat(vkFormat);
            var imageInfo = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                Flags = supportsAttachmentUsage
                    ? ImageCreateFlags.CreateMutableFormatBit | ImageCreateFlags.CreateExtendedUsageBit
                    : 0,
                ImageType = ImageType.Type2D,
                Format = vkFormat,
                Extent = new Extent3D(width, height, 1),
                MipLevels = 1,
                ArrayLayers = 1,
                Samples = SampleCountFlags.Count1Bit,
                Tiling = ImageTiling.Optimal,
                Usage = supportsAttachmentUsage
                    ? ImageUsageFlags.TransferDstBit |
                      ImageUsageFlags.SampledBit |
                      ImageUsageFlags.ColorAttachmentBit |
                      ImageUsageFlags.StorageBit |
                      ImageUsageFlags.TransferSrcBit
                    : ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit,
                SharingMode = SharingMode.Exclusive,
                InitialLayout = ImageLayout.Undefined,
            };
            Check(_vk.CreateImage(_device, &imageInfo, null, out var image), "vkCreateImage(texture)");
            _vk.GetImageMemoryRequirements(_device, image, out var imageRequirements);
            var memoryInfo = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = imageRequirements.Size,
                MemoryTypeIndex = FindMemoryType(
                    imageRequirements.MemoryTypeBits,
                    MemoryPropertyFlags.DeviceLocalBit),
            };
            Check(_vk.AllocateMemory(_device, &memoryInfo, null, out var imageMemory), "vkAllocateMemory(texture)");
            Check(_vk.BindImageMemory(_device, image, imageMemory, 0), "vkBindImageMemory(texture)");

            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = image,
                ViewType = ImageViewType.Type2D,
                Format = vkFormat,
                Components = ToVkComponentMapping(texture.DstSelect),
                SubresourceRange = ColorSubresourceRange(),
            };
            Check(_vk.CreateImageView(_device, &viewInfo, null, out var view), "vkCreateImageView(texture)");
            var debugName = TextureDebugName(texture, vkFormat);
            SetDebugName(ObjectType.Image, image.Handle, $"{debugName} image");
            SetDebugName(ObjectType.ImageView, view.Handle, $"{debugName} view");
            var resource = new TextureResource
            {
                Address = texture.Address,
                StagingBuffer = stagingBuffer,
                StagingMemory = stagingMemory,
                Image = image,
                ImageMemory = imageMemory,
                View = view,
                Width = width,
                Height = height,
                RowLength = rowLength,
                DstSelect = texture.DstSelect,
                NeedsUpload = true,
                OwnsStorage = true,
                SamplerState = texture.Sampler,
                CpuContentFingerprint = contentFingerprint,
                UpdatesCpuContent = texture.Address != 0,
            };

            if (texture.Address != 0 &&
                !_guestImages.ContainsKey(texture.Address))
            {
                var guestFormat = GetGuestTextureFormat(texture.Format, texture.NumberType);
                var guestImage = new GuestImageResource
                {
                    Address = texture.Address,
                    Width = width,
                    Height = height,
                    MipLevels = 1,
                    GuestFormat = guestFormat,
                    Format = vkFormat,
                    Image = image,
                    Memory = imageMemory,
                    View = view,
                    InitialUploadPending = true,
                    IsCpuBacked = true,
                    CpuContentFingerprint = contentFingerprint,
                };
                _guestImages.Add(texture.Address, guestImage);
                resource.OwnsStorage = false;
                resource.GuestImage = guestImage;
                lock (_gate)
                {
                    if (guestFormat != 0)
                    {
                        _availableGuestImages[texture.Address] = guestFormat;
                    }

                    _guestImageExtents[texture.Address] =
                        (width, height, expectedSize);
                }
            }

            return resource;
        }

        private (VkBuffer Buffer, DeviceMemory Memory) CreateTextureStagingBuffer(
            byte[] pixels,
            string debugName)
        {
            var size = (ulong)pixels.Length;
            var buffer = CreateBuffer(
                size,
                BufferUsageFlags.TransferSrcBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                out var memory);
            void* mapped;
            Check(_vk.MapMemory(_device, memory, 0, size, 0, &mapped), "vkMapMemory(texture)");
            fixed (byte* source = pixels)
            {
                System.Buffer.MemoryCopy(source, mapped, pixels.Length, pixels.Length);
            }
            _vk.UnmapMemory(_device, memory);
            SetDebugName(ObjectType.Buffer, buffer.Handle, debugName);
            return (buffer, memory);
        }

        private static ulong ComputeTextureContentFingerprint(ReadOnlySpan<byte> pixels)
        {
            const ulong offsetBasis = 14695981039346656037;
            const ulong prime = 1099511628211;

            var h0 = offsetBasis ^ (ulong)pixels.Length;
            var h1 = offsetBasis;
            var h2 = offsetBasis;
            var h3 = offsetBasis;

            var words = MemoryMarshal.Cast<byte, ulong>(pixels);
            var index = 0;
            var blockEnd = words.Length - (words.Length & 3);
            for (; index < blockEnd; index += 4)
            {
                h0 = (h0 ^ words[index]) * prime;
                h1 = (h1 ^ words[index + 1]) * prime;
                h2 = (h2 ^ words[index + 2]) * prime;
                h3 = (h3 ^ words[index + 3]) * prime;
            }

            for (; index < words.Length; index++)
            {
                h0 = (h0 ^ words[index]) * prime;
            }

            var hash = h0;
            hash = (hash ^ h1) * prime;
            hash = (hash ^ h2) * prime;
            hash = (hash ^ h3) * prime;

            foreach (var value in pixels[(words.Length * sizeof(ulong))..])
            {
                hash = (hash ^ value) * prime;
            }

            return hash;
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

                var viewFormat = GetTextureFormat(texture.Format, texture.NumberType);
                if (!IsCompatibleViewFormat(source.Format, viewFormat))
                {
                    throw new InvalidOperationException(
                        $"Feedback view format {viewFormat} is incompatible with " +
                        $"render-target format {source.Format}.");
                }

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

        private TextureResource CreateDepthFeedbackSnapshot(
            GuestDrawTexture texture,
            GuestDepthResource source)
        {
            var image = default(Image);
            var memory = default(DeviceMemory);
            var view = default(ImageView);
            try
            {
                var imageInfo = new ImageCreateInfo
                {
                    SType = StructureType.ImageCreateInfo,
                    ImageType = ImageType.Type2D,
                    Format = DepthFormat,
                    Extent = new Extent3D(source.Width, source.Height, 1),
                    MipLevels = 1,
                    ArrayLayers = 1,
                    Samples = SampleCountFlags.Count1Bit,
                    Tiling = ImageTiling.Optimal,
                    Usage = ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit,
                    SharingMode = SharingMode.Exclusive,
                    InitialLayout = ImageLayout.Undefined,
                };
                Check(
                    _vk.CreateImage(_device, &imageInfo, null, out image),
                    "vkCreateImage(depth feedback snapshot)");
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
                    "vkAllocateMemory(depth feedback snapshot)");
                Check(
                    _vk.BindImageMemory(_device, image, memory, 0),
                    "vkBindImageMemory(depth feedback snapshot)");
                var viewInfo = new ImageViewCreateInfo
                {
                    SType = StructureType.ImageViewCreateInfo,
                    Image = image,
                    ViewType = ImageViewType.Type2D,
                    Format = DepthFormat,
                    Components = ToVkComponentMapping(texture.DstSelect),
                    SubresourceRange = new ImageSubresourceRange(
                        ImageAspectFlags.DepthBit,
                        0,
                        1,
                        0,
                        1),
                };
                Check(
                    _vk.CreateImageView(_device, &viewInfo, null, out view),
                    "vkCreateImageView(depth feedback snapshot)");
                SetDebugName(
                    ObjectType.Image,
                    image.Handle,
                    $"SharpEmu depth feedback 0x{source.Address:X16} image");
                SetDebugName(
                    ObjectType.ImageView,
                    view.Handle,
                    $"SharpEmu depth feedback 0x{source.Address:X16} view");
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
                    SamplerState = texture.Sampler,
                    DepthFeedbackSource = source,
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

        private void DumpTextureUpload(
            GuestDrawTexture texture,
            byte[] pixels,
            uint rowLength,
            uint width,
            uint height)
        {
            if (!string.Equals(
                    Environment.GetEnvironmentVariable("SHARPEMU_DUMP_TEXTURES"),
                    "1",
                    StringComparison.Ordinal) ||
                texture.IsFallback ||
                texture.IsStorage ||
                GetTextureBytesPerPixel(texture.Format) != 4 ||
                width == 0 ||
                height == 0 ||
                !_dumpedTextures.Add((texture.Address, width, height, texture.Format)))
            {
                return;
            }

            var rowBytes = checked((int)rowLength * 4);
            var visibleRowBytes = checked((int)width * 4);
            if (pixels.Length < checked(rowBytes * (int)height))
            {
                return;
            }

            var directory = Path.Combine(AppContext.BaseDirectory, "texture-dumps");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(
                directory,
                $"tex-{texture.Address:X16}-{width}x{height}-fmt{texture.Format}-row{rowLength}.bmp");
            WriteRgbaBmp(path, pixels, rowBytes, visibleRowBytes, (int)width, (int)height);
        }

        private void TraceTextureUploadContents(
            GuestDrawTexture texture,
            byte[] pixels,
            uint rowLength,
            uint width,
            uint height,
            Format format)
        {
            if (!_traceGuestImageAddressFilterEnabled ||
                !AddressListContains("SHARPEMU_TRACE_GUEST_IMAGE_ADDRS", texture.Address) ||
                !_tracedTextureUploadContents.Add(
                    (texture.Address, width, height, texture.Format)))
            {
                return;
            }

            var bytesPerPixel = checked((uint)GetTextureBytesPerPixel(texture.Format));
            var nonzeroBytes = 0L;
            ulong hash = 14695981039346656037UL;
            foreach (var value in pixels)
            {
                nonzeroBytes += value == 0 ? 0 : 1;
                hash = (hash ^ value) * 1099511628211UL;
            }

            var centerOffset = checked(
                ((int)(height / 2) * (int)rowLength + (int)(width / 2)) *
                (int)bytesPerPixel);
            var center = centerOffset + bytesPerPixel <= pixels.Length
                ? Convert.ToHexString(
                    pixels.AsSpan(centerOffset, checked((int)bytesPerPixel)))
                : "out-of-range";
            Console.Error.WriteLine(
                "[LOADER][TRACE] " +
                $"vk.texture_upload_contents addr=0x{texture.Address:X16} " +
                $"size={width}x{height} row={rowLength} format={format} " +
                $"guest_format={texture.Format} nonzero_bytes={nonzeroBytes}/{pixels.Length} " +
                $"nonblack_pixels={CountNonblackPixels(pixels, format, bytesPerPixel)}/{(ulong)rowLength * height} " +
                $"center={center} sample_unique={CountSampledUniquePixels(pixels, bytesPerPixel)} " +
                $"hash=0x{hash:X16}");

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
                $"{sequence:D4}-texture-0x{texture.Address:X16}-{width}x{height}-" +
                $"row{rowLength}-fmt{texture.Format}-{format}.rgba");
            File.WriteAllBytes(path, pixels);
        }

        private static void WriteRgbaBmp(
            string path,
            byte[] rgba,
            int sourceRowBytes,
            int visibleRowBytes,
            int width,
            int height)
        {
            const int fileHeaderSize = 14;
            const int infoHeaderSize = 40;
            const int bytesPerPixel = 4;
            var pixelBytes = checked(width * height * bytesPerPixel);
            var fileSize = fileHeaderSize + infoHeaderSize + pixelBytes;
            var output = new byte[fileSize];

            output[0] = (byte)'B';
            output[1] = (byte)'M';
            WriteUInt32(output, 2, (uint)fileSize);
            WriteUInt32(output, 10, fileHeaderSize + infoHeaderSize);
            WriteUInt32(output, 14, infoHeaderSize);
            WriteInt32(output, 18, width);
            WriteInt32(output, 22, -height);
            WriteUInt16(output, 26, 1);
            WriteUInt16(output, 28, 32);
            WriteUInt32(output, 34, (uint)pixelBytes);

            var destinationOffset = fileHeaderSize + infoHeaderSize;
            for (var y = 0; y < height; y++)
            {
                var sourceOffset = y * sourceRowBytes;
                for (var x = 0; x < visibleRowBytes; x += bytesPerPixel)
                {
                    var destination = destinationOffset + y * visibleRowBytes + x;
                    output[destination + 0] = rgba[sourceOffset + x + 2];
                    output[destination + 1] = rgba[sourceOffset + x + 1];
                    output[destination + 2] = rgba[sourceOffset + x + 0];
                    output[destination + 3] = rgba[sourceOffset + x + 3];
                }
            }

            File.WriteAllBytes(path, output);
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

        private Sampler CreateSampler(GuestSampler sampler)
        {
            if (_samplers.TryGetValue(sampler, out var cachedSampler))
            {
                return cachedSampler;
            }

            var minLod = DecodeSamplerMipFilter(sampler) == 0
                ? 0f
                : DecodeSamplerMinLod(sampler);
            var maxLod = DecodeSamplerMipFilter(sampler) == 0
                ? 0f
                : DecodeSamplerMaxLod(sampler);
            var samplerInfo = new SamplerCreateInfo
            {
                SType = StructureType.SamplerCreateInfo,
                MagFilter = ToVkFilter(DecodeSamplerMagFilter(sampler)),
                MinFilter = ToVkFilter(DecodeSamplerMinFilter(sampler)),
                MipmapMode = ToVkMipFilter(DecodeSamplerMipFilter(sampler)),
                AddressModeU = ToVkSamplerAddressMode(DecodeSamplerClampX(sampler)),
                AddressModeV = ToVkSamplerAddressMode(DecodeSamplerClampY(sampler)),
                AddressModeW = ToVkSamplerAddressMode(DecodeSamplerClampZ(sampler)),
                MipLodBias = DecodeSamplerLodBias(sampler),
                CompareEnable = DecodeSamplerDepthCompare(sampler) != 0,
                CompareOp = ToVkCompareOp(DecodeSamplerDepthCompare(sampler)),
                MinLod = minLod,
                MaxLod = Math.Max(minLod, maxLod),
                BorderColor = ToVkBorderColor(DecodeSamplerBorderColor(sampler)),
            };
            Sampler vkSampler;
            Check(
                _vk.CreateSampler(_device, &samplerInfo, null, out vkSampler),
                "vkCreateSampler(texture)");
            _samplers.Add(sampler, vkSampler);
            return vkSampler;
        }

        private static ComponentMapping ToVkComponentMapping(uint dstSelect)
        {
            return new ComponentMapping(
                ToVkComponentSwizzle(dstSelect & 0x7),
                ToVkComponentSwizzle((dstSelect >> 3) & 0x7),
                ToVkComponentSwizzle((dstSelect >> 6) & 0x7),
                ToVkComponentSwizzle((dstSelect >> 9) & 0x7));
        }

        private static ComponentSwizzle ToVkComponentSwizzle(uint selector) =>
            selector switch
            {
                0 => ComponentSwizzle.Zero,
                1 => ComponentSwizzle.One,
                4 => ComponentSwizzle.R,
                5 => ComponentSwizzle.G,
                6 => ComponentSwizzle.B,
                7 => ComponentSwizzle.A,
                _ => ComponentSwizzle.Identity,
            };

        private static byte[] ExpandRgb32Pixels(byte[] pixels)
        {
            var texelCount = pixels.Length / 12;
            var expanded = new byte[checked(texelCount * 16)];
            for (var texel = 0; texel < texelCount; texel++)
            {
                System.Buffer.BlockCopy(pixels, texel * 12, expanded, texel * 16, 12);
                expanded[texel * 16 + 14] = 0x80;
                expanded[texel * 16 + 15] = 0x3F;
            }

            return expanded;
        }

        private GlobalBufferResource CreateGlobalBufferResource(
            GuestMemoryBuffer guestBuffer)
        {
            if (guestBuffer.BaseAddress == 0)
            {
                return CreateTransientGlobalBufferResource(guestBuffer);
            }

            var size = (ulong)Math.Max(guestBuffer.Length, sizeof(uint));
            var endAddress = checked(guestBuffer.BaseAddress + size);
            GuestBufferAllocation? allocation = null;
            var allocationPriority = -1;
            // This runs for every bound global buffer. Preserve the previous
            // stable queue preference without allocating LINQ sort state.
            foreach (var candidate in _guestBufferAllocations)
            {
                if (candidate.BaseAddress > guestBuffer.BaseAddress ||
                    candidate.BaseAddress + candidate.Size < endAddress)
                {
                    continue;
                }

                var candidatePriority = string.Equals(
                        candidate.QueueName,
                        _activeGuestQueue.Name,
                        StringComparison.Ordinal)
                    ? 2
                    : string.Equals(
                        candidate.QueueName,
                        SharedReadOnlyGuestBufferQueue,
                        StringComparison.Ordinal)
                        ? 1
                        : 0;
                if (candidatePriority > allocationPriority)
                {
                    allocation = candidate;
                    allocationPriority = candidatePriority;
                }
            }

            if (allocation is null)
            {
                throw new InvalidOperationException(
                    $"no Vulkan guest buffer allocation covers " +
                    $"0x{guestBuffer.BaseAddress:X16}-0x{endAddress:X16}");
            }

            var guestOffset = guestBuffer.BaseAddress - allocation.BaseAddress;
            var descriptorOffset = guestOffset &
                ~(GuestStorageBufferOffsetAlignment - 1);
            var byteBias = guestOffset - descriptorOffset;
            if (descriptorOffset % _minStorageBufferOffsetAlignment != 0)
            {
                throw new InvalidOperationException(
                    $"guest buffer alias offset 0x{descriptorOffset:X} is not aligned to Vulkan's " +
                    $"minStorageBufferOffsetAlignment={_minStorageBufferOffsetAlignment}");
            }

            var expectedBias = guestBuffer.BaseAddress &
                (GuestStorageBufferOffsetAlignment - 1);
            if (byteBias != expectedBias)
            {
                throw new InvalidOperationException(
                    $"guest buffer allocation base 0x{allocation.BaseAddress:X16} " +
                    $"does not satisfy alias alignment " +
                    $"{GuestStorageBufferOffsetAlignment}");
            }

            var source = guestBuffer.Data.AsSpan(0, guestBuffer.Length);
            var shadow = allocation.Shadow.AsSpan(checked((int)guestOffset), guestBuffer.Length);
            if (!source.SequenceEqual(shadow))
            {
                // HOST_COHERENT does not permit racing a mapped CPU write with
                // an in-flight shader access. Retire prior users, publish their
                // dirty ranges to guest memory, then upload the current guest
                // bytes (which may be newer than the parser's captured array).
                if (string.Equals(
                        allocation.QueueName,
                        SharedReadOnlyGuestBufferQueue,
                        StringComparison.Ordinal))
                {
                    // Shared read-only storage can still be referenced by any
                    // logical guest queue, so replacing its mapped contents
                    // requires retiring every prior reader.
                    WaitForAllGuestSubmissionsForCpuVisibility();
                    WriteBackAllDirtyGuestBuffers();
                }
                else
                {
                    // Writable aliases are private to one logical guest queue.
                    // Retiring unrelated queues here recreates the global FIFO
                    // and turns routine buffer refreshes into queue-wide stalls.
                    WaitForActiveGuestQueueSubmissionsForCpuVisibility();
                    WriteBackAllDirtyGuestBuffers(_activeGuestQueue.Name);
                }
                var mapped = new Span<byte>(
                    (void*)(allocation.Mapped + checked((nint)guestOffset)),
                    guestBuffer.Length);
                if (_guestMemory?.TryRead(guestBuffer.BaseAddress, mapped) != true)
                {
                    source.CopyTo(mapped);
                }

                mapped.CopyTo(shadow);
            }

            if (ShouldTraceVulkanResources() &&
                _tracedGlobalBuffers.Add((guestBuffer.BaseAddress, guestBuffer.Length)))
            {
                Console.Error.WriteLine(
                    $"[LOADER][TRACE] vk.global_buffer base=0x{guestBuffer.BaseAddress:X16} " +
                    $"bytes={guestBuffer.Length}");
            }
            if (guestBuffer.Pooled)
            {
                GuestDataPool.Return(guestBuffer.Data);
            }

            return new GlobalBufferResource
            {
                BaseAddress = guestBuffer.BaseAddress,
                Writable = guestBuffer.Writable,
                WriteBackToGuest = guestBuffer.WriteBackToGuest,
                Buffer = allocation.Buffer,
                Memory = allocation.Memory,
                Mapped = allocation.Mapped + checked((nint)guestOffset),
                Offset = descriptorOffset,
                Size = checked((size + byteBias + 3) & ~3UL),
                GuestOffset = guestOffset,
                GuestSize = size,
                Allocation = allocation,
            };
        }

        private GlobalBufferResource CreateTransientGlobalBufferResource(
            GuestMemoryBuffer guestBuffer)
        {
            var buffer = CreateHostBuffer(
                guestBuffer.Data.AsSpan(0, guestBuffer.Length),
                BufferUsageFlags.StorageBufferBit,
                out var memory,
                out var mapped);
            if (guestBuffer.Pooled)
            {
                GuestDataPool.Return(guestBuffer.Data);
            }

            return new GlobalBufferResource
            {
                BaseAddress = 0,
                Writable = false,
                WriteBackToGuest = false,
                Buffer = buffer,
                Memory = memory,
                Mapped = mapped,
                Offset = 0,
                Size = (ulong)Math.Max(guestBuffer.Length, sizeof(uint)),
                GuestOffset = 0,
                GuestSize = (ulong)Math.Max(guestBuffer.Length, sizeof(uint)),
            };
        }

        private void PrepareGuestBufferAllocations(
            IReadOnlyList<GuestMemoryBuffer> buffers)
        {
            if (buffers.Count == 0)
            {
                return;
            }

            var ranges = new List<(ulong Start, ulong End, bool Writable)>(buffers.Count);
            foreach (var buffer in buffers)
            {
                if (buffer.BaseAddress == 0)
                {
                    continue;
                }

                var size = (ulong)Math.Max(buffer.Length, sizeof(uint));
                var alignedStart = buffer.BaseAddress &
                    ~(GuestStorageBufferOffsetAlignment - 1);
                var paddedEnd = checked(buffer.BaseAddress + size + 3) & ~3UL;
                ranges.Add((
                    alignedStart,
                    paddedEnd,
                    buffer.Writable && buffer.WriteBackToGuest));
            }

            if (ranges.Count == 0)
            {
                return;
            }

            ranges.Sort(static (left, right) => left.Start.CompareTo(right.Start));
            var merged = new List<(ulong Start, ulong End, bool Writable)>(ranges.Count);
            foreach (var range in ranges)
            {
                if (merged.Count == 0 || range.Start > merged[^1].End)
                {
                    merged.Add(range);
                    continue;
                }

                var previous = merged[^1];
                merged[^1] = (
                    previous.Start,
                    Math.Max(previous.End, range.End),
                    previous.Writable || range.Writable);
            }

            foreach (var range in merged)
            {
                EnsureGuestBufferAllocation(
                    range.Start,
                    range.End,
                    range.Writable
                        ? _activeGuestQueue.Name
                        : SharedReadOnlyGuestBufferQueue);
            }
        }

        private void EnsureGuestBufferAllocation(
            ulong requestedStart,
            ulong requestedEnd,
            string queueName)
        {
            var start = requestedStart;
            var end = requestedEnd;
            List<GuestBufferAllocation> overlaps;
            do
            {
                overlaps = _guestBufferAllocations
                    .Where(allocation =>
                        string.Equals(
                            allocation.QueueName,
                            queueName,
                            StringComparison.Ordinal) &&
                        allocation.BaseAddress < end &&
                        start < allocation.BaseAddress + allocation.Size)
                    .ToList();
                var expandedStart = overlaps.Aggregate(
                    start,
                    static (value, allocation) => Math.Min(value, allocation.BaseAddress));
                var expandedEnd = overlaps.Aggregate(
                    end,
                    static (value, allocation) =>
                        Math.Max(value, allocation.BaseAddress + allocation.Size));
                if (expandedStart == start && expandedEnd == end)
                {
                    break;
                }

                start = expandedStart;
                end = expandedEnd;
            }
            while (true);

            if (overlaps.Count == 1 &&
                overlaps[0].BaseAddress <= requestedStart &&
                overlaps[0].BaseAddress + overlaps[0].Size >= requestedEnd)
            {
                return;
            }

            if (overlaps.Count > 0)
            {
                // Growing/merging an aliased allocation is rare. Synchronize
                // only this structural transition so no in-flight descriptor
                // can observe storage being replaced underneath it.
                WaitForAllGuestSubmissionsForCpuVisibility();
                WriteBackAllDirtyGuestBuffers();
            }

            var replacement = CreateGuestBufferAllocation(start, end, queueName);
            foreach (var overlap in overlaps)
            {
                _guestBufferAllocations.Remove(overlap);
                DestroyGuestBufferAllocation(overlap);
            }

            _guestBufferAllocations.Add(replacement);
            _guestBufferAllocations.Sort(static (left, right) =>
            {
                var queueOrder = string.CompareOrdinal(left.QueueName, right.QueueName);
                return queueOrder != 0
                    ? queueOrder
                    : left.BaseAddress.CompareTo(right.BaseAddress);
            });
            TraceVulkanShader(
                $"vk.guest_buffer_allocation queue={queueName} " +
                $"base=0x{start:X16} bytes={replacement.Size} " +
                $"merged={overlaps.Count}");
        }

        private GuestBufferAllocation CreateGuestBufferAllocation(
            ulong start,
            ulong end,
            string queueName)
        {
            var size = checked(end - start);
            if (size == 0 || size > int.MaxValue)
            {
                throw new InvalidOperationException(
                    $"guest buffer allocation is outside the supported host span: " +
                    $"base=0x{start:X16} bytes={size}");
            }

            var buffer = CreateBuffer(
                size,
                BufferUsageFlags.StorageBufferBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                out var memory);
            void* mapped;
            Check(_vk.MapMemory(_device, memory, 0, size, 0, &mapped), "vkMapMemory(guest buffer)");
            var shadow = new byte[checked((int)size)];
            _ = _guestMemory?.TryRead(start, shadow);
            shadow.CopyTo(new Span<byte>(mapped, shadow.Length));
            SetDebugName(
                ObjectType.Buffer,
                buffer.Handle,
                $"SharpEmu guest VA 0x{start:X16}-0x{end:X16}");
            return new GuestBufferAllocation
            {
                QueueName = queueName,
                BaseAddress = start,
                Size = size,
                Buffer = buffer,
                Memory = memory,
                Mapped = (nint)mapped,
                Shadow = shadow,
            };
        }

        private void DestroyGuestBufferAllocation(GuestBufferAllocation allocation)
        {
            if (allocation.Mapped != 0)
            {
                _vk.UnmapMemory(_device, allocation.Memory);
            }

            _vk.DestroyBuffer(_device, allocation.Buffer, null);
            _vk.FreeMemory(_device, allocation.Memory, null);
        }

        private VertexBufferResource CreateVertexBufferResource(
            GuestVertexBuffer guestBuffer)
        {
            ReadOnlySpan<byte> source = guestBuffer.Data.AsSpan(0, guestBuffer.Length);
            byte[]? forcedVertexColors = null;
            if (_forceTitleVertexColorWhite &&
                guestBuffer.Location == 0 &&
                guestBuffer.ComponentCount == 4 &&
                guestBuffer.DataFormat == 10 &&
                guestBuffer.NumberFormat == 0 &&
                guestBuffer.Stride == 16 &&
                guestBuffer.OffsetBytes == 12 &&
                guestBuffer.Length == 67568)
            {
                forcedVertexColors = source.ToArray();
                for (var offset = 12; offset + 3 < forcedVertexColors.Length; offset += 16)
                {
                    forcedVertexColors[offset] = 0xFF;
                    forcedVertexColors[offset + 1] = 0xFF;
                    forcedVertexColors[offset + 2] = 0xFF;
                }

                source = forcedVertexColors;
                Console.Error.WriteLine(
                    $"[LOADER][TRACE] vk.vertex_force_title_color_white " +
                    $"base=0x{guestBuffer.BaseAddress:X16} bytes={guestBuffer.Length}");
            }
            var buffer = CreateHostBuffer(
                source,
                BufferUsageFlags.VertexBufferBit,
                out var memory,
                out _);
            var size = (ulong)Math.Max(guestBuffer.Length, sizeof(uint));
            if (_setDebugUtilsObjectName is not null)
            {
                SetDebugName(
                    ObjectType.Buffer,
                    buffer.Handle,
                    $"SharpEmu vertex loc{guestBuffer.Location} " +
                    $"0x{guestBuffer.BaseAddress:X16} {guestBuffer.Length}b");
            }
            if (_tracedVertexBufferCount++ < 64)
            {
                TraceVulkanShader(
                    $"vk.vertex_buffer loc={guestBuffer.Location} " +
                    $"base=0x{guestBuffer.BaseAddress:X16} stride={guestBuffer.Stride} " +
                    $"offset={guestBuffer.OffsetBytes} comps={guestBuffer.ComponentCount} " +
                    $"fmt={guestBuffer.DataFormat}/num={guestBuffer.NumberFormat} " +
                    $"bytes={guestBuffer.Length}");
            }

            return CreateVertexBufferResource(
                buffer,
                memory,
                size,
                guestBuffer,
                ownsBuffer: true);
        }

        private static VertexBufferResource CreateVertexBufferResource(
            VkBuffer buffer,
            DeviceMemory memory,
            ulong size,
            GuestVertexBuffer guestBuffer,
            bool ownsBuffer)
        {
            return new VertexBufferResource
            {
                Buffer = buffer,
                Memory = memory,
                OwnsBuffer = ownsBuffer,
                Size = size,
                Location = guestBuffer.Location,
                ComponentCount = guestBuffer.ComponentCount,
                DataFormat = guestBuffer.DataFormat,
                NumberFormat = guestBuffer.NumberFormat,
                Stride = guestBuffer.Stride,
                OffsetBytes = guestBuffer.OffsetBytes,
            };
        }

        private static VertexBufferResource CreateVertexBufferAlias(
            VertexBufferResource shared,
            GuestVertexBuffer guestBuffer) => new()
        {
            Buffer = shared.Buffer,
            Memory = shared.Memory,
            OwnsBuffer = false,
            Size = shared.Size,
            Location = guestBuffer.Location,
            ComponentCount = guestBuffer.ComponentCount,
            DataFormat = guestBuffer.DataFormat,
            NumberFormat = guestBuffer.NumberFormat,
            Stride = guestBuffer.Stride,
            OffsetBytes = guestBuffer.OffsetBytes,
        };

        private VkBuffer CreateHostBuffer(
            ReadOnlySpan<byte> data,
            BufferUsageFlags usage,
            out DeviceMemory memory,
            out nint mapped)
        {
            var size = (ulong)Math.Max(data.Length, sizeof(uint));
            var capacity = BitOperations.RoundUpToPowerOf2(size);
            var key = new VulkanHostBufferPoolKey(usage, capacity);

            VulkanHostBufferAllocation allocation;
            if (_hostBufferPool.TryRent(key, out var pooled))
            {
                allocation = pooled;
            }
            else
            {
                var buffer = CreateBuffer(
                    capacity,
                    usage,
                    MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                    out var allocatedMemory);
                // Persistently mapped: map/unmap per draw was a measurable
                // share of the per-draw fixed cost, and HOST_COHERENT memory
                // may legally stay mapped for its lifetime.
                void* persistentMapping;
                Check(
                    _vk.MapMemory(_device, allocatedMemory, 0, capacity, 0, &persistentMapping),
                    "vkMapMemory(host persistent)");
                allocation = new VulkanHostBufferAllocation(
                    buffer,
                    allocatedMemory,
                    key,
                    (nint)persistentMapping);
                _hostBufferPool.Register(allocation);
            }

            memory = allocation.Memory;
            mapped = allocation.Mapped;
            fixed (byte* source = data)
            {
                System.Buffer.MemoryCopy(
                    source,
                    (void*)allocation.Mapped,
                    checked((long)allocation.Key.Capacity),
                    data.Length);
            }

            return allocation.Buffer;
        }

        private void RecycleHostBuffer(VkBuffer buffer, DeviceMemory memory)
        {
            if (buffer.Handle == 0)
            {
                return;
            }

            if (_hostBufferPool.Return(buffer, memory))
            {
                return;
            }

            _vk.DestroyBuffer(_device, buffer, null);
            if (memory.Handle != 0)
            {
                _vk.FreeMemory(_device, memory, null);
            }
        }

        private void DestroyHostBufferAllocation(VulkanHostBufferAllocation allocation)
        {
            _vk.UnmapMemory(_device, allocation.Memory);
            _vk.DestroyBuffer(_device, allocation.Buffer, null);
            _vk.FreeMemory(_device, allocation.Memory, null);
        }

        private static PrimitiveTopology GetPrimitiveTopology(uint primitiveType) =>
            primitiveType switch
            {
                1 => PrimitiveTopology.PointList,
                2 => PrimitiveTopology.LineList,
                3 => PrimitiveTopology.LineStrip,
                5 => PrimitiveTopology.TriangleFan,
                6 => PrimitiveTopology.TriangleStrip,
                GuestPrimitiveRectList => PrimitiveTopology.TriangleStrip,
                _ => PrimitiveTopology.TriangleList,
            };

        // Strip and fan topologies are the ones for which a restart index
        // splits primitives; list topologies never restart.
        private static bool RequiresPrimitiveRestart(PrimitiveTopology topology) =>
            topology is PrimitiveTopology.LineStrip
                or PrimitiveTopology.TriangleStrip
                or PrimitiveTopology.TriangleFan;

        private static Format ToVkVertexFormat(
            uint dataFormat,
            uint numberFormat,
            uint componentCount) =>
            (dataFormat, numberFormat) switch
            {
                (1, 0) => Format.R8Unorm,
                (1, 1) => Format.R8SNorm,
                (1, 4) => Format.R8Uint,
                (1, 5) => Format.R8Sint,
                (1, 9) => Format.R8Srgb,
                (2, 0) => Format.R16Unorm,
                (2, 1) => Format.R16SNorm,
                (2, 4) => Format.R16Uint,
                (2, 5) => Format.R16Sint,
                (2, 7) => Format.R16Sfloat,
                (3, 0) => Format.R8G8Unorm,
                (3, 1) => Format.R8G8SNorm,
                (3, 4) => Format.R8G8Uint,
                (3, 5) => Format.R8G8Sint,
                (3, 9) => Format.R8G8Srgb,
                (4, 4) => Format.R32Uint,
                (4, 5) => Format.R32Sint,
                (4, 7) => Format.R32Sfloat,
                (5, 0) => Format.R16G16Unorm,
                (5, 1) => Format.R16G16SNorm,
                (5, 2) => Format.R16G16Uscaled,
                (5, 3) => Format.R16G16Sscaled,
                (5, 4) => Format.R16G16Uint,
                (5, 5) => Format.R16G16Sint,
                (5, 7) => Format.R16G16Sfloat,
                (6, 7) => Format.B10G11R11UfloatPack32,
                (7, 7) => Format.B10G11R11UfloatPack32,
                (8, 0) => Format.A2B10G10R10UnormPack32,
                (8, 1) => Format.A2B10G10R10SNormPack32,
                (8, 2) => Format.A2B10G10R10UscaledPack32,
                (8, 3) => Format.A2B10G10R10SscaledPack32,
                (8, 4) => Format.A2B10G10R10UintPack32,
                (8, 5) => Format.A2B10G10R10SintPack32,
                // RDNA COLOR_2_10_10_10 stores component 0 (R) in bits
                // 0..9 and A in 30..31. Vulkan names that exact bit layout
                // A2B10G10R10_PACK32 (the packed name is MSB-to-LSB).
                (9, 0) => Format.A2B10G10R10UnormPack32,
                (9, 1) => Format.A2B10G10R10SNormPack32,
                (9, 2) => Format.A2B10G10R10UscaledPack32,
                (9, 3) => Format.A2B10G10R10SscaledPack32,
                (9, 4) => Format.A2B10G10R10UintPack32,
                (9, 5) => Format.A2B10G10R10SintPack32,
                (10, 0) => Format.R8G8B8A8Unorm,
                (10, 1) => Format.R8G8B8A8SNorm,
                (10, 2) => Format.R8G8B8A8Uscaled,
                (10, 3) => Format.R8G8B8A8Sscaled,
                (10, 4) => Format.R8G8B8A8Uint,
                (10, 5) => Format.R8G8B8A8Sint,
                (10, 9) => Format.R8G8B8A8Srgb,
                (11, 4) => Format.R32G32Uint,
                (11, 5) => Format.R32G32Sint,
                (11, 7) => Format.R32G32Sfloat,
                (12, 0) => Format.R16G16B16A16Unorm,
                (12, 1) => Format.R16G16B16A16SNorm,
                (12, 2) => Format.R16G16B16A16Uscaled,
                (12, 3) => Format.R16G16B16A16Sscaled,
                (12, 4) => Format.R16G16B16A16Uint,
                (12, 5) => Format.R16G16B16A16Sint,
                (12, 6) => Format.R16G16B16A16SNorm,
                (12, 7) => Format.R16G16B16A16Sfloat,
                (13, 4) => Format.R32G32B32Uint,
                (13, 5) => Format.R32G32B32Sint,
                (13, 7) => Format.R32G32B32Sfloat,
                (14, 4) => Format.R32G32B32A32Uint,
                (14, 5) => Format.R32G32B32A32Sint,
                (14, 7) => Format.R32G32B32A32Sfloat,
                (16, 0) => Format.B5G6R5UnormPack16,
                (17, 0) => Format.R5G5B5A1UnormPack16,
                (19, 0) => Format.R4G4B4A4UnormPack16,
                (34, 7) => Format.E5B9G9R9UfloatPack32,
                _ => ToVkFloatVertexFormat(componentCount),
            };

        private static Format ToVkFloatVertexFormat(uint componentCount) =>
            componentCount switch
            {
                1 => Format.R32Sfloat,
                2 => Format.R32G32Sfloat,
                3 => Format.R32G32B32Sfloat,
                4 => Format.R32G32B32A32Sfloat,
                _ => Format.R32Sfloat,
            };

        private static ulong GetVertexBindingOffset(VertexBufferResource vertexBuffer)
        {
            if (vertexBuffer.OffsetBytes < vertexBuffer.Size)
            {
                return vertexBuffer.OffsetBytes;
            }

            TraceVulkanShader(
                $"vk.vertex_offset_oob loc={vertexBuffer.Location} " +
                $"offset={vertexBuffer.OffsetBytes} size={vertexBuffer.Size}");
            return 0;
        }

        private static uint GetDrawVertexCount(
            uint primitiveType,
            uint vertexCount,
            GuestIndexBuffer? indexBuffer)
        {
            if (primitiveType == GuestPrimitiveRectList && indexBuffer is null)
            {
                return 4;
            }

            return vertexCount;
        }

        private static BlendFactor ToVkBlendFactor(uint factor) =>
            factor switch
            {
                0 => BlendFactor.Zero,
                1 => BlendFactor.One,
                2 => BlendFactor.SrcColor,
                3 => BlendFactor.OneMinusSrcColor,
                4 => BlendFactor.SrcAlpha,
                5 => BlendFactor.OneMinusSrcAlpha,
                6 => BlendFactor.DstAlpha,
                7 => BlendFactor.OneMinusDstAlpha,
                8 => BlendFactor.DstColor,
                9 => BlendFactor.OneMinusDstColor,
                10 => BlendFactor.SrcAlphaSaturate,
                13 => BlendFactor.ConstantColor,
                14 => BlendFactor.OneMinusConstantColor,
                15 => BlendFactor.Src1Color,
                16 => BlendFactor.OneMinusSrc1Color,
                17 => BlendFactor.Src1Alpha,
                18 => BlendFactor.OneMinusSrc1Alpha,
                19 => BlendFactor.ConstantAlpha,
                20 => BlendFactor.OneMinusConstantAlpha,
                _ => BlendFactor.One,
            };

        private static BlendOp ToVkBlendOp(uint function) =>
            function switch
            {
                0 => BlendOp.Add,
                1 => BlendOp.Subtract,
                2 => BlendOp.Min,
                3 => BlendOp.Max,
                4 => BlendOp.ReverseSubtract,
                _ => BlendOp.Add,
            };

        private static uint DecodeSamplerClampX(GuestSampler sampler) =>
            sampler.Word0 & 0x7u;

        private static uint DecodeSamplerClampY(GuestSampler sampler) =>
            (sampler.Word0 >> 3) & 0x7u;

        private static uint DecodeSamplerClampZ(GuestSampler sampler) =>
            (sampler.Word0 >> 6) & 0x7u;

        private static uint DecodeSamplerDepthCompare(GuestSampler sampler) =>
            (sampler.Word0 >> 12) & 0x7u;

        private static float DecodeSamplerMinLod(GuestSampler sampler) =>
            (sampler.Word1 & 0xFFFu) / 256.0f;

        private static float DecodeSamplerMaxLod(GuestSampler sampler) =>
            ((sampler.Word1 >> 12) & 0xFFFu) / 256.0f;

        private static float DecodeSamplerLodBias(GuestSampler sampler)
        {
            var raw = sampler.Word2 & 0x3FFFu;
            var signed = (short)((raw ^ 0x2000u) - 0x2000u);
            return signed / 256.0f;
        }

        private static uint DecodeSamplerMagFilter(GuestSampler sampler) =>
            (sampler.Word2 >> 20) & 0x3u;

        private static uint DecodeSamplerMinFilter(GuestSampler sampler) =>
            (sampler.Word2 >> 22) & 0x3u;

        private static uint DecodeSamplerMipFilter(GuestSampler sampler) =>
            (sampler.Word2 >> 26) & 0x3u;

        private static uint DecodeSamplerBorderColor(GuestSampler sampler) =>
            (sampler.Word3 >> 30) & 0x3u;

        private static SamplerAddressMode ToVkSamplerAddressMode(uint mode) =>
            mode switch
            {
                0 => SamplerAddressMode.Repeat,
                1 => SamplerAddressMode.MirroredRepeat,
                2 => SamplerAddressMode.ClampToEdge,
                3 or 5 or 7 => SamplerAddressMode.MirrorClampToEdge,
                4 or 6 => SamplerAddressMode.ClampToBorder,
                _ => SamplerAddressMode.ClampToEdge,
            };

        private static Filter ToVkFilter(uint filter) =>
            filter is 1 or 3 ? Filter.Linear : Filter.Nearest;

        private static SamplerMipmapMode ToVkMipFilter(uint filter) =>
            filter == 2 ? SamplerMipmapMode.Linear : SamplerMipmapMode.Nearest;

        private static CompareOp ToVkCompareOp(uint compare) =>
            compare switch
            {
                1 => CompareOp.Less,
                2 => CompareOp.Equal,
                3 => CompareOp.LessOrEqual,
                4 => CompareOp.Greater,
                5 => CompareOp.NotEqual,
                6 => CompareOp.GreaterOrEqual,
                7 => CompareOp.Always,
                _ => CompareOp.Never,
            };

        private static BorderColor ToVkBorderColor(uint color) =>
            color switch
            {
                1 => BorderColor.FloatTransparentBlack,
                2 => BorderColor.FloatOpaqueWhite,
                _ => BorderColor.FloatOpaqueBlack,
            };

        private static ColorComponentFlags ToVkColorWriteMask(uint mask)
        {
            var flags = default(ColorComponentFlags);
            if ((mask & 1u) != 0)
            {
                flags |= ColorComponentFlags.RBit;
            }

            if ((mask & 2u) != 0)
            {
                flags |= ColorComponentFlags.GBit;
            }

            if ((mask & 4u) != 0)
            {
                flags |= ColorComponentFlags.BBit;
            }

            if ((mask & 8u) != 0)
            {
                flags |= ColorComponentFlags.ABit;
            }

            return flags;
        }

        private static GuestRect ClampScissor(GuestRect? scissor, Extent2D extent)
        {
            if (scissor is not { } rect)
            {
                return new GuestRect(0, 0, extent.Width, extent.Height);
            }

            var left = Math.Clamp(rect.X, 0, checked((int)extent.Width));
            var top = Math.Clamp(rect.Y, 0, checked((int)extent.Height));
            var right = Math.Clamp(
                rect.X + checked((int)rect.Width),
                left,
                checked((int)extent.Width));
            var bottom = Math.Clamp(
                rect.Y + checked((int)rect.Height),
                top,
                checked((int)extent.Height));
            return new GuestRect(
                left,
                top,
                checked((uint)(right - left)),
                checked((uint)(bottom - top)));
        }

        private static readonly float ViewportDebugEpsilon = float.TryParse(
            Environment.GetEnvironmentVariable("SHARPEMU_VIEWPORT_EPSILON"),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var viewportEpsilon)
            ? viewportEpsilon
            : 0f;

        private static Viewport ClampViewport(GuestViewport? viewport, Extent2D extent)
        {
            if (viewport is not { } rect)
            {
                return new Viewport(0, 0, extent.Width, extent.Height, 0, 1);
            }

            // Do NOT trim the rectangle to the render target: Vulkan allows
            // viewports that extend beyond the framebuffer (rendering is
            // confined by the scissor), and trimming changes the guest's
            // scale and offset. That skews texel addressing on 1:1 draws -
            // source rows get skipped or duplicated - which shredded the
            // game's pre-composed tile surfaces. Only guard what the spec
            // requires: a positive width and hardware viewport bounds.
            const float bound = 32767f;
            var x = Math.Clamp(rect.X, -bound, bound);
            var y = Math.Clamp(rect.Y, -bound, bound);
            var width = Math.Clamp(rect.Width, 1e-3f, bound);
            var height = Math.Clamp(rect.Height, -bound, bound);
            if (height == 0f)
            {
                height = extent.Height;
            }

            var minDepth = Math.Clamp(rect.MinDepth, 0f, 1f);
            var maxDepth = Math.Clamp(rect.MaxDepth, minDepth, 1f);
            return new Viewport(x, y, width, height, minDepth, maxDepth);
        }

        private static byte[] CreateFallbackTexturePixels(uint format, uint width, uint height, ulong expectedSize)
        {
            if (format is 9 or 10)
            {
                return CreateBlackFrame(width, height);
            }

            return new byte[checked((int)expectedSize)];
        }

        private static ulong GetTextureBytesPerPixel(uint format) =>
            format switch
            {
                1 => 1UL,
                2 => 2UL,
                3 => 2UL,
                4 => 4UL,
                5 => 4UL,
                6 => 4UL,
                7 => 4UL,
                9 => 4UL,
                10 => 4UL,
                11 => 8UL,
                12 => 8UL,
                13 => 12UL,
                14 => 16UL,
                _ => 4UL,
            };

        private static ulong GetTextureByteCount(uint format, uint width, uint height)
        {
            var blockBytes = format switch
            {
                // BC1 (169/170) and BC4 (175/176) are 8 bytes per 4x4 block;
                // BC2/BC3/BC5/BC6H/BC7 are 16 bytes per block.
                169 or 170 or 175 or 176 => 8UL,
                171 or 172 or 173 or 174 or
                177 or 178 or 179 or 180 or 181 or 182 => 16UL,
                _ => 0UL,
            };
            return blockBytes == 0
                ? checked((ulong)width * height * GetTextureBytesPerPixel(format))
                : checked(((ulong)width + 3) / 4 * (((ulong)height + 3) / 4) * blockBytes);
        }

        private bool SupportsColorAttachment(Format format)
        {
            _vk.GetPhysicalDeviceFormatProperties(_physicalDevice, format, out var properties);
            return (properties.OptimalTilingFeatures & FormatFeatureFlags.ColorAttachmentBit) != 0;
        }

        internal static Format GetTextureFormat(uint format, uint numberType) =>
            (format, numberType) switch
            {
                (9, _) => Format.A2B10G10R10UnormPack32,
                (1, 0) => Format.R8Unorm,
                (1, 1) => Format.R8SNorm,
                (1, 2) => Format.R8Uscaled,
                (1, 3) => Format.R8Sscaled,
                (1, 4) => Format.R8Uint,
                (1, 5) => Format.R8Sint,
                (2, 7) => Format.R16Sfloat,
                (2, 0) => Format.R16Unorm,
                (2, 1) => Format.R16SNorm,
                (2, 2) => Format.R16Uscaled,
                (2, 3) => Format.R16Sscaled,
                (2, 4) => Format.R16Uint,
                (2, 5) => Format.R16Sint,
                (3, 0) => Format.R8G8Unorm,
                (3, 1) => Format.R8G8SNorm,
                (3, 2) => Format.R8G8Uscaled,
                (3, 3) => Format.R8G8Sscaled,
                (3, 4) => Format.R8G8Uint,
                (3, 5) => Format.R8G8Sint,
                (4, 4) => Format.R32Uint,
                (4, 5) => Format.R32Sint,
                (4, 7) => Format.R32Sfloat,
                (5, 0) => Format.R16G16Unorm,
                (5, 4) => Format.R16G16Uint,
                (5, 5) => Format.R16G16Sint,
                (5, 7) => Format.R16G16Sfloat,
                (6, 7) => Format.B10G11R11UfloatPack32,
                (7, 7) => Format.B10G11R11UfloatPack32,
                (8, 0) => Format.A2B10G10R10UnormPack32,
                (8, 1) => Format.A2B10G10R10SNormPack32,
                (8, 2) => Format.A2B10G10R10UscaledPack32,
                (8, 3) => Format.A2B10G10R10SscaledPack32,
                (8, 4) => Format.A2B10G10R10UintPack32,
                (8, 5) => Format.A2B10G10R10SintPack32,
                (10, 0) => Format.R8G8B8A8Unorm,
                (10, 4) => Format.R8G8B8A8Uint,
                (10, 5) => Format.R8G8B8A8Sint,
                (10, 9) => Format.R8G8B8A8Srgb,
                (1, 9) => Format.R8Srgb,
                (3, 9) => Format.R8G8Srgb,
                (11, 4) => Format.R32G32Uint,
                (11, 5) => Format.R32G32Sint,
                (11, 7) => Format.R32G32Sfloat,
                (12, 0) => Format.R16G16B16A16Unorm,
                (12, 4) => Format.R16G16B16A16Uint,
                (12, 5) => Format.R16G16B16A16Sint,
                (12, 7) => Format.R16G16B16A16Sfloat,
                (13, 4) => Format.R32G32B32A32Uint,
                (13, 5) => Format.R32G32B32A32Sint,
                (13, _) => Format.R32G32B32A32Sfloat,
                (14, 4) => Format.R32G32B32A32Uint,
                (14, 5) => Format.R32G32B32A32Sint,
                (14, 7) => Format.R32G32B32A32Sfloat,
                (16, 0) => Format.B5G6R5UnormPack16,
                (17, 0) => Format.R5G5B5A1UnormPack16,
                (19, 0) => Format.R4G4B4A4UnormPack16,
                (34, 7) => Format.E5B9G9R9UfloatPack32,
                (169, _) => Format.BC1RgbaUnormBlock,
                (170, _) => Format.BC1RgbaSrgbBlock,
                (171, _) => Format.BC2UnormBlock,
                (172, _) => Format.BC2SrgbBlock,
                (173, _) => Format.BC3UnormBlock,
                (174, _) => Format.BC3SrgbBlock,
                (175, 1) => Format.BC4SNormBlock,
                (175, _) => Format.BC4UnormBlock,
                (176, _) => Format.BC4SNormBlock,
                (177, 1) => Format.BC5SNormBlock,
                (177, _) => Format.BC5UnormBlock,
                (178, _) => Format.BC5SNormBlock,
                (179, _) => Format.BC6HUfloatBlock,
                (180, _) => Format.BC6HSfloatBlock,
                (181, _) => Format.BC7UnormBlock,
                (182, _) => Format.BC7SrgbBlock,
                _ => Format.R8G8B8A8Unorm,
            };

        private static Format GetRenderTargetFormat(uint format, uint numberType) =>
            (format, numberType) switch
            {
                (4, 4) => Format.R32Uint,
                (4, 5) => Format.R32Sint,
                (4, 7) => Format.R32Sfloat,
                (5, 4) => Format.R16G16Uint,
                (5, 5) => Format.R16G16Sint,
                (5, 7) => Format.R16G16Sfloat,
                (6, 7) => Format.B10G11R11UfloatPack32,
                (7, 7) => Format.B10G11R11UfloatPack32,
                (9, _) => Format.A2B10G10R10UnormPack32,
                (10, 9) => Format.R8G8B8A8Srgb,
                (10, 4) => Format.R8G8B8A8Uint,
                (10, 5) => Format.R8G8B8A8Sint,
                (10, _) => Format.R8G8B8A8Unorm,
                (11, 7) => Format.R32G32Sfloat,
                (12, 4) => Format.R16G16B16A16Uint,
                (12, 5) => Format.R16G16B16A16Sint,
                (12, 7) => Format.R16G16B16A16Sfloat,
                (13, 7) => Format.R32G32B32A32Sfloat,
                (14, 7) => Format.R32G32B32A32Sfloat,
                (_, 0) => GetTextureFormat(format, numberType),
                (_, 9) => GetTextureFormat(format, numberType),
                _ => Format.Undefined,
            };
        private static bool IsBlockCompressedFormat(Format format) =>
            format is Format.BC1RgbaUnormBlock or
                Format.BC1RgbaSrgbBlock or
                Format.BC2UnormBlock or
                Format.BC2SrgbBlock or
                Format.BC3UnormBlock or
                Format.BC3SrgbBlock or
                Format.BC4UnormBlock or
                Format.BC4SNormBlock or
                Format.BC5UnormBlock or
                Format.BC5SNormBlock or
                Format.BC6HUfloatBlock or
                Format.BC6HSfloatBlock or
                Format.BC7UnormBlock or
                Format.BC7SrgbBlock;

        private VkBuffer CreateBuffer(
            ulong size,
            BufferUsageFlags usage,
            MemoryPropertyFlags memoryFlags,
            out DeviceMemory memory)
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
                MemoryTypeIndex = FindMemoryType(requirements.MemoryTypeBits, memoryFlags),
            };
            Check(_vk.AllocateMemory(_device, &memoryInfo, null, out memory), "vkAllocateMemory");
            Check(_vk.BindBufferMemory(_device, buffer, memory, 0), "vkBindBufferMemory");
            return buffer;
        }

        private void CreateStagingBuffer(ulong size)
        {
            _stagingBuffer = CreateBuffer(
                size,
                BufferUsageFlags.TransferSrcBit | BufferUsageFlags.TransferDstBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                out _stagingMemory);
            _stagingSize = size;
        }

        private uint FindMemoryType(uint typeBits, MemoryPropertyFlags requiredFlags)
        {
            _vk.GetPhysicalDeviceMemoryProperties(_physicalDevice, out var properties);
            var memoryTypes = &properties.MemoryTypes.Element0;
            for (uint index = 0; index < properties.MemoryTypeCount; index++)
            {
                if ((typeBits & (1u << (int)index)) != 0 &&
                    (memoryTypes[index].PropertyFlags & requiredFlags) == requiredFlags)
                {
                    return index;
                }
            }

            throw new InvalidOperationException("No compatible Vulkan host-visible memory type was found.");
        }

        private const uint MaxComputeZSlicesPerSubmission = 8;
        // An indirect guest dispatch above this size is not credible frame work
        // (at the minimum 64-thread group used by the captured title this is
        // already over one billion invocations).  Treat it as poisoned
        // indirect-command data and quarantine it instead of feeding a host
        // API a multi-billion-workgroup command.  This is validation, not a
        // clamp: the raw dimensions remain visible in the trace so the
        // producer can be fixed without changing the guest value.
        private const ulong MaxCredibleGuestWorkgroupsPerDispatch = 16UL * 1024 * 1024;

        private void ExecuteComputeDispatch(VulkanComputeGuestDispatch work)
        {
            var perfStart = Stopwatch.GetTimestamp();
            Interlocked.Increment(ref _perfDrawCount);
            PerfOverlay.RecordDraw();
            try
            {
                ExecuteComputeDispatchCore(work);
            }
            finally
            {
                Interlocked.Add(
                    ref _perfDrawTicks,
                    Stopwatch.GetTimestamp() - perfStart);
            }
        }

        private void ExecuteComputeDispatchCore(VulkanComputeGuestDispatch work)
        {
            FlushBatchedGuestCommands();
            if (_deviceLost)
            {
                return;
            }

            if (_skipAllCompute ||
                AddressListContains("SHARPEMU_SKIP_COMPUTE_CS", work.ShaderAddress) ||
                (_skipTallComputeZ > 0 && work.GroupCountZ >= _skipTallComputeZ))
            {
                TraceVulkanShader(
                    $"vk.compute_skip cs=0x{work.ShaderAddress:X16} " +
                    $"groups={work.GroupCountX}x{work.GroupCountY}x{work.GroupCountZ} " +
                    $"textures={work.Textures.Count}");
                return;
            }

            if (!TryValidateComputeDispatch(work, out var validationError))
            {
                LogRejectedComputeDispatch(work, validationError);
                return;
            }

            TranslatedDrawResources? resources = null;
            CommandBuffer commandBuffer = default;
            var submitted = false;
            var chunksSubmitted = 0;
            try
            {
                EnsureGuestSubmissionCapacity();
                resources = CreateComputeDispatchResources(work);

                var batchCount = Math.Max(
                    1u,
                    (uint)Math.Ceiling(work.GroupCountZ / (double)MaxComputeZSlicesPerSubmission));
                var threadLimits = stackalloc uint[3]
                {
                    work.ThreadCountX,
                    work.ThreadCountY,
                    work.ThreadCountZ,
                };

                for (var batchIndex = 0u; batchIndex < batchCount; batchIndex++)
                {
                    var zStart = batchIndex * MaxComputeZSlicesPerSubmission;
                    var zCount = Math.Min(MaxComputeZSlicesPerSubmission, work.GroupCountZ - zStart);
                    var isFirstBatch = batchIndex == 0;
                    var isLastBatch = batchIndex == batchCount - 1;

                    if (!isFirstBatch)
                    {
                        // Each chunk is its own queue submission; without
                        // this the in-flight submission cap only applies to
                        // the first chunk of a tall dispatch.
                        EnsureGuestSubmissionCapacity();
                    }

                    commandBuffer = AllocateGuestCommandBuffer();
                    _commandBuffer = commandBuffer;
                    var beginInfo = new CommandBufferBeginInfo
                    {
                        SType = StructureType.CommandBufferBeginInfo,
                        Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
                    };
                    Check(
                        _vk.BeginCommandBuffer(_commandBuffer, &beginInfo),
                        "vkBeginCommandBuffer(compute)");

                    BeginDebugLabel(_commandBuffer, resources.DebugName);
                    if (isFirstBatch)
                    {
                        RecordGlobalBufferVisibilityBarrier(
                            _commandBuffer,
                            resources,
                            PipelineStageFlags.ComputeShaderBit);
                        RecordTextureUploads(resources, PipelineStageFlags.ComputeShaderBit);
                        RecordStorageImagesForWrite(resources, PipelineStageFlags.ComputeShaderBit);
                    }
                    else
                    {
                        // Chunks are submitted without CPU waits; this
                        // barrier orders them against the previous chunk's
                        // shader writes on the same queue.
                        var chunkBarrier = new MemoryBarrier
                        {
                            SType = StructureType.MemoryBarrier,
                            SrcAccessMask = AccessFlags.ShaderWriteBit,
                            DstAccessMask = AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit,
                        };
                        _vk.CmdPipelineBarrier(
                            _commandBuffer,
                            PipelineStageFlags.ComputeShaderBit,
                            PipelineStageFlags.ComputeShaderBit,
                            0,
                            1,
                            &chunkBarrier,
                            0,
                            null,
                            0,
                            null);
                    }

                    _vk.CmdBindPipeline(
                        _commandBuffer,
                        PipelineBindPoint.Compute,
                        resources.Pipeline);
                    if (resources.DescriptorSet.Handle != 0)
                    {
                        var descriptorSet = resources.DescriptorSet;
                        _vk.CmdBindDescriptorSets(
                            _commandBuffer,
                            PipelineBindPoint.Compute,
                            resources.PipelineLayout,
                            0,
                            1,
                            &descriptorSet,
                            0,
                            null);
                    }

                    _vk.CmdPushConstants(
                        _commandBuffer,
                        resources.PipelineLayout,
                        ShaderStageFlags.ComputeBit,
                        0,
                        3 * sizeof(uint),
                        threadLimits);

                    RecordChunkedComputeDispatch(_commandBuffer, work, zStart, zCount);

                    if (isLastBatch)
                    {
                        RecordStorageImagesForRead(resources, PipelineStageFlags.ComputeShaderBit);
                    }

                    EndDebugLabel(_commandBuffer);
                    Check(_vk.EndCommandBuffer(_commandBuffer), "vkEndCommandBuffer(compute)");

                    TraceVulkanShader(
                        $"vk.compute_submit cs=0x{work.ShaderAddress:X16} " +
                        $"batch={batchIndex}/{batchCount} z={zStart}..{zStart + zCount}");
                    if (isLastBatch)
                    {
                        SubmitGuestCommandBuffer(
                            commandBuffer,
                            [resources],
                            GetTraceImages(resources, shaderAddress: work.ShaderAddress));
                        submitted = true;
                    }
                    else
                    {
                        SubmitGuestCommandBuffer(
                            commandBuffer,
                            [],
                            [],
                            referencedResources: [resources]);
                        chunksSubmitted++;
                        commandBuffer = default;
                    }
                }

                MarkSampledImagesInitialized(resources);
                MarkStorageImagesInitialized(resources, traceContents: false);
                if (work.WritesGlobalMemory)
                {
                    // The CPU submit thread may immediately consume an indirect
                    // argument written by this dispatch. Wait for the specific
                    // guest fences and publish only dirty ranges; a queue-wide
                    // idle unnecessarily serialized presentation work too.
                    WaitForActiveGuestQueueSubmissionsForCpuVisibility();
                    WriteBackAllDirtyGuestBuffers(_activeGuestQueue.Name);
                }
                TraceVulkanShader(
                    $"vk.compute_dispatch groups={work.GroupCountX}x" +
                    $"{work.GroupCountY}x{work.GroupCountZ} " +
                    $"base={work.BaseGroupX}x{work.BaseGroupY}x{work.BaseGroupZ} " +
                    $"textures={work.Textures.Count} cs=0x{work.ShaderAddress:X16} " +
                    $"batches={batchCount}");
            }
            catch (Exception exception)
            {
                if (TryMarkDeviceLost(exception))
                {
                    return;
                }

                Console.Error.WriteLine(
                    $"[LOADER][ERROR] Vulkan compute dispatch failed " +
                    $"cs=0x{work.ShaderAddress:X16}: {exception.Message}");
            }
            finally
            {
                _commandBuffer = _presentationCommandBuffer;
                if (!submitted && commandBuffer.Handle != 0)
                {
                    _vk.FreeCommandBuffers(
                        _device,
                        _commandPool,
                        1,
                        &commandBuffer);
                }

                if (!submitted && resources is not null)
                {
                    if (chunksSubmitted > 0)
                    {
                        // Earlier chunks were submitted with empty resource
                        // lists and may still execute against these
                        // pipelines/images; destroy only after every
                        // submission issued so far has completed.
                        _deferredResourceDestroys.Enqueue((resources, _submitTimeline));
                    }
                    else
                    {
                        DestroyTranslatedDrawResources(resources);
                    }
                }
            }
        }

        private bool TryValidateComputeDispatch(
            VulkanComputeGuestDispatch work,
            out string error)
        {
            if (work.LocalSizeX == 0 || work.LocalSizeY == 0 || work.LocalSizeZ == 0)
            {
                error = "zero-local-size";
                return false;
            }

            if (work.LocalSizeX > _maxComputeWorkGroupSizeX ||
                work.LocalSizeY > _maxComputeWorkGroupSizeY ||
                work.LocalSizeZ > _maxComputeWorkGroupSizeZ)
            {
                error =
                    $"local-size-exceeds-device({work.LocalSizeX}x{work.LocalSizeY}x{work.LocalSizeZ}>" +
                    $"{_maxComputeWorkGroupSizeX}x{_maxComputeWorkGroupSizeY}x{_maxComputeWorkGroupSizeZ})";
                return false;
            }

            var localInvocations =
                (ulong)work.LocalSizeX * work.LocalSizeY * work.LocalSizeZ;
            if (localInvocations > _maxComputeWorkGroupInvocations)
            {
                error =
                    $"local-invocations-exceed-device({localInvocations}>" +
                    $"{_maxComputeWorkGroupInvocations})";
                return false;
            }

            if ((ulong)work.BaseGroupX + work.GroupCountX > _maxComputeWorkGroupCountX ||
                (ulong)work.BaseGroupY + work.GroupCountY > _maxComputeWorkGroupCountY ||
                (ulong)work.BaseGroupZ + work.GroupCountZ > _maxComputeWorkGroupCountZ)
            {
                error =
                    $"group-range-exceeds-device(base={work.BaseGroupX}x{work.BaseGroupY}x{work.BaseGroupZ}," +
                    $"count={work.GroupCountX}x{work.GroupCountY}x{work.GroupCountZ}," +
                    $"limit={_maxComputeWorkGroupCountX}x{_maxComputeWorkGroupCountY}x" +
                    $"{_maxComputeWorkGroupCountZ})";
                return false;
            }

            if (work.IsIndirect)
            {
                ulong totalWorkgroups;
                try
                {
                    totalWorkgroups = checked(
                        (ulong)work.GroupCountX * work.GroupCountY * work.GroupCountZ);
                }
                catch (OverflowException)
                {
                    error = "indirect-workgroup-count-overflow";
                    return false;
                }

                if (totalWorkgroups > MaxCredibleGuestWorkgroupsPerDispatch)
                {
                    error =
                        $"poisoned-indirect-workgroup-count({totalWorkgroups}>" +
                        $"{MaxCredibleGuestWorkgroupsPerDispatch})";
                    return false;
                }
            }

            error = string.Empty;
            return true;
        }

        private void LogRejectedComputeDispatch(
            VulkanComputeGuestDispatch work,
            string reason)
        {
            if (_rejectedComputeDispatches.Count >= 256 ||
                !_rejectedComputeDispatches.Add(
                    (work.ShaderAddress,
                     work.GroupCountX,
                     work.GroupCountY,
                     work.GroupCountZ,
                     reason)))
            {
                return;
            }

            Console.Error.WriteLine(
                $"[LOADER][WARN] vk.compute_reject cs=0x{work.ShaderAddress:X16} " +
                $"source={(work.IsIndirect ? "indirect" : "direct")} " +
                $"groups={work.GroupCountX}x{work.GroupCountY}x{work.GroupCountZ} " +
                $"base={work.BaseGroupX}x{work.BaseGroupY}x{work.BaseGroupZ} " +
                $"local={work.LocalSizeX}x{work.LocalSizeY}x{work.LocalSizeZ} " +
                $"reason={reason}");
        }

        private void RecordGlobalBufferVisibilityBarrier(
            CommandBuffer commandBuffer,
            TranslatedDrawResources resources,
            PipelineStageFlags destinationStages)
        {
            if (resources.GlobalMemoryBuffers.Length == 0)
            {
                return;
            }

            // Queue submission order alone is not a shader-memory dependency.
            // This makes stores through any aliased guest view available to
            // later vertex/fragment/compute reads and writes on the same queue.
            var barrier = new MemoryBarrier
            {
                SType = StructureType.MemoryBarrier,
                SrcAccessMask = AccessFlags.ShaderWriteBit,
                DstAccessMask = AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit,
            };
            _vk.CmdPipelineBarrier(
                commandBuffer,
                PipelineStageFlags.AllCommandsBit,
                destinationStages,
                0,
                1,
                &barrier,
                0,
                null,
                0,
                null);
        }

        private static void MarkGuestBufferDirty(
            GuestBufferAllocation allocation,
            ulong offset,
            ulong length)
        {
            if (length == 0)
            {
                return;
            }

            var start = offset;
            var end = checked(offset + length);
            for (var index = allocation.DirtyRanges.Count - 1; index >= 0; index--)
            {
                var existing = allocation.DirtyRanges[index];
                var existingEnd = existing.Offset + existing.Length;
                if (end < existing.Offset || existingEnd < start)
                {
                    continue;
                }

                start = Math.Min(start, existing.Offset);
                end = Math.Max(end, existingEnd);
                allocation.DirtyRanges.RemoveAt(index);
            }

            allocation.DirtyRanges.Add(new DirtyGuestBufferRange(start, end - start));
        }

        private void WriteBackAllDirtyGuestBuffers(string? queueName = null)
        {
            var memory = _guestMemory;
            if (memory is null)
            {
                return;
            }

            foreach (var allocation in _guestBufferAllocations)
            {
                if (queueName is not null &&
                    !string.Equals(allocation.QueueName, queueName, StringComparison.Ordinal))
                {
                    continue;
                }

                if (allocation.DirtyRanges.Count != 0 &&
                    allocation.LastUseTimeline > _completedTimeline)
                {
                    // A mapped HOST_COHERENT allocation still cannot be read
                    // by the CPU while a shader may be writing it. Callers
                    // normally retire the relevant fences first; keep this
                    // helper fail-closed if a future path forgets to do so.
                    TraceVulkanShader(
                        $"vk.global_writeback_deferred base=0x{allocation.BaseAddress:X16} " +
                        $"last_use={allocation.LastUseTimeline} completed={_completedTimeline}");
                    continue;
                }

                for (var index = allocation.DirtyRanges.Count - 1; index >= 0; index--)
                {
                    var range = allocation.DirtyRanges[index];
                    if (range.Length == 0 || range.Length > int.MaxValue)
                    {
                        continue;
                    }

                    var mappedBytes = new ReadOnlySpan<byte>(
                        (void*)(allocation.Mapped + checked((nint)range.Offset)),
                        checked((int)range.Length));
                    var shadowBytes = allocation.Shadow.AsSpan(
                        checked((int)range.Offset),
                        mappedBytes.Length);
                    var guestAddress = allocation.BaseAddress + range.Offset;
                    var changedBytes = 0UL;
                    var changedRuns = 0;
                    var changedPages = 0;
                    var writtenRuns = 0;
                    var writtenPages = 0;
                    var failedRuns = 0;
                    var unreadablePages = 0;
                    var fallbackWrites = 0;
                    var firstChangedOffset = -1;
                    allocation.DirtyRanges.RemoveAt(index);

                    // A writable descriptor only identifies a potential write
                    // range. Publishing the entire mapped view would overwrite
                    // unrelated live CPU data with its old snapshot. Compare
                    // against the last synchronized image. For each changed
                    // page, start with current guest bytes and overlay only the
                    // shader changes before one bounded write. This preserves
                    // live CPU changes in unchanged bytes without degenerating
                    // into millions of writes for alternating output patterns.
                    const int pageSize = 4096;
                    const int unreadableMergeGap = 16;
                    var livePageBuffer = GuestDataPool.Rent(pageSize);
                    var pageRuns = new List<(int Start, int Length)>(64);
                    try
                    {
                        for (var pageStart = 0;
                             pageStart < mappedBytes.Length;
                             pageStart += pageSize)
                        {
                            var pageEnd = Math.Min(pageStart + pageSize, mappedBytes.Length);
                            pageRuns.Clear();
                            var cursor = pageStart;
                            while (cursor < pageEnd)
                            {
                                while (cursor < pageEnd &&
                                       mappedBytes[cursor] == shadowBytes[cursor])
                                {
                                    cursor++;
                                }

                                if (cursor == pageEnd)
                                {
                                    break;
                                }

                                var runStart = cursor;
                                while (cursor < pageEnd &&
                                       mappedBytes[cursor] != shadowBytes[cursor])
                                {
                                    cursor++;
                                }

                                var runLength = cursor - runStart;
                                pageRuns.Add((runStart, runLength));
                                changedRuns++;
                                changedBytes += (ulong)runLength;
                                if (firstChangedOffset < 0)
                                {
                                    firstChangedOffset = runStart;
                                }
                            }

                            if (pageRuns.Count == 0)
                            {
                                continue;
                            }

                            changedPages++;
                            var pageLength = pageEnd - pageStart;
                            var livePage = livePageBuffer.AsSpan(0, pageLength);
                            if (memory.TryRead(guestAddress + (ulong)pageStart, livePage))
                            {
                                foreach (var run in pageRuns)
                                {
                                    mappedBytes.Slice(run.Start, run.Length).CopyTo(
                                        livePage.Slice(run.Start - pageStart, run.Length));
                                }

                                if (memory.TryWrite(guestAddress + (ulong)pageStart, livePage))
                                {
                                    foreach (var run in pageRuns)
                                    {
                                        mappedBytes.Slice(run.Start, run.Length).CopyTo(
                                            shadowBytes.Slice(run.Start, run.Length));
                                    }

                                    writtenPages++;
                                    writtenRuns += pageRuns.Count;
                                    continue;
                                }

                                foreach (var run in pageRuns)
                                {
                                    failedRuns++;
                                    MarkGuestBufferDirty(
                                        allocation,
                                        range.Offset + (ulong)run.Start,
                                        (ulong)run.Length);
                                }

                                continue;
                            }

                            // A partial/unreadable edge cannot be safely
                            // reconstructed as a page. Fall back to bounded
                            // changed spans, coalescing only tiny gaps.
                            unreadablePages++;
                            for (var runIndex = 0; runIndex < pageRuns.Count; runIndex++)
                            {
                                var firstRunIndex = runIndex;
                                var mergedStart = pageRuns[runIndex].Start;
                                var mergedEnd = mergedStart + pageRuns[runIndex].Length;
                                while (runIndex + 1 < pageRuns.Count &&
                                       pageRuns[runIndex + 1].Start - mergedEnd <=
                                       unreadableMergeGap)
                                {
                                    runIndex++;
                                    mergedEnd = pageRuns[runIndex].Start +
                                        pageRuns[runIndex].Length;
                                }

                                var lastRunIndex = runIndex;
                                var mergedLength = mergedEnd - mergedStart;
                                var mergedLive = livePageBuffer.AsSpan(0, mergedLength);
                                if (memory.TryRead(
                                        guestAddress + (ulong)mergedStart,
                                        mergedLive))
                                {
                                    for (var overlayIndex = firstRunIndex;
                                         overlayIndex <= lastRunIndex;
                                         overlayIndex++)
                                    {
                                        var run = pageRuns[overlayIndex];
                                        mappedBytes.Slice(run.Start, run.Length).CopyTo(
                                            mergedLive.Slice(
                                                run.Start - mergedStart,
                                                run.Length));
                                    }

                                    fallbackWrites++;
                                    if (memory.TryWrite(
                                            guestAddress + (ulong)mergedStart,
                                            mergedLive))
                                    {
                                        for (var overlayIndex = firstRunIndex;
                                             overlayIndex <= lastRunIndex;
                                             overlayIndex++)
                                        {
                                            var run = pageRuns[overlayIndex];
                                            mappedBytes.Slice(run.Start, run.Length).CopyTo(
                                                shadowBytes.Slice(run.Start, run.Length));
                                        }

                                        writtenRuns += lastRunIndex - firstRunIndex + 1;
                                        continue;
                                    }
                                }

                                // Even the merged span crosses an unreadable
                                // edge. Exact changed runs remain safe because
                                // they never carry stale gap bytes.
                                for (var exactIndex = firstRunIndex;
                                     exactIndex <= lastRunIndex;
                                     exactIndex++)
                                {
                                    var run = pageRuns[exactIndex];
                                    var changed = mappedBytes.Slice(run.Start, run.Length);
                                    fallbackWrites++;
                                    if (memory.TryWrite(
                                            guestAddress + (ulong)run.Start,
                                            changed))
                                    {
                                        changed.CopyTo(shadowBytes.Slice(
                                            run.Start,
                                            run.Length));
                                        writtenRuns++;
                                    }
                                    else
                                    {
                                        failedRuns++;
                                        MarkGuestBufferDirty(
                                            allocation,
                                            range.Offset + (ulong)run.Start,
                                            (ulong)run.Length);
                                    }
                                }
                            }
                        }
                    }
                    finally
                    {
                        GuestDataPool.Return(livePageBuffer);
                    }

                    var probe = mappedBytes[..Math.Min(mappedBytes.Length, 256)];
                    var nonzero = 0;
                    foreach (var value in probe)
                    {
                        nonzero += value == 0 ? 0 : 1;
                    }

                    var firstForRange = _tracedGlobalWritebacks.Count < 256 &&
                        _tracedGlobalWritebacks.Add((guestAddress, range.Length));
                    var traceSmallMutation = range.Length <= 4096 &&
                        _tracedSmallGlobalWritebackEvents++ < 1024;
                    var traceLargeMutation = range.Length >= 1024 * 1024 &&
                        _tracedLargeGlobalWritebackEvents++ < 256;
                    if (firstForRange || traceSmallMutation || traceLargeMutation)
                    {
                        var head = firstChangedOffset >= 0
                            ? mappedBytes.Slice(
                                firstChangedOffset,
                                Math.Min(mappedBytes.Length - firstChangedOffset, 32))
                            : ReadOnlySpan<byte>.Empty;
                        TraceVulkanShader(
                            $"vk.global_writeback base=0x{guestAddress:X16} " +
                            $"potential_bytes={mappedBytes.Length} changed_bytes={changedBytes} " +
                            $"changed_runs={changedRuns} changed_pages={changedPages} " +
                            $"written_pages={writtenPages} written_runs={writtenRuns} " +
                            $"unreadable_pages={unreadablePages} " +
                            $"fallback_writes={fallbackWrites} failed_runs={failedRuns} " +
                            $"probe_nonzero={nonzero}/{probe.Length} " +
                            $"changed_head={Convert.ToHexString(head)}");
                    }
                }
            }
        }

        private void RecordChunkedComputeDispatch(
            CommandBuffer commandBuffer,
            VulkanComputeGuestDispatch work,
            uint zStart,
            uint zCount)
        {
            const uint maxWorkgroupsPerCommand = 4096;
            ulong commandCount = 0;
            var maxXChunk = Math.Max(
                1u,
                Math.Min(
                    work.GroupCountX,
                    Math.Min(_maxComputeWorkGroupCountX, maxWorkgroupsPerCommand)));
            for (var x = 0u; x < work.GroupCountX;)
            {
                var countX = Math.Min(maxXChunk, work.GroupCountX - x);
                var xyBudget = Math.Max(maxWorkgroupsPerCommand / countX, 1u);
                var maxYChunk = Math.Max(
                    1u,
                    Math.Min(
                        work.GroupCountY,
                        Math.Min(_maxComputeWorkGroupCountY, xyBudget)));
                for (var y = 0u; y < work.GroupCountY;)
                {
                    var countY = Math.Min(maxYChunk, work.GroupCountY - y);
                    var xyzBudget = Math.Max(xyBudget / countY, 1u);
                    var maxZChunk = Math.Max(
                        1u,
                        Math.Min(
                            zCount,
                            Math.Min(_maxComputeWorkGroupCountZ, xyzBudget)));
                    for (var z = 0u; z < zCount;)
                    {
                        var countZ = Math.Min(maxZChunk, zCount - z);
                        _vk.CmdDispatchBase(
                            commandBuffer,
                            checked(work.BaseGroupX + x),
                            checked(work.BaseGroupY + y),
                            checked(work.BaseGroupZ + zStart + z),
                            countX,
                            countY,
                            countZ);
                        commandCount++;
                        z += countZ;
                    }

                    y += countY;
                }

                x += countX;
            }

            if (commandCount > 1)
            {
                TraceVulkanShader(
                    $"vk.compute_chunked cs=0x{work.ShaderAddress:X16} " +
                    $"groups={work.GroupCountX}x{work.GroupCountY}x{work.GroupCountZ} " +
                    $"base={work.BaseGroupX}x{work.BaseGroupY}x{work.BaseGroupZ} " +
                    $"z_range={zStart}..{zStart + zCount} commands={commandCount} " +
                    $"command_budget={maxWorkgroupsPerCommand} " +
                    $"device_limit={_maxComputeWorkGroupCountX}x" +
                    $"{_maxComputeWorkGroupCountY}x{_maxComputeWorkGroupCountZ}");
            }
        }

        private void ExecuteOffscreenDraw(VulkanOffscreenGuestDraw work)
        {
            if (_deviceLost || work.Targets.Count == 0)
            {
                return;
            }

            var perfStart = System.Diagnostics.Stopwatch.GetTimestamp();
            Interlocked.Increment(ref _perfDrawCount);
            PerfOverlay.RecordDraw();
            try
            {
                ExecuteOffscreenDrawCore(work);
            }
            finally
            {
                // Single atomic add per draw: staging -start/+end separately
                // let the stats window reset land between them and report
                // huge negative draw_ms values.
                Interlocked.Add(
                    ref _perfDrawTicks,
                    System.Diagnostics.Stopwatch.GetTimestamp() - perfStart);
            }
        }

        private void ExecuteOffscreenDrawCore(VulkanOffscreenGuestDraw work)
        {
            if (work.Targets.Count > _maxColorAttachments)
            {
                Console.Error.WriteLine(
                    $"[LOADER][WARN] Vulkan skipped MRT draw requesting {work.Targets.Count} color attachments; " +
                    $"the selected device supports {_maxColorAttachments}.");
                ReturnPooledGuestData(work.Draw);
                return;
            }

            var targetFormats = new VulkanRenderTargetFormat[work.Targets.Count];
            for (var index = 0; index < targetFormats.Length; index++)
            {
                var target = work.Targets[index];
                if (!TryDecodeRenderTargetFormat(target.Format, target.NumberType, out targetFormats[index]) ||
                    !SupportsColorAttachment(targetFormats[index].Format))
                {
                    Console.Error.WriteLine(
                        $"[LOADER][WARN] Vulkan skipped MRT draw with unsupported color target " +
                        $"format={target.Format} number_type={target.NumberType}.");
                    ReturnPooledGuestData(work.Draw);
                    return;
                }
            }

            if (work.Draw.RenderState.Blends.Count != targetFormats.Length)
            {
                Console.Error.WriteLine(
                    "[LOADER][WARN] Vulkan skipped MRT draw with mismatched attachment/blend counts.");
                ReturnPooledGuestData(work.Draw);
                return;
            }

            for (var index = 0; index < targetFormats.Length; index++)
            {
                if (targetFormats[index].IsInteger &&
                    work.Draw.RenderState.Blends[index].Enable)
                {
                    Console.Error.WriteLine(
                        "[LOADER][WARN] Vulkan skipped MRT draw with blending enabled for an integer attachment.");
                    ReturnPooledGuestData(work.Draw);
                    return;
                }
            }

            if (!_supportsIndependentBlend)
            {
                for (var index = 1; index < work.Draw.RenderState.Blends.Count; index++)
                {
                    if (work.Draw.RenderState.Blends[index] !=
                        work.Draw.RenderState.Blends[0])
                    {
                        Console.Error.WriteLine(
                            "[LOADER][WARN] Vulkan skipped MRT draw requiring unsupported independentBlend.");
                        ReturnPooledGuestData(work.Draw);
                        return;
                    }
                }
            }

            var formats = new Format[targetFormats.Length];
            for (var index = 0; index < targetFormats.Length; index++)
            {
                formats[index] = targetFormats[index].Format;
            }

            var hasStorageFeedback = false;
            foreach (var texture in work.Draw.Textures)
            {
                if (!texture.IsStorage || texture.Address == 0)
                {
                    continue;
                }

                foreach (var target in work.Targets)
                {
                    if (target.Address != 0 && target.Address == texture.Address)
                    {
                        hasStorageFeedback = true;
                        break;
                    }
                }

                if (hasStorageFeedback)
                {
                    break;
                }
            }

            if (hasStorageFeedback)
            {
                Console.Error.WriteLine(
                    $"[LOADER][WARN] Vulkan skipped storage render-target feedback loop " +
                    $"targets={string.Join(',', work.Targets.Where(target => target.Address != 0).Select(target => $"0x{target.Address:X16}"))}; " +
                    "sampled aliases use ordered snapshots");
                ReturnPooledGuestData(work.Draw);
                return;
            }

            var targets = new GuestImageResource[work.Targets.Count];
            EnsureGuestSubmissionCapacity();
            for (var index = 0; index < targets.Length; index++)
            {
                var targetDescriptor = work.Targets[index].Address == 0 &&
                    work.DepthTarget is { } depthOnlyTarget
                        ? GetDepthOnlyColorTarget(depthOnlyTarget)
                        : work.Targets[index];
                targets[index] = GetOrCreateGuestImage(targetDescriptor, formats[index]);
                if (work.Targets[index].Address != 0 &&
                    TakeGuestImageInitialData(work.Targets[index].Address) is { } initialData &&
                    !targets[index].Initialized &&
                    (ulong)initialData.Length ==
                        (ulong)targets[index].Width * targets[index].Height * 4)
                {
                    UploadGuestImageInitialData(targets[index], initialData);
                }
            }

            var firstTarget = targets[0];
            TranslatedDrawResources? resources = null;
            CommandBuffer commandBuffer = default;
            var submitted = false;
            RenderPass transientRenderPass = default;
            Framebuffer transientFramebuffer = default;
            try
            {
                var extent = new Extent2D(firstTarget.Width, firstTarget.Height);
                var draw = work.Draw;
                var clearDepthForDraw = draw.RenderState.Depth.ClearEnable;
                if (work.DepthTarget?.ReadOnly == true && draw.RenderState.Depth.WriteEnable)
                {
                    draw = draw with
                    {
                        RenderState = draw.RenderState with
                        {
                            Depth = draw.RenderState.Depth with { WriteEnable = false },
                        },
                    };
                }
                GuestDepthResource? depth = null;
                DepthFramebufferResource? depthFramebuffer = null;
                if (ShouldAttachGuestDepth(
                        work.DepthTarget,
                        draw.RenderState.Depth) &&
                    work.DepthTarget is { } depthTarget)
                {
                    var effectiveDepthTarget = depthTarget;
                    if (depthTarget.Width < firstTarget.Width || depthTarget.Height < firstTarget.Height)
                    {
                        var matchingTexture = work.Draw.Textures.FirstOrDefault(texture =>
                            texture.Address == depthTarget.Address &&
                            texture.Width >= firstTarget.Width &&
                            texture.Height >= firstTarget.Height);
                        if (matchingTexture is not null)
                        {
                            effectiveDepthTarget = depthTarget with
                            {
                                Width = matchingTexture.Width,
                                Height = matchingTexture.Height,
                            };
                        }
                        else if (depthTarget.Width == 1 && depthTarget.Height == 1)
                        {
                            // Some Gen5 streams leave DB_DEPTH_SIZE_XY at its
                            // clear-state value while binding a full-size DB
                            // surface. The color attachment and viewport still
                            // define the render extent; treating that stale 1x1
                            // value literally discards the entire draw.
                            effectiveDepthTarget = depthTarget with
                            {
                                Width = firstTarget.Width,
                                Height = firstTarget.Height,
                            };
                        }

                        if (effectiveDepthTarget != depthTarget &&
                            _tracedDepthExtentFallbacks.Add(
                                (depthTarget.Address,
                                 effectiveDepthTarget.Width,
                                 effectiveDepthTarget.Height)))
                        {
                            Console.Error.WriteLine(
                                $"[LOADER][WARN] Vulkan repaired stale guest depth extent " +
                                $"addr=0x{depthTarget.Address:X16} " +
                                $"{depthTarget.Width}x{depthTarget.Height} -> " +
                                $"{effectiveDepthTarget.Width}x{effectiveDepthTarget.Height}");
                        }
                    }

                    depth = GetOrCreateGuestDepth(effectiveDepthTarget);
                    PrepareFirstUseDepth(depth, draw.RenderState.Depth);
                    if (clearDepthForDraw)
                    {
                        depth.GuestClearDepth = effectiveDepthTarget.ClearDepth;
                        depth.ClearDepth = effectiveDepthTarget.ClearDepth;
                    }
                    if (targets.Length == 1)
                    {
                        depthFramebuffer = GetOrCreateDepthFramebuffer(firstTarget, depth);
                    }
                }

                if (clearDepthForDraw)
                {
                    // DB_RENDER_CONTROL.DEPTH_CLEAR_ENABLE makes this a DB
                    // clear operation. The draw still produces color, but its
                    // interpolated vertex Z is not the guest clear value.
                    draw = draw with
                    {
                        RenderState = draw.RenderState with
                        {
                            Depth = draw.RenderState.Depth with
                            {
                                TestEnable = false,
                                WriteEnable = false,
                                ClearEnable = false,
                            },
                        },
                    };
                }

                var renderPass = depthFramebuffer is null
                    ? firstTarget.Initialized
                        ? firstTarget.RenderPass
                        : firstTarget.InitialRenderPass
                    : firstTarget.Initialized
                        ? depth!.Initialized && !clearDepthForDraw
                            ? depthFramebuffer.LoadRenderPass
                            : depthFramebuffer.DepthClearRenderPass
                        : depth!.Initialized && !clearDepthForDraw
                            ? depthFramebuffer.ColorClearRenderPass
                            : depthFramebuffer.BothClearRenderPass;
                var framebuffer = depthFramebuffer?.Framebuffer ?? firstTarget.Framebuffer;
                if (targets.Length > 1)
                {
                    (renderPass, framebuffer) = CreateRenderPassAndFramebuffer(
                        formats,
                        targets.Select(target => target.MipViews.Length > 0
                            ? target.MipViews[0]
                            : target.View).ToArray(),
                        firstTarget.Width,
                        firstTarget.Height,
                        targets.Select(target =>
                            target.Initialized || target.InitialUploadPending).ToArray(),
                        depth,
                        depth?.Initialized == true && !clearDepthForDraw);
                    transientRenderPass = renderPass;
                    transientFramebuffer = framebuffer;
                }

                resources = CreateTranslatedDrawResources(
                    draw,
                    renderPass,
                    formats,
                    extent,
                    targets,
                    hasDepthAttachment: depth is not null,
                    feedbackDepth: depth);
                resources.TransientRenderPass = transientRenderPass;
                resources.TransientFramebuffer = transientFramebuffer;
                transientRenderPass = default;
                transientFramebuffer = default;
                resources.DebugName =
                    $"SharpEmu offscreen mrt={targets.Length} " +
                    $"first=0x{work.Targets[0].Address:X16} " +
                    $"{firstTarget.Width}x{firstTarget.Height}";

                commandBuffer = BeginBatchedGuestCommands();
                _commandBuffer = commandBuffer;

                // Lifetime: recorded commands reference these resources, so
                // they join the batch before recording and are destroyed only
                // after the batch's fence signals.
                _batchResources.Add(resources);
                submitted = true;

                BeginDebugLabel(_commandBuffer, resources.DebugName);
                var hasStorageImages = false;
                foreach (var texture in resources.Textures)
                {
                    if (texture is null)
                    {
                        continue;
                    }

                    hasStorageImages |= texture.IsStorage;
                }

                CloseOpenTranslatedRenderPass();
                RecordGlobalBufferVisibilityBarrier(
                    _commandBuffer,
                    resources,
                    PipelineStageFlags.VertexShaderBit |
                    PipelineStageFlags.FragmentShaderBit);
                RecordRenderTargetFeedbackSnapshots(
                    resources,
                    PipelineStageFlags.FragmentShaderBit);
                RecordDepthFeedbackSnapshots(
                    resources,
                    PipelineStageFlags.FragmentShaderBit);
                RecordTextureUploads(resources, PipelineStageFlags.FragmentShaderBit);
                RecordStorageImagesForWrite(resources, PipelineStageFlags.FragmentShaderBit);

                var toColorAttachments = stackalloc ImageMemoryBarrier[targets.Length];
                var anyPriorContents = false;
                for (var index = 0; index < targets.Length; index++)
                {
                    var hasPriorContents =
                        targets[index].Initialized || targets[index].InitialUploadPending;
                    anyPriorContents |= hasPriorContents;
                    toColorAttachments[index] = new ImageMemoryBarrier
                    {
                        SType = StructureType.ImageMemoryBarrier,
                        SrcAccessMask = hasPriorContents ? AccessFlags.ShaderReadBit : 0,
                        DstAccessMask = AccessFlags.ColorAttachmentWriteBit,
                        OldLayout = hasPriorContents
                            ? ImageLayout.ShaderReadOnlyOptimal
                            : ImageLayout.Undefined,
                        NewLayout = ImageLayout.ColorAttachmentOptimal,
                        SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                        DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                        Image = targets[index].Image,
                        SubresourceRange = ColorSubresourceRange(),
                    };
                }
                _vk.CmdPipelineBarrier(
                    _commandBuffer,
                    anyPriorContents
                        ? PipelineStageFlags.AllCommandsBit
                        : PipelineStageFlags.TopOfPipeBit,
                    PipelineStageFlags.ColorAttachmentOutputBit,
                    0,
                    0,
                    null,
                    0,
                    null,
                    (uint)targets.Length,
                    toColorAttachments);

                if (depth is not null &&
                    depth.Layout == ImageLayout.ShaderReadOnlyOptimal)
                {
                    var toDepthAttachment = new ImageMemoryBarrier
                    {
                        SType = StructureType.ImageMemoryBarrier,
                        SrcAccessMask = AccessFlags.ShaderReadBit,
                        DstAccessMask =
                            AccessFlags.DepthStencilAttachmentReadBit |
                            AccessFlags.DepthStencilAttachmentWriteBit,
                        OldLayout = ImageLayout.ShaderReadOnlyOptimal,
                        NewLayout = ImageLayout.DepthStencilAttachmentOptimal,
                        SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                        DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                        Image = depth.Image,
                        SubresourceRange = new ImageSubresourceRange(
                            ImageAspectFlags.DepthBit, 0, 1, 0, 1),
                    };
                    _vk.CmdPipelineBarrier(
                        _commandBuffer,
                        PipelineStageFlags.FragmentShaderBit |
                        PipelineStageFlags.ComputeShaderBit,
                        PipelineStageFlags.EarlyFragmentTestsBit |
                        PipelineStageFlags.LateFragmentTestsBit,
                        0,
                        0,
                        null,
                        0,
                        null,
                        1,
                        &toDepthAttachment);
                }

                BeginTranslatedRenderPass(
                    renderPass,
                    framebuffer,
                    extent,
                    colorAttachmentCount: targets.Length,
                    hasDepthAttachment: depth is not null,
                    clearDepth: depth?.ClearDepth ?? 1f);
                RecordTranslatedDrawInPass(resources, extent);
                _vk.CmdEndRenderPass(_commandBuffer);

                var toShaderRead = stackalloc ImageMemoryBarrier[targets.Length];
                for (var index = 0; index < targets.Length; index++)
                {
                    toShaderRead[index] = new ImageMemoryBarrier
                    {
                        SType = StructureType.ImageMemoryBarrier,
                        SrcAccessMask = AccessFlags.ColorAttachmentWriteBit,
                        DstAccessMask = AccessFlags.ShaderReadBit,
                        OldLayout = ImageLayout.ColorAttachmentOptimal,
                        NewLayout = ImageLayout.ShaderReadOnlyOptimal,
                        SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                        DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                        Image = targets[index].Image,
                        SubresourceRange = ColorSubresourceRange(),
                    };
                }
                _vk.CmdPipelineBarrier(
                    _commandBuffer,
                    PipelineStageFlags.ColorAttachmentOutputBit,
                    PipelineStageFlags.FragmentShaderBit,
                    0,
                    0,
                    null,
                    0,
                    null,
                    (uint)targets.Length,
                    toShaderRead);

                if (hasStorageImages)
                {
                    RecordStorageImagesForRead(resources, PipelineStageFlags.FragmentShaderBit);
                }

                EndDebugLabel(_commandBuffer);

                var traceImages = GetTraceImages(resources, targets, work.ShaderAddress);
                _batchTraceImages.AddRange(traceImages);
                if (++_batchDrawCount >= 64 ||
                    (_traceGuestImageShaderFilterEnabled && traceImages.Count != 0))
                {
                    FlushBatchedGuestCommands();
                }

                foreach (var target in targets)
                {
                    target.Initialized = true;
                    target.InitialUploadPending = false;
                }
                if (depth is not null)
                {
                    depth.Initialized = true;
                    depth.Layout = ImageLayout.DepthStencilAttachmentOptimal;
                    if (clearDepthForDraw)
                    {
                        depth.InitializationSource = "guest-depth-clear";
                    }
                    else if (draw.RenderState.Depth.WriteEnable)
                    {
                        depth.InitializationSource = "translated-depth-write";
                    }
                }
                MarkSampledImagesInitialized(resources);
                MarkStorageImagesInitialized(resources, traceContents: false);

                if (work.PublishTarget)
                {
                    for (var index = 0; index < targets.Length; index++)
                    {
                        var guestTextureFormat = VulkanVideoPresenter.GetGuestTextureFormat(
                            work.Targets[index].Format,
                            work.Targets[index].NumberType);
                        if (guestTextureFormat == 0)
                        {
                            continue;
                        }

                        lock (_gate)
                        {
                            _availableGuestImages[targets[index].Address] = guestTextureFormat;
                        }
                    }
                }

                var tracePixelSpirv = false;
                if (_tracePixelSpirvBytes > 0 &&
                    _tracePixelSpirvBytes == work.Draw.PixelSpirv.Length)
                {
                    var pixelWriteCount = _pixelSpirvWriteCounts.TryGetValue(
                        _tracePixelSpirvBytes,
                        out var previousPixelWriteCount)
                            ? previousPixelWriteCount + 1
                            : 1;
                    _pixelSpirvWriteCounts[_tracePixelSpirvBytes] = pixelWriteCount;
                    tracePixelSpirv =
                        pixelWriteCount == _tracePixelSpirvOccurrence;
                }
                var traceTitleDraw =
                    !_tracedTitleDraw &&
                    _traceTitleDrawEnabled &&
                    IsTitleDraw(work.Draw.VertexBuffers);
                _tracedTitleDraw |= traceTitleDraw;

                foreach (var target in targets)
                {
                    var traceAddressWrite =
                        ShouldTraceGuestImageWriteForDiagnostics(target.Address);
                    var traceSmallWrites = _traceGuestWritesMode == "small" &&
                        target.Width <= 512 && target.Height <= 256;
                    var traceLargeWrites =
                        (_traceGuestWritesMode == "large" ||
                         _traceLargeGuestWriteOrdinal != 0) &&
                        target.Width >= 2560 && target.Height >= 1440;
                    if (traceAddressWrite || traceSmallWrites ||
                        traceLargeWrites || tracePixelSpirv || traceTitleDraw)
                    {
                        var writeCount = _tracedGuestWriteCounts.TryGetValue(
                            target.Address,
                            out var previousCount)
                            ? previousCount + 1
                            : 1;
                        _tracedGuestWriteCounts[target.Address] = writeCount;
                        var shouldTraceWrite = tracePixelSpirv || traceTitleDraw
                            ? true
                            : traceAddressWrite && _traceGuestWriteOrdinal > 0
                                ? writeCount == _traceGuestWriteOrdinal
                            : _traceLargeGuestWriteOrdinal != 0
                                ? writeCount == _traceLargeGuestWriteOrdinal
                            : writeCount <=
                                (traceLargeWrites ? 2 : traceSmallWrites ? 48 : 3);
                        if (traceAddressWrite || shouldTraceWrite)
                        {
                            var sampledTextures = string.Join(
                                ',',
                                work.Draw.Textures.Select(texture =>
                                    $"0x{texture.Address:X}:{texture.Width}x{texture.Height}:" +
                                    $"f{texture.Format}:n{texture.NumberType}:" +
                                    $"storage={(texture.IsStorage ? 1 : 0)}"));
                            var pixelDigest = Convert.ToHexString(
                                SHA256.HashData(work.Draw.PixelSpirv).AsSpan(0, 4));
                            Console.Error.WriteLine(
                                $"[LOADER][TRACE] vk.guest_write_sample " +
                                $"addr=0x{target.Address:X16} write={writeCount} " +
                                $"vs_bytes={work.Draw.VertexSpirv.Length} " +
                                $"ps_bytes={work.Draw.PixelSpirv.Length} ps_hash={pixelDigest} " +
                                $"vertices={work.Draw.VertexCount} instances={work.Draw.InstanceCount} " +
                                $"primitive=0x{work.Draw.PrimitiveType:X} " +
                                $"readback={(shouldTraceWrite ? 1 : 0)} textures=[{sampledTextures}]");
                        }

                        if (shouldTraceWrite)
                        {
                            _commandBuffer = _presentationCommandBuffer;
                            FlushBatchedGuestCommands();
                            Check(
                                _vk.QueueWaitIdle(_queue),
                                "vkQueueWaitIdle(guest write trace)");
                            TraceGuestImageContents(target);
                        }
                    }
                }
                if (_traceVulkanShaderEnabled)
                {
                    TraceVulkanShader(
                        $"vk.offscreen_draw mrt={targets.Length} " +
                        $"size={firstTarget.Width}x{firstTarget.Height} " +
                        $"textures={work.Draw.Textures.Count}");
                }
            }
            catch (Exception exception)
            {
                if (TryMarkDeviceLost(exception))
                {
                    return;
                }

                lock (_gate)
                {
                    foreach (var target in work.Targets)
                    {
                        if (!_guestImages.TryGetValue(target.Address, out var failedTarget) ||
                            !failedTarget.Initialized)
                        {
                            _availableGuestImages.Remove(target.Address);
                        }
                    }
                }

                Console.Error.WriteLine(
                    $"[LOADER][ERROR] Vulkan offscreen draw failed " +
                    $"mrt={work.Targets.Count}: {exception.Message}");
            }
            finally
            {
                _commandBuffer = _presentationCommandBuffer;
                // The command buffer is the shared batch; it is submitted and
                // freed by FlushBatchedGuestCommands. Resources joined the
                // batch list before recording, so only pre-recording failures
                // (submitted still false) own their cleanup here.
                if (!submitted && resources is not null)
                {
                    DestroyTranslatedDrawResources(resources);
                }

                if (transientFramebuffer.Handle != 0)
                {
                    _vk.DestroyFramebuffer(_device, transientFramebuffer, null);
                }

                if (transientRenderPass.Handle != 0)
                {
                    _vk.DestroyRenderPass(_device, transientRenderPass, null);
                }
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ExecuteGuestImageWrite(VulkanGuestImageWrite work)
        {
            if (_deviceLost || !_guestImages.TryGetValue(work.Address, out var target))
            {
                return;
            }

            if (work.Pixels is { } pixels)
            {
                if (pixels.Length > 0)
                {
                    UploadGuestImageInitialData(target, pixels);
                }

                return;
            }

            // Recorded into the shared batch command buffer: recording order
            // preserves queue-order semantics against earlier batched draws,
            // and the fill no longer costs a submit + full queue drain.
            var commandBuffer = BeginBatchedGuestCommands();
            CloseOpenTranslatedRenderPass();
            var toTransferDst = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = target.Initialized ? AccessFlags.ShaderReadBit : 0,
                DstAccessMask = AccessFlags.TransferWriteBit,
                OldLayout = target.Initialized
                    ? ImageLayout.ShaderReadOnlyOptimal
                    : ImageLayout.Undefined,
                NewLayout = ImageLayout.TransferDstOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = target.Image,
                SubresourceRange = ColorSubresourceRange(0, target.MipLevels),
            };
            _vk.CmdPipelineBarrier(
                commandBuffer,
                target.Initialized
                    ? PipelineStageFlags.FragmentShaderBit
                    : PipelineStageFlags.TopOfPipeBit,
                PipelineStageFlags.TransferBit,
                0,
                0,
                null,
                0,
                null,
                1,
                &toTransferDst);

            var clearValue = new ClearColorValue(
                (work.FillValue & 0xFF) / 255f,
                ((work.FillValue >> 8) & 0xFF) / 255f,
                ((work.FillValue >> 16) & 0xFF) / 255f,
                ((work.FillValue >> 24) & 0xFF) / 255f);
            var range = ColorSubresourceRange(0, target.MipLevels);
            _vk.CmdClearColorImage(
                commandBuffer,
                target.Image,
                ImageLayout.TransferDstOptimal,
                &clearValue,
                1,
                &range);

            var toShaderRead = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = AccessFlags.TransferWriteBit,
                DstAccessMask = AccessFlags.ShaderReadBit,
                OldLayout = ImageLayout.TransferDstOptimal,
                NewLayout = ImageLayout.ShaderReadOnlyOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = target.Image,
                SubresourceRange = ColorSubresourceRange(0, target.MipLevels),
            };
            _vk.CmdPipelineBarrier(
                commandBuffer,
                PipelineStageFlags.TransferBit,
                PipelineStageFlags.FragmentShaderBit,
                0,
                0,
                null,
                0,
                null,
                1,
                &toShaderRead);
            target.Initialized = true;
        }

        private void UploadGuestImageInitialData(GuestImageResource target, byte[] pixels)
        {
            var byteCount = (ulong)pixels.Length;
            var staging = CreateBuffer(
                byteCount,
                BufferUsageFlags.TransferSrcBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                out var stagingMemory);
            try
            {
                void* mapped;
                Check(
                    _vk.MapMemory(_device, stagingMemory, 0, byteCount, 0, &mapped),
                    "vkMapMemory(guest image init)");
                fixed (byte* source = pixels)
                {
                    System.Buffer.MemoryCopy(source, mapped, pixels.Length, pixels.Length);
                }

                _vk.UnmapMemory(_device, stagingMemory);

                // Recorded into the shared batch; the staging buffer joins
                // the batch's retire list and is destroyed when the batch
                // fence signals, so the upload costs no queue drain.
                var commandBuffer = BeginBatchedGuestCommands();
                CloseOpenTranslatedRenderPass();

                var toTransferDst = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask = target.Initialized ? AccessFlags.ShaderReadBit : 0,
                    DstAccessMask = AccessFlags.TransferWriteBit,
                    OldLayout = target.Initialized
                        ? ImageLayout.ShaderReadOnlyOptimal
                        : ImageLayout.Undefined,
                    NewLayout = ImageLayout.TransferDstOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = target.Image,
                    SubresourceRange = ColorSubresourceRange(0, target.MipLevels),
                };
                _vk.CmdPipelineBarrier(
                    commandBuffer,
                    target.Initialized
                        ? PipelineStageFlags.FragmentShaderBit
                        : PipelineStageFlags.TopOfPipeBit,
                    PipelineStageFlags.TransferBit,
                    0,
                    0,
                    null,
                    0,
                    null,
                    1,
                    &toTransferDst);

                var copyRegion = new BufferImageCopy
                {
                    BufferOffset = 0,
                    BufferRowLength = 0,
                    BufferImageHeight = 0,
                    ImageSubresource = new ImageSubresourceLayers(
                        ImageAspectFlags.ColorBit,
                        0,
                        0,
                        1),
                    ImageOffset = default,
                    ImageExtent = new Extent3D(target.Width, target.Height, 1),
                };
                _vk.CmdCopyBufferToImage(
                    commandBuffer,
                    staging,
                    target.Image,
                    ImageLayout.TransferDstOptimal,
                    1,
                    &copyRegion);

                var toShaderRead = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask = AccessFlags.TransferWriteBit,
                    DstAccessMask = AccessFlags.ShaderReadBit,
                    OldLayout = ImageLayout.TransferDstOptimal,
                    NewLayout = ImageLayout.ShaderReadOnlyOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = target.Image,
                    SubresourceRange = ColorSubresourceRange(0, target.MipLevels),
                };
                _vk.CmdPipelineBarrier(
                    commandBuffer,
                    PipelineStageFlags.TransferBit,
                    PipelineStageFlags.FragmentShaderBit,
                    0,
                    0,
                    null,
                    0,
                    null,
                    1,
                    &toShaderRead);

                target.Initialized = true;
                _batchRetireBuffers.Add((staging, stagingMemory));
                staging = default;
                stagingMemory = default;
                if (_traceGuestImageEvents)
                {
                    Console.Error.WriteLine(
                        $"[GIMG] seeded addr=0x{target.Address:X} " +
                        $"{target.Width}x{target.Height}");
                }
            }
            finally
            {
                if (staging.Handle != 0)
                {
                    _vk.DestroyBuffer(_device, staging, null);
                }

                if (stagingMemory.Handle != 0)
                {
                    _vk.FreeMemory(_device, stagingMemory, null);
                }
            }
        }

        private GuestImageResource GetOrCreateGuestImage(
            GuestRenderTarget target,
            Format format)
        {
            var mipLevels = ClampMipLevels(target.Width, target.Height, target.MipLevels);
            var guestFormat = GetGuestTextureFormat(target.Format, target.NumberType);
            var requestedKey = new GuestImageVariantKey(
                target.Address,
                target.Width,
                target.Height,
                mipLevels,
                guestFormat,
                format);
            if (_guestImages.TryGetValue(target.Address, out var existing))
            {
                if (existing.Width == target.Width &&
                    existing.Height == target.Height &&
                    existing.MipLevels == mipLevels &&
                    existing.GuestFormat == guestFormat &&
                    existing.Format == format)
                {
                    existing.IsCpuBacked = false;
                    existing.CpuContentFingerprint = 0;
                    if (existing.RenderPass.Handle == 0)
                    {
                        var attachmentView = existing.MipViews.Length > 0
                            ? existing.MipViews[0]
                            : existing.View;
                        var promoted = CreateRenderPassAndFramebuffer(
                            existing.Format,
                            attachmentView,
                            existing.Width,
                            existing.Height);
                        existing.RenderPass = promoted.RenderPass;
                        existing.InitialRenderPass = promoted.InitialRenderPass;
                        existing.Framebuffer = promoted.Framebuffer;
                        var promotedRenderPass = promoted.RenderPass;
                        var promotedInitialRenderPass = promoted.InitialRenderPass;
                        var promotedFramebuffer = promoted.Framebuffer;
                        var promotedName = GuestImageDebugName(target, format);
                        SetDebugName(ObjectType.RenderPass, promotedRenderPass.Handle, $"{promotedName} renderpass");
                        SetDebugName(ObjectType.RenderPass, promotedInitialRenderPass.Handle, $"{promotedName} initial-renderpass");
                        SetDebugName(ObjectType.Framebuffer, promotedFramebuffer.Handle, $"{promotedName} framebuffer");
                    }

                    return existing;
                }

                if (_traceGuestImageEvents)
                {
                    Console.Error.WriteLine(
                        $"[GIMG] recreate addr=0x{target.Address:X} " +
                        $"old={existing.Width}x{existing.Height}/{existing.Format}/m{existing.MipLevels} " +
                        $"new={target.Width}x{target.Height}/{format}/m{mipLevels} " +
                        $"initialized={existing.Initialized}");
                }

                _guestImageVariants.Add(
                    new GuestImageVariantKey(
                        existing.Address,
                        existing.Width,
                        existing.Height,
                        existing.MipLevels,
                    existing.GuestFormat,
                    existing.Format),
                    existing);
                _guestImages.Remove(target.Address);
                lock (_gate)
                {
                    _availableGuestImages.Remove(target.Address);
                    _guestImageExtents.Remove(target.Address);
                }

                // Address-filtered readback diagnostics should inspect the
                // replacement image too; a resized/reformatted allocation at
                // the same guest address is a different piece of evidence.
                _tracedGuestImageContents.Remove(target.Address);

                SharpEmu.HLE.GuestImageWriteTracker.Untrack(target.Address);
            }

            if (_guestImageVariants.Remove(requestedKey, out var retained))
            {
                retained.IsCpuBacked = false;
                retained.CpuContentFingerprint = 0;
                _guestImages.Add(target.Address, retained);
                lock (_gate)
                {
                    _guestImageExtents[target.Address] = (
                        target.Width,
                        target.Height,
                        GetTextureByteCount(target.Format, target.Width, target.Height));
                }

                if (target.Width <= 1920 && target.Height <= 1080)
                {
                    SharpEmu.HLE.GuestImageWriteTracker.Track(
                        target.Address,
                        (ulong)target.Width * target.Height * GetTextureBytesPerPixel(target.Format),
                        CurrentGuestWorkSequenceForDiagnostics,
                        "vulkan.render-target");
                }

                if (_traceGuestImageEvents)
                {
                    Console.Error.WriteLine(
                        $"[GIMG] retained addr=0x{target.Address:X} " +
                        $"{target.Width}x{target.Height} fmt={format} " +
                        $"initialized={retained.Initialized}");
                }

                return retained;
            }

            var imageInfo = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                Flags =
                    ImageCreateFlags.CreateMutableFormatBit |
                    ImageCreateFlags.CreateExtendedUsageBit,
                ImageType = ImageType.Type2D,
                Format = format,
                Extent = new Extent3D(target.Width, target.Height, 1),
                MipLevels = mipLevels,
                ArrayLayers = 1,
                Samples = SampleCountFlags.Count1Bit,
                Tiling = ImageTiling.Optimal,
                Usage =
                    ImageUsageFlags.ColorAttachmentBit |
                    ImageUsageFlags.SampledBit |
                    ImageUsageFlags.StorageBit |
                    ImageUsageFlags.TransferSrcBit |
                    ImageUsageFlags.TransferDstBit,
                SharingMode = SharingMode.Exclusive,
                InitialLayout = ImageLayout.Undefined,
            };
            Check(_vk.CreateImage(_device, &imageInfo, null, out var image), "vkCreateImage(offscreen)");
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
                "vkAllocateMemory(offscreen)");
            Check(_vk.BindImageMemory(_device, image, memory, 0), "vkBindImageMemory(offscreen)");
            // Rendering and uploads only define the mips they touch; define the whole
            // chain once so full-chain sampled binds never read Undefined layout.
            TransitionNewGuestImageToSampled(image, mipLevels);

            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = image,
                ViewType = ImageViewType.Type2D,
                Format = format,
                Components = new ComponentMapping(
                    ComponentSwizzle.Identity,
                    ComponentSwizzle.Identity,
                    ComponentSwizzle.Identity,
                    ComponentSwizzle.Identity),
                SubresourceRange = ColorSubresourceRange(0, mipLevels),
            };
            Check(
                _vk.CreateImageView(_device, &viewInfo, null, out var view),
                "vkCreateImageView(offscreen)");

            var mipViews = new ImageView[mipLevels];
            for (uint mipLevel = 0; mipLevel < mipLevels; mipLevel++)
            {
                viewInfo.SubresourceRange = ColorSubresourceRange(mipLevel, 1);
                ImageView mipView;
                Check(
                    _vk.CreateImageView(
                        _device,
                        &viewInfo,
                        null,
                        out mipView),
                    "vkCreateImageView(offscreen mip)");
                mipViews[mipLevel] = mipView;
            }

            var (renderPass, initialRenderPass, framebuffer) =
                CreateRenderPassAndFramebuffer(
                    format,
                    mipViews[0],
                    target.Width,
                    target.Height);

            var resource = new GuestImageResource
            {
                Address = target.Address,
                Width = target.Width,
                Height = target.Height,
                MipLevels = mipLevels,
                GuestFormat = guestFormat,
                Format = format,
                Image = image,
                Memory = memory,
                View = view,
                MipViews = mipViews,
                RenderPass = renderPass,
                InitialRenderPass = initialRenderPass,
                Framebuffer = framebuffer,
            };
            var debugName = GuestImageDebugName(target, format);
            SetDebugName(ObjectType.Image, image.Handle, $"{debugName} image");
            SetDebugName(ObjectType.ImageView, view.Handle, $"{debugName} view");
            for (var mipLevel = 0; mipLevel < mipViews.Length; mipLevel++)
            {
                SetDebugName(
                    ObjectType.ImageView,
                    mipViews[mipLevel].Handle,
                    $"{debugName} mip{mipLevel}");
            }
            SetDebugName(ObjectType.RenderPass, renderPass.Handle, $"{debugName} renderpass");
            SetDebugName(ObjectType.RenderPass, initialRenderPass.Handle, $"{debugName} initial-renderpass");
            SetDebugName(ObjectType.Framebuffer, framebuffer.Handle, $"{debugName} framebuffer");
            _guestImages.Add(target.Address, resource);
            lock (_gate)
            {
                _guestImageExtents[target.Address] = (
                    target.Width,
                    target.Height,
                    GetTextureByteCount(target.Format, target.Width, target.Height));
            }

            if (target.Width <= 1920 && target.Height <= 1080)
            {
                SharpEmu.HLE.GuestImageWriteTracker.Track(
                    target.Address,
                    (ulong)target.Width * target.Height * GetTextureBytesPerPixel(target.Format),
                    CurrentGuestWorkSequenceForDiagnostics,
                    "vulkan.render-target");
            }

            if (_traceGuestImageEvents)
            {
                Console.Error.WriteLine(
                    $"[GIMG] created-as-rt addr=0x{target.Address:X} " +
                    $"{target.Width}x{target.Height} fmt={format}");
            }

            return resource;
        }

        private (RenderPass RenderPass, RenderPass InitialRenderPass, Framebuffer Framebuffer)
            CreateRenderPassAndFramebuffer(
            Format format,
            ImageView attachmentView,
            uint width,
            uint height)
        {
            var load = CreateRenderPassAndFramebuffer(
                [format], [attachmentView], width, height, [true], null, false);
            var initial = CreateRenderPassAndFramebuffer(
                [format], [attachmentView], width, height, [false], null, false);
            _vk.DestroyFramebuffer(_device, initial.Framebuffer, null);
            return (load.RenderPass, initial.RenderPass, load.Framebuffer);
        }

        private (RenderPass RenderPass, Framebuffer Framebuffer) CreateRenderPassAndFramebuffer(
            IReadOnlyList<Format> formats,
            IReadOnlyList<ImageView> attachmentViews,
            uint width,
            uint height) =>
            CreateRenderPassAndFramebuffer(
                formats,
                attachmentViews,
                width,
                height,
                Enumerable.Repeat(true, formats.Count).ToArray(),
                null,
                false);

        private (RenderPass RenderPass, Framebuffer Framebuffer) CreateRenderPassAndFramebuffer(
            IReadOnlyList<Format> formats,
            IReadOnlyList<ImageView> attachmentViews,
            uint width,
            uint height,
            IReadOnlyList<bool> initialized,
            GuestDepthResource? depth,
            bool depthInitialized)
        {
            if (formats.Count == 0 ||
                formats.Count != attachmentViews.Count ||
                formats.Count != initialized.Count)
            {
                throw new InvalidOperationException(
                    "render target formats, views, and initialization states must have matching counts");
            }

            var attachmentCount = formats.Count + (depth is null ? 0 : 1);
            var attachments = stackalloc AttachmentDescription[attachmentCount];
            var colorReferences = stackalloc AttachmentReference[formats.Count];
            var views = stackalloc ImageView[attachmentCount];
            for (var index = 0; index < formats.Count; index++)
            {
                attachments[index] = new AttachmentDescription
                {
                    Format = formats[index],
                    Samples = SampleCountFlags.Count1Bit,
                    LoadOp = initialized[index]
                        ? AttachmentLoadOp.Load
                        : AttachmentLoadOp.Clear,
                    StoreOp = AttachmentStoreOp.Store,
                    StencilLoadOp = AttachmentLoadOp.DontCare,
                    StencilStoreOp = AttachmentStoreOp.DontCare,
                    InitialLayout = ImageLayout.ColorAttachmentOptimal,
                    FinalLayout = ImageLayout.ColorAttachmentOptimal,
                };
                colorReferences[index] = new AttachmentReference
                {
                    Attachment = (uint)index,
                    Layout = ImageLayout.ColorAttachmentOptimal,
                };
                views[index] = attachmentViews[index];
            }

            AttachmentReference depthReference = default;
            if (depth is not null)
            {
                attachments[formats.Count] = new AttachmentDescription
                {
                    Format = DepthFormat,
                    Samples = SampleCountFlags.Count1Bit,
                    LoadOp = depthInitialized
                        ? AttachmentLoadOp.Load
                        : AttachmentLoadOp.Clear,
                    StoreOp = AttachmentStoreOp.Store,
                    StencilLoadOp = AttachmentLoadOp.DontCare,
                    StencilStoreOp = AttachmentStoreOp.DontCare,
                    InitialLayout = depthInitialized
                        ? ImageLayout.DepthStencilAttachmentOptimal
                        : ImageLayout.Undefined,
                    FinalLayout = ImageLayout.DepthStencilAttachmentOptimal,
                };
                depthReference = new AttachmentReference
                {
                    Attachment = (uint)formats.Count,
                    Layout = ImageLayout.DepthStencilAttachmentOptimal,
                };
                views[formats.Count] = depth.View;
            }

            var subpass = new SubpassDescription
            {
                PipelineBindPoint = PipelineBindPoint.Graphics,
                ColorAttachmentCount = (uint)formats.Count,
                PColorAttachments = colorReferences,
                PDepthStencilAttachment = depth is null ? null : &depthReference,
            };
            var renderPassInfo = new RenderPassCreateInfo
            {
                SType = StructureType.RenderPassCreateInfo,
                AttachmentCount = (uint)attachmentCount,
                PAttachments = attachments,
                SubpassCount = 1,
                PSubpasses = &subpass,
            };
            Check(
                _vk.CreateRenderPass(_device, &renderPassInfo, null, out var renderPass),
                "vkCreateRenderPass(offscreen)");

            var framebufferInfo = new FramebufferCreateInfo
            {
                SType = StructureType.FramebufferCreateInfo,
                RenderPass = renderPass,
                AttachmentCount = (uint)attachmentCount,
                PAttachments = views,
                Width = width,
                Height = height,
                Layers = 1,
            };
            Check(
                _vk.CreateFramebuffer(_device, &framebufferInfo, null, out var framebuffer),
                "vkCreateFramebuffer(offscreen)");

            return (renderPass, framebuffer);
        }

        private (Image Image, DeviceMemory Memory, ImageView View) CreateDepthAttachment(
            uint width,
            uint height)
        {
            var imageInfo = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                ImageType = ImageType.Type2D,
                Format = DepthFormat,
                Extent = new Extent3D(Math.Max(width, 1), Math.Max(height, 1), 1),
                MipLevels = 1,
                ArrayLayers = 1,
                Samples = SampleCountFlags.Count1Bit,
                Tiling = ImageTiling.Optimal,
                Usage =
                    ImageUsageFlags.DepthStencilAttachmentBit |
                    ImageUsageFlags.SampledBit |
                    ImageUsageFlags.TransferSrcBit |
                    ImageUsageFlags.TransferDstBit,
                SharingMode = SharingMode.Exclusive,
                InitialLayout = ImageLayout.Undefined,
            };
            Check(_vk.CreateImage(_device, &imageInfo, null, out var image), "vkCreateImage(depth)");
            _vk.GetImageMemoryRequirements(_device, image, out var requirements);
            var memoryInfo = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = requirements.Size,
                MemoryTypeIndex = FindMemoryType(requirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
            };
            Check(_vk.AllocateMemory(_device, &memoryInfo, null, out var memory), "vkAllocateMemory(depth)");
            Check(_vk.BindImageMemory(_device, image, memory, 0), "vkBindImageMemory(depth)");

            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = image,
                ViewType = ImageViewType.Type2D,
                Format = DepthFormat,
                SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.DepthBit, 0, 1, 0, 1),
            };
            Check(_vk.CreateImageView(_device, &viewInfo, null, out var view), "vkCreateImageView(depth)");
            return (image, memory, view);
        }

        private GuestDepthResource GetOrCreateGuestDepth(GuestDepthTarget target)
        {
            var key = new GuestDepthKey(
                target.Address,
                target.ReadAddress,
                target.Width,
                target.Height,
                target.GuestFormat,
                target.SwizzleMode);
            if (_guestDepthImages.TryGetValue(key, out var existing))
            {
                existing.GuestClearDepth = target.ClearDepth;
                if (!existing.Initialized && existing.InitializationSource == "none")
                {
                    existing.ClearDepth = target.ClearDepth;
                }
                return existing;
            }

            var (image, memory, view) = CreateDepthAttachment(target.Width, target.Height);
            var resource = new GuestDepthResource
            {
                Key = key,
                Address = target.Address,
                ReadAddress = target.ReadAddress,
                WriteAddress = target.WriteAddress,
                Width = target.Width,
                Height = target.Height,
                GuestFormat = target.GuestFormat,
                SwizzleMode = target.SwizzleMode,
                Image = image,
                Memory = memory,
                View = view,
                GuestClearDepth = target.ClearDepth,
                ClearDepth = target.ClearDepth,
            };
            SetDebugName(
                ObjectType.Image,
                image.Handle,
                $"SharpEmu guest depth 0x{target.Address:X16} {target.Width}x{target.Height}");
            SetDebugName(
                ObjectType.ImageView,
                view.Handle,
                $"SharpEmu guest depth view 0x{target.Address:X16}");
            _guestDepthImages.Add(key, resource);
            if (_traceGuestImageEvents || _traceVulkanShaderEnabled)
            {
                Console.Error.WriteLine(
                    $"[GIMG] created-depth addr=0x{target.Address:X} " +
                    $"read=0x{target.ReadAddress:X} write=0x{target.WriteAddress:X} " +
                    $"{target.Width}x{target.Height} zfmt={target.GuestFormat} " +
                    $"sw={target.SwizzleMode} clear={target.ClearDepth:0.######}");
            }

            return resource;
        }

        private static void PrepareFirstUseDepth(
            GuestDepthResource depth,
            GuestDepthState state)
        {
            if (depth.Initialized || depth.InitializationSource != "none")
            {
                return;
            }

            var effectiveClear = depth.GuestClearDepth;
            var source = "guest-clear";
            if (state.TestEnable && !state.WriteEnable)
            {
                switch (state.CompareOp)
                {
                    case 1: // Less
                    case 3: // LessOrEqual
                        effectiveClear = 1f;
                        source = "neutral-first-use";
                        break;
                    case 4: // Greater
                    case 6: // GreaterOrEqual
                        effectiveClear = 0f;
                        source = "neutral-first-use";
                        break;
                    case 7: // Always
                        break;
                    default: // Never, Equal, NotEqual
                        source = "guest-clear-ambiguous";
                        Console.Error.WriteLine(
                            $"[LOADER][WARN] Vulkan has no neutral first-use depth clear " +
                            $"addr=0x{depth.Address:X16} compare={state.CompareOp}; " +
                            $"using guest clear {depth.GuestClearDepth:0.######}.");
                        break;
                }
            }

            depth.ClearDepth = effectiveClear;
            depth.InitializationSource = source;
            if (_traceDepthInitialization)
            {
                Console.Error.WriteLine(
                    $"[LOADER][TRACE] vk.depth_init " +
                    $"addr=0x{depth.Address:X16} size={depth.Width}x{depth.Height} " +
                    $"source={source} guest_clear={depth.GuestClearDepth:0.######} " +
                    $"effective_clear={effectiveClear:0.######} compare={state.CompareOp} " +
                    $"test={(state.TestEnable ? 1 : 0)} write={(state.WriteEnable ? 1 : 0)} " +
                    $"initialized=0");
            }
        }

        private GuestRenderTarget GetDepthOnlyColorTarget(GuestDepthTarget depth)
        {
            var key = new GuestDepthKey(
                depth.Address,
                depth.ReadAddress,
                depth.Width,
                depth.Height,
                depth.GuestFormat,
                depth.SwizzleMode);
            if (!_depthOnlyColorAddresses.TryGetValue(key, out var address))
            {
                address = _nextDepthOnlyColorAddress;
                _nextDepthOnlyColorAddress = checked(_nextDepthOnlyColorAddress + 0x1000_0000UL);
                _depthOnlyColorAddresses.Add(key, address);
            }

            // The translated fragment module still declares a color output,
            // even for a guest depth-only pass.  A private, never-published
            // color attachment keeps that output legal while the persistent
            // guest DB surface remains the only observable result.
            return new GuestRenderTarget(
                address,
                depth.Width,
                depth.Height,
                Format: 10,
                NumberType: 0);
        }

        private DepthFramebufferResource GetOrCreateDepthFramebuffer(
            GuestImageResource color,
            GuestDepthResource depth)
        {
            if (color.DepthFramebuffers.TryGetValue(depth.Key, out var existing))
            {
                return existing;
            }

            if (depth.Width < color.Width || depth.Height < color.Height)
            {
                throw new InvalidOperationException(
                    $"guest depth 0x{depth.Address:X16} extent {depth.Width}x{depth.Height} " +
                    $"is smaller than color target 0x{color.Address:X16} " +
                    $"{color.Width}x{color.Height}");
            }

            var attachmentView = color.MipViews.Length > 0 ? color.MipViews[0] : color.View;
            var loadRenderPass = CreateDepthRenderPass(
                color.Format,
                clearColor: false,
                clearDepth: false);
            var colorClearRenderPass = CreateDepthRenderPass(
                color.Format,
                clearColor: true,
                clearDepth: false);
            var depthClearRenderPass = CreateDepthRenderPass(
                color.Format,
                clearColor: false,
                clearDepth: true);
            var bothClearRenderPass = CreateDepthRenderPass(
                color.Format,
                clearColor: true,
                clearDepth: true);
            var attachments = stackalloc ImageView[2];
            attachments[0] = attachmentView;
            attachments[1] = depth.View;
            var framebufferInfo = new FramebufferCreateInfo
            {
                SType = StructureType.FramebufferCreateInfo,
                RenderPass = loadRenderPass,
                AttachmentCount = 2,
                PAttachments = attachments,
                Width = color.Width,
                Height = color.Height,
                Layers = 1,
            };
            Check(
                _vk.CreateFramebuffer(_device, &framebufferInfo, null, out var framebuffer),
                "vkCreateFramebuffer(offscreen depth)");
            var resource = new DepthFramebufferResource
            {
                Depth = depth,
                LoadRenderPass = loadRenderPass,
                ColorClearRenderPass = colorClearRenderPass,
                DepthClearRenderPass = depthClearRenderPass,
                BothClearRenderPass = bothClearRenderPass,
                Framebuffer = framebuffer,
            };
            var name = $"SharpEmu color 0x{color.Address:X16} depth 0x{depth.Address:X16}";
            SetDebugName(ObjectType.RenderPass, loadRenderPass.Handle, $"{name} load");
            SetDebugName(ObjectType.RenderPass, colorClearRenderPass.Handle, $"{name} color-clear");
            SetDebugName(ObjectType.RenderPass, depthClearRenderPass.Handle, $"{name} depth-clear");
            SetDebugName(ObjectType.RenderPass, bothClearRenderPass.Handle, $"{name} both-clear");
            SetDebugName(ObjectType.Framebuffer, framebuffer.Handle, $"{name} framebuffer");
            color.DepthFramebuffers.Add(depth.Key, resource);
            return resource;
        }

        private RenderPass CreateDepthRenderPass(
            Format colorFormat,
            bool clearColor,
            bool clearDepth)
        {
            var attachments = stackalloc AttachmentDescription[2];
            attachments[0] = new AttachmentDescription
            {
                Format = colorFormat,
                Samples = SampleCountFlags.Count1Bit,
                LoadOp = clearColor ? AttachmentLoadOp.Clear : AttachmentLoadOp.Load,
                StoreOp = AttachmentStoreOp.Store,
                StencilLoadOp = AttachmentLoadOp.DontCare,
                StencilStoreOp = AttachmentStoreOp.DontCare,
                InitialLayout = ImageLayout.ColorAttachmentOptimal,
                FinalLayout = ImageLayout.ColorAttachmentOptimal,
            };
            attachments[1] = new AttachmentDescription
            {
                Format = DepthFormat,
                Samples = SampleCountFlags.Count1Bit,
                LoadOp = clearDepth ? AttachmentLoadOp.Clear : AttachmentLoadOp.Load,
                StoreOp = AttachmentStoreOp.Store,
                StencilLoadOp = AttachmentLoadOp.DontCare,
                StencilStoreOp = AttachmentStoreOp.DontCare,
                InitialLayout = clearDepth
                    ? ImageLayout.Undefined
                    : ImageLayout.DepthStencilAttachmentOptimal,
                FinalLayout = ImageLayout.DepthStencilAttachmentOptimal,
            };
            var colorReference = new AttachmentReference
            {
                Attachment = 0,
                Layout = ImageLayout.ColorAttachmentOptimal,
            };
            var depthReference = new AttachmentReference
            {
                Attachment = 1,
                Layout = ImageLayout.DepthStencilAttachmentOptimal,
            };
            var subpass = new SubpassDescription
            {
                PipelineBindPoint = PipelineBindPoint.Graphics,
                ColorAttachmentCount = 1,
                PColorAttachments = &colorReference,
                PDepthStencilAttachment = &depthReference,
            };
            var dependency = new SubpassDependency
            {
                SrcSubpass = Vk.SubpassExternal,
                DstSubpass = 0,
                SrcStageMask = PipelineStageFlags.LateFragmentTestsBit,
                DstStageMask =
                    PipelineStageFlags.EarlyFragmentTestsBit |
                    PipelineStageFlags.LateFragmentTestsBit,
                SrcAccessMask = AccessFlags.DepthStencilAttachmentWriteBit,
                DstAccessMask =
                    AccessFlags.DepthStencilAttachmentReadBit |
                    AccessFlags.DepthStencilAttachmentWriteBit,
                DependencyFlags = DependencyFlags.ByRegionBit,
            };
            var createInfo = new RenderPassCreateInfo
            {
                SType = StructureType.RenderPassCreateInfo,
                AttachmentCount = 2,
                PAttachments = attachments,
                SubpassCount = 1,
                PSubpasses = &subpass,
                DependencyCount = 1,
                PDependencies = &dependency,
            };
            Check(
                _vk.CreateRenderPass(_device, &createInfo, null, out var renderPass),
                "vkCreateRenderPass(offscreen depth)");
            return renderPass;
        }

        private static uint ClampMipLevels(uint width, uint height, uint requestedMipLevels)
        {
            var largestDimension = Math.Max(width, height);
            uint maximumMipLevels = 1;
            while (largestDimension > 1)
            {
                largestDimension >>= 1;
                maximumMipLevels++;
            }

            return Math.Min(Math.Max(requestedMipLevels, 1u), maximumMipLevels);
        }

        private void DestroyGuestImage(GuestImageResource resource)
        {
            foreach (var depthFramebuffer in resource.DepthFramebuffers.Values)
            {
                DestroyDepthFramebuffer(depthFramebuffer);
            }
            resource.DepthFramebuffers.Clear();

            foreach (var view in resource.FormatViews.Values)
            {
                if (view.Handle != 0)
                {
                    _vk.DestroyImageView(_device, view, null);
                }
            }
            resource.FormatViews.Clear();

            if (resource.Framebuffer.Handle != 0)
            {
                _vk.DestroyFramebuffer(_device, resource.Framebuffer, null);
            }

            if (resource.RenderPass.Handle != 0)
            {
                _vk.DestroyRenderPass(_device, resource.RenderPass, null);
            }

            if (resource.InitialRenderPass.Handle != 0)
            {
                _vk.DestroyRenderPass(_device, resource.InitialRenderPass, null);
            }

            if (resource.View.Handle != 0)
            {
                _vk.DestroyImageView(_device, resource.View, null);
            }

            foreach (var mipView in resource.MipViews)
            {
                if (mipView.Handle != 0)
                {
                    _vk.DestroyImageView(_device, mipView, null);
                }
            }

            if (resource.Image.Handle != 0)
            {
                _vk.DestroyImage(_device, resource.Image, null);
            }

            if (resource.Memory.Handle != 0)
            {
                _vk.FreeMemory(_device, resource.Memory, null);
            }

        }

        private void DestroyDepthFramebuffer(DepthFramebufferResource resource)
        {
            if (resource.Framebuffer.Handle != 0)
            {
                _vk.DestroyFramebuffer(_device, resource.Framebuffer, null);
            }

            if (resource.LoadRenderPass.Handle != 0)
            {
                _vk.DestroyRenderPass(_device, resource.LoadRenderPass, null);
            }
            if (resource.ColorClearRenderPass.Handle != 0)
            {
                _vk.DestroyRenderPass(_device, resource.ColorClearRenderPass, null);
            }
            if (resource.DepthClearRenderPass.Handle != 0)
            {
                _vk.DestroyRenderPass(_device, resource.DepthClearRenderPass, null);
            }
            if (resource.BothClearRenderPass.Handle != 0)
            {
                _vk.DestroyRenderPass(_device, resource.BothClearRenderPass, null);
            }
        }

        private void DestroyGuestDepth(GuestDepthResource resource)
        {
            foreach (var sampleView in resource.SampleViews.Values)
            {
                if (sampleView.Handle != 0)
                {
                    _vk.DestroyImageView(_device, sampleView, null);
                }
            }
            resource.SampleViews.Clear();

            if (resource.View.Handle != 0)
            {
                _vk.DestroyImageView(_device, resource.View, null);
            }
            if (resource.Image.Handle != 0)
            {
                _vk.DestroyImage(_device, resource.Image, null);
            }
            if (resource.Memory.Handle != 0)
            {
                _vk.FreeMemory(_device, resource.Memory, null);
            }
        }

        private bool TryGetOrCreateGuestImageView(
            GuestImageResource resource,
            Format format,
            uint mipLevel,
            uint levelCount,
            uint dstSelect,
            out ImageView view)
        {
            try
            {
                view = GetOrCreateGuestImageView(resource, format, mipLevel, levelCount, dstSelect);
                return true;
            }
            catch (Exception exception)
            {
                view = default;
                TraceVulkanShader(
                    $"vk.texture_alias_view_failed addr=0x{resource.Address:X16} " +
                    $"image_format={resource.Format} view_format={format}: {exception.Message}");
                return false;
            }
        }

        private ImageView GetOrCreateGuestImageView(
            GuestImageResource resource,
            Format format,
            uint mipLevel,
            uint levelCount,
            uint dstSelect = 0xFAC)
        {
            if (mipLevel >= resource.MipLevels)
            {
                throw new InvalidOperationException(
                    $"View mip {mipLevel} exceeds image mip count {resource.MipLevels}.");
            }

            levelCount = Math.Max(levelCount, 1);
            levelCount = Math.Min(levelCount, resource.MipLevels - mipLevel);
            if (format == resource.Format && dstSelect == 0xFAC)
            {
                if (mipLevel == 0 && levelCount == resource.MipLevels)
                {
                    return resource.View;
                }

                if (levelCount == 1)
                {
                    return resource.MipViews[mipLevel];
                }
            }

            if (!IsCompatibleViewFormat(resource.Format, format))
            {
                throw new InvalidOperationException(
                    $"Incompatible image view format {format} for image {resource.Format}.");
            }

            var key = (format, mipLevel, levelCount, dstSelect);
            if (resource.FormatViews.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = resource.Image,
                ViewType = ImageViewType.Type2D,
                Format = format,
                Components = ToVkComponentMapping(dstSelect),
                SubresourceRange = ColorSubresourceRange(mipLevel, levelCount),
            };
            ImageView view;
            Check(
                _vk.CreateImageView(_device, &viewInfo, null, out view),
                "vkCreateImageView(guest alias)");
            resource.FormatViews.Add(key, view);
            SetDebugName(
                ObjectType.ImageView,
                view.Handle,
                $"SharpEmu guest 0x{resource.Address:X16} alias {format} mip{mipLevel}+{levelCount}");
            TraceVulkanShader(
                $"vk.texture_alias_view addr=0x{resource.Address:X16} " +
                $"image_format={resource.Format} view_format={format} " +
                $"mip={mipLevel} levels={levelCount} dst=0x{dstSelect:X3}");
            return view;
        }

        internal static bool IsCompatibleViewFormat(Format imageFormat, Format viewFormat)
        {
            if (imageFormat == viewFormat)
            {
                return true;
            }

            var imageClass = GetFormatCompatibilityClass(imageFormat);
            return imageClass != 0 && imageClass == GetFormatCompatibilityClass(viewFormat);
        }

        private static uint GetFormatCompatibilityClass(Format format) =>
            format switch
            {
                Format.R8Unorm or
                Format.R8Uint or
                Format.R8Sint => 8,
                Format.R16Sfloat => 16,
                Format.R32Uint or
                Format.R32Sint or
                Format.R32Sfloat or
                Format.R16G16Unorm or
                Format.R16G16Uint or
                Format.R16G16Sint or
                Format.R16G16Sfloat or
                Format.R8G8B8A8Unorm or
                Format.R8G8B8A8Srgb or
                Format.R8G8B8A8Uint or
                Format.R8G8B8A8Sint or
                Format.A2R10G10B10UnormPack32 or
                Format.A2B10G10R10UnormPack32 or
                Format.B10G11R11UfloatPack32 => 32,
                Format.R32G32Uint or
                Format.R32G32Sint or
                Format.R32G32Sfloat or
                Format.R16G16B16A16Unorm or
                Format.R16G16B16A16Uint or
                Format.R16G16B16A16Sint or
                Format.R16G16B16A16Sfloat => 64,
                Format.R32G32B32Sfloat => 96,
                Format.R32G32B32A32Uint or
                Format.R32G32B32A32Sint or
                Format.R32G32B32A32Sfloat => 128,
                _ => 0,
            };

        private void WaitForRenderWork()
        {
            var gpuWorkInFlight = _pendingGuestSubmissions.Count > 0 ||
                Array.Exists(_frameFencePending, static pending => pending);
            lock (_gate)
            {
                if (_closed ||
                    _pendingGuestWorkCount > 0 ||
                    (_latestPresentation is { } latest &&
                     latest.Sequence != _presentedSequence &&
                     latest.RequiredGuestWorkSequence <= _completedGuestWorkSequence))
                {
                    return;
                }

                System.Threading.Monitor.Wait(_gate, gpuWorkInFlight ? 1 : 8);
            }
        }

        private void Render(double _)
        {
            try
            {
                RenderCore();
            }
            catch (Exception exception)
            {
                // Device loss can strike between any two Vulkan calls in the frame;
                // keep the window loop pumping instead of tearing the presenter down.
                if (!TryMarkDeviceLost(exception))
                {
                    throw;
                }
            }
        }

        private void RenderCore()
        {
            if (Volatile.Read(ref _presenterCloseRequested))
            {
                Console.Error.WriteLine("[LOADER][WARN] Vulkan VideoOut closing on host shutdown request.");
                _window.Close();
                return;
            }

            if (!_vulkanReady)
            {
                return;
            }

            if (_deviceLost)
            {
                // Drain queued work so producers aren't back-pressured, then
                // return without any Vulkan call (fences never signal post-loss).
                while (TryTakeGuestWork(out var lostWork))
                {
                    CompleteGuestWork(lostWork);
                }

                return;
            }

            // Reuse of a frame slot waits only on that slot's fence, keeping
            // up to MaxFramesInFlight frames pipelined between CPU and GPU.
            var frameSlot = _currentFrameSlot;
            if (!TryWaitFrameSlot(frameSlot, _frameSlotWaitBudgetNs))
            {
                // The GPU is still finishing this slot's previous frame (slow
                // compute backlog). Don't block the macOS main thread — return
                // to the Cocoa event pump so the window keeps handling input
                // (F1 overlay, drag, close) and redrawing. The frame is retried
                // next Render(); the fence signals once the GPU catches up.
                return;
            }

            _presentationCommandBuffer = _frameCommandBuffers[frameSlot];
            _commandBuffer = _presentationCommandBuffer;
            if (!_deviceLost)
            {
                CollectCompletedGuestSubmissions(waitForOldest: false);
            }

            EvictDirtyCachedTextures();
            var completedWork = 0;
            var renderWorkDeadline = _renderWorkBudgetTicks > 0
                ? System.Diagnostics.Stopwatch.GetTimestamp() + _renderWorkBudgetTicks
                : long.MaxValue;
            while (completedWork < MaxGuestWorkPerRender)
            {
                // Never block the macOS main thread waiting for in-flight GPU
                // work to drain. If submission is at capacity (a slow-compute
                // backlog), stop processing and let the event pump run; the
                // remaining queued work is picked up on later frames as the GPU
                // completions free up capacity (collected non-blockingly here).
                CollectCompletedGuestSubmissions(waitForOldest: false);
                if (_pendingGuestSubmissions.Count >= MaxInFlightGuestSubmissions)
                {
                    break;
                }

                if (!TryTakeGuestWork(out var pendingGuestWork))
                {
                    break;
                }

                if (!string.Equals(
                        _activeGuestQueue.Name,
                        pendingGuestWork.Queue.Name,
                        StringComparison.Ordinal))
                {
                    // A host command buffer must never contain commands from
                    // two independent guest queues: an ordered action fences
                    // only its own queue's predecessor submissions.
                    FlushBatchedGuestCommands();
                }

                _activeGuestQueue = pendingGuestWork.Queue;
                _activeGuestWorkSequence = pendingGuestWork.Sequence;
                Volatile.Write(
                    ref _executingGuestWorkSequence,
                    pendingGuestWork.Sequence);
                using var guestQueueScope = EnterGuestQueue(
                    pendingGuestWork.Queue.Name,
                    pendingGuestWork.Queue.SubmissionId);
                _enqueueAsImmediateQueueFollowup = true;
                _immediateFollowupTail = null;
                var work = pendingGuestWork.Work;

                var traceWork = ShouldTracePresentedGuestImageContentsForDiagnostics();
                var workStart = traceWork ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
                if (traceWork && work is VulkanComputeGuestDispatch or VulkanOffscreenGuestDraw)
                {
                    Console.Error.WriteLine(
                        $"[LOADER][TRACE] vk.render_work_enter #{completedWork} " +
                        $"sequence={pendingGuestWork.Sequence} " +
                        $"queue={pendingGuestWork.Queue.Name} " +
                        $"submission={pendingGuestWork.Queue.SubmissionId} " +
                        $"queued_ms={(System.Diagnostics.Stopwatch.GetTimestamp() - pendingGuestWork.EnqueuedTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F3} " +
                        work.GetType().Name);
                }
                try
                {
                    switch (work)
                    {
                        case VulkanOffscreenGuestDraw offscreenDraw:
                            ExecuteOffscreenDraw(offscreenDraw);
                            break;
                        case VulkanComputeGuestDispatch computeDispatch:
                            ExecuteComputeDispatch(computeDispatch);
                            break;
                        case VulkanGuestImageWrite guestImageWrite:
                            ExecuteGuestImageWrite(guestImageWrite);
                            break;
                        case VulkanOrderedGuestAction orderedAction:
                            ExecuteOrderedGuestAction(orderedAction);
                            break;
                        case VulkanOrderedGuestFlip orderedFlip:
                            ExecuteOrderedGuestFlip(orderedFlip);
                            break;
                        case VulkanOrderedGuestFlipWait flipWait:
                            ExecuteOrderedGuestFlipWait(flipWait);
                            break;
                    }
                }
                finally
                {
                    CompleteGuestWork(pendingGuestWork);
                    _enqueueAsImmediateQueueFollowup = false;
                    _immediateFollowupTail = null;
                    Volatile.Write(ref _executingGuestWorkSequence, 0);
                }

                if (workStart != 0)
                {
                    var elapsedMs = (System.Diagnostics.Stopwatch.GetTimestamp() - workStart)
                        * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                    if (elapsedMs > 250.0)
                    {
                        var desc = work switch
                        {
                            VulkanComputeGuestDispatch c => $"compute cs=0x{c.ShaderAddress:X16} groups={c.GroupCountX}x{c.GroupCountY}x{c.GroupCountZ}",
                            VulkanOffscreenGuestDraw d =>
                                $"draw mrt={d.Targets.Count} " +
                                $"rt=0x{d.Targets[0].Address:X16} " +
                                $"{d.Targets[0].Width}x{d.Targets[0].Height}",
                            _ => work.GetType().Name,
                        };
                        Console.Error.WriteLine(
                            $"[LOADER][WARN] vk.slow_render_work {elapsedMs:F0}ms " +
                            $"queue={pendingGuestWork.Queue.Name} " +
                            $"submission={pendingGuestWork.Queue.SubmissionId} " +
                            $"sequence={pendingGuestWork.Sequence}: {desc}");
                    }
                }

                completedWork++;

                // Return to the main-thread event pump + present once the
                // per-frame budget is spent; remaining guest work is drained
                // on subsequent Render() calls. Without this a compute-heavy
                // backlog freezes the window (macOS "Not Responding").
                if (System.Diagnostics.Stopwatch.GetTimestamp() >= renderWorkDeadline)
                {
                    break;
                }
            }

            FlushBatchedGuestCommands();
            CollectAbandonedGuestImageVersions();

            if (!TryTakePresentation(_presentedSequence, out var presentation))
            {
				// A render-loop tick with no newer flip is normal. Warn only when
				// an actual queued presentation is waiting on unfinished guest work.
                if (ShouldTracePresentedGuestImageContentsForDiagnostics() &&
					HasPendingGuestPresentation(_presentedSequence) &&
                    _presentNotTakenLoggedSequence != _presentedSequence)
                {
                    _presentNotTakenLoggedSequence = _presentedSequence;
                    Console.Error.WriteLine(
                        $"[LOADER][WARN] vk.present_not_taken seq={_presentedSequence} " +
                        "— presentation submitted but its required guest work isn't complete; nothing shown.");
                }

                return;
            }

            if (ShouldTracePresentedGuestImageContentsForDiagnostics())
            {
                Console.Error.WriteLine(
                    $"[LOADER][TRACE] vk.present_taken addr=0x{presentation.GuestImageAddress:X16} " +
                    $"version={presentation.GuestImageVersion} " +
                    $"drawKind={presentation.DrawKind} hasPixels={presentation.Pixels is not null} " +
                    $"hasTranslatedDraw={presentation.TranslatedDraw is not null}");
            }

            if (presentation.Pixels is null &&
                presentation.DrawKind != GuestDrawKind.FullscreenBarycentric &&
                presentation.TranslatedDraw is null &&
                presentation.GuestImageAddress == 0)
            {
                return;
            }

            byte[]? pixels = null;
            if (presentation.Pixels is { } sourcePixels)
            {
                pixels = presentation.Width == _extent.Width && presentation.Height == _extent.Height
                    ? sourcePixels
                    : ScaleBgra(
                        sourcePixels,
                        presentation.Width,
                        presentation.Height,
                        _extent.Width,
                        _extent.Height);
                if ((ulong)pixels.Length > _stagingSize)
                {
                    return;
                }
            }

            TranslatedDrawResources? translatedResources = null;
            GuestImageResource? presentedGuestImage = null;
            var ownsPresentedGuestImageVersion = false;
            if (presentation.GuestImageVersion != 0)
            {
                ownsPresentedGuestImageVersion = _guestImageVersions.Remove(
                    presentation.GuestImageVersion,
                    out presentedGuestImage);
            }
            else if (presentation.GuestImageAddress != 0)
            {
                _guestImages.TryGetValue(
                    presentation.GuestImageAddress,
                    out presentedGuestImage);
            }

            if (presentation.GuestImageAddress != 0 &&
                (presentedGuestImage is null || !presentedGuestImage.Initialized))
            {
                if (ShouldTracePresentedGuestImageContentsForDiagnostics())
                {
                    Console.Error.WriteLine(
                        $"[LOADER][WARN] vk.present_dropped addr=0x{presentation.GuestImageAddress:X16} " +
                        $"version={presentation.GuestImageVersion} " +
                        $"found={(presentedGuestImage is not null)} " +
                        $"initialized={(presentedGuestImage?.Initialized ?? false)} " +
                        $"— no swapchain present this frame (black).");
                }

                if (ownsPresentedGuestImageVersion && presentedGuestImage is not null)
                {
                    DestroyGuestImage(presentedGuestImage);
                }

                return;
            }
            if (ownsPresentedGuestImageVersion)
            {
                System.Diagnostics.Debug.Assert(
                    _frameGuestImageVersions[frameSlot] is null,
                    "A reusable frame slot cannot still own a flip version.");
                _frameGuestImageVersions[frameSlot] = presentedGuestImage;
            }
            if (presentedGuestImage is not null)
            {
                _directPresentationCount++;
                var traceAddressedPresentation =
                    ShouldTraceAddressedPresentedGuestImage(presentedGuestImage);
                if (traceAddressedPresentation ||
                    ShouldTracePresentedGuestImageContentsForDiagnostics() &&
                    (_directPresentationCount is 1 or 30 or 120 ||
                     _directPresentationCount % 600 == 0))
                {
                    Console.Error.WriteLine(
                        $"[LOADER][TRACE] vk.present_sample frame={_directPresentationCount} " +
                        $"addr=0x{presentedGuestImage.Address:X16}");
                }
            }

            if (presentation.TranslatedDraw is { } translatedDraw)
            {
                try
                {
                    translatedResources = CreateTranslatedDrawResources(
                        translatedDraw,
                        _renderPass,
                        [_swapchainFormat],
                        _extent);
                    if (ShouldTracePresentedGuestImageContentsForDiagnostics() &&
                        !_firstGuestDrawPresented &&
                        translatedResources.Textures is
                        [
                        { GuestImage: { } guestImage },
                        ] &&
                        _tracedGuestImageContents.Add(guestImage.Address))
                    {
                        TraceGuestImageContents(guestImage);
                    }
                }
                catch (Exception exception)
                {
                    _presentedSequence = presentation.Sequence;
                    Console.Error.WriteLine(
                        $"[LOADER][ERROR] Vulkan VideoOut translated draw setup failed: {exception.Message}");
                    return;
                }
            }

            uint imageIndex;
            var acquireResult = _swapchainApi.AcquireNextImage(
                _device,
                _swapchain,
                ulong.MaxValue,
                _frameImageAvailable[frameSlot],
                default,
                &imageIndex);
            if (acquireResult == Result.ErrorOutOfDateKhr)
            {
                RecreateSwapchainResources("vkAcquireNextImageKHR", acquireResult);
                if (translatedResources is not null)
                {
                    DestroyTranslatedDrawResources(translatedResources);
                }
                if (ownsPresentedGuestImageVersion && presentedGuestImage is not null)
                {
                    if (_frameGuestImageVersions.Length > frameSlot &&
                        ReferenceEquals(
                            _frameGuestImageVersions[frameSlot],
                            presentedGuestImage))
                    {
                        _frameGuestImageVersions[frameSlot] = null;
                        _capturedGuestFlipVersions.Remove(
                            presentedGuestImage.FlipVersion);
                        DestroyGuestImage(presentedGuestImage);
                    }
                }

                return;
            }

            CheckSwapchainResult(acquireResult, "vkAcquireNextImageKHR");
            var recreateAfterPresent = acquireResult == Result.SuboptimalKhr;

            if (pixels is not null)
            {
                // The staging buffer is shared across frame slots; a CPU
                // pixel upload (splash / host frames) degrades to serial
                // presentation rather than corrupting an in-flight copy.
                WaitAllFrameSlots();
                void* mapped;
                Check(
                    _vk.MapMemory(_device, _stagingMemory, 0, (ulong)pixels.Length, 0, &mapped),
                    "vkMapMemory");
                fixed (byte* source = pixels)
                {
                    System.Buffer.MemoryCopy(source, mapped, pixels.Length, pixels.Length);
                }
                _vk.UnmapMemory(_device, _stagingMemory);
            }

            Check(_vk.ResetCommandBuffer(_commandBuffer, 0), "vkResetCommandBuffer");
            var beginInfo = new CommandBufferBeginInfo
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
            };
            Check(_vk.BeginCommandBuffer(_commandBuffer, &beginInfo), "vkBeginCommandBuffer");

            PipelineStageFlags waitStage;
            if (pixels is not null)
            {
                RecordUpload(imageIndex);
                waitStage = PipelineStageFlags.TransferBit;
            }
            else if (presentation.DrawKind == GuestDrawKind.FullscreenBarycentric)
            {
                var clearValue = default(ClearValue);
                var renderPassInfo = new RenderPassBeginInfo
                {
                    SType = StructureType.RenderPassBeginInfo,
                    RenderPass = _renderPass,
                    Framebuffer = _framebuffers[imageIndex],
                    RenderArea = new Rect2D(new Offset2D(0, 0), _extent),
                    ClearValueCount = 1,
                    PClearValues = &clearValue,
                };
                _vk.CmdBeginRenderPass(
                    _commandBuffer,
                    &renderPassInfo,
                    SubpassContents.Inline);
                _vk.CmdBindPipeline(
                    _commandBuffer,
                    PipelineBindPoint.Graphics,
                    _barycentricPipeline);
                _vk.CmdDraw(_commandBuffer, 3, 1, 0, 0);
                _vk.CmdEndRenderPass(_commandBuffer);
                waitStage = PipelineStageFlags.ColorAttachmentOutputBit;
            }
            else if (presentedGuestImage is not null)
            {
                RecordGuestImageBlit(imageIndex, presentedGuestImage);
                waitStage = PipelineStageFlags.TransferBit;
            }
            else if (translatedResources is not null)
            {
                RecordTranslatedDraw(imageIndex, translatedResources);
                waitStage = PipelineStageFlags.AllCommandsBit;
            }
            else
            {
                throw new InvalidOperationException(
                    $"Unsupported translated guest draw: {presentation.DrawKind}.");
            }

            if (PerfOverlay.Enabled)
            {
                RecordOverlayBlit(imageIndex, frameSlot);
            }

            Check(_vk.EndCommandBuffer(_commandBuffer), "vkEndCommandBuffer");

            var imageAvailable = _frameImageAvailable[frameSlot];
            var commandBuffer = _commandBuffer;
            var renderFinished = _renderFinishedPerImage[imageIndex];
            var submitInfo = new SubmitInfo
            {
                SType = StructureType.SubmitInfo,
                WaitSemaphoreCount = 1,
                PWaitSemaphores = &imageAvailable,
                PWaitDstStageMask = &waitStage,
                CommandBufferCount = 1,
                PCommandBuffers = &commandBuffer,
                SignalSemaphoreCount = 1,
                PSignalSemaphores = &renderFinished,
            };
            Check(
                _vk.QueueSubmit(_queue, 1, &submitInfo, _frameFences[frameSlot]),
                "vkQueueSubmit");
            _submitTimeline++;
            _frameTimelines[frameSlot] = _submitTimeline;
            _frameFencePending[frameSlot] = true;
            _frameTranslatedResources[frameSlot] = translatedResources;
            if (translatedResources is not null)
            {
                // CPU-side layout bookkeeping only; later command buffers are
                // recorded after this submission, so queue order makes the
                // flags valid before any dependent GPU work runs.
                MarkSampledImagesInitialized(translatedResources);
                MarkStorageImagesInitialized(translatedResources);
            }

            var swapchain = _swapchain;
            var presentInfo = new PresentInfoKHR
            {
                SType = StructureType.PresentInfoKhr,
                WaitSemaphoreCount = 1,
                PWaitSemaphores = &renderFinished,
                SwapchainCount = 1,
                PSwapchains = &swapchain,
                PImageIndices = &imageIndex,
            };
            var presentResult = _swapchainApi.QueuePresent(_queue, &presentInfo);
            if (presentResult == Result.ErrorOutOfDateKhr)
            {
                // The submitted frame still executes; RecreateSwapchainResources
                // drains it (and every frame slot) before destroying anything.
                RecreateSwapchainResources("vkQueuePresentKHR", presentResult);
                return;
            }

            CheckSwapchainResult(presentResult, "vkQueuePresentKHR");
            recreateAfterPresent |= presentResult == Result.SuboptimalKhr;
            VideoOutExports.ReportPresentedFrame();
            PerfOverlay.RecordPresent();
            if (_swapchainReadbackPending || !_pendingAliasImageDumps.IsEmpty)
            {
                // Diagnostics read back GPU memory and need this frame done.
                WaitFrameSlot(frameSlot);
                if (_swapchainReadbackPending)
                {
                    TraceSwapchainReadback();
                }

                while (_pendingAliasImageDumps.TryDequeue(out var aliasImage))
                {
                    TraceGuestImageContents(aliasImage);
                }
            }

            CollectCompletedGuestSubmissions(waitForOldest: false);
            _imageInitialized[imageIndex] = true;
            _currentFrameSlot = (frameSlot + 1) % MaxFramesInFlight;
            _presentedSequence = presentation.Sequence;
            if (presentation.IsSplash && !_splashPresented)
            {
                _splashPresented = true;
                Console.Error.WriteLine(
                    $"[LOADER][INFO] Vulkan VideoOut presented splash: " +
                    $"{presentation.Width}x{presentation.Height}");
            }
            else if (!presentation.IsSplash && !_firstFramePresented)
            {
                _firstFramePresented = true;
                Console.Error.WriteLine(
                    $"[LOADER][INFO] Vulkan VideoOut presented first frame: " +
                    $"{presentation.Width}x{presentation.Height}");
            }

            if (pixels is null && !_firstGuestDrawPresented)
            {
                _firstGuestDrawPresented = true;
                Console.Error.WriteLine(
                    $"[LOADER][INFO] Vulkan VideoOut presented guest frame: " +
                    (presentedGuestImage is not null
                        ? $"image=0x{presentedGuestImage.Address:X16} " +
                          $"{presentedGuestImage.Width}x{presentedGuestImage.Height}"
                        : presentation.TranslatedDraw is null
                        ? $"{presentation.DrawKind}"
                        : $"shader textures={presentation.TranslatedDraw.Textures.Count}"));
            }

            if (recreateAfterPresent)
            {
                RecreateSwapchainResources("present suboptimal", Result.SuboptimalKhr);
            }
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

        private void RecordTranslatedDraw(uint imageIndex, TranslatedDrawResources resources)
        {
            BeginDebugLabel(_commandBuffer, "SharpEmu swapchain draw");
            RecordGlobalBufferVisibilityBarrier(
                _commandBuffer,
                resources,
                PipelineStageFlags.VertexShaderBit |
                PipelineStageFlags.FragmentShaderBit);
            RecordTextureUploads(resources, PipelineStageFlags.FragmentShaderBit);
            RecordStorageImagesForWrite(resources, PipelineStageFlags.FragmentShaderBit);
            RecordTranslatedGraphicsPass(
                resources,
                _renderPass,
                _framebuffers[imageIndex],
                _extent);
            RecordStorageImagesForRead(resources, PipelineStageFlags.FragmentShaderBit);
            EndDebugLabel(_commandBuffer);
        }

        private void RecordTextureUploads(
            TranslatedDrawResources resources,
            PipelineStageFlags shaderStage)
        {
            foreach (var texture in resources.Textures)
            {
                if (texture.GuestDepth is { } depth)
                {
                    RecordGuestDepthForSampling(depth, shaderStage);
                }

                if (!texture.NeedsUpload)
                {
                    continue;
                }

                var toTransfer = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    DstAccessMask = AccessFlags.TransferWriteBit,
                    OldLayout = ImageLayout.Undefined,
                    NewLayout = ImageLayout.TransferDstOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = texture.Image,
                    SubresourceRange = ColorSubresourceRange(),
                };
                _vk.CmdPipelineBarrier(
                    _commandBuffer,
                    PipelineStageFlags.TopOfPipeBit,
                    PipelineStageFlags.TransferBit,
                    0,
                    0,
                    null,
                    0,
                    null,
                    1,
                    &toTransfer);

                var copyRegion = new BufferImageCopy
                {
                    BufferRowLength = texture.RowLength > texture.Width
                        ? texture.RowLength
                        : 0,
                    ImageSubresource = new ImageSubresourceLayers
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        LayerCount = 1,
                    },
                    ImageExtent = new Extent3D(texture.Width, texture.Height, 1),
                };
                _vk.CmdCopyBufferToImage(
                    _commandBuffer,
                    texture.StagingBuffer,
                    texture.Image,
                    ImageLayout.TransferDstOptimal,
                    1,
                    &copyRegion);

                var toShaderRead = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask = AccessFlags.TransferWriteBit,
                    DstAccessMask = AccessFlags.ShaderReadBit,
                    OldLayout = ImageLayout.TransferDstOptimal,
                    NewLayout = ImageLayout.ShaderReadOnlyOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = texture.Image,
                    SubresourceRange = ColorSubresourceRange(),
                };
                _vk.CmdPipelineBarrier(
                    _commandBuffer,
                    PipelineStageFlags.TransferBit,
                    shaderStage,
                    0,
                    0,
                    null,
                    0,
                    null,
                    1,
                    &toShaderRead);
                if (texture.Cached)
                {
                    // The queue executes command buffers in submission order,
                    // so once this upload is recorded every later draw can
                    // reuse the image without restaging it.
                    texture.NeedsUpload = false;
                }
            }
        }

        private void RecordGuestDepthForSampling(
            GuestDepthResource depth,
            PipelineStageFlags shaderStage)
        {
            if (depth.Layout == ImageLayout.ShaderReadOnlyOptimal)
            {
                return;
            }

            if (!depth.Initialized)
            {
                var depthRange = new ImageSubresourceRange(
                    ImageAspectFlags.DepthBit,
                    0,
                    1,
                    0,
                    1);
                var toTransfer = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    DstAccessMask = AccessFlags.TransferWriteBit,
                    OldLayout = depth.Layout,
                    NewLayout = ImageLayout.TransferDstOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = depth.Image,
                    SubresourceRange = depthRange,
                };
                _vk.CmdPipelineBarrier(
                    _commandBuffer,
                    PipelineStageFlags.TopOfPipeBit,
                    PipelineStageFlags.TransferBit,
                    0,
                    0,
                    null,
                    0,
                    null,
                    1,
                    &toTransfer);
                var clearValue = new ClearDepthStencilValue(depth.ClearDepth, 0);
                _vk.CmdClearDepthStencilImage(
                    _commandBuffer,
                    depth.Image,
                    ImageLayout.TransferDstOptimal,
                    &clearValue,
                    1,
                    &depthRange);
                depth.Initialized = true;
                if (depth.InitializationSource == "none")
                {
                    depth.InitializationSource = "sample-clear";
                }
                depth.Layout = ImageLayout.TransferDstOptimal;
            }

            var barrier = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = depth.Layout == ImageLayout.TransferDstOptimal
                    ? AccessFlags.TransferWriteBit
                    : AccessFlags.DepthStencilAttachmentWriteBit,
                DstAccessMask = AccessFlags.ShaderReadBit,
                OldLayout = depth.Layout,
                NewLayout = ImageLayout.ShaderReadOnlyOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = depth.Image,
                SubresourceRange = new ImageSubresourceRange(
                    ImageAspectFlags.DepthBit,
                    0,
                    1,
                    0,
                    1),
            };
            _vk.CmdPipelineBarrier(
                _commandBuffer,
                depth.Layout == ImageLayout.TransferDstOptimal
                    ? PipelineStageFlags.TransferBit
                    : PipelineStageFlags.LateFragmentTestsBit,
                shaderStage,
                0,
                0,
                null,
                0,
                null,
                1,
                &barrier);
            depth.Layout = ImageLayout.ShaderReadOnlyOptimal;
        }

        private void RecordRenderTargetFeedbackSnapshots(
            TranslatedDrawResources resources,
            PipelineStageFlags shaderStage)
        {
            foreach (var texture in resources.Textures)
            {
                if (texture.FeedbackSource is not { } source)
                {
                    continue;
                }

                // Initialize every destination mip to a deterministic zero.
                // Render-target writes currently populate mip 0; leaving the
                // remaining sampled mips undefined turns guest LOD selection
                // into driver-dependent colored garbage.
                var destinationToTransfer = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    DstAccessMask = AccessFlags.TransferWriteBit,
                    OldLayout = ImageLayout.Undefined,
                    NewLayout = ImageLayout.TransferDstOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = texture.Image,
                    SubresourceRange = ColorSubresourceRange(0, source.MipLevels),
                };
                _vk.CmdPipelineBarrier(
                    _commandBuffer,
                    PipelineStageFlags.TopOfPipeBit,
                    PipelineStageFlags.TransferBit,
                    0,
                    0,
                    null,
                    0,
                    null,
                    1,
                    &destinationToTransfer);

                var clearValue = new ClearColorValue(0f, 0f, 0f, 0f);
                // Avoid overlapping a clear and copy on mip 0: without an
                // intervening dependency two transfer writes to one
                // subresource are not ordered merely because they were
                // recorded in that order. Initialized sources overwrite mip
                // 0 directly and clear only the otherwise undefined tail.
                var clearBaseMip = source.Initialized ? 1u : 0u;
                if (clearBaseMip < source.MipLevels)
                {
                    var destinationRange = ColorSubresourceRange(
                        clearBaseMip,
                        source.MipLevels - clearBaseMip);
                    _vk.CmdClearColorImage(
                        _commandBuffer,
                        texture.Image,
                        ImageLayout.TransferDstOptimal,
                        &clearValue,
                        1,
                        &destinationRange);
                }

                if (source.Initialized)
                {
                    var sourceToTransfer = new ImageMemoryBarrier
                    {
                        SType = StructureType.ImageMemoryBarrier,
                        SrcAccessMask = AccessFlags.ShaderReadBit,
                        DstAccessMask = AccessFlags.TransferReadBit,
                        OldLayout = ImageLayout.ShaderReadOnlyOptimal,
                        NewLayout = ImageLayout.TransferSrcOptimal,
                        SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                        DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                        Image = source.Image,
                        // Only mip 0 has defined render-target contents.
                        SubresourceRange = ColorSubresourceRange(),
                    };
                    _vk.CmdPipelineBarrier(
                        _commandBuffer,
                        shaderStage |
                        PipelineStageFlags.ColorAttachmentOutputBit,
                        PipelineStageFlags.TransferBit,
                        0,
                        0,
                        null,
                        0,
                        null,
                        1,
                        &sourceToTransfer);

                    var copy = new ImageCopy
                    {
                        SrcSubresource = new ImageSubresourceLayers(
                            ImageAspectFlags.ColorBit,
                            0,
                            0,
                            1),
                        DstSubresource = new ImageSubresourceLayers(
                            ImageAspectFlags.ColorBit,
                            0,
                            0,
                            1),
                        Extent = new Extent3D(source.Width, source.Height, 1),
                    };
                    _vk.CmdCopyImage(
                        _commandBuffer,
                        source.Image,
                        ImageLayout.TransferSrcOptimal,
                        texture.Image,
                        ImageLayout.TransferDstOptimal,
                        1,
                        &copy);

                    var sourceToShaderRead = new ImageMemoryBarrier
                    {
                        SType = StructureType.ImageMemoryBarrier,
                        SrcAccessMask = AccessFlags.TransferReadBit,
                        DstAccessMask = AccessFlags.ShaderReadBit,
                        OldLayout = ImageLayout.TransferSrcOptimal,
                        NewLayout = ImageLayout.ShaderReadOnlyOptimal,
                        SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                        DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                        Image = source.Image,
                        SubresourceRange = ColorSubresourceRange(),
                    };
                    _vk.CmdPipelineBarrier(
                        _commandBuffer,
                        PipelineStageFlags.TransferBit,
                        shaderStage,
                        0,
                        0,
                        null,
                        0,
                        null,
                        1,
                        &sourceToShaderRead);
                }

                var destinationToShaderRead = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask = AccessFlags.TransferWriteBit,
                    DstAccessMask = AccessFlags.ShaderReadBit,
                    OldLayout = ImageLayout.TransferDstOptimal,
                    NewLayout = ImageLayout.ShaderReadOnlyOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = texture.Image,
                    SubresourceRange = ColorSubresourceRange(0, source.MipLevels),
                };
                _vk.CmdPipelineBarrier(
                    _commandBuffer,
                    PipelineStageFlags.TransferBit,
                    shaderStage,
                    0,
                    0,
                    null,
                    0,
                    null,
                    1,
                    &destinationToShaderRead);

                TraceVulkanShader(
                    $"vk.feedback_snapshot_copy addr=0x{source.Address:X16} " +
                    $"size={source.Width}x{source.Height} initialized={source.Initialized}");
            }
        }

        private void RecordDepthFeedbackSnapshots(
            TranslatedDrawResources resources,
            PipelineStageFlags shaderStage)
        {
            foreach (var texture in resources.Textures)
            {
                if (texture.DepthFeedbackSource is not { } source)
                {
                    continue;
                }

                var depthRange = new ImageSubresourceRange(
                    ImageAspectFlags.DepthBit,
                    0,
                    1,
                    0,
                    1);
                var destinationToTransfer = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    DstAccessMask = AccessFlags.TransferWriteBit,
                    OldLayout = ImageLayout.Undefined,
                    NewLayout = ImageLayout.TransferDstOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = texture.Image,
                    SubresourceRange = depthRange,
                };
                _vk.CmdPipelineBarrier(
                    _commandBuffer,
                    PipelineStageFlags.TopOfPipeBit,
                    PipelineStageFlags.TransferBit,
                    0,
                    0,
                    null,
                    0,
                    null,
                    1,
                    &destinationToTransfer);

                if (source.Initialized)
                {
                    var sourceToTransfer = new ImageMemoryBarrier
                    {
                        SType = StructureType.ImageMemoryBarrier,
                        SrcAccessMask = source.Layout == ImageLayout.ShaderReadOnlyOptimal
                            ? AccessFlags.ShaderReadBit
                            : AccessFlags.DepthStencilAttachmentWriteBit,
                        DstAccessMask = AccessFlags.TransferReadBit,
                        OldLayout = source.Layout,
                        NewLayout = ImageLayout.TransferSrcOptimal,
                        SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                        DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                        Image = source.Image,
                        SubresourceRange = depthRange,
                    };
                    _vk.CmdPipelineBarrier(
                        _commandBuffer,
                        shaderStage |
                        PipelineStageFlags.EarlyFragmentTestsBit |
                        PipelineStageFlags.LateFragmentTestsBit,
                        PipelineStageFlags.TransferBit,
                        0,
                        0,
                        null,
                        0,
                        null,
                        1,
                        &sourceToTransfer);
                    var copy = new ImageCopy
                    {
                        SrcSubresource = new ImageSubresourceLayers(
                            ImageAspectFlags.DepthBit,
                            0,
                            0,
                            1),
                        DstSubresource = new ImageSubresourceLayers(
                            ImageAspectFlags.DepthBit,
                            0,
                            0,
                            1),
                        Extent = new Extent3D(source.Width, source.Height, 1),
                    };
                    _vk.CmdCopyImage(
                        _commandBuffer,
                        source.Image,
                        ImageLayout.TransferSrcOptimal,
                        texture.Image,
                        ImageLayout.TransferDstOptimal,
                        1,
                        &copy);

                    var sourceToAttachment = new ImageMemoryBarrier
                    {
                        SType = StructureType.ImageMemoryBarrier,
                        SrcAccessMask = AccessFlags.TransferReadBit,
                        DstAccessMask =
                            AccessFlags.DepthStencilAttachmentReadBit |
                            AccessFlags.DepthStencilAttachmentWriteBit,
                        OldLayout = ImageLayout.TransferSrcOptimal,
                        NewLayout = ImageLayout.DepthStencilAttachmentOptimal,
                        SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                        DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                        Image = source.Image,
                        SubresourceRange = depthRange,
                    };
                    _vk.CmdPipelineBarrier(
                        _commandBuffer,
                        PipelineStageFlags.TransferBit,
                        PipelineStageFlags.EarlyFragmentTestsBit |
                        PipelineStageFlags.LateFragmentTestsBit,
                        0,
                        0,
                        null,
                        0,
                        null,
                        1,
                        &sourceToAttachment);
                    source.Layout = ImageLayout.DepthStencilAttachmentOptimal;
                }
                else
                {
                    var clearValue = new ClearDepthStencilValue(source.ClearDepth, 0);
                    _vk.CmdClearDepthStencilImage(
                        _commandBuffer,
                        texture.Image,
                        ImageLayout.TransferDstOptimal,
                        &clearValue,
                        1,
                        &depthRange);
                }

                var destinationToShader = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask = AccessFlags.TransferWriteBit,
                    DstAccessMask = AccessFlags.ShaderReadBit,
                    OldLayout = ImageLayout.TransferDstOptimal,
                    NewLayout = ImageLayout.ShaderReadOnlyOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = texture.Image,
                    SubresourceRange = depthRange,
                };
                _vk.CmdPipelineBarrier(
                    _commandBuffer,
                    PipelineStageFlags.TransferBit,
                    shaderStage,
                    0,
                    0,
                    null,
                    0,
                    null,
                    1,
                    &destinationToShader);
            }
        }

        private void RecordStorageImagesForWrite(
            TranslatedDrawResources resources,
            PipelineStageFlags shaderStage)
        {
            var transitioned = new HashSet<GuestImageResource>();
            foreach (var texture in resources.Textures)
            {
                if (!texture.IsStorage ||
                    texture.GuestImage is not { } guestImage ||
                    !transitioned.Add(guestImage))
                {
                    continue;
                }

                var barrier = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask =
                        guestImage.Initialized || guestImage.InitialUploadPending
                        ? AccessFlags.ShaderReadBit
                        : 0,
                    DstAccessMask =
                        AccessFlags.ShaderReadBit |
                        AccessFlags.ShaderWriteBit,
                    OldLayout =
                        guestImage.Initialized || guestImage.InitialUploadPending
                        ? ImageLayout.ShaderReadOnlyOptimal
                        : ImageLayout.Undefined,
                    NewLayout = ImageLayout.General,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = guestImage.Image,
                    SubresourceRange = ColorSubresourceRange(0, guestImage.MipLevels),
                };
                _vk.CmdPipelineBarrier(
                    _commandBuffer,
                    guestImage.Initialized || guestImage.InitialUploadPending
                        ? shaderStage
                        : PipelineStageFlags.TopOfPipeBit,
                    shaderStage,
                    0,
                    0,
                    null,
                    0,
                    null,
                    1,
                    &barrier);
            }
        }

        private void RecordStorageImagesForRead(
            TranslatedDrawResources resources,
            PipelineStageFlags shaderStage)
        {
            var transitioned = new HashSet<GuestImageResource>();
            foreach (var texture in resources.Textures)
            {
                if (!texture.IsStorage ||
                    texture.GuestImage is not { } guestImage ||
                    !transitioned.Add(guestImage))
                {
                    continue;
                }

                var barrier = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask =
                        AccessFlags.ShaderReadBit |
                        AccessFlags.ShaderWriteBit,
                    DstAccessMask = AccessFlags.ShaderReadBit,
                    OldLayout = ImageLayout.General,
                    NewLayout = ImageLayout.ShaderReadOnlyOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = guestImage.Image,
                    SubresourceRange = ColorSubresourceRange(0, guestImage.MipLevels),
                };
                _vk.CmdPipelineBarrier(
                    _commandBuffer,
                    shaderStage,
                    shaderStage,
                    0,
                    0,
                    null,
                    0,
                    null,
                    1,
                    &barrier);
            }
        }

        private void MarkStorageImagesInitialized(
            TranslatedDrawResources resources,
            bool traceContents = true)
        {
            List<GuestImageResource>? traceImages = null;
            lock (_gate)
            {
                foreach (var texture in resources.Textures)
                {
                    if (!texture.IsStorage ||
                        texture.Address == 0 ||
                        texture.GuestImage is not { } guestImage)
                    {
                        continue;
                    }

                    guestImage.Initialized = true;
                    guestImage.InitialUploadPending = false;
                    if (guestImage.GuestFormat != 0)
                    {
                        _availableGuestImages[texture.Address] = guestImage.GuestFormat;
                    }

                    if (traceContents &&
                        ShouldTraceGuestImageContents(guestImage))
                    {
                        traceImages ??= [];
                        traceImages.Add(guestImage);
                    }
                }
            }

            if (traceImages is null)
            {
                return;
            }

            foreach (var image in traceImages)
            {
                TraceGuestImageContents(image);
            }
        }

        private static void MarkSampledImagesInitialized(
            TranslatedDrawResources resources)
        {
            lock (_gate)
            {
                foreach (var texture in resources.Textures)
                {
                    if (!texture.NeedsUpload ||
                        texture.IsStorage ||
                        texture.Address == 0 ||
                        texture.GuestImage is not { } guestImage)
                    {
                        continue;
                    }

                    guestImage.Initialized = true;
                    guestImage.InitialUploadPending = false;
                    if (texture.UpdatesCpuContent)
                    {
                        guestImage.CpuContentFingerprint = texture.CpuContentFingerprint;
                    }
                }
            }
        }

        private bool ShouldTraceGuestImageContents(
            GuestImageResource image,
            ulong shaderAddress = 0)
        {
            if (image.Address == 0)
            {
                return false;
            }

            if (_traceGuestImageShaderFilterEnabled &&
                !AddressListContains(
                    "SHARPEMU_TRACE_GUEST_IMAGE_SHADER_ADDRS",
                    shaderAddress))
            {
                return false;
            }

            if ((_traceGuestImageWidth > 0 && image.Width != _traceGuestImageWidth) ||
                (_traceGuestImageHeight > 0 && image.Height != _traceGuestImageHeight))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(_traceGuestImageFormat) &&
                (!Enum.TryParse<Format>(
                        _traceGuestImageFormat,
                        ignoreCase: true,
                        out var expectedFormat) ||
                 image.Format != expectedFormat))
            {
                return false;
            }

            var addressMatched = ShouldTraceGuestImageAddressForDiagnostics(image.Address);
            if (addressMatched && _traceGuestImageOccurrence > 0)
            {
                var count = _guestImageTraceCounts.TryGetValue(image.Address, out var previous)
                    ? previous + 1
                    : 1;
                _guestImageTraceCounts[image.Address] = count;
                return count == _traceGuestImageOccurrence;
            }

            var broadTrace =
                ShouldTraceGuestImageContentsForDiagnostics() &&
                image.Width >= 1280 &&
                image.Height >= 720;
            if (GuestImageTraceInterval() is { } interval)
            {
                var addressFilter = Environment.GetEnvironmentVariable(
                    "SHARPEMU_TRACE_GUEST_IMAGE_ADDRS");
                if (!string.IsNullOrWhiteSpace(addressFilter) && !addressMatched)
                {
                    return false;
                }

                if (image.Width < 1280 || image.Height < 720)
                {
                    return false;
                }

                _globalGuestImageDrawCount++;
                if (_globalGuestImageDrawCount < GuestImageTraceStartAfter() ||
                    _intervalReadbackCount > 3000)
                {
                    return false;
                }

                var count = _guestImageTraceCounts.TryGetValue(image.Address, out var previous)
                    ? previous + 1
                    : 1;
                _guestImageTraceCounts[image.Address] = count;
                return count % interval == 0;
            }

            return (addressMatched || broadTrace) &&
                   _tracedGuestImageContents.Add(image.Address);
        }

        private readonly Dictionary<ulong, long> _guestImageTraceCounts = new();
        private long _globalGuestImageDrawCount;
        private long _intervalReadbackCount;

        private static long? _cachedGuestImageTraceInterval = long.MinValue;
        private static long _cachedGuestImageTraceStartAfter;

        // SHARPEMU_TRACE_GUEST_IMAGES=every:N[@M] — read back 1280x720+ guest
        // images every Nth draw into each, starting after M total such draws.
        private static long? GuestImageTraceInterval()
        {
            if (_cachedGuestImageTraceInterval == long.MinValue)
            {
                var mode = Environment.GetEnvironmentVariable("SHARPEMU_TRACE_GUEST_IMAGES");
                long? interval = null;
                if (mode is not null && mode.StartsWith("every:", StringComparison.Ordinal))
                {
                    var spec = mode["every:".Length..];
                    var at = spec.IndexOf('@');
                    var intervalText = at < 0 ? spec : spec[..at];
                    if (long.TryParse(intervalText, out var parsed) && parsed > 0)
                    {
                        interval = parsed;
                    }

                    if (at >= 0 && long.TryParse(spec[(at + 1)..], out var after) && after > 0)
                    {
                        _cachedGuestImageTraceStartAfter = after;
                    }
                }

                _cachedGuestImageTraceInterval = interval;
            }

            return _cachedGuestImageTraceInterval;
        }

        private static long GuestImageTraceStartAfter()
        {
            _ = GuestImageTraceInterval();
            return _cachedGuestImageTraceStartAfter;
        }

        // Diagnostics toggles are read once: these run per draw / per cached
        // texture hit, and env lookups plus string parsing are far too
        // expensive there (and non-trivially so under Rosetta 2).
        private static readonly bool _vulkanValidationEnabled =
            string.Equals(
                Environment.GetEnvironmentVariable("SHARPEMU_VK_VALIDATION"),
                "1",
                StringComparison.Ordinal);
        // Object names and command labels are useful in RenderDoc and validation
        // captures, but formatting them and calling the debug-utils driver hooks
        // for every draw is measurable overhead in normal gameplay.
        private static readonly bool _vulkanDebugUtilsEnabled =
            _vulkanValidationEnabled ||
            string.Equals(
                Environment.GetEnvironmentVariable("SHARPEMU_VK_DEBUG_LABELS"),
                "1",
                StringComparison.Ordinal);
        private static readonly string? _traceGuestImagesMode =
            Environment.GetEnvironmentVariable("SHARPEMU_TRACE_GUEST_IMAGES");
        private static readonly bool _traceGuestImagesEnabled =
            string.Equals(_traceGuestImagesMode, "1", StringComparison.Ordinal);
        private static readonly bool _tracePresentedGuestImagesEnabled =
            _traceGuestImagesEnabled ||
            string.Equals(_traceGuestImagesMode, "present", StringComparison.OrdinalIgnoreCase);
        private static readonly bool _traceVulkanResourcesEnabled =
            string.Equals(
                Environment.GetEnvironmentVariable("SHARPEMU_LOG_VK_RESOURCES"),
                "1",
                StringComparison.Ordinal);
        private static readonly bool _traceVulkanShaderEnabled =
            string.Equals(
                Environment.GetEnvironmentVariable("SHARPEMU_LOG_AGC"),
                "1",
                StringComparison.Ordinal) ||
            string.Equals(
                Environment.GetEnvironmentVariable("SHARPEMU_LOG_AGC_SHADER"),
                "1",
                StringComparison.Ordinal);
        private static readonly bool _traceDepthInitialization =
            string.Equals(
                Environment.GetEnvironmentVariable("SHARPEMU_TRACE_DEPTH_INIT"),
                "1",
                StringComparison.Ordinal);
        private static readonly long _traceGuestImageOccurrence =
            long.TryParse(
                Environment.GetEnvironmentVariable("SHARPEMU_TRACE_GUEST_IMAGE_OCCURRENCE"),
                out var traceGuestImageOccurrence) &&
            traceGuestImageOccurrence > 0
                ? traceGuestImageOccurrence
                : 0;
        private static readonly uint _traceGuestImageWidth =
            uint.TryParse(
                Environment.GetEnvironmentVariable("SHARPEMU_TRACE_GUEST_IMAGE_WIDTH"),
                out var traceGuestImageWidth)
                ? traceGuestImageWidth
                : 0;
        private static readonly uint _traceGuestImageHeight =
            uint.TryParse(
                Environment.GetEnvironmentVariable("SHARPEMU_TRACE_GUEST_IMAGE_HEIGHT"),
                out var traceGuestImageHeight)
                ? traceGuestImageHeight
                : 0;
        private static readonly string? _traceGuestImageFormat =
            Environment.GetEnvironmentVariable("SHARPEMU_TRACE_GUEST_IMAGE_FORMAT");
        private static readonly long _tracePresentedGuestImageOccurrence =
            long.TryParse(
                Environment.GetEnvironmentVariable(
                    "SHARPEMU_TRACE_PRESENTED_GUEST_IMAGE_OCCURRENCE"),
                out var tracePresentedGuestImageOccurrence) &&
            tracePresentedGuestImageOccurrence > 0
                ? tracePresentedGuestImageOccurrence
                : 0;
        private static readonly bool _traceGuestImageShaderFilterEnabled =
            !string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable(
                    "SHARPEMU_TRACE_GUEST_IMAGE_SHADER_ADDRS"));
        private static readonly bool _traceGuestImageAddressFilterEnabled =
            !string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable(
                    "SHARPEMU_TRACE_GUEST_IMAGE_ADDRS"));
        private static readonly bool _forceFullscreenPipeline =
            Environment.GetEnvironmentVariable("SHARPEMU_FORCE_FULLSCREEN_PIPELINE") == "1";
        private static readonly bool _forceFullscreenVertex =
            Environment.GetEnvironmentVariable("SHARPEMU_FORCE_FULLSCREEN_VERTEX") == "1";
        private static readonly bool _forceTitleFullscreenVertex =
            Environment.GetEnvironmentVariable("SHARPEMU_FORCE_TITLE_FULLSCREEN_VERTEX") == "1";
        private static readonly bool _forceDefaultRasterState =
            Environment.GetEnvironmentVariable("SHARPEMU_FORCE_DEFAULT_RASTER_STATE") == "1";
        private static readonly bool _forceTitleDefaultRasterState =
            Environment.GetEnvironmentVariable("SHARPEMU_FORCE_TITLE_DEFAULT_RASTER_STATE") == "1";
        private static readonly bool _forceTitleSolidFragment =
            Environment.GetEnvironmentVariable("SHARPEMU_FORCE_TITLE_SOLID_FRAGMENT") == "1";
        private static readonly bool _forceSolidFragment =
            Environment.GetEnvironmentVariable("SHARPEMU_FORCE_SOLID_FRAGMENT") == "1";
        private static readonly uint? _forceAttributeFragmentLocation =
            uint.TryParse(
                Environment.GetEnvironmentVariable("SHARPEMU_FORCE_ATTRIBUTE_FRAGMENT"),
                out var forceAttributeFragmentLocation)
                    ? forceAttributeFragmentLocation
                    : null;
        private static readonly string? _fixedFragmentDumpPath =
            Environment.GetEnvironmentVariable("SHARPEMU_DUMP_FIXED_SOLID_FRAGMENT");
        private static readonly bool _forceTitleDefaultBlend =
            Environment.GetEnvironmentVariable("SHARPEMU_FORCE_TITLE_DEFAULT_BLEND") == "1";
        private static readonly bool _forceTitleDefaultViewportScissor =
            Environment.GetEnvironmentVariable("SHARPEMU_FORCE_TITLE_DEFAULT_VIEWPORT_SCISSOR") == "1";
        private static readonly bool _forceTitleDisableCull =
            Environment.GetEnvironmentVariable("SHARPEMU_FORCE_TITLE_DISABLE_CULL") == "1";
        private static readonly bool _forceTitleDisableDepth =
            Environment.GetEnvironmentVariable("SHARPEMU_FORCE_TITLE_DISABLE_DEPTH") == "1";
        private static readonly bool _traceTitleState =
            Environment.GetEnvironmentVariable("SHARPEMU_TRACE_TITLE_STATE") == "1";
        private static readonly bool _forceTitleVertexColorWhite =
            Environment.GetEnvironmentVariable("SHARPEMU_FORCE_TITLE_VERTEX_COLOR_WHITE") == "1";
        private static readonly bool _chunkedDrawsEnabled =
            Environment.GetEnvironmentVariable("SHARPEMU_ENABLE_CHUNKED_DRAWS") == "1";
        private static readonly string? _traceGuestWritesMode =
            Environment.GetEnvironmentVariable("SHARPEMU_TRACE_GUEST_WRITES");
        private static readonly long _traceGuestWriteOrdinal =
            long.TryParse(
                Environment.GetEnvironmentVariable("SHARPEMU_TRACE_GUEST_WRITE_ORDINAL"),
                out var traceGuestWriteOrdinal)
                    ? traceGuestWriteOrdinal
                    : 0;
        private static readonly long _traceLargeGuestWriteOrdinal =
            ParseTraceLargeGuestWriteOrdinal(_traceGuestWritesMode);
        private static readonly int _tracePixelSpirvBytes =
            int.TryParse(
                Environment.GetEnvironmentVariable("SHARPEMU_TRACE_PIXEL_SPIRV_BYTES"),
                out var tracePixelSpirvBytes)
                    ? tracePixelSpirvBytes
                    : 0;
        private static readonly int _tracePixelSpirvOccurrence =
            int.TryParse(
                Environment.GetEnvironmentVariable("SHARPEMU_TRACE_PIXEL_SPIRV_OCCURRENCE"),
                out var tracePixelSpirvOccurrence)
                    ? Math.Max(tracePixelSpirvOccurrence, 1)
                    : 1;
        private static readonly bool _traceTitleDrawEnabled =
            Environment.GetEnvironmentVariable("SHARPEMU_TRACE_TITLE_DRAW") == "1";
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<
            string,
            (bool Wildcard, ulong[] Addresses)> _cachedAddressLists = new();

        private static long ParseTraceLargeGuestWriteOrdinal(string? mode)
        {
            return mode is not null &&
                mode.StartsWith("large@", StringComparison.Ordinal) &&
                long.TryParse(mode.AsSpan("large@".Length), out var ordinal) &&
                ordinal > 0
                    ? ordinal
                    : 0;
        }

        private static bool ShouldTraceGuestImageContentsForDiagnostics() =>
            _traceGuestImagesEnabled;

        private static bool ShouldTraceGuestImageAddressForDiagnostics(ulong address)
        {
            return AddressListContains(
                "SHARPEMU_TRACE_GUEST_IMAGE_ADDRS",
                address);
        }

        private static bool ShouldTraceGuestImageWriteForDiagnostics(ulong address)
        {
            return AddressListContains(
                "SHARPEMU_TRACE_GUEST_WRITES",
                address);
        }

        private static bool AddressListContains(
            string environmentVariable,
            ulong address)
        {
            var (wildcard, addresses) = _cachedAddressLists.GetOrAdd(
                environmentVariable,
                static name => ParseAddressList(Environment.GetEnvironmentVariable(name)));
            return wildcard || Array.IndexOf(addresses, address) >= 0;
        }

        private static (bool Wildcard, ulong[] Addresses) ParseAddressList(string? addresses)
        {
            if (string.IsNullOrWhiteSpace(addresses))
            {
                return (false, []);
            }

            var parsedAddresses = new List<ulong>();
            foreach (var token in addresses.Split(
                         [',', ';', ' ', '\t'],
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (token == "*")
                {
                    return (true, []);
                }

                var span = token.AsSpan();
                if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    span = span[2..];
                }

                if (ulong.TryParse(
                        span,
                        System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var parsed))
                {
                    parsedAddresses.Add(parsed);
                }
            }

            return (false, parsedAddresses.ToArray());
        }

        private static bool ShouldTracePresentedGuestImageContentsForDiagnostics() =>
            _tracePresentedGuestImagesEnabled;

        private bool ShouldTraceAddressedPresentedGuestImage(GuestImageResource image)
        {
            if (!AddressListContains(
                    "SHARPEMU_TRACE_PRESENTED_GUEST_IMAGE_ADDRS",
                    image.Address))
            {
                return false;
            }

            var count = _presentedGuestImageTraceCounts.TryGetValue(
                image.Address,
                out var previous)
                ? previous + 1
                : 1;
            _presentedGuestImageTraceCounts[image.Address] = count;
            return _tracePresentedGuestImageOccurrence == 0
                ? count == 1
                : count == _tracePresentedGuestImageOccurrence;
        }

        private static bool ShouldTraceVulkanResources() =>
            _traceVulkanResourcesEnabled;

        private void RecordTranslatedGraphicsPass(
            TranslatedDrawResources resources,
            RenderPass renderPass,
            Framebuffer framebuffer,
            Extent2D extent)
        {
            BeginTranslatedRenderPass(renderPass, framebuffer, extent);
            RecordTranslatedDrawInPass(resources, extent);
            _vk.CmdEndRenderPass(_commandBuffer);
        }

        private void BeginTranslatedRenderPass(
            RenderPass renderPass,
            Framebuffer framebuffer,
            Extent2D extent,
            int colorAttachmentCount = 1,
            bool hasDepthAttachment = false,
            float clearDepth = 1f)
        {
            colorAttachmentCount = Math.Max(colorAttachmentCount, 1);
            var clearValueCount = colorAttachmentCount + (hasDepthAttachment ? 1 : 0);
            var clearValues = stackalloc ClearValue[clearValueCount];
            for (var index = 0; index < colorAttachmentCount; index++)
            {
                clearValues[index] = default;
            }
            // Reverse-Z is not assumed; clear depth to 1.0 (far) so a standard
            // LessOrEqual/Less test keeps the nearest fragment.
            if (hasDepthAttachment)
            {
                clearValues[colorAttachmentCount] = new ClearValue
                {
                    DepthStencil = new ClearDepthStencilValue(clearDepth, 0),
                };
            }
            var renderPassInfo = new RenderPassBeginInfo
            {
                SType = StructureType.RenderPassBeginInfo,
                RenderPass = renderPass,
                Framebuffer = framebuffer,
                RenderArea = new Rect2D(new Offset2D(0, 0), extent),
                ClearValueCount = (uint)clearValueCount,
                PClearValues = clearValues,
            };
            _vk.CmdBeginRenderPass(
                _commandBuffer,
                &renderPassInfo,
                SubpassContents.Inline);
        }

        private void RecordTranslatedDrawInPass(
            TranslatedDrawResources resources,
            Extent2D extent)
        {
            _vk.CmdBindPipeline(
                _commandBuffer,
                PipelineBindPoint.Graphics,
                resources.Pipeline);
            if (resources.DescriptorSet.Handle != 0)
            {
                var descriptorSet = resources.DescriptorSet;
                _vk.CmdBindDescriptorSets(
                    _commandBuffer,
                    PipelineBindPoint.Graphics,
                    resources.PipelineLayout,
                    0,
                    1,
                    &descriptorSet,
                    0,
                    null);
            }

            var drawScissor = ClampScissor(resources.Scissor, extent);
            if (drawScissor.Width == 0 || drawScissor.Height == 0)
            {
                return;
            }

            var drawViewport = ClampViewport(resources.Viewport, extent);
            if (ViewportDebugEpsilon != 0f)
            {
                drawViewport.X += ViewportDebugEpsilon;
                drawViewport.Y += ViewportDebugEpsilon;
            }
            _vk.CmdSetViewport(_commandBuffer, 0, 1, &drawViewport);
            if (resources.VertexBuffers.Length != 0)
            {
                var buffers = stackalloc VkBuffer[resources.VertexBuffers.Length];
                var offsets = stackalloc ulong[resources.VertexBuffers.Length];
                for (var index = 0; index < resources.VertexBuffers.Length; index++)
                {
                    buffers[index] = resources.VertexBuffers[index].Buffer;
                    offsets[index] = GetVertexBindingOffset(resources.VertexBuffers[index]);
                }

                _vk.CmdBindVertexBuffers(
                    _commandBuffer,
                    0,
                    (uint)resources.VertexBuffers.Length,
                    buffers,
                    offsets);
            }

            // Replaying a full-screen primitive once per 512x512 scissor tile
            // multiplies an ordinary 4K composite into 32 complete draws. On
            // MoltenVK this starves the render thread and makes the guest fall
            // behind its own flip queue. Vulkan clips a normal fullscreen draw
            // efficiently; keep tiling only as an explicit driver diagnostic.
            var maxPixelsPerDraw = _chunkedDrawsEnabled
                ? 512u * 512u
                : uint.MaxValue;
            var rowsPerDraw = Math.Max(
                1u,
                Math.Min(drawScissor.Height, maxPixelsPerDraw / Math.Max(drawScissor.Width, 1u)));
            var drawCount = 0u;
            for (var y = 0u; y < drawScissor.Height; y += rowsPerDraw)
            {
                var scissor = new Rect2D(
                    new Offset2D(
                        drawScissor.X,
                        checked(drawScissor.Y + (int)y)),
                    new Extent2D(
                        drawScissor.Width,
                        Math.Min(rowsPerDraw, drawScissor.Height - y)));
                _vk.CmdSetScissor(_commandBuffer, 0, 1, &scissor);

                if (resources.IndexBuffer.Handle != 0)
                {
                    _vk.CmdBindIndexBuffer(
                        _commandBuffer,
                        resources.IndexBuffer,
                        0,
                        resources.Index32Bit ? IndexType.Uint32 : IndexType.Uint16);
                    _vk.CmdDrawIndexed(
                        _commandBuffer,
                        resources.VertexCount,
                        resources.InstanceCount,
                        0,
                        0,
                        0);
                }
                else
                {
                    _vk.CmdDraw(
                        _commandBuffer,
                        resources.VertexCount,
                        resources.InstanceCount,
                        0,
                        0);
                }

                drawCount++;
            }

            if (drawCount > 1)
            {
                TraceVulkanShader(
                    $"vk.graphics_chunked target={extent.Width}x{extent.Height} " +
                    $"draws={drawCount} rows={rowsPerDraw} " +
                    $"scissor={drawScissor.X},{drawScissor.Y},{drawScissor.Width}x{drawScissor.Height} " +
                    $"viewport={drawViewport.X:0.###},{drawViewport.Y:0.###}," +
                    $"{drawViewport.Width:0.###}x{drawViewport.Height:0.###} " +
                    $"name={resources.DebugName}");
            }
        }

        private void DestroyTranslatedDrawResources(TranslatedDrawResources resources)
        {
            if (resources.TransientFramebuffer.Handle != 0)
            {
                _vk.DestroyFramebuffer(_device, resources.TransientFramebuffer, null);
            }

            if (resources.TransientRenderPass.Handle != 0)
            {
                _vk.DestroyRenderPass(_device, resources.TransientRenderPass, null);
            }

            foreach (var texture in resources.Textures)
            {
                if (texture is null || texture.Cached)
                {
                    continue;
                }

                if (texture.OwnsStorage && texture.View.Handle != 0)
                {
                    _vk.DestroyImageView(_device, texture.View, null);
                }

                if (texture.OwnsStorage && texture.Image.Handle != 0)
                {
                    _vk.DestroyImage(_device, texture.Image, null);
                }

                if (texture.OwnsStorage && texture.ImageMemory.Handle != 0)
                {
                    _vk.FreeMemory(_device, texture.ImageMemory, null);
                }

                if (texture.StagingBuffer.Handle != 0)
                {
                    _vk.DestroyBuffer(_device, texture.StagingBuffer, null);
                }

                if (texture.StagingMemory.Handle != 0)
                {
                    _vk.FreeMemory(_device, texture.StagingMemory, null);
                }

                if (texture.NeedsUpload &&
                    texture.GuestImage is { Initialized: false } guestImage)
                {
                    guestImage.InitialUploadPending = false;
                }
            }

            foreach (var globalBuffer in resources.GlobalMemoryBuffers)
            {
                if (globalBuffer is null || globalBuffer.Allocation is not null)
                {
                    continue;
                }

                RecycleHostBuffer(globalBuffer.Buffer, globalBuffer.Memory);
            }

            foreach (var vertexBuffer in resources.VertexBuffers)
            {
                if (vertexBuffer is null || !vertexBuffer.OwnsBuffer)
                {
                    continue;
                }

                RecycleHostBuffer(vertexBuffer.Buffer, vertexBuffer.Memory);
            }

            RecycleHostBuffer(resources.IndexBuffer, resources.IndexMemory);

            if (!resources.PipelineCached && resources.Pipeline.Handle != 0)
            {
                _vk.DestroyPipeline(_device, resources.Pipeline, null);
            }

            if (resources.DescriptorPool.Handle != 0)
            {
                if (_recycledDescriptorPools.Count < 256)
                {
                    _recycledDescriptorPools.Push(resources.DescriptorPool);
                }
                else
                {
                    _vk.DestroyDescriptorPool(_device, resources.DescriptorPool, null);
                }
            }

            if (!resources.DescriptorLayoutCached &&
                resources.PipelineLayout.Handle != 0)
            {
                _vk.DestroyPipelineLayout(_device, resources.PipelineLayout, null);
            }

            if (!resources.DescriptorLayoutCached &&
                resources.DescriptorSetLayout.Handle != 0)
            {
                _vk.DestroyDescriptorSetLayout(_device, resources.DescriptorSetLayout, null);
            }
        }

        private void RecordUpload(uint imageIndex)
        {
            var oldLayout = _imageInitialized[imageIndex]
                ? ImageLayout.PresentSrcKhr
                : ImageLayout.Undefined;
            var toTransfer = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = _imageInitialized[imageIndex] ? AccessFlags.MemoryReadBit : 0,
                DstAccessMask = AccessFlags.TransferWriteBit,
                OldLayout = oldLayout,
                NewLayout = ImageLayout.TransferDstOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = _swapchainImages[imageIndex],
                SubresourceRange = ColorSubresourceRange(),
            };
            _vk.CmdPipelineBarrier(
                _commandBuffer,
                _imageInitialized[imageIndex]
                    ? PipelineStageFlags.BottomOfPipeBit
                    : PipelineStageFlags.TopOfPipeBit,
                PipelineStageFlags.TransferBit,
                0,
                0,
                null,
                0,
                null,
                1,
                &toTransfer);

            var copyRegion = new BufferImageCopy
            {
                ImageSubresource = new ImageSubresourceLayers
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    LayerCount = 1,
                },
                ImageExtent = new Extent3D(_extent.Width, _extent.Height, 1),
            };
            _vk.CmdCopyBufferToImage(
                _commandBuffer,
                _stagingBuffer,
                _swapchainImages[imageIndex],
                ImageLayout.TransferDstOptimal,
                1,
                &copyRegion);

            var toPresent = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = AccessFlags.TransferWriteBit,
                DstAccessMask = AccessFlags.MemoryReadBit,
                OldLayout = ImageLayout.TransferDstOptimal,
                NewLayout = ImageLayout.PresentSrcKhr,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = _swapchainImages[imageIndex],
                SubresourceRange = ColorSubresourceRange(),
            };
            _vk.CmdPipelineBarrier(
                _commandBuffer,
                PipelineStageFlags.TransferBit,
                PipelineStageFlags.BottomOfPipeBit,
                0,
                0,
                null,
                0,
                null,
                1,
                &toPresent);
        }

        private void RecordGuestImageBlit(
            uint imageIndex,
            GuestImageResource source)
        {
            var presentedCount = Interlocked.Increment(ref _presentedSwapchainCount);
            var periodicDumpInterval = SwapchainDumpInterval();
            var traceDestination =
                ShouldTracePresentedGuestImageContentsForDiagnostics() &&
                (!_tracedPresentedSwapchain ||
                 periodicDumpInterval > 0 && presentedCount % periodicDumpInterval == 0);
            _tracedPresentedSwapchain |= traceDestination;
            BeginDebugLabel(
                _commandBuffer,
                $"SharpEmu present image 0x{source.Address:X16}");

            var sourceToTransfer = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                // An offscreen target is last written as a color attachment,
                // then put in ShaderReadOnlyOptimal for later sampling.  A
                // layout-only handoff to ShaderRead does not make that write
                // visible to this transfer when no shader sample occurs in
                // between.  NVIDIA's Linux driver exposed the resulting stale
                // (usually black) image while Windows drivers happened to
                // tolerate it.  Include all preceding writes before blitting
                // the image into the swapchain.
                SrcAccessMask = AccessFlags.MemoryWriteBit | AccessFlags.ShaderReadBit,
                DstAccessMask = AccessFlags.TransferReadBit,
                OldLayout = ImageLayout.ShaderReadOnlyOptimal,
                NewLayout = ImageLayout.TransferSrcOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = source.Image,
                SubresourceRange = ColorSubresourceRange(),
            };
            var destinationToTransfer = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = _imageInitialized[imageIndex]
                    ? AccessFlags.MemoryReadBit
                    : 0,
                DstAccessMask = AccessFlags.TransferWriteBit,
                OldLayout = _imageInitialized[imageIndex]
                    ? ImageLayout.PresentSrcKhr
                    : ImageLayout.Undefined,
                NewLayout = ImageLayout.TransferDstOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = _swapchainImages[imageIndex],
                SubresourceRange = ColorSubresourceRange(),
            };
            var barriers = stackalloc ImageMemoryBarrier[2];
            barriers[0] = sourceToTransfer;
            barriers[1] = destinationToTransfer;
            _vk.CmdPipelineBarrier(
                _commandBuffer,
                PipelineStageFlags.AllCommandsBit,
                PipelineStageFlags.TransferBit,
                0,
                0,
                null,
                0,
                null,
                2,
                barriers);

            var sourceOffsets = new ImageBlit.SrcOffsetsBuffer
            {
                Element0 = new Offset3D(0, 0, 0),
                Element1 = new Offset3D(
                    checked((int)source.Width),
                    checked((int)source.Height),
                    1),
            };
            var destinationOffsets = new ImageBlit.DstOffsetsBuffer
            {
                Element0 = new Offset3D(0, 0, 0),
                Element1 = new Offset3D(
                    checked((int)_extent.Width),
                    checked((int)_extent.Height),
                    1),
            };
            var region = new ImageBlit
            {
                SrcSubresource = new ImageSubresourceLayers(
                    ImageAspectFlags.ColorBit,
                    0,
                    0,
                    1),
                SrcOffsets = sourceOffsets,
                DstSubresource = new ImageSubresourceLayers(
                    ImageAspectFlags.ColorBit,
                    0,
                    0,
                    1),
                DstOffsets = destinationOffsets,
            };
            // Nearest keeps integer upscales pixel-crisp, but any fractional
            // scale (e.g. a 3840x2160 guest frame into a 2560x1440 swapchain)
            // must blend neighbours or it silently drops every Nth source
            // row/column, which shreds 1-2px features in the guest frame.
            var isIntegerUpscale =
                source.Width != 0 && source.Height != 0 &&
                _extent.Width >= source.Width && _extent.Height >= source.Height &&
                _extent.Width % source.Width == 0 && _extent.Height % source.Height == 0;
            _vk.CmdBlitImage(
                _commandBuffer,
                source.Image,
                ImageLayout.TransferSrcOptimal,
                _swapchainImages[imageIndex],
                ImageLayout.TransferDstOptimal,
                1,
                &region,
                isIntegerUpscale ? Filter.Nearest : Filter.Linear);

            if (traceDestination)
            {
                var destinationToReadback = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask = AccessFlags.TransferWriteBit,
                    DstAccessMask = AccessFlags.TransferReadBit,
                    OldLayout = ImageLayout.TransferDstOptimal,
                    NewLayout = ImageLayout.TransferSrcOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = _swapchainImages[imageIndex],
                    SubresourceRange = ColorSubresourceRange(),
                };
                _vk.CmdPipelineBarrier(
                    _commandBuffer,
                    PipelineStageFlags.TransferBit,
                    PipelineStageFlags.TransferBit,
                    0,
                    0,
                    null,
                    0,
                    null,
                    1,
                    &destinationToReadback);

                var copyRegion = new BufferImageCopy
                {
                    ImageSubresource = new ImageSubresourceLayers
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        LayerCount = 1,
                    },
                    ImageExtent = new Extent3D(_extent.Width, _extent.Height, 1),
                };
                _vk.CmdCopyImageToBuffer(
                    _commandBuffer,
                    _swapchainImages[imageIndex],
                    ImageLayout.TransferSrcOptimal,
                    _stagingBuffer,
                    1,
                    &copyRegion);
                _swapchainReadbackPending = true;
            }

            var sourceToShaderRead = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = AccessFlags.TransferReadBit,
                DstAccessMask = AccessFlags.ShaderReadBit,
                OldLayout = ImageLayout.TransferSrcOptimal,
                NewLayout = ImageLayout.ShaderReadOnlyOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = source.Image,
                SubresourceRange = ColorSubresourceRange(),
            };
            var destinationToPresent = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = traceDestination
                    ? AccessFlags.TransferReadBit
                    : AccessFlags.TransferWriteBit,
                DstAccessMask = AccessFlags.MemoryReadBit,
                OldLayout = traceDestination
                    ? ImageLayout.TransferSrcOptimal
                    : ImageLayout.TransferDstOptimal,
                NewLayout = ImageLayout.PresentSrcKhr,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = _swapchainImages[imageIndex],
                SubresourceRange = ColorSubresourceRange(),
            };
            barriers[0] = sourceToShaderRead;
            barriers[1] = destinationToPresent;
            _vk.CmdPipelineBarrier(
                _commandBuffer,
                PipelineStageFlags.TransferBit,
                PipelineStageFlags.AllCommandsBit,
                0,
                0,
                null,
                0,
                null,
                2,
                barriers);
            EndDebugLabel(_commandBuffer);
        }

        private void TraceSwapchainReadback()
        {
            _swapchainReadbackPending = false;
            var byteCount = checked((ulong)_extent.Width * _extent.Height * 4);
            void* mapped;
            Check(
                _vk.MapMemory(_device, _stagingMemory, 0, byteCount, 0, &mapped),
                "vkMapMemory(swapchain readback)");
            try
            {
                var bytes = new ReadOnlySpan<byte>(mapped, checked((int)byteCount));
                var nonzeroBytes = 0L;
                var nonblackPixels = 0L;
                ulong hash = 14695981039346656037UL;
                for (var offset = 0; offset < bytes.Length; offset += 4)
                {
                    var b0 = bytes[offset];
                    var b1 = bytes[offset + 1];
                    var b2 = bytes[offset + 2];
                    var b3 = bytes[offset + 3];
                    nonzeroBytes += b0 == 0 ? 0 : 1;
                    nonzeroBytes += b1 == 0 ? 0 : 1;
                    nonzeroBytes += b2 == 0 ? 0 : 1;
                    nonzeroBytes += b3 == 0 ? 0 : 1;
                    nonblackPixels += b0 != 0 || b1 != 0 || b2 != 0 ? 1 : 0;
                    hash = (hash ^ b0) * 1099511628211UL;
                    hash = (hash ^ b1) * 1099511628211UL;
                    hash = (hash ^ b2) * 1099511628211UL;
                    hash = (hash ^ b3) * 1099511628211UL;
                }

                Console.Error.WriteLine(
                    $"[LOADER][TRACE] vk.swapchain_image size={_extent.Width}x{_extent.Height} " +
                    $"format={_swapchainFormat} nonzero_bytes={nonzeroBytes}/{byteCount} " +
                    $"nonblack_pixels={nonblackPixels}/{(ulong)_extent.Width * _extent.Height} " +
                    $"hash=0x{hash:X16}");

                var dumpDir = Environment.GetEnvironmentVariable("SHARPEMU_GUEST_IMAGE_DUMP_DIR");
                if (!string.IsNullOrWhiteSpace(dumpDir))
                {
                    Directory.CreateDirectory(dumpDir);
                    var seq = Interlocked.Increment(ref _guestImageDumpSequence);
                    var path = Path.Combine(
                        dumpDir,
                        $"present-{seq:D4}-{_extent.Width}x{_extent.Height}-{_swapchainFormat}.bgra");
                    File.WriteAllBytes(path, bytes.ToArray());
                    Console.Error.WriteLine($"[LOADER][TRACE] vk.swapchain_dump path={path}");
					// Continuous readback is intentionally opt-in: each 1080p frame
					// is several megabytes and synchronously waits for the GPU.
					if (string.Equals(
							Environment.GetEnvironmentVariable("SHARPEMU_GUEST_IMAGE_DUMP_CONTINUOUS"),
							"1",
							StringComparison.Ordinal))
					{
						_tracedPresentedSwapchain = false;
					}
                }
            }
            finally
            {
                _vk.UnmapMemory(_device, _stagingMemory);
            }
        }

        private static readonly long _swapchainDumpInterval = ParseSwapchainDumpInterval();

        private static long SwapchainDumpInterval() => _swapchainDumpInterval;

        private static long ParseSwapchainDumpInterval()
        {
            var raw = Environment.GetEnvironmentVariable("SHARPEMU_SWAPCHAIN_DUMP_EVERY");
            return long.TryParse(raw, out var interval) && interval > 0 ? interval : 0;
        }

        private Extent2D ChooseExtent(SurfaceCapabilitiesKHR capabilities)
        {
            if (capabilities.CurrentExtent.Width != uint.MaxValue)
            {
                var fallbackWidth = _extent.Width != 0
                    ? _extent.Width
                    : DefaultWindowWidth;
                var fallbackHeight = _extent.Height != 0
                    ? _extent.Height
                    : DefaultWindowHeight;
                return new Extent2D(
                    ClampSurfaceExtent(
                        capabilities.CurrentExtent.Width,
                        fallbackWidth,
                        capabilities.MinImageExtent.Width,
                        capabilities.MaxImageExtent.Width),
                    ClampSurfaceExtent(
                        capabilities.CurrentExtent.Height,
                        fallbackHeight,
                        capabilities.MinImageExtent.Height,
                        capabilities.MaxImageExtent.Height));
            }

            var size = _window.FramebufferSize;
            return new Extent2D(
                ClampSurfaceExtent(
                    (uint)Math.Max(size.X, 1),
                    DefaultWindowWidth,
                    capabilities.MinImageExtent.Width,
                    capabilities.MaxImageExtent.Width),
                ClampSurfaceExtent(
                    (uint)Math.Max(size.Y, 1),
                    DefaultWindowHeight,
                    capabilities.MinImageExtent.Height,
                    capabilities.MaxImageExtent.Height));
        }

        private static uint ClampSurfaceExtent(
            uint value,
            uint fallback,
            uint minimum,
            uint maximum)
        {
            value = value <= 1 && fallback > 1 ? fallback : value;
            minimum = Math.Max(minimum, 1u);
            maximum = Math.Max(maximum, minimum);
            return Math.Clamp(value, minimum, maximum);
        }

        private static SurfaceFormatKHR ChooseSurfaceFormat(IReadOnlyList<SurfaceFormatKHR> formats)
        {
            foreach (var format in formats)
            {
                if (format.Format is Format.B8G8R8A8Srgb or Format.B8G8R8A8Unorm &&
                    format.ColorSpace == ColorSpaceKHR.SpaceSrgbNonlinearKhr)
                {
                    return format;
                }
            }

            return formats.Count > 0
                ? formats[0]
                : throw new InvalidOperationException("The Vulkan surface exposes no pixel formats.");
        }

        private static CompositeAlphaFlagsKHR ChooseCompositeAlpha(CompositeAlphaFlagsKHR supported)
        {
            foreach (var candidate in new[]
                     {
                         CompositeAlphaFlagsKHR.OpaqueBitKhr,
                         CompositeAlphaFlagsKHR.PreMultipliedBitKhr,
                         CompositeAlphaFlagsKHR.PostMultipliedBitKhr,
                         CompositeAlphaFlagsKHR.InheritBitKhr,
                     })
            {
                if ((supported & candidate) != 0)
                {
                    return candidate;
                }
            }

            throw new InvalidOperationException("The Vulkan surface exposes no composite alpha mode.");
        }

        private static ImageSubresourceRange ColorSubresourceRange(
            uint baseMipLevel = 0,
            uint levelCount = 1) =>
            new()
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = baseMipLevel,
                LevelCount = levelCount,
                LayerCount = 1,
            };

        private static byte[] ScaleBgra(byte[] source, uint sourceWidth, uint sourceHeight, uint width, uint height)
        {
            var destination = new byte[checked((int)(width * height * 4))];
            for (uint y = 0; y < height; y++)
            {
                var sourceY = (uint)(((ulong)y * sourceHeight) / height);
                for (uint x = 0; x < width; x++)
                {
                    var sourceX = (uint)(((ulong)x * sourceWidth) / width);
                    var sourceOffset = checked((int)(((ulong)sourceY * sourceWidth + sourceX) * 4));
                    var destinationOffset = checked((int)(((ulong)y * width + x) * 4));
                    source.AsSpan(sourceOffset, 4).CopyTo(destination.AsSpan(destinationOffset, 4));
                }
            }

            return destination;
        }

        private void DisposeVulkan()
        {
            if (!_vulkanReady)
            {
                return;
            }

            if (_debugUtils is not null && _debugMessenger.Handle != 0)
            {
                _debugUtils.DestroyDebugUtilsMessenger(_instance, _debugMessenger, null);
            }
            _vulkanReady = false;
            _vk.DeviceWaitIdle(_device);
            SavePipelineCache(force: true);
            DrainFrameSlots();
            CollectCompletedGuestSubmissions(waitForOldest: false);
            ClearCachedTextureIdentities();
            foreach (var pipeline in _computePipelines.Values)
            {
                _vk.DestroyPipeline(_device, pipeline, null);
            }
            _computePipelines.Clear();
            foreach (var pipeline in _graphicsPipelines.Values)
            {
                _vk.DestroyPipeline(_device, pipeline, null);
            }
            _graphicsPipelines.Clear();
            foreach (var layout in _descriptorLayouts.Values)
            {
                _vk.DestroyPipelineLayout(_device, layout.PipelineLayout, null);
                if (layout.DescriptorSetLayout.Handle != 0)
                {
                    _vk.DestroyDescriptorSetLayout(
                        _device,
                        layout.DescriptorSetLayout,
                        null);
                }
            }
            _descriptorLayouts.Clear();
            foreach (var sampler in _samplers.Values)
            {
                _vk.DestroySampler(_device, sampler, null);
            }
            _samplers.Clear();
            _shaderDigests.Clear();
            WriteBackAllDirtyGuestBuffers();
            foreach (var allocation in _guestBufferAllocations)
            {
                DestroyGuestBufferAllocation(allocation);
            }
            _guestBufferAllocations.Clear();
            _hostBufferPool.Dispose();
            foreach (var guestImage in _guestImages.Values)
            {
                DestroyGuestImage(guestImage);
            }
            _guestImages.Clear();
            foreach (var guestImageVariant in _guestImageVariants.Values)
            {
                DestroyGuestImage(guestImageVariant);
            }
            _guestImageVariants.Clear();
            foreach (var guestImageVersion in _guestImageVersions.Values)
            {
                DestroyGuestImage(guestImageVersion);
            }
            _guestImageVersions.Clear();
            _capturedGuestFlipVersions.Clear();
            while (_deferredGuestImageVersionDestroys.TryDequeue(out var deferredVersion))
            {
                DestroyGuestImage(deferredVersion.Image);
            }
            foreach (var guestDepth in _guestDepthImages.Values)
            {
                DestroyGuestDepth(guestDepth);
            }
            _guestDepthImages.Clear();
            lock (_gate)
            {
                _availableGuestImages.Clear();
                _lastOrderedGuestFlipVersions.Clear();
            }
            DestroySwapchainResources();
            if (_device.Handle != 0)
            {
                if (_pipelineCache.Handle != 0)
                {
                    _vk.DestroyPipelineCache(_device, _pipelineCache, null);
                    _pipelineCache = default;
                }
                _vk.DestroyDevice(_device, null);
                _device = default;
            }
            if (_surface.Handle != 0)
            {
                _surfaceApi.DestroySurface(_instance, _surface, null);
                _surface = default;
            }
            if (_instance.Handle != 0)
            {
                _vk.DestroyInstance(_instance, null);
                _instance = default;
            }
        }

        private void RecreateSwapchainResources(string operation, Result result)
        {
            if (_device.Handle == 0)
            {
                return;
            }

            Check(
                _surfaceApi.GetPhysicalDeviceSurfaceCapabilities(
                    _physicalDevice,
                    _surface,
                    out var capabilities),
                "vkGetPhysicalDeviceSurfaceCapabilitiesKHR");
            var framebufferSize = _window.FramebufferSize;
            var hasFixedExtent = capabilities.CurrentExtent.Width != uint.MaxValue;
            var surfaceWidth = hasFixedExtent
                ? capabilities.CurrentExtent.Width
                : (uint)Math.Max(framebufferSize.X, 0);
            var surfaceHeight = hasFixedExtent
                ? capabilities.CurrentExtent.Height
                : (uint)Math.Max(framebufferSize.Y, 0);
            if (surfaceWidth <= 1 || surfaceHeight <= 1)
            {
                if (!_swapchainRecreateDeferred)
                {
                    _swapchainRecreateDeferred = true;
                    Console.Error.WriteLine(
                        $"[LOADER][INFO] Vulkan VideoOut deferred swapchain recreation: " +
                        $"surface={surfaceWidth}x{surfaceHeight}");
                }

                return;
            }

            _swapchainRecreateDeferred = false;
            Console.Error.WriteLine(
                $"[LOADER][INFO] Vulkan VideoOut recreating swapchain after {operation}: {result}");
            _vk.DeviceWaitIdle(_device);
            DrainFrameSlots();
            CollectCompletedGuestSubmissions(waitForOldest: false);
            DestroySwapchainResources();
            CreateSwapchain();
            CreateCommandResources();
            CreateGuestDrawResources();
            Console.Error.WriteLine(
                $"[LOADER][INFO] Vulkan VideoOut recreated swapchain: " +
                $"{_extent.Width}x{_extent.Height}, format={_swapchainFormat}");
        }

        private void DestroySwapchainResources()
        {
            if (_stagingBuffer.Handle != 0)
            {
                _vk.DestroyBuffer(_device, _stagingBuffer, null);
                _stagingBuffer = default;
            }
            if (_stagingMemory.Handle != 0)
            {
                _vk.FreeMemory(_device, _stagingMemory, null);
                _stagingMemory = default;
                _stagingSize = 0;
            }
            foreach (var semaphore in _frameImageAvailable)
            {
                if (semaphore.Handle != 0)
                {
                    _vk.DestroySemaphore(_device, semaphore, null);
                }
            }
            _frameImageAvailable = [];
            foreach (var semaphore in _renderFinishedPerImage)
            {
                if (semaphore.Handle != 0)
                {
                    _vk.DestroySemaphore(_device, semaphore, null);
                }
            }
            _renderFinishedPerImage = [];
            if (_overlayImage.Handle != 0)
            {
                _vk.DestroyImage(_device, _overlayImage, null);
                _overlayImage = default;
            }
            if (_overlayImageMemory.Handle != 0)
            {
                _vk.FreeMemory(_device, _overlayImageMemory, null);
                _overlayImageMemory = default;
            }
            for (var slot = 0; slot < _overlayStagingBuffers.Length; slot++)
            {
                if (_overlayStagingBuffers[slot].Handle != 0)
                {
                    _vk.DestroyBuffer(_device, _overlayStagingBuffers[slot], null);
                }
                if (_overlayStagingMemory[slot].Handle != 0)
                {
                    _vk.FreeMemory(_device, _overlayStagingMemory[slot], null);
                }
            }
            _overlayStagingBuffers = [];
            _overlayStagingMemory = [];
            _overlayStagingMapped = [];
            _overlayImageInitialized = false;
            foreach (var fence in _frameFences)
            {
                if (fence.Handle != 0)
                {
                    _vk.DestroyFence(_device, fence, null);
                }
            }
            _frameFences = [];
            _frameFencePending = [];
            _frameTimelines = [];
            _frameTranslatedResources = [];
            _frameGuestImageVersions = [];
            while (_recycledGuestFences.TryPop(out var recycledFence))
            {
                _vk.DestroyFence(_device, recycledFence, null);
            }
            if (_barycentricPipeline.Handle != 0)
            {
                _vk.DestroyPipeline(_device, _barycentricPipeline, null);
                _barycentricPipeline = default;
            }
            if (_pipelineLayout.Handle != 0)
            {
                _vk.DestroyPipelineLayout(_device, _pipelineLayout, null);
                _pipelineLayout = default;
            }
            foreach (var framebuffer in _framebuffers)
            {
                if (framebuffer.Handle != 0)
                {
                    _vk.DestroyFramebuffer(_device, framebuffer, null);
                }
            }
            if (_renderPass.Handle != 0)
            {
                _vk.DestroyRenderPass(_device, _renderPass, null);
                _renderPass = default;
            }
            foreach (var imageView in _swapchainImageViews)
            {
                if (imageView.Handle != 0)
                {
                    _vk.DestroyImageView(_device, imageView, null);
                }
            }
            if (_commandPool.Handle != 0)
            {
                // Destroying the pool frees every command buffer allocated
                // from it, including recycled and per-frame ones.
                _recycledGuestCommandBuffers.Clear();
                _frameCommandBuffers = [];
                _vk.DestroyCommandPool(_device, _commandPool, null);
                _commandPool = default;
                _commandBuffer = default;
                _presentationCommandBuffer = default;
            }
            if (_swapchain.Handle != 0)
            {
                _swapchainApi.DestroySwapchain(_device, _swapchain, null);
                _swapchain = default;
            }

            _swapchainImages = [];
            _swapchainImageViews = [];
            _framebuffers = [];
            _imageInitialized = [];
        }

        private static void CheckSwapchainResult(Result result, string operation)
        {
            if (result is Result.Success or Result.SuboptimalKhr)
            {
                return;
            }

            if (result == Result.ErrorDeviceLost)
            {
                throw new VulkanDeviceLostException(operation);
            }

            throw new InvalidOperationException($"{operation} failed with {result}.");
        }

        private static void Check(Result result, string operation)
        {
            if (result == Result.ErrorDeviceLost)
            {
                throw new VulkanDeviceLostException(operation);
            }

            if (result != Result.Success)
            {
                throw new InvalidOperationException($"{operation} failed with {result}.");
            }
        }

        // Typed so the frame-boundary catch can recognize device loss without
        // depending on the exact wording of the exception message.
        private sealed class VulkanDeviceLostException(string operation)
            : InvalidOperationException($"{operation} failed with {Result.ErrorDeviceLost}.");

        private bool TryMarkDeviceLost(Exception exception)
        {
            // Prefer the typed signal; fall back to the message for losses that
            // surface through other layers (e.g. Silk.NET bindings).
            if (exception is not VulkanDeviceLostException &&
                !exception.Message.Contains(nameof(Result.ErrorDeviceLost), StringComparison.Ordinal))
            {
                return false;
            }

            _deviceLost = true;
            if (!_deviceLostLogged)
            {
                _deviceLostLogged = true;
                Console.Error.WriteLine(
                    "[LOADER][ERROR] Vulkan device lost; dropping subsequent guest GPU work. " +
                    exception.Message);
            }

            return true;
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
