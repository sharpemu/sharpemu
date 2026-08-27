// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using SharpEmu.Libs.Gpu;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Vulkan;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.VideoOut;

internal static unsafe partial class VulkanVideoPresenter
{
    // This partial queues guest GPU work.
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
    // Storage-image initialization is copied only by the first queued writer.
    // Later dispatches targeting the same image must not each retain another
    // multi-megabyte guest-memory snapshot while waiting for that first writer
    // to reach the presenter.  Reference counts let failed/completed work
    // retire its reservation without leaving a permanent false cache hit.
    private readonly record struct PendingGuestImageUpload(int Count, long OwnerSequence);
    private static readonly Dictionary<(ulong Address, uint Format), PendingGuestImageUpload>
        _pendingGuestImageUploads = new();
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
    private static readonly bool _traceOrderedActionLatency =
        string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_TRACE_ORDERED_ACTION_LATENCY"),
            "1",
            StringComparison.Ordinal);
    private static readonly HashSet<(ulong Address, uint Width, uint Height)>
        _tracedGuestImageSubmissions = [];
    private const string DebugUtilsExtensionName = "VK_EXT_debug_utils";
    private const string SwapchainColorspaceExtensionName = "VK_EXT_swapchain_colorspace";
    private const uint NvidiaVendorId = 0x10DE;
    private const uint AmdVendorId = 0x1002;
    // Other GPU PCI vendor IDs, for reference when adding future rules:
    // Intel 0x8086, Apple 0x106B, Qualcomm 0x5143 (Windows-on-ARM), Microsoft software 0x1414.
    private const int LastResortPenalty = 1000;
    private const string PortabilityEnumerationExtensionName = "VK_KHR_portability_enumeration";
    private const string PortabilitySubsetExtensionName = "VK_KHR_portability_subset";

    private static long _enqueuedGuestWorkSequence;
    // Largest contiguous completed sequence, retained for compact diagnostics.
    // Per-queue scheduling can complete a later global id first, so correctness
    // checks use IsGuestWorkCompletedLocked rather than numeric <= comparisons.
    private static long _completedGuestWorkSequence;
    private static readonly HashSet<long> _completedGuestWorkOutOfOrder = [];
    private static readonly Dictionary<string, long> _lastEnqueuedGuestWorkByQueue =
        new(StringComparer.Ordinal);
    private static readonly Dictionary<string, GuestGpuLabelDependency>
        _requiredGpuLabelDependenciesByGuestQueue = new(StringComparer.Ordinal);
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

    internal static bool ShouldRefreshGuestGlobalBuffer(
        bool writable,
        bool capturedMatchesShadow,
        bool liveMatchesShadow) =>
        writable ? !liveMatchesShadow : !capturedMatchesShadow;

    internal static bool ShouldVersionReadOnlyGuestGlobalBuffer(
        bool writable,
        bool needsRefresh,
        bool allocationInFlight,
        bool allocationInOpenBatch) =>
        !writable &&
        needsRefresh &&
        (allocationInFlight || allocationInOpenBatch);

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
        ulong shaderAddress = 0,
        int baseVertex = 0)
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
            shaderAddress,
            baseVertex);
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
        ulong shaderAddress = 0,
        int baseVertex = 0)
    {
        if (pixelSpirv.Length == 0 ||
            targets.Count == 0 ||
            targets.Count > 8 ||
            AnyRenderTargetInvalid(targets))
        {
            var dropCount = Interlocked.Increment(ref _offscreenDropTraceCount);
            if (dropCount <= 16 || dropCount % 500 == 0)
            {
                var detail = targets.Count == 0
                    ? "<no targets>"
                    : string.Join(
                        " ",
                        targets.Select(t => $"0x{t.Address:X}:{t.Width}x{t.Height}"));
                Console.Error.WriteLine(
                    $"[LOADER][WARN] vk.offscreen_drop#{dropCount} spirv={pixelSpirv.Length} " +
                    $"mrt={targets.Count} vs=0x{shaderAddress:X16} {detail}");
            }

            return;
        }

        var firstTarget = targets[0];
        if (RenderTargetsMismatchedOrAliased(targets, firstTarget))
        {
            var skipCount = Interlocked.Increment(ref _mrtSkipTraceCount);
            if (skipCount <= 16 || skipCount % 200 == 0)
            {
                var aliased = false;
                for (var i = 0; i < targets.Count && !aliased; i++)
                {
                    for (var j = i + 1; j < targets.Count; j++)
                    {
                        if (targets[i].Address == targets[j].Address)
                        {
                            aliased = true;
                            break;
                        }
                    }
                }

                var detail = string.Join(
                    " ",
                    targets.Select(t =>
                        $"0x{t.Address:X}:{t.Width}x{t.Height}:f{t.Format}/{t.NumberType}"));
                Console.Error.WriteLine(
                    $"[LOADER][WARN] vk.mrt_skip#{skipCount} mrt={targets.Count} " +
                    $"aliased={aliased} vs=0x{shaderAddress:X16} {detail}");
            }

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
                        effectiveRenderState,
                        baseVertex),
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
        ulong shaderAddress = 0,
        int baseVertex = 0)
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
                        renderState ?? GuestRenderState.Default,
                        baseVertex),
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
        uint FillValue,
        uint RowOffset = 0);

    /// <summary>
    /// Reports the extent of a live guest image so DMA writes to its backing
    /// memory can be mirrored into the Vulkan image (PS5 render targets alias
    /// guest memory, so CP DMA fills/copies are visible to later GPU reads).
    /// </summary>

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

    private static readonly ConcurrentDictionary<ulong, byte> _pendingGuestColorClears = new();

    /// <summary>
    /// Clear a guest colour target to zero at its next render pass.
    ///
    /// Deliberately not <see cref="SubmitOffscreenColorClear"/>: that enqueues
    /// a CmdClearColorImage which lands outside the render pass that follows
    /// it, so a target cleared this way was still observed reading back its
    /// previous contents. Dropping <c>Initialized</c> makes the render pass
    /// itself clear via <see cref="AttachmentLoadOp.Clear"/>.
    /// </summary>
    internal static void RequestGuestColorClear(ulong address)
    {
        if (address != 0)
        {
            _pendingGuestColorClears[address] = 0;
        }
    }

    /// <summary>
    /// Apply a solid color clear to offscreen guest render targets without a
    /// graphics pipeline. Used for empty-SRT procedural clear draws that
    /// otherwise lose the device on QueueSubmit with Address-0 descriptors.
    /// </summary>
    internal static void SubmitOffscreenColorClear(
        IReadOnlyList<GuestRenderTarget> targets,
        float red,
        float green,
        float blue,
        float alpha,
        ulong shaderAddress = 0)
    {
        if (targets.Count == 0 ||
            targets.Count > 8 ||
            AnyRenderTargetInvalid(targets))
        {
            return;
        }

        var firstTarget = targets[0];
        if (RenderTargetsMismatchedOrAliased(targets, firstTarget))
        {
            Console.Error.WriteLine(
                "[LOADER][WARN] Vulkan skipped MRT color clear with mismatched dimensions or aliased targets.");
            return;
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
                new VulkanOffscreenColorClear(
                    targets.ToArray(),
                    red,
                    green,
                    blue,
                    alpha,
                    shaderAddress));
            foreach (var target in targets)
            {
                _guestImageWorkSequences[target.Address] = workSequence;
            }
        }
    }

    internal static void SubmitGuestImageWrite(ulong address, byte[] pixels, uint rowOffset = 0)
    {
        lock (_gate)
        {
            if (_closed || !_guestImageExtents.ContainsKey(address))
            {
                return;
            }

            _guestImageWorkSequences[address] = EnqueueGuestWorkLocked(
                new VulkanGuestImageWrite(address, pixels, 0, rowOffset));
        }
    }

    private static long _mrtSkipTraceCount;
    private static long _offscreenDropTraceCount;
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

            if (_thread is null)
            {
                StartPresenterLocked();
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
    /// Enqueues a GPU cache dependency at its position in the guest queue.
    /// This operation does not make guest data visible to the CPU.
    /// </summary>
    public static long SubmitGuestCacheOperation(
        GuestGpuCacheOperation operation,
        Action applyHostState,
        string debugName) =>
        SubmitGuestCacheOperations([operation], applyHostState, debugName);

    /// <summary>
    /// Enqueues exact guest cache operations in one queue position.
    /// </summary>
    public static long SubmitGuestCacheOperations(
        IReadOnlyList<GuestGpuCacheOperation> operations,
        Action applyHostState,
        string debugName)
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(applyHostState);
        if (operations.Count == 0)
        {
            return 0;
        }

        var snapshot = operations.ToArray();
        lock (_gate)
        {
            return _closed || _thread is null
                ? 0
                : EnqueueGuestWorkLocked(
                    new VulkanGuestCacheOperation(snapshot, applyHostState, debugName));
        }
    }

    /// <summary>
    /// Enqueues a GPU-only label marker. The callback receives the last Vulkan
    /// timeline token for the current logical guest queue. A return value of
    /// zero tells AGC to use the CPU-visible compatibility path.
    /// </summary>
    public static long SubmitGpuLabelSignal(
        Action<GuestGpuLabelDependency> publishGpu,
        Action? publishHost,
        string debugName)
    {
        ArgumentNullException.ThrowIfNull(publishGpu);
        lock (_gate)
        {
            return !_gpuLabelVirtualWritesEnabled ||
                !Volatile.Read(ref _gpuLabelTimelineAvailable) ||
                _closed ||
                _thread is null
                ? 0
                : EnqueueGuestWorkLocked(
                    new VulkanGpuLabelSignal(publishGpu, publishHost, debugName));
        }
    }

    /// <summary>
    /// Adds the producer timeline token to the next Vulkan submission from
    /// the current logical guest queue.
    /// </summary>
    public static void RequireGpuLabelDependency(GuestGpuLabelDependency dependency)
    {
        if (dependency.IsEmpty || !_gpuLabelTimelineRequested)
        {
            return;
        }

        lock (_gate)
        {
            var queue = _submittingGuestQueue ?? VulkanGuestQueueIdentity.Default;
            _requiredGpuLabelDependenciesByGuestQueue[queue.Name] =
                _requiredGpuLabelDependenciesByGuestQueue.TryGetValue(
                    queue.Name,
                    out var current)
                    ? current.Merge(dependency)
                    : dependency;
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

    internal static ulong GetGuestImageByteCount(uint format, uint width, uint height)
    {
        var blockBytes = format switch
        {
            169 or 170 or 175 or 176 => 8UL,
            171 or 172 or 173 or 174 or
            177 or 178 or 179 or 180 or 181 or 182 => 16UL,
            _ => 0UL,
        };
        if (blockBytes != 0)
        {
            return checked(((ulong)width + 3) / 4 * (((ulong)height + 3) / 4) * blockBytes);
        }

        var bytesPerPixel = format switch
        {
            1 => 1UL,
            2 or 3 or 16 or 17 or 19 => 2UL,
            11 or 12 => 8UL,
            13 => 12UL,
            14 => 16UL,
            _ => 4UL,
        };
        return checked((ulong)width * height * bytesPerPixel);
    }

    internal static ulong GetGuestImageByteCount(
        uint format,
        uint width,
        uint height,
        uint depth) =>
        checked(GetGuestImageByteCount(format, width, height) * Math.Max(depth, 1u));

    // Guest memory handle for render-thread self-healing: when a draw whose
    // texel copy was skipped misses the texture cache (eviction, cache
    // clear, or any other race), the presenter re-reads the texels itself
    // instead of showing a fallback pattern.
    private static volatile SharpEmu.HLE.ICpuMemory? _guestMemory;

    internal static void AttachGuestMemory(SharpEmu.HLE.ICpuMemory memory) =>
        _guestMemory = memory;

    // Display buffers registered through sceVideoOutRegisterBuffers remain
    // valid flip targets even before AGC has rendered into them.

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

    internal static bool TryDecodeRenderTargetFormat(
        uint dataFormat,
        uint numberType,
        out VulkanRenderTargetFormat result) =>
        TryDecodeRenderTargetFormat(
            dataFormat,
            numberType,
            componentSwap: 0,
            out result);

    internal static bool TryDecodeRenderTargetFormat(
        uint dataFormat,
        uint numberType,
        uint componentSwap,
        out VulkanRenderTargetFormat result)
    {
        var format = (dataFormat, numberType, componentSwap) switch
        {
            // Early G-buffer / scene targets (R16 + RG32). GTA V Enhanced hits
            // these as color targets; texture decode already knew them.
            (2, 0, _) => Format.R16Unorm,
            (2, 1, _) => Format.R16SNorm,
            (2, 2, _) => Format.R16Uscaled,
            (2, 3, _) => Format.R16Sscaled,
            (2, 4, _) => Format.R16Uint,
            (2, 5, _) => Format.R16Sint,
            (2, 7, _) => Format.R16Sfloat,
            (4, 4, _) => Format.R32Uint,
            (4, 5, _) => Format.R32Sint,
            (4, 7, _) => Format.R32Sfloat,
            (5, 4, _) => Format.R16G16Uint,
            (5, 5, _) => Format.R16G16Sint,
            (5, 7, _) => Format.R16G16Sfloat,
            (6, 7, _) or (7, 7, _) => Format.B10G11R11UfloatPack32,
            (9, _, 1) => Format.A2R10G10B10UnormPack32,
            (9, _, _) => Format.A2B10G10R10UnormPack32,
            (10, 4, _) => Format.R8G8B8A8Uint,
            (10, 5, _) => Format.R8G8B8A8Sint,
            (10, 6 or 9, 1) => Format.B8G8R8A8Srgb,
            (10, 6 or 9, _) => Format.R8G8B8A8Srgb,
            (10, 0, 1) => Format.B8G8R8A8Unorm,
            (10, _, _) => Format.R8G8B8A8Unorm,
            (11, 4, _) => Format.R32G32Uint,
            (11, 5, _) => Format.R32G32Sint,
            (11, 7, _) => Format.R32G32Sfloat,
            (12, 4, _) => Format.R16G16B16A16Uint,
            (12, 5, _) => Format.R16G16B16A16Sint,
            (12, 7, _) => Format.R16G16B16A16Sfloat,
            (13, 7, _) or (14, 7, _) => Format.R32G32B32A32Sfloat,
            (20, 0, _) => Format.R32Uint,
            (29, 0, _) or (4, 0, _) => Format.R32Sfloat,
            (1, 0, _) or (36, 0, _) => Format.R8Unorm,
            (49, 0, _) => Format.R8Uint,
            (3, 0, _) => Format.R8G8Unorm,
            (5, 0, _) => Format.R16G16Unorm,
            (7, 0, _) => Format.B10G11R11UfloatPack32,
            (12, 0, _) => Format.R16G16B16A16Unorm,
            (13, 0, _) or (14, 0, _) => Format.R32G32B32A32Sfloat,
            (22, 0, _) or (71, 0, _) => Format.R16G16B16A16Sfloat,
            (56, 0, _) or (62, 0, _) or (64, 0, _) => Format.R8G8B8A8Unorm,
            (75, 0, _) => Format.R32G32Sfloat,
            _ => Format.Undefined,
        };

        if (format == Format.Undefined ||
            !TryGetRenderTargetComponentCount(dataFormat, out var componentCount) ||
            !Gen5ColorComponentMapping.TryResolveRenderTarget(
                componentSwap,
                componentCount,
                out var orderMapping))
        {
            result = default;
            return false;
        }

        var outputKind = format switch
        {
            Format.R8Uint or Format.R16Uint or Format.R32Uint or Format.R16G16Uint or
                Format.R32G32Uint or Format.R8G8B8A8Uint or Format.R16G16B16A16Uint =>
                Gen5PixelOutputKind.Uint,
            Format.R16Sint or Format.R32Sint or Format.R16G16Sint or Format.R32G32Sint or
                Format.R8G8B8A8Sint or Format.R16G16B16A16Sint => Gen5PixelOutputKind.Sint,
            _ => Gen5PixelOutputKind.Float,
        };

        var hostToStorage = dataFormat switch
        {
            9 when componentSwap == 1 => new Gen5ColorComponentMapping(0xC6),
            10 when componentSwap == 1 && numberType is 0 or 6 or 9 =>
                new Gen5ColorComponentMapping(0xC6),
            _ => Gen5ColorComponentMapping.Identity,
        };
        result = new VulkanRenderTargetFormat(
            format,
            outputKind,
            hostToStorage.Then(orderMapping));
        return true;
    }

    private static bool TryGetRenderTargetComponentCount(
        uint dataFormat,
        out uint componentCount)
    {
        componentCount = dataFormat switch
        {
            1 or 2 or 4 or 20 or 29 or 36 or 49 => 1,
            3 or 5 or 11 or 75 => 2,
            6 or 7 => 3,
            9 or 10 or 12 or 13 or 14 or 22 or 56 or 62 or 64 or 71 => 4,
            _ => 0,
        };
        return componentCount != 0;
    }

    private static long EnqueueGuestWorkLocked(object work)
    {
        var payloadBytes = GetGuestWorkPayloadBytes(work);
        var isPayloadWork = IsPayloadBearingGuestWork(work);
        var backpressureLogged = false;
        // Work executed by the render-thread consumer can enqueue an ordered
        // same-queue completion marker. Blocking that consumer on the normal
        // producer backpressure limit deadlocks a full queue: no other thread
        // can drain an item to make room for the marker. The consumer has
        // already removed the current item, and each immediate follow-up is
        // bounded by that item, so admitting it cannot cause unbounded growth.
        //
        // Item cap applies to payload-bearing compute/draw/image writes. Zero-
        // payload ordered sync / flip markers use a higher sync ceiling so
        // ACQUIRE/label traffic cannot hard-block behind the 512 draw cap.
        // Byte budget remains the RAM safety valve for fat dispatches
        // (SHARPEMU_PENDING_GUEST_WORK_MB).
        while (!_enqueueAsImmediateQueueFollowup &&
               !_closed &&
               _thread is not null &&
               ((isPayloadWork &&
                 _pendingPayloadGuestWorkCount >= _maxPendingGuestWorkItems) ||
                (!isPayloadWork &&
                 _pendingSyncGuestWorkCount >= _maxPendingGuestSyncItems) ||
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
                        $"payload={_pendingPayloadGuestWorkCount}/{_maxPendingGuestWorkItems} " +
                        $"sync={_pendingSyncGuestWorkCount}/{_maxPendingGuestSyncItems} " +
                        $"logical_queues={_pendingGuestWorkByQueue.Count} " +
                        $"retained_mb={_pendingGuestWorkBytes / (1024 * 1024)} " +
                        $"incoming_mb={payloadBytes / (1024 * 1024)} " +
                        $"budget_mb={_maxPendingGuestWorkBytes / (1024 * 1024)} " +
                        $"work={work.GetType().Name}" +
                        GetGuestWorkPayloadBreakdown(work) +
                        FormatGuestQueueBacklogLocked());
                }

                // Sustained full-queue backpressure is the North Yankton soft-lock
                // signature: ordered actions pile up and producers block for seconds.
                var atItemCap = isPayloadWork
                    ? _pendingPayloadGuestWorkCount >= _maxPendingGuestWorkItems
                    : _pendingSyncGuestWorkCount >= _maxPendingGuestSyncItems;
                if (atItemCap &&
                    _pendingGuestWorkCount != _guestQueueStarvationLastQueued)
                {
                    _guestQueueStarvationLastQueued = _pendingGuestWorkCount;
                    var starvationCount = Interlocked.Increment(
                        ref _guestQueueStarvationTraceCount);
                    if (starvationCount <= 8 || (starvationCount & (starvationCount - 1)) == 0)
                    {
                        Console.Error.WriteLine(
                            $"[LOADER][WARN] vk.guest_queue_starvation " +
                            $"count={starvationCount} " +
                            $"queued={_pendingGuestWorkCount} " +
                            $"payload={_pendingPayloadGuestWorkCount}/{_maxPendingGuestWorkItems} " +
                            $"sync={_pendingSyncGuestWorkCount}/{_maxPendingGuestSyncItems} " +
                            $"work={work.GetType().Name}" +
                            FormatGuestQueueBacklogLocked());
                    }
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
        if (isPayloadWork)
        {
            _pendingPayloadGuestWorkCount++;
        }
        else
        {
            _pendingSyncGuestWorkCount++;
        }

        _pendingGuestWorkBytes = SaturatingAdd(_pendingGuestWorkBytes, payloadBytes);
        // Wake the render loop. Also wake threads that wait for work to finish
        // or for queue space.
        System.Threading.Monitor.PulseAll(_gate);
        return sequence;
    }

    private static bool IsPayloadBearingGuestWork(object work) => work is
        VulkanComputeGuestDispatch or
        VulkanOffscreenGuestDraw or
        VulkanGuestImageWrite or
        VulkanOffscreenColorClear;

    private static bool IsPrioritySyncGuestWork(object work) => work is
        VulkanOrderedGuestAction or
        VulkanGuestCacheOperation or
        VulkanGpuLabelSignal or
        VulkanOrderedGuestFlip or
        VulkanOrderedGuestFlipWait;

    private static string FormatGuestQueueBacklogLocked()
    {
        var typeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var orderedNameCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var queue in _pendingGuestWorkByQueue.Values)
        {
            for (var node = queue.First; node is not null; node = node.Next)
            {
                var work = node.Value.Work;
                var typeName = work.GetType().Name;
                typeCounts[typeName] = typeCounts.GetValueOrDefault(typeName) + 1;
                if (work is VulkanOrderedGuestAction ordered)
                {
                    var prefix = GetOrderedActionDebugPrefix(ordered.DebugName);
                    orderedNameCounts[prefix] = orderedNameCounts.GetValueOrDefault(prefix) + 1;
                }
            }
        }

        static string TopEntries(Dictionary<string, int> counts, int limit)
        {
            if (counts.Count == 0)
            {
                return "-";
            }

            return string.Join(
                ',',
                counts
                    .OrderByDescending(static entry => entry.Value)
                    .Take(limit)
                    .Select(static entry => $"{entry.Key}:{entry.Value}"));
        }

        return $" types=[{TopEntries(typeCounts, 6)}]" +
               $" ordered=[{TopEntries(orderedNameCounts, 8)}]";
    }

    private static string GetOrderedActionDebugPrefix(string debugName)
    {
        if (string.IsNullOrEmpty(debugName))
        {
            return "(empty)";
        }

        var span = debugName.AsSpan();
        var cut = span.IndexOfAny(' ', '=');
        if (cut <= 0)
        {
            cut = Math.Min(span.Length, 48);
        }

        return debugName[..cut];
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

    private static bool TryTakeGuestWork(
        out PendingGuestWork work,
        HashSet<string>? excludedQueues = null,
        bool preferSyncWork = false)
    {
        lock (_gate)
        {
            if (preferSyncWork &&
                TryTakePreferredSyncGuestWorkLocked(out work, excludedQueues))
            {
                return true;
            }

            var queuesToProbe = _pendingGuestQueueSchedule.Count;
            while (_pendingGuestQueueSchedule.Count > 0 && queuesToProbe > 0)
            {
                if (_pendingGuestQueueCursor >= _pendingGuestQueueSchedule.Count)
                {
                    _pendingGuestQueueCursor = 0;
                }

                var queueName = _pendingGuestQueueSchedule[_pendingGuestQueueCursor];
                if (excludedQueues?.Contains(queueName) == true)
                {
                    _pendingGuestQueueCursor =
                        (_pendingGuestQueueCursor + 1) % _pendingGuestQueueSchedule.Count;
                    queuesToProbe--;
                    continue;
                }

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

                RemoveTakenGuestWorkLocked(queueName, queue, advanceScheduleCursor: true);
                return true;
            }

            work = default;
            return false;
        }
    }

    private static bool TryTakePreferredSyncGuestWorkLocked(
        out PendingGuestWork work,
        HashSet<string>? excludedQueues)
    {
        // When the backlog is elevated, prefer draining ordered sync / flip
        // markers ahead of heavy compute/draw heads on other logical queues.
        // Within a queue, FIFO still holds — we only choose among ready heads.
        for (var index = 0; index < _pendingGuestQueueSchedule.Count; index++)
        {
            var queueName = _pendingGuestQueueSchedule[index];
            if (excludedQueues?.Contains(queueName) == true)
            {
                continue;
            }

            if (!_pendingGuestWorkByQueue.TryGetValue(queueName, out var queue) ||
                queue.First is not { } first)
            {
                continue;
            }

            work = first.Value;
            if (!IsPrioritySyncGuestWork(work.Work) ||
                !IsGuestWorkCompletedLocked(work.RequiredSequence))
            {
                continue;
            }

            RemoveTakenGuestWorkLocked(queueName, queue, advanceScheduleCursor: false);
            if (_pendingGuestQueueSchedule.Count > 0)
            {
                _pendingGuestQueueCursor =
                    (Math.Min(index, _pendingGuestQueueSchedule.Count - 1) + 1) %
                    _pendingGuestQueueSchedule.Count;
            }
            else
            {
                _pendingGuestQueueCursor = 0;
            }

            return true;
        }

        work = default;
        return false;
    }

    private static void RemoveTakenGuestWorkLocked(
        string queueName,
        LinkedList<PendingGuestWork> queue,
        bool advanceScheduleCursor)
    {
        var work = queue.First!.Value;
        queue.RemoveFirst();
        _pendingGuestWorkCount--;
        if (IsPayloadBearingGuestWork(work.Work))
        {
            _pendingPayloadGuestWorkCount = Math.Max(0, _pendingPayloadGuestWorkCount - 1);
        }
        else
        {
            _pendingSyncGuestWorkCount = Math.Max(0, _pendingSyncGuestWorkCount - 1);
        }

        var scheduleIndex = _pendingGuestQueueSchedule.IndexOf(queueName);
        if (queue.Count == 0)
        {
            _pendingGuestWorkByQueue.Remove(queueName);
            if (scheduleIndex >= 0)
            {
                _pendingGuestQueueSchedule.RemoveAt(scheduleIndex);
                if (_pendingGuestQueueCursor > scheduleIndex)
                {
                    _pendingGuestQueueCursor--;
                }
                else if (_pendingGuestQueueCursor >= _pendingGuestQueueSchedule.Count)
                {
                    _pendingGuestQueueCursor = 0;
                }
            }
        }
        else if (advanceScheduleCursor && scheduleIndex >= 0)
        {
            _pendingGuestQueueCursor =
                (scheduleIndex + 1) % _pendingGuestQueueSchedule.Count;
        }
    }

    private static bool RequeueGuestWorkFront(in PendingGuestWork work)
    {
        lock (_gate)
        {
            if (_closed)
            {
                return false;
            }

            if (!_pendingGuestWorkByQueue.TryGetValue(work.Queue.Name, out var queue))
            {
                queue = new LinkedList<PendingGuestWork>();
                _pendingGuestWorkByQueue.Add(work.Queue.Name, queue);
                _pendingGuestQueueSchedule.Add(work.Queue.Name);
            }

            // TryTakeGuestWork removes only the item count. Payload ownership
            // remains live until CompleteGuestWork, so requeueing must not add
            // the retained-byte total a second time.
            queue.AddFirst(work);
            _pendingGuestWorkCount++;
            if (IsPayloadBearingGuestWork(work.Work))
            {
                _pendingPayloadGuestWorkCount++;
            }
            else
            {
                _pendingSyncGuestWorkCount++;
            }

            System.Threading.Monitor.PulseAll(_gate);
            return true;
        }
    }

    private static bool WaitForFollowupGuestWork(int timeoutMilliseconds)
    {
        lock (_gate)
        {
            if (_pendingGuestWorkCount > 0)
            {
                return true;
            }

            if (_closed)
            {
                return false;
            }

            System.Threading.Monitor.Wait(_gate, timeoutMilliseconds);
            return _pendingGuestWorkCount > 0;
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
        var uniqueSnapshots = new HashSet<byte[]>(
            System.Collections.Generic.ReferenceEqualityComparer.Instance);
        foreach (var texture in textures)
        {
            if (uniqueSnapshots.Add(texture.RgbaPixels))
            {
                bytes = SaturatingAdd(bytes, (ulong)texture.RgbaPixels.LongLength);
            }
        }

        return bytes;
    }

    private static ulong GetGlobalBufferPayloadBytes(
        IReadOnlyList<GuestMemoryBuffer> buffers)
    {
        var bytes = 0UL;
        var uniqueSnapshots = new HashSet<byte[]>(
            System.Collections.Generic.ReferenceEqualityComparer.Instance);
        foreach (var buffer in buffers)
        {
            if (uniqueSnapshots.Add(buffer.Data))
            {
                bytes = SaturatingAdd(bytes, (ulong)buffer.Data.LongLength);
            }
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

}
