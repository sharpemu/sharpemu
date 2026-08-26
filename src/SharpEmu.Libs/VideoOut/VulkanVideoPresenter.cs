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
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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

    internal enum StorageImageComponentKind
    {
        Float,
        Sint,
        Uint,
    }

    internal readonly record struct SpirvStorageImageContract(
        SpirvImageFormat Format,
        StorageImageComponentKind ComponentKind,
        SpirvImageDim Dimension);

    internal static bool TryReadSpirvStorageImageContracts(
        ReadOnlySpan<byte> spirv,
        out SpirvStorageImageContract[] contracts,
        out string error)
    {
        contracts = [];
        error = string.Empty;
        if (spirv.Length < 5 * sizeof(uint) ||
            BinaryPrimitives.ReadUInt32LittleEndian(spirv) != 0x07230203u)
        {
            error = "invalid-spirv-header";
            return false;
        }

        var componentTypes = new Dictionary<uint, StorageImageComponentKind>();
        var storageImageTypes = new Dictionary<uint, SpirvStorageImageContract>();
        var uniformConstantPointers = new Dictionary<uint, uint>();
        var uniformConstantVariables = new List<(uint Id, uint PointerType)>();
        var bindings = new Dictionary<uint, uint>();
        for (var offset = 5 * sizeof(uint); offset < spirv.Length;)
        {
            var instruction = BinaryPrimitives.ReadUInt32LittleEndian(
                spirv.Slice(offset, sizeof(uint)));
            var wordCount = checked((int)(instruction >> 16));
            var byteCount = checked(wordCount * sizeof(uint));
            if (wordCount == 0 || offset + byteCount > spirv.Length)
            {
                error = "invalid-spirv-instruction-size";
                return false;
            }

            switch ((SpirvOp)(instruction & 0xFFFFu))
            {
                case SpirvOp.TypeInt when wordCount >= 4:
                    componentTypes[ReadSpirvWord(spirv, offset, 1)] =
                        ReadSpirvWord(spirv, offset, 3) != 0
                            ? StorageImageComponentKind.Sint
                            : StorageImageComponentKind.Uint;
                    break;
                case SpirvOp.TypeFloat when wordCount >= 3:
                    componentTypes[ReadSpirvWord(spirv, offset, 1)] =
                        StorageImageComponentKind.Float;
                    break;
                case SpirvOp.TypeImage when wordCount >= 9 &&
                    ReadSpirvWord(spirv, offset, 7) == 2:
                    var imageType = ReadSpirvWord(spirv, offset, 1);
                    var componentType = ReadSpirvWord(spirv, offset, 2);
                    if (!componentTypes.TryGetValue(componentType, out var componentKind))
                    {
                        error = $"unknown-storage-component-type({componentType})";
                        return false;
                    }

                    storageImageTypes[imageType] = new SpirvStorageImageContract(
                        (SpirvImageFormat)ReadSpirvWord(spirv, offset, 8),
                        componentKind,
                        (SpirvImageDim)ReadSpirvWord(spirv, offset, 3));
                    break;
                case SpirvOp.TypePointer when wordCount >= 4 &&
                    ReadSpirvWord(spirv, offset, 2) ==
                    (uint)SpirvStorageClass.UniformConstant:
                    uniformConstantPointers[ReadSpirvWord(spirv, offset, 1)] =
                        ReadSpirvWord(spirv, offset, 3);
                    break;
                case SpirvOp.Variable when wordCount >= 4 &&
                    ReadSpirvWord(spirv, offset, 3) ==
                    (uint)SpirvStorageClass.UniformConstant:
                    uniformConstantVariables.Add((
                        ReadSpirvWord(spirv, offset, 2),
                        ReadSpirvWord(spirv, offset, 1)));
                    break;
                case SpirvOp.Decorate when wordCount >= 4 &&
                    ReadSpirvWord(spirv, offset, 2) ==
                    (uint)SpirvDecoration.Binding:
                    bindings[ReadSpirvWord(spirv, offset, 1)] =
                        ReadSpirvWord(spirv, offset, 3);
                    break;
            }

            offset += byteCount;
        }

        var result = new List<(uint Binding, SpirvStorageImageContract Contract)>();
        foreach (var variable in uniformConstantVariables)
        {
            if (!uniformConstantPointers.TryGetValue(variable.PointerType, out var imageType) ||
                !storageImageTypes.TryGetValue(imageType, out var contract))
            {
                continue;
            }

            if (!bindings.TryGetValue(variable.Id, out var binding) ||
                result.Any(entry => entry.Binding == binding))
            {
                error = $"invalid-storage-image-binding({variable.Id})";
                return false;
            }

            result.Add((binding, contract));
        }

        contracts = result
            .OrderBy(static entry => entry.Binding)
            .Select(static entry => entry.Contract)
            .ToArray();
        return true;
    }

    private static uint ReadSpirvWord(
        ReadOnlySpan<byte> spirv,
        int instructionOffset,
        int wordIndex) =>
        BinaryPrimitives.ReadUInt32LittleEndian(
            spirv.Slice(
                instructionOffset + wordIndex * sizeof(uint),
                sizeof(uint)));

    internal static bool TryValidateStorageImageContract(
        SpirvStorageImageContract shaderContract,
        uint guestFormat,
        uint guestNumberType,
        uint guestType,
        bool supportsStorage,
        out Format vulkanFormat,
        out string error)
    {
        vulkanFormat = Presenter.GetStorageImageFormat(
            Presenter.GetTextureFormat(guestFormat, guestNumberType));
        var guestComponentKind = guestNumberType switch
        {
            4 => StorageImageComponentKind.Uint,
            5 => StorageImageComponentKind.Sint,
            _ => StorageImageComponentKind.Float,
        };
        var guestDimension = IsGuestTexture3D(guestType)
            ? SpirvImageDim.Dim3D
            : SpirvImageDim.Dim2D;
        if (shaderContract.Dimension != guestDimension)
        {
            error = $"dimension-mismatch(spirv={shaderContract.Dimension}," +
                $"guest-type={guestType}/{guestDimension})";
            return false;
        }

        if (shaderContract.ComponentKind != guestComponentKind)
        {
            error = $"component-kind-mismatch(spirv={shaderContract.ComponentKind}," +
                $"guest={guestComponentKind})";
            return false;
        }

        if (shaderContract.Format != SpirvImageFormat.Unknown &&
            TryGetVulkanStorageImageFormat(shaderContract.Format, out var typedFormat) &&
            typedFormat != vulkanFormat)
        {
            error = $"typed-format-mismatch(spirv={shaderContract.Format}/{typedFormat}," +
                $"guest={guestFormat}/num={guestNumberType},vk={vulkanFormat})";
            return false;
        }

        if (shaderContract.Format != SpirvImageFormat.Unknown &&
            !TryGetVulkanStorageImageFormat(shaderContract.Format, out _))
        {
            error = $"unsupported-spirv-storage-format({shaderContract.Format})";
            return false;
        }

        if (!supportsStorage)
        {
            error = $"vulkan-storage-feature-missing(vk={vulkanFormat})";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool TryGetVulkanStorageImageFormat(
        SpirvImageFormat format,
        out Format vulkanFormat)
    {
        vulkanFormat = format switch
        {
            SpirvImageFormat.Rgba32f => Format.R32G32B32A32Sfloat,
            SpirvImageFormat.Rgba16f => Format.R16G16B16A16Sfloat,
            SpirvImageFormat.R32f => Format.R32Sfloat,
            SpirvImageFormat.Rgba8 => Format.R8G8B8A8Unorm,
            SpirvImageFormat.Rgba8Snorm => Format.R8G8B8A8SNorm,
            SpirvImageFormat.Rg32f => Format.R32G32Sfloat,
            SpirvImageFormat.Rg16f => Format.R16G16Sfloat,
            SpirvImageFormat.R11fG11fB10f => Format.B10G11R11UfloatPack32,
            SpirvImageFormat.R16f => Format.R16Sfloat,
            SpirvImageFormat.Rgba16 => Format.R16G16B16A16Unorm,
            SpirvImageFormat.Rgb10A2 => Format.A2B10G10R10UnormPack32,
            SpirvImageFormat.Rg16 => Format.R16G16Unorm,
            SpirvImageFormat.Rg8 => Format.R8G8Unorm,
            SpirvImageFormat.R16 => Format.R16Unorm,
            SpirvImageFormat.R8 => Format.R8Unorm,
            SpirvImageFormat.Rgba16Snorm => Format.R16G16B16A16SNorm,
            SpirvImageFormat.Rg16Snorm => Format.R16G16SNorm,
            SpirvImageFormat.Rg8Snorm => Format.R8G8SNorm,
            SpirvImageFormat.R16Snorm => Format.R16SNorm,
            SpirvImageFormat.R8Snorm => Format.R8SNorm,
            SpirvImageFormat.Rgba32i => Format.R32G32B32A32Sint,
            SpirvImageFormat.Rgba16i => Format.R16G16B16A16Sint,
            SpirvImageFormat.Rgba8i => Format.R8G8B8A8Sint,
            SpirvImageFormat.R32i => Format.R32Sint,
            SpirvImageFormat.Rg32i => Format.R32G32Sint,
            SpirvImageFormat.Rg16i => Format.R16G16Sint,
            SpirvImageFormat.Rg8i => Format.R8G8Sint,
            SpirvImageFormat.R16i => Format.R16Sint,
            SpirvImageFormat.R8i => Format.R8Sint,
            SpirvImageFormat.Rgba32ui => Format.R32G32B32A32Uint,
            SpirvImageFormat.Rgba16ui => Format.R16G16B16A16Uint,
            SpirvImageFormat.Rgba8ui => Format.R8G8B8A8Uint,
            SpirvImageFormat.R32ui => Format.R32Uint,
            SpirvImageFormat.Rgb10A2ui => Format.A2B10G10R10UintPack32,
            SpirvImageFormat.Rg32ui => Format.R32G32Uint,
            SpirvImageFormat.Rg16ui => Format.R16G16Uint,
            SpirvImageFormat.Rg8ui => Format.R8G8Uint,
            SpirvImageFormat.R16ui => Format.R16Uint,
            SpirvImageFormat.R8ui => Format.R8Uint,
            _ => Format.Undefined,
        };
        return vulkanFormat != Format.Undefined;
    }

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
    private static readonly bool _traceGlobalWritebackTiming =
        string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_TRACE_GLOBAL_WRITEBACK_TIMING"),
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
        string debugName)
    {
        ArgumentNullException.ThrowIfNull(applyHostState);
        lock (_gate)
        {
            return _closed || _thread is null
                ? 0
                : EnqueueGuestWorkLocked(
                    new VulkanGuestCacheOperation(operation, applyHostState, debugName));
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

        private const int MaxInFlightGuestSubmissions = 8;
        private bool _gpuLabelTimelineEnabled;
        private VkSemaphore _graphicsGuestTimelineSemaphore;
        private VkSemaphore _computeGuestTimelineSemaphore;
        private ulong _graphicsGuestTimelineValue;
        private ulong _computeGuestTimelineValue;
        private long _gpuLabelTimelineSignalCount;
        private long _gpuLabelTimelineCrossQueueWaitCount;
        private readonly Dictionary<string, GuestGpuLabelDependency>
            _lastSubmittedGpuLabelDependencyByGuestQueue = new(StringComparer.Ordinal);
        private readonly GpuLabelHostPublicationQueue _gpuLabelHostPublications = new();
        private long _graphicsQueueSubmitCount;
        private long _computeQueueSubmitCount;
        // Monotonic submission/completion counters across every queue submit
        // (guest batches, compute chunks and presents). Fences on a single
        // queue signal in submission order, so "timeline <= completed" means
        // the GPU is done with everything submitted up to that point; this
        // lets evicted resources be destroyed without a queue drain.
        private ulong _submitTimeline;
        private ulong _completedTimeline;
        private readonly Queue<(TranslatedDrawResources Resources, ulong RetireTimeline)>
            _deferredResourceDestroys = new();
        private readonly Queue<(GuestImageResource Image, ulong RetireTimeline)>
            _deferredGuestImageVersionDestroys = new();
        private readonly Stack<Fence> _recycledGuestFences = new();
        private readonly Stack<CommandBuffer> _recycledGuestCommandBuffers = new();
        private readonly List<(VkBuffer Buffer, DeviceMemory Memory)> _batchRetireBuffers = new();
        private readonly List<VulkanDetilePass.Transients> _batchRetireDetile = new();
        private const int MaxRecycledGuestFences = 32;
        private const int MaxRecycledGuestCommandBuffers = 32;
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
        private readonly Dictionary<byte[], string> _shaderDigests =
            new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<DescriptorLayoutKey, DescriptorLayoutBundle>
            _descriptorLayouts = new();
        private readonly VulkanHostBufferPool _hostBufferPool;
        private readonly List<GuestBufferAllocation> _guestBufferAllocations = [];
        private readonly Queue<PendingGuestSubmission> _pendingGuestSubmissions = new();
        // Submissions whose fence timed out. Keep GPU objects alive until the
        // fence signals (or the device is lost) so a single hung compute
        // dispatch cannot re-block every subsequent capacity wait for the full
        // fence timeout (~3s → ~0.3 FPS).
        private readonly Queue<PendingGuestSubmission> _abandonedGuestSubmissions = new();
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

        private readonly record struct DirtyGuestBufferRange(
            ulong Offset,
            ulong Length,
            string QueueName,
            ulong Timeline);

        private sealed class GuestBufferAllocation
        {
            public ulong BaseAddress;
            public ulong Size;
            public VkBuffer Buffer;
            public DeviceMemory Memory;
            public nint Mapped;
            public byte[] Shadow = [];
            public ulong LastUseTimeline;
            public List<DirtyGuestBufferRange> DirtyRanges { get; } = [];
        }

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
            public bool PerInstance;
            public uint BaseRecord;
        }

        private const ulong SwapchainAcquireTimeoutNs = 250_000_000;

        private sealed record PendingGuestSubmission(
            Fence Fence,
            CommandBuffer CommandBuffer,
            IReadOnlyList<TranslatedDrawResources> Resources,
            IReadOnlyList<GuestImageResource> TraceImages,
            IReadOnlyList<(VkBuffer Buffer, DeviceMemory Memory)> RetireBuffers,
            IReadOnlyList<VulkanDetilePass.Transients> RetireDetile,
            ulong Timeline,
            GuestGpuLabelDependency LabelDependency,
            string DebugName,
            VulkanGuestQueueIdentity Queue,
            long WorkSequence);

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



        [ThreadStatic]
        private static string? _pendingShaderModuleDumpPath;






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

        // The optional reuse path keeps compatible draws in one render pass.
        // The pass closes before transfer, storage, depth, or barrier work.
        private GuestImageResource? _openPassTarget;

        private void CloseOpenTranslatedRenderPass()
        {
            if (_openPassTarget is not { } target)
            {
                return;
            }

            _openPassTarget = null;
            _openPassKey = null;
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
                PipelineStageFlags.AllCommandsBit,
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
                    _batchRetireBuffers.Count > 0 ? _batchRetireBuffers.ToArray() : [],
                    retireDetile: _batchRetireDetile.Count > 0
                        ? _batchRetireDetile.ToArray()
                        : []);
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

                foreach (var transients in _batchRetireDetile)
                {
                    _detilePass?.Retire(transients);
                }

                ReleaseGuestCommandBuffer(_batchCommandBuffer);
                throw;
            }
            finally
            {
                _batchResources.Clear();
                _batchTraceImages.Clear();
                _batchRetireBuffers.Clear();
                _batchRetireDetile.Clear();
                _batchCommandBuffer = default;
            }
        }

        private void SubmitGuestCommandBuffer(
            CommandBuffer commandBuffer,
            IReadOnlyList<TranslatedDrawResources> resources,
            IReadOnlyList<GuestImageResource> traceImages,
            IReadOnlyList<(VkBuffer Buffer, DeviceMemory Memory)>? retireBuffers = null,
            IReadOnlyList<TranslatedDrawResources>? referencedResources = null,
            IReadOnlyList<VulkanDetilePass.Transients>? retireDetile = null,
            bool useComputeQueue = false)
        {
            var fence = AcquireGuestFence();
            GuestGpuLabelDependency submittedLabelDependency = default;
            try
            {
                var physicalCompute = useComputeQueue && _useDedicatedComputeQueue;
                var submitQueue = physicalCompute ? _computeQueue : _queue;
                var dependency = TakeRequiredGpuLabelDependency(_activeGuestQueue.Name);
                if (_gpuLabelTimelineEnabled &&
                    _lastSubmittedGpuLabelDependencyByGuestQueue.TryGetValue(
                        _activeGuestQueue.Name,
                        out var priorQueueDependency))
                {
                    // A guest queue is serial. Keep its order when host work moves
                    // between the graphics queue and the compute queue.
                    dependency = ResolveGpuLabelSubmissionDependency(
                        dependency,
                        priorQueueDependency);
                }
                var submitInfo = new SubmitInfo
                {
                    SType = StructureType.SubmitInfo,
                    CommandBufferCount = 1,
                    PCommandBuffers = &commandBuffer,
                };
                VkSemaphore* waitSemaphores = stackalloc VkSemaphore[2];
                ulong* waitValues = stackalloc ulong[2];
                PipelineStageFlags* waitStages = stackalloc PipelineStageFlags[2];
                var waitCount = 0u;
                if (_gpuLabelTimelineEnabled)
                {
                    if (physicalCompute && dependency.GraphicsTimeline != 0)
                    {
                        waitSemaphores[waitCount] = _graphicsGuestTimelineSemaphore;
                        waitValues[waitCount] = dependency.GraphicsTimeline;
                        waitStages[waitCount++] = PipelineStageFlags.AllCommandsBit;
                    }
                    if (!physicalCompute && dependency.ComputeTimeline != 0)
                    {
                        waitSemaphores[waitCount] = _computeGuestTimelineSemaphore;
                        waitValues[waitCount] = dependency.ComputeTimeline;
                        waitStages[waitCount++] = PipelineStageFlags.AllCommandsBit;
                    }
                    _gpuLabelTimelineCrossQueueWaitCount += waitCount;

                    var signalSemaphore = physicalCompute
                        ? _computeGuestTimelineSemaphore
                        : _graphicsGuestTimelineSemaphore;
                    var signalValue = physicalCompute
                        ? ++_computeGuestTimelineValue
                        : ++_graphicsGuestTimelineValue;
                    var timelineInfo = new TimelineSemaphoreSubmitInfo
                    {
                        SType = StructureType.TimelineSemaphoreSubmitInfo,
                        WaitSemaphoreValueCount = waitCount,
                        PWaitSemaphoreValues = waitCount == 0 ? null : waitValues,
                        SignalSemaphoreValueCount = 1,
                        PSignalSemaphoreValues = &signalValue,
                    };
                    submitInfo.PNext = &timelineInfo;
                    submitInfo.WaitSemaphoreCount = waitCount;
                    submitInfo.PWaitSemaphores = waitCount == 0 ? null : waitSemaphores;
                    submitInfo.PWaitDstStageMask = waitCount == 0 ? null : waitStages;
                    submitInfo.SignalSemaphoreCount = 1;
                    submitInfo.PSignalSemaphores = &signalSemaphore;

                    SubmitGuestQueue(submitQueue, &submitInfo, fence, resources);
                    submittedLabelDependency =
                        physicalCompute
                            ? new GuestGpuLabelDependency(0, signalValue)
                            : new GuestGpuLabelDependency(signalValue, 0);
                    _lastSubmittedGpuLabelDependencyByGuestQueue[_activeGuestQueue.Name] =
                        submittedLabelDependency;
                }
                else
                {
                    SubmitGuestQueue(submitQueue, &submitInfo, fence, resources);
                }

                if (useComputeQueue && _useDedicatedComputeQueue)
                {
                    _computeQueueSubmitCount++;
                }
                else
                {
                    _graphicsQueueSubmitCount++;
                }
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
                            globalBuffer.GuestSize,
                            _activeGuestQueue.Name,
                            _submitTimeline);
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
                    retireDetile ?? [],
                    _submitTimeline,
                    submittedLabelDependency,
                    resources.Count > 0 ? resources[0].DebugName : "batch",
                    _activeGuestQueue,
                    _activeGuestWorkSequence));
            _lastSubmittedTimelineByGuestQueue[_activeGuestQueue.Name] =
                _submitTimeline;
        }

        private void SubmitGuestQueue(
            Queue submitQueue,
            SubmitInfo* submitInfo,
            Fence fence,
            IReadOnlyList<TranslatedDrawResources> resources)
        {
            var submitContext = ResolveGuestSubmitContext(resources);
            _lastSubmitDebugName = submitContext;
            var submitLabel = string.IsNullOrEmpty(submitContext)
                ? "vkQueueSubmit(guest)"
                : $"vkQueueSubmit(guest) during {submitContext}";
            using (RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.QueueSubmit))
            {
                Check(_vk.QueueSubmit(submitQueue, 1, submitInfo, fence), submitLabel);
            }
        }

        private static GuestGpuLabelDependency TakeRequiredGpuLabelDependency(
            string queueName)
        {
            lock (_gate)
            {
                if (!_requiredGpuLabelDependenciesByGuestQueue.Remove(
                        queueName,
                        out var dependency))
                {
                    return default;
                }

                return dependency;
            }
        }

        private void CreateGuestTimelineSemaphores()
        {
            var typeInfo = new SemaphoreTypeCreateInfo
            {
                SType = StructureType.SemaphoreTypeCreateInfo,
                SemaphoreType = SemaphoreType.Timeline,
                InitialValue = 0,
            };
            var createInfo = new SemaphoreCreateInfo
            {
                SType = StructureType.SemaphoreCreateInfo,
                PNext = &typeInfo,
            };
            Check(
                _vk.CreateSemaphore(
                    _device,
                    &createInfo,
                    null,
                    out _graphicsGuestTimelineSemaphore),
                "vkCreateSemaphore(graphics guest timeline)");
            if (_useDedicatedComputeQueue)
            {
                Check(
                    _vk.CreateSemaphore(
                        _device,
                        &createInfo,
                        null,
                        out _computeGuestTimelineSemaphore),
                    "vkCreateSemaphore(compute guest timeline)");
            }
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

            while (_abandonedGuestSubmissions.Count != 0)
            {
                CollectAbandonedGuestSubmissions();
                if (_abandonedGuestSubmissions.Count == 0)
                {
                    break;
                }

                if (!_abandonedGuestSubmissions.TryPeek(out var oldest))
                {
                    break;
                }

                var fence = oldest.Fence;
                var result = _vk.WaitForFences(
                    _device,
                    1,
                    &fence,
                    true,
                    _guestFenceWaitTimeoutNs);
                if (result == Result.Timeout || result == Result.ErrorDeviceLost)
                {
                    if (result == Result.ErrorDeviceLost)
                    {
                        _deviceLost = true;
                    }

                    while (_abandonedGuestSubmissions.TryDequeue(out var abandoned))
                    {
                        RetireGuestSubmission(abandoned);
                    }

                    break;
                }

                Check(result, $"vkWaitForFences(abandoned: {oldest.DebugName})");
                CollectAbandonedGuestSubmissions();
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
                    if (isProbeWait)
                    {
                        return;
                    }

                    if (_tracedFenceTimeouts.Add(oldest.DebugName))
                    {
                        Console.Error.WriteLine(
                            $"[LOADER][WARN] vk.fence_wait_timeout submission='{oldest.DebugName}' " +
                            $"— GPU work not completing after {_guestFenceWaitTimeoutNs / 1_000_000}ms; " +
                            "abandoning in-flight tracking so later frames are not re-blocked.");
                    }

                    // Move out of the blocking queue without destroying GPU
                    // objects yet — the work may still be running. Poll and
                    // retire from the abandoned list once the fence signals.
                    _pendingGuestSubmissions.Dequeue();
                    _abandonedGuestSubmissions.Enqueue(oldest);
                }
                else if (result == Result.ErrorDeviceLost)
                {
                    _deviceLost = true;
                    if (!_deviceLostLogged)
                    {
                        _deviceLostLogged = true;
                        var work = !string.IsNullOrEmpty(_lastGuestWorkLabel)
                            ? $"last_work={_lastGuestWorkLabel}"
                            : "work=<none>";
                        Console.Error.WriteLine(
                            "[LOADER][ERROR] Vulkan device lost; dropping subsequent guest GPU work. " +
                            $"{work} last_submit={oldest.DebugName} " +
                            "vkWaitForFences(guest) failed with ErrorDeviceLost.");
                    }
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
                RetireGuestSubmission(submission);
            }

            CollectAbandonedGuestSubmissions();
            ProcessDeferredTextureDestroys();
        }

        private void CollectAbandonedGuestSubmissions()
        {
            var pending = _abandonedGuestSubmissions.Count;
            for (var i = 0; i < pending; i++)
            {
                if (!_abandonedGuestSubmissions.TryDequeue(out var submission))
                {
                    break;
                }

                var status = _vk.GetFenceStatus(_device, submission.Fence);
                if (status == Result.NotReady && !_deviceLost)
                {
                    _abandonedGuestSubmissions.Enqueue(submission);
                    continue;
                }

                if (status == Result.ErrorDeviceLost)
                {
                    _deviceLost = true;
                }
                else if (status != Result.NotReady && status != Result.Success)
                {
                    Check(status, $"vkGetFenceStatus(abandoned: {submission.DebugName})");
                }

                RetireGuestSubmission(submission);
            }
        }

        private void RetireGuestSubmission(PendingGuestSubmission submission)
        {
            if (!_deviceLost)
            {
                if (submission.Timeline > _completedTimeline)
                {
                    _completedTimeline = submission.Timeline;
                }

                _gpuLabelHostPublications.Complete(submission.LabelDependency);

                foreach (var image in submission.TraceImages)
                {
                    TraceGuestImageContents(image);
                }
            }
            else
            {
                // A lost device cannot complete the label producer. Do not
                // expose its value to CPU-visible guest memory.
                _gpuLabelHostPublications.Cancel();
            }

            // The fence has signalled, so the detile dispatch that used these
            // is done reading them; hand them back for the next texture.
            foreach (var transients in submission.RetireDetile)
            {
                _detilePass?.Retire(transients);
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

        private bool TryMakeActiveGuestQueueSubmissionsCpuVisible()
        {
            FlushBatchedGuestCommands();
            if (!_lastSubmittedTimelineByGuestQueue.TryGetValue(
                    _activeGuestQueue.Name,
                    out var targetTimeline) ||
                targetTimeline <= _completedTimeline)
            {
                return true;
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

            var fence = target.Fence;
            var status = _vk.GetFenceStatus(_device, fence);
            if (status == Result.NotReady)
            {
                // Never block the drain for seconds on ordered-action visibility.
                // A multi-second WaitForFences here tanked Dead Cells (~0.4fps)
                // and soft-locked GTA after intro once the GPU was busy. Sync
                // item ceiling is high enough that deferring a tick is safe;
                // take a short probe wait on dedicated render threads only.
                if (OperatingSystem.IsMacOS())
                {
                    return false;
                }

                const ulong orderedVisibilityProbeNs = 2_000_000UL; // 2ms
                var waitResult = _vk.WaitForFences(
                    _device,
                    1,
                    &fence,
                    true,
                    orderedVisibilityProbeNs);
                if (waitResult == Result.Timeout)
                {
                    return false;
                }

                if (waitResult == Result.ErrorDeviceLost)
                {
                    _deviceLost = true;
                    return true;
                }

                Check(
                    waitResult,
                    $"vkWaitForFences(queue visibility: {_activeGuestQueue.Name})");
                var waitTrace = Interlocked.Increment(ref _orderedActionFenceWaitTraceCount);
                if (waitTrace <= 8 || (waitTrace & (waitTrace - 1)) == 0)
                {
                    Console.Error.WriteLine(
                        $"[LOADER][TRACE] vk.ordered_action_fence_wait " +
                        $"count={waitTrace} queue={_activeGuestQueue.Name} " +
                        $"submission='{target.DebugName}'");
                }

                CollectCompletedGuestSubmissions(waitForOldest: false);
                if (_traceVulkanShaderEnabled)
                {
                    TraceVulkanShader(
                        $"vk.queue_visibility queue={_activeGuestQueue.Name} " +
                        $"submission={_activeGuestQueue.SubmissionId} " +
                        $"target_timeline={targetTimeline} " +
                        $"completed_timeline={_completedTimeline} waited=1");
                }

                return true;
            }

            if (status == Result.ErrorDeviceLost)
            {
                _deviceLost = true;
                return true;
            }

            Check(status, $"vkGetFenceStatus(queue visibility: {_activeGuestQueue.Name})");
            CollectCompletedGuestSubmissions(waitForOldest: false);
            if (_traceVulkanShaderEnabled)
            {
                TraceVulkanShader(
                    $"vk.queue_visibility queue={_activeGuestQueue.Name} " +
                    $"submission={_activeGuestQueue.SubmissionId} " +
                    $"target_timeline={targetTimeline} completed_timeline={_completedTimeline}");
            }

            return true;
        }

        private void WaitForGuestBufferAllocationForCpuVisibility(
            GuestBufferAllocation allocation)
        {
            if (IsGuestBufferAllocationReferencedByOpenBatch(allocation))
            {
                FlushBatchedGuestCommands();
            }

            var targetTimeline = allocation.LastUseTimeline;
            if (targetTimeline <= _completedTimeline)
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
                    $"Guest buffer 0x{allocation.BaseAddress:X16} lost pending timeline " +
                    $"{targetTimeline} (completed={_completedTimeline}).");
            }

            var fence = target.Fence;
            Check(
                _vk.WaitForFences(_device, 1, &fence, true, ulong.MaxValue),
                $"vkWaitForFences(buffer visibility: 0x{allocation.BaseAddress:X16})");
            CollectCompletedGuestSubmissions(waitForOldest: false);
        }

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

        private void ExecuteOrderedGuestFlip(VulkanOrderedGuestFlip work)
        {
            Agc.AgcExports.MarkAllSurfacesCleared();
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
                MarkGuestFlipVersionSafe(work);
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
                MarkGuestFlipVersionSafe(work);

                lock (_gate)
                {
                    var sequence = (_latestPresentation?.Sequence ?? 0) + 1;
                    var isHdr =
                        VideoOutExports.TryGetDisplayBufferInfo(
                            work.VideoOutHandle,
                            work.DisplayBufferIndex,
                            out var displayBuffer) &&
                        VideoOutExports.IsHdrPixelFormat(displayBuffer.PixelFormat);
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
                        GuestImageVersion: work.Version,
                        IsHdr: isHdr);
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
                    MarkGuestFlipVersionSafe(work);
                    ReleaseGuestCommandBuffer(commandBuffer);
                    DestroyGuestImage(snapshot);
                }
            }
        }

        private bool TryExecuteOrderedGuestFlipWait(VulkanOrderedGuestFlipWait work)
        {
            var safe = _guestFlipCompletion.IsSafe(
                work.VideoOutHandle,
                work.DisplayBufferIndex,
                work.Version);
            TraceVulkanShader(
                $"vk.flip_wait_safe version={work.Version} " +
                $"queue={_activeGuestQueue.Name} submission={_activeGuestQueue.SubmissionId} " +
                $"handle={work.VideoOutHandle} index={work.DisplayBufferIndex} " +
                $"capture_complete={(safe ? 1 : 0)}");
            if (!safe && !_loggedFlipWaitOrderViolation)
            {
                _loggedFlipWaitOrderViolation = true;
                Console.Error.WriteLine(
                    $"[LOADER][WARN] vk.flip_wait_order version={work.Version} " +
                    $"handle={work.VideoOutHandle} index={work.DisplayBufferIndex} " +
                    "executed before its flip capture; deferring.");
            }

            return safe;
        }

        private void MarkGuestFlipVersionSafe(VulkanOrderedGuestFlip work)
        {
            _guestFlipCompletion.MarkSafe(
                work.VideoOutHandle,
                work.DisplayBufferIndex,
                work.Version);
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
                LogicalWidth = source.LogicalWidth,
                LogicalHeight = source.LogicalHeight,
                MipLevels = 1,
                TileMode = source.TileMode,
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
                    DestroyGuestImage(unsubmittedVersion);
                    FlipProgressTracker.RecordFlip(unsubmittedVersion.FlipVersion);
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
                DestroyGuestImage(guestImageVersion);
                FlipProgressTracker.RecordFlip(guestImageVersion.FlipVersion);
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

        private Format PresentationTargetFormat =>
            _hdrOutputActive ? Format.B8G8R8A8Unorm : _swapchainFormat;

        private ImageLayout PresentationTargetFinalLayout =>
            _hdrOutputActive ? ImageLayout.ShaderReadOnlyOptimal : ImageLayout.PresentSrcKhr;

        private Image PresentationTargetImage(uint imageIndex) =>
            _hdrOutputActive ? _presentationImages[imageIndex] : _swapchainImages[imageIndex];

        private void CreatePresentationImage(int index)
        {
            var imageInfo = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                Flags = ImageCreateFlags.CreateMutableFormatBit,
                ImageType = ImageType.Type2D,
                Format = Format.B8G8R8A8Unorm,
                Extent = new Extent3D(_extent.Width, _extent.Height, 1),
                MipLevels = 1,
                ArrayLayers = 1,
                Samples = SampleCountFlags.Count1Bit,
                Tiling = ImageTiling.Optimal,
                Usage = ImageUsageFlags.ColorAttachmentBit |
                        ImageUsageFlags.TransferDstBit |
                        ImageUsageFlags.TransferSrcBit |
                        ImageUsageFlags.SampledBit,
                SharingMode = SharingMode.Exclusive,
                InitialLayout = ImageLayout.Undefined,
            };
            Check(
                _vk.CreateImage(_device, &imageInfo, null, out _presentationImages[index]),
                "vkCreateImage(HDR presentation source)");
            _vk.GetImageMemoryRequirements(
                _device,
                _presentationImages[index],
                out var requirements);
            var allocationInfo = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = requirements.Size,
                MemoryTypeIndex = FindMemoryType(
                    requirements.MemoryTypeBits,
                    MemoryPropertyFlags.DeviceLocalBit),
            };
            Check(
                _vk.AllocateMemory(
                    _device,
                    &allocationInfo,
                    null,
                    out _presentationImageMemory[index]),
                "vkAllocateMemory(HDR presentation source)");
            Check(
                _vk.BindImageMemory(
                    _device,
                    _presentationImages[index],
                    _presentationImageMemory[index],
                    0),
                "vkBindImageMemory(HDR presentation source)");

            _presentationImageViews[index] = CreatePresentationImageView(
                _presentationImages[index],
                Format.B8G8R8A8Unorm);
            _presentationSampleViews[index] = CreatePresentationImageView(
                _presentationImages[index],
                Format.B8G8R8A8Srgb);
        }

        private ImageView CreatePresentationImageView(Image image, Format format)
        {
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
                SubresourceRange = ColorSubresourceRange(),
            };
            Check(
                _vk.CreateImageView(_device, &viewInfo, null, out var view),
                "vkCreateImageView(HDR presentation source)");
            return view;
        }

        private void CreateHdrPresentationResources()
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
            var colorReference = new AttachmentReference(0, ImageLayout.ColorAttachmentOptimal);
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
                _vk.CreateRenderPass(_device, &renderPassInfo, null, out _hdrRenderPass),
                "vkCreateRenderPass(HDR presentation)");

            _hdrFramebuffers = new Framebuffer[_swapchainImageViews.Length];
            for (var index = 0; index < _swapchainImageViews.Length; index++)
            {
                var view = _swapchainImageViews[index];
                var framebufferInfo = new FramebufferCreateInfo
                {
                    SType = StructureType.FramebufferCreateInfo,
                    RenderPass = _hdrRenderPass,
                    AttachmentCount = 1,
                    PAttachments = &view,
                    Width = _extent.Width,
                    Height = _extent.Height,
                    Layers = 1,
                };
                Check(
                    _vk.CreateFramebuffer(
                        _device,
                        &framebufferInfo,
                        null,
                        out _hdrFramebuffers[index]),
                    "vkCreateFramebuffer(HDR presentation)");
            }

            var binding = new DescriptorSetLayoutBinding
            {
                Binding = 1,
                DescriptorType = DescriptorType.CombinedImageSampler,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.FragmentBit,
            };
            var descriptorLayoutInfo = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = 1,
                PBindings = &binding,
            };
            Check(
                _vk.CreateDescriptorSetLayout(
                    _device,
                    &descriptorLayoutInfo,
                    null,
                    out _hdrDescriptorSetLayout),
                "vkCreateDescriptorSetLayout(HDR presentation)");

            var descriptorPoolSize = new DescriptorPoolSize
            {
                Type = DescriptorType.CombinedImageSampler,
                DescriptorCount = checked((uint)_presentationSampleViews.Length * 2),
            };
            var descriptorPoolInfo = new DescriptorPoolCreateInfo
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                MaxSets = checked((uint)_presentationSampleViews.Length * 2),
                PoolSizeCount = 1,
                PPoolSizes = &descriptorPoolSize,
            };
            Check(
                _vk.CreateDescriptorPool(
                    _device,
                    &descriptorPoolInfo,
                    null,
                    out _hdrDescriptorPool),
                "vkCreateDescriptorPool(HDR presentation)");

            var samplerInfo = new SamplerCreateInfo
            {
                SType = StructureType.SamplerCreateInfo,
                MagFilter = Filter.Linear,
                MinFilter = Filter.Linear,
                MipmapMode = SamplerMipmapMode.Linear,
                AddressModeU = SamplerAddressMode.ClampToEdge,
                AddressModeV = SamplerAddressMode.ClampToEdge,
                AddressModeW = SamplerAddressMode.ClampToEdge,
                MaxLod = 1f,
            };
            Check(
                _vk.CreateSampler(_device, &samplerInfo, null, out _hdrSampler),
                "vkCreateSampler(HDR presentation)");

            _hdrDescriptorSets = new DescriptorSet[_presentationSampleViews.Length];
            _hdrPqDescriptorSets = new DescriptorSet[_presentationSampleViews.Length];
            for (var pq = 0; pq < 2; pq++)
            {
                var descriptorSets = pq == 0 ? _hdrDescriptorSets : _hdrPqDescriptorSets;
                var imageViews = pq == 0 ? _presentationSampleViews : _presentationImageViews;
                for (var index = 0; index < descriptorSets.Length; index++)
                {
                    var layout = _hdrDescriptorSetLayout;
                    var allocateInfo = new DescriptorSetAllocateInfo
                    {
                        SType = StructureType.DescriptorSetAllocateInfo,
                        DescriptorPool = _hdrDescriptorPool,
                        DescriptorSetCount = 1,
                        PSetLayouts = &layout,
                    };
                    Check(
                        _vk.AllocateDescriptorSets(
                            _device,
                            &allocateInfo,
                            out descriptorSets[index]),
                        "vkAllocateDescriptorSets(HDR presentation)");
                    var imageInfo = new DescriptorImageInfo
                    {
                        Sampler = _hdrSampler,
                        ImageView = imageViews[index],
                        ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
                    };
                    var write = new WriteDescriptorSet
                    {
                        SType = StructureType.WriteDescriptorSet,
                        DstSet = descriptorSets[index],
                        DstBinding = 1,
                        DescriptorCount = 1,
                        DescriptorType = DescriptorType.CombinedImageSampler,
                        PImageInfo = &imageInfo,
                    };
                    _vk.UpdateDescriptorSets(_device, 1, &write, 0, null);
                }
            }

            var descriptorSetLayout = _hdrDescriptorSetLayout;
            var pipelineLayoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 1,
                PSetLayouts = &descriptorSetLayout,
            };
            Check(
                _vk.CreatePipelineLayout(
                    _device,
                    &pipelineLayoutInfo,
                    null,
                    out _hdrPipelineLayout),
                "vkCreatePipelineLayout(HDR presentation)");
            CreateHdrPresentationPipelines();
        }

        private void CreateHdrPresentationPipelines()
        {
            CreateHdrPresentationPipeline(
                SpirvFixedShaders.CreateCopyFragment(_hdrSdrWhiteLevel),
                "SDR",
                out _hdrPipeline);
            CreateHdrPresentationPipeline(
                SpirvFixedShaders.CreatePqToScRgbFragment(),
                "PQ",
                out _hdrPqPipeline);
        }

        private void CreateHdrPresentationPipeline(
            byte[] fragmentBytes,
            string label,
            out Pipeline pipeline)
        {
            var vertexModule = CreateShaderModule(SpirvFixedShaders.CreateFullscreenVertex(1));
            var fragmentModule = CreateShaderModule(fragmentBytes);
            var entryPoint = (byte*)SilkMarshal.StringToPtr("main");
            try
            {
                var stages = stackalloc PipelineShaderStageCreateInfo[2];
                stages[0] = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.VertexBit,
                    Module = vertexModule,
                    PName = entryPoint,
                };
                stages[1] = new PipelineShaderStageCreateInfo
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
                var blendAttachment = new PipelineColorBlendAttachmentState
                {
                    ColorWriteMask = ColorComponentFlags.RBit |
                                     ColorComponentFlags.GBit |
                                     ColorComponentFlags.BBit |
                                     ColorComponentFlags.ABit,
                };
                var blend = new PipelineColorBlendStateCreateInfo
                {
                    SType = StructureType.PipelineColorBlendStateCreateInfo,
                    AttachmentCount = 1,
                    PAttachments = &blendAttachment,
                };
                var pipelineInfo = new GraphicsPipelineCreateInfo
                {
                    SType = StructureType.GraphicsPipelineCreateInfo,
                    StageCount = 2,
                    PStages = stages,
                    PVertexInputState = &vertexInput,
                    PInputAssemblyState = &inputAssembly,
                    PViewportState = &viewportState,
                    PRasterizationState = &rasterization,
                    PMultisampleState = &multisample,
                    PColorBlendState = &blend,
                    Layout = _hdrPipelineLayout,
                    RenderPass = _hdrRenderPass,
                };
                Check(
                    _vk.CreateGraphicsPipelines(
                        _device,
                        _pipelineCache,
                        1,
                        &pipelineInfo,
                        null,
                        out pipeline),
                    $"vkCreateGraphicsPipelines(HDR {label} presentation)");
                MarkPipelineCacheDirty();
            }
            finally
            {
                SilkMarshal.Free((nint)entryPoint);
                _vk.DestroyShaderModule(_device, fragmentModule, null);
                _vk.DestroyShaderModule(_device, vertexModule, null);
            }
        }

        private void RecordHdrPresentation(uint imageIndex, bool isHdr)
        {
            var sourceBarrier = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = AccessFlags.MemoryWriteBit |
                                AccessFlags.TransferWriteBit |
                                AccessFlags.ColorAttachmentWriteBit,
                DstAccessMask = AccessFlags.ShaderReadBit,
                OldLayout = ImageLayout.ShaderReadOnlyOptimal,
                NewLayout = ImageLayout.ShaderReadOnlyOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = _presentationImages[imageIndex],
                SubresourceRange = ColorSubresourceRange(),
            };
            _vk.CmdPipelineBarrier(
                _commandBuffer,
                PipelineStageFlags.AllCommandsBit,
                PipelineStageFlags.FragmentShaderBit,
                0,
                0,
                null,
                0,
                null,
                1,
                &sourceBarrier);

            var clearValue = new ClearValue
            {
                Color = new ClearColorValue(0f, 0f, 0f, 1f),
            };
            var renderPassInfo = new RenderPassBeginInfo
            {
                SType = StructureType.RenderPassBeginInfo,
                RenderPass = _hdrRenderPass,
                Framebuffer = _hdrFramebuffers[imageIndex],
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
                isHdr ? _hdrPqPipeline : _hdrPipeline);
            var descriptorSet = isHdr
                ? _hdrPqDescriptorSets[imageIndex]
                : _hdrDescriptorSets[imageIndex];
            _vk.CmdBindDescriptorSets(
                _commandBuffer,
                PipelineBindPoint.Graphics,
                _hdrPipelineLayout,
                0,
                1,
                &descriptorSet,
                0,
                null);
            _vk.CmdDraw(_commandBuffer, 3, 1, 0, 0);
            _vk.CmdEndRenderPass(_commandBuffer);
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

        private static int _shaderModuleDumpSequence;

        private ShaderModule CreateShaderModule(byte[] code)
        {
            string? dumpPath = null;
            var dumpDirectory = Environment.GetEnvironmentVariable("SHARPEMU_SHADER_SPIRV_DUMP_DIR");
            if (!string.IsNullOrWhiteSpace(dumpDirectory))
            {
                Directory.CreateDirectory(dumpDirectory);

                var sequence = Interlocked.Increment(ref _shaderModuleDumpSequence);
                dumpPath = Path.Combine(dumpDirectory, $"{sequence:D4}.spv");
                File.WriteAllBytes(dumpPath, code);
            
                _pendingShaderModuleDumpPath = dumpPath;
            }

            
            try
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
            finally
            {
                _pendingShaderModuleDumpPath = null;
            }
        }

        private TranslatedDrawResources CreateTranslatedDrawResources(
            VulkanTranslatedGuestDraw draw,
            RenderPass renderPass,
            IReadOnlyList<Format> renderTargetFormats,
            Extent2D extent,
            IReadOnlyList<GuestImageResource>? feedbackTargets = null,
            bool hasDepthAttachment = false,
            GuestDepthResource? feedbackDepth = null,
            GuestDepthResource? directReadOnlyDepthFeedback = null)
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
                VertexCount = GetDrawVertexCount(
                    draw.PrimitiveType,
                    draw.VertexCount,
                    draw.IndexBuffer,
                    draw.VertexBuffers.Count > 0),
                InstanceCount = Math.Max(draw.InstanceCount, 1),
                BaseVertex = draw.BaseVertex,
                Topology = GetPrimitiveTopology(
                    draw.PrimitiveType,
                    indexed: draw.IndexBuffer is not null,
                    vertexCount: draw.VertexCount,
                    hasVertexBuffers: draw.VertexBuffers.Count > 0),
                Blends = draw.RenderState.Blends.ToArray(),
                BlendConstant = draw.RenderState.BlendConstant,
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

                var hostMovieTextures = FindHostMovieTextureBindings(draw.Textures);
                for (var index = 0; index < draw.Textures.Count; index++)
                {
                    var texture = draw.Textures[index];
                    var usesDirectReadOnlyDepth =
                        directReadOnlyDepthFeedback is not null &&
                        IsMatchingGuestDepthTexture(
                            texture,
                            directReadOnlyDepthFeedback);
                    var resolved = usesDirectReadOnlyDepth
                        ? CreateReadOnlyDepthFeedbackResource(
                            texture,
                            directReadOnlyDepthFeedback!)
                        : index == hostMovieTextures.Luma
                        ? CreateHostMovieTextureResource(texture, plane: 0)
                        : index == hostMovieTextures.Chroma
                            ? CreateHostMovieTextureResource(texture, plane: 1)
                            : ResolveTextureResource(texture);
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
                        usesDirectReadOnlyDepth
                            ? resolved
                            : feedbackDepth is not null &&
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
                        indexBuffer.TryReturnPooledData();
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
                        GuestDataPool.Shared.Return(vertex.Data);
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
                        ImageLayout = resources.Textures[index].ReadOnlyDepthFeedback
                            ? ImageLayout.DepthStencilReadOnlyOptimal
                            : isStorage ||
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

                // One Vulkan binding per unique host buffer and input rate
                // (fetch_index). Attributes share that binding with
                // Offset = OffsetBytes.
                var bindingByBuffer = new Dictionary<(ulong Handle, bool PerInstance), uint>();
                var vertexBindingList = new List<VertexInputBindingDescription>();
                var vertexAttributeDescriptions =
                    new VertexInputAttributeDescription[resources.VertexBuffers.Length];
                for (var index = 0; index < resources.VertexBuffers.Length; index++)
                {
                    var vertexBuffer = resources.VertexBuffers[index];
                    var bufferKey = (vertexBuffer.Buffer.Handle, vertexBuffer.PerInstance);
                    if (!bindingByBuffer.TryGetValue(bufferKey, out var bindingIndex))
                    {
                        bindingIndex = (uint)vertexBindingList.Count;
                        bindingByBuffer[bufferKey] = bindingIndex;
                        vertexBindingList.Add(new VertexInputBindingDescription
                        {
                            Binding = bindingIndex,
                            Stride = vertexBuffer.Stride == 0
                                ? Math.Max(vertexBuffer.ComponentCount, 1) * sizeof(float)
                                : vertexBuffer.Stride,
                            InputRate = vertexBuffer.PerInstance
                                ? VertexInputRate.Instance
                                : VertexInputRate.Vertex,
                        });
                    }

                    vertexAttributeDescriptions[index] = new VertexInputAttributeDescription
                    {
                        Location = vertexBuffer.Location,
                        Binding = bindingIndex,
                        Format = ToVkVertexFormat(
                            vertexBuffer.DataFormat,
                            vertexBuffer.NumberFormat,
                            vertexBuffer.ComponentCount),
                        Offset = vertexBuffer.OffsetBytes,
                    };
                }

                var vertexBindingDescriptions = vertexBindingList.ToArray();

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
                        DepthBiasEnable = raster.DepthBiasEnable,
                        // D32Sfloat cannot reproduce fixed-point guest bias
                        // scaling without the depth-bias-control extension.
                        DepthBiasConstantFactor = raster.ResolveDepthBiasConstantFactor(
                            hostDepthBits: 0),
                        DepthBiasClamp = _supportsDepthBiasClamp
                            ? raster.DepthBiasClamp
                            : 0,
                        DepthBiasSlopeFactor = raster.DepthBiasSlopeFactor,
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
                    var dynamicStateValues = stackalloc DynamicState[3];
                    dynamicStateValues[0] = DynamicState.Viewport;
                    dynamicStateValues[1] = DynamicState.Scissor;
                    // CB_BLEND_RED..ALPHA vary per draw without a pipeline
                    // identity change, so the constant stays dynamic.
                    dynamicStateValues[2] = DynamicState.BlendConstants;
                    var dynamicState = new PipelineDynamicStateCreateInfo
                    {
                        SType = StructureType.PipelineDynamicStateCreateInfo,
                        DynamicStateCount = 3,
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
                !(texture.ArrayedView && texture.ArrayLayers > 1) &&
                TryResolveGuestImageAlias(texture, vkFormat, out var guestImage) &&
                TryGetOrCreateGuestImageView(
                    guestImage,
                    vkFormat,
                    mipLevel: texture.BaseMipLevel,
                    levelCount: texture.MipLevels,
                    dstSelect: texture.DstSelect,
                    out var view,
                    arrayedView: texture.ArrayedView))
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

                // With the write tracker off, AGC ships real texels again but
                // aliased guest images created as GPU RTs stay !IsCpuBacked, so
                // fingerprint refresh never runs and CPU-updated planes (guest
                // Bink) stay black. Promote only when this draw carried
                // non-zero guest pixels — same outcome as the old per-draw path.
                if (!guestImage.IsCpuBacked &&
                    !SharpEmu.HLE.GuestImageWriteTracker.Enabled &&
                    texture.RgbaPixels.Length > 0 &&
                    texture.RgbaPixels.AsSpan().IndexOfAnyExcept((byte)0) >= 0)
                {
                    guestImage.IsCpuBacked = true;
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
                    Depth = guestImage.Depth,
                    Type = guestImage.Type,
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
            if (guestImage.Width != texture.Width ||
                guestImage.Height != texture.Height ||
                guestImage.Depth != GetGuestTextureDepth(texture.Type, texture.Depth) ||
                IsGuestTexture3D(guestImage.Type) != IsGuestTexture3D(texture.Type) ||
                guestImage.MipLevels != 1 ||
                texture.RgbaPixels.Length == 0)
            {
                return false;
            }

            // IsCpuBacked alone used to gate this path, but it is a latch that
            // flips false the first time the address is used as a render target
            // and never flips back. On PS5 that address is unified memory: a
            // surface that was rendered into once and is later rewritten by the
            // guest CPU (glyph atlas rasterization, a 4K UI sheet redrawn on the
            // brightness screen) must still be re-read. Fall back on the write
            // tracker, which reports genuine CPU stores and leaves pure
            // render-into-then-sample feedback untouched.
            bool hasUploadedGeneration;
            long uploadedGeneration;
            lock (_gate)
            {
                hasUploadedGeneration = _cpuBackedUploadGenerations.TryGetValue(
                    texture.Address,
                    out uploadedGeneration);
            }

            if (!ShouldRefreshGuestImageFromCpu(
                    guestImage.IsCpuBacked,
                    texture.WriteGeneration,
                    hasUploadedGeneration,
                    uploadedGeneration))
            {
                return false;
            }

            var rowLength = texture.TileMode == 0
                ? Math.Max(texture.Pitch, texture.Width)
                : texture.Width;
            var depth = GetGuestTextureDepth(texture.Type, texture.Depth);
            var expectedSize = GetTextureByteCount(
                texture.Format,
                rowLength,
                texture.Height,
                depth);
            if (expectedSize == 0 || expectedSize > int.MaxValue)
            {
                return false;
            }

            var pixels = texture.RgbaPixels.Length == (int)expectedSize
                ? texture.RgbaPixels
                : CreateFallbackTexturePixels(texture.Format, rowLength, texture.Height, expectedSize);
            var fingerprint = ComputeTextureContentFingerprint(pixels);
            TraceTextureUploadContents(
                texture,
                pixels,
                rowLength,
                texture.Width,
                texture.Height,
                guestImage.Format,
                "refresh");
            if ((guestImage.Initialized || guestImage.InitialUploadPending) &&
                guestImage.CpuContentFingerprint == fingerprint)
            {
                // Content unchanged despite a newer write generation: advance
                // the recorded generation so later draws can skip the copy
                // again instead of restaging identical texels every draw.
                if (texture.WriteGeneration >= 0)
                {
                    lock (_gate)
                    {
                        _cpuBackedUploadGenerations[texture.Address] =
                            texture.WriteGeneration;
                    }
                }

                TrackSampledTextureSource(texture);
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
                Depth = guestImage.Depth,
                Type = guestImage.Type,
                RowLength = rowLength,
                DstSelect = texture.DstSelect,
                NeedsUpload = true,
                RefreshesExistingImage =
                    guestImage.Initialized || guestImage.InitialUploadPending,
                SamplerState = texture.Sampler,
                GuestImage = guestImage,
                CpuContentFingerprint = fingerprint,
                UpdatesCpuContent = true,
                WriteGeneration = texture.WriteGeneration,
            };
            TrackSampledTextureSource(texture);
            return true;
        }


        [MethodImpl(MethodImplOptions.NoInlining)]
        private TextureResource ResolveStorageImageResource(GuestDrawTexture texture)
        {
            if (texture.Address == 0)
            {
                return CreateStorageScratchResource(texture);
            }

            var guestImage = ResolveStorageGuestImage(texture);
            var vkFormat = GetStorageImageFormat(
                GetTextureFormat(texture.Format, texture.NumberType));
            if (!SupportsStorageImage(vkFormat))
            {
                throw new InvalidOperationException(
                    $"Storage image format {vkFormat} is unsupported for guest " +
                    $"format={texture.Format}/num={texture.NumberType}.");
            }
            var selectedMipLevel = GetStorageMipLevel(texture);
            var mipWidth = GetMipDimension(guestImage.Width, selectedMipLevel);
            var mipHeight = GetMipDimension(guestImage.Height, selectedMipLevel);
            var mipDepth = GetMipDimension(guestImage.Depth, selectedMipLevel);
            var view = GetOrCreateGuestImageIdentityView(
                guestImage,
                vkFormat,
                selectedMipLevel,
                levelCount: 1);
            var resource = new TextureResource
            {
                Address = texture.Address,
                Image = guestImage.Image,
                View = view,
                Width = mipWidth,
                Height = mipHeight,
                Depth = mipDepth,
                Type = guestImage.Type,
                RowLength = mipWidth,
                DstSelect = texture.DstSelect,
                MipLevel = selectedMipLevel,
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
                    texture.Height,
                    GetGuestTextureDepth(texture.Type, texture.Depth));
                if ((ulong)texture.RgbaPixels.Length == expectedSize &&
                    texture.RgbaPixels.AsSpan().IndexOfAnyExcept((byte)0) >= 0)
                {
                    var uploadPixels = texture.Format == 13
                        ? ExpandRgb32Pixels(texture.RgbaPixels)
                        : texture.RgbaPixels;
                    var uploadSize = (ulong)uploadPixels.Length;
                    (resource.StagingBuffer, resource.StagingMemory) =
                        CreateTextureStagingBuffer(
                            uploadPixels,
                            $"{TextureDebugName(texture, guestImage.Format)} storage staging");
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
            var depth = GetGuestTextureDepth(texture.Type, texture.Depth);
            var vkFormat = GetStorageImageFormat(
                GetTextureFormat(texture.Format, texture.NumberType));
            if (!SupportsStorageImage(vkFormat))
            {
                throw new InvalidOperationException(
                    $"Storage scratch format {vkFormat} is unsupported for guest " +
                    $"format={texture.Format}/num={texture.NumberType}.");
            }
            var imageInfo = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                ImageType = GetGuestTextureImageType(texture.Type),
                Format = vkFormat,
                Extent = new Extent3D(width, height, depth),
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
                ViewType = GetGuestTextureViewType(texture.Type),
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
                Depth = depth,
                Type = texture.Type,
                LogicalWidth = width,
                LogicalHeight = height,
                LogicalDepth = depth,
                MipLevels = 1,
                GuestFormat = GetGuestTextureFormat(texture.Format, texture.NumberType),
                Format = vkFormat,
                Image = image,
                Memory = memory,
                View = view,
                SupportsStorageUsage = true,
            };

            return new TextureResource
            {
                Address = 0,
                Image = image,
                ImageMemory = memory,
                View = view,
                Width = width,
                Height = height,
                Depth = depth,
                Type = texture.Type,
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

            var format = GetStorageImageFormat(
                GetTextureFormat(texture.Format, texture.NumberType));
            var guestImage = GetOrCreateGuestImage(
                new GuestRenderTarget(
                    texture.Address,
                    texture.Width,
                    texture.Height,
                    texture.Format,
                    texture.NumberType,
                    texture.ResourceMipLevels,
                    TileMode: texture.TileMode),
                format,
                requiresStorage: true,
                texture.Type,
                GetGuestTextureDepth(texture.Type, texture.Depth));
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


        private GlobalBufferResource CreateGlobalBufferResource(
            GuestMemoryBuffer guestBuffer)
        {
            if (guestBuffer.BaseAddress == 0)
            {
                return CreateTransientGlobalBufferResource(guestBuffer);
            }

            var size = (ulong)Math.Max(guestBuffer.Length, sizeof(uint));
            if (guestBuffer.BaseAddress > ulong.MaxValue - size)
            {
                return CreateTransientGlobalBufferResource(guestBuffer);
            }

            var endAddress = guestBuffer.BaseAddress + size;
            var allocation = FindGuestBufferAllocation(
                guestBuffer.BaseAddress,
                endAddress);

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
            var capturedMatchesShadow = source.SequenceEqual(shadow);
            var liveMatchesShadow = capturedMatchesShadow;
            if (guestBuffer.Writable && _guestMemory is not null)
            {
                if (_guestMemory.TryCompare(
                        guestBuffer.BaseAddress,
                        shadow,
                        out var comparedLiveMatchesShadow))
                {
                    liveMatchesShadow = comparedLiveMatchesShadow;
                }
                else
                {
                    var live = GuestDataPool.Shared.Rent(guestBuffer.Length);
                    try
                    {
                        var liveSpan = live.AsSpan(0, guestBuffer.Length);
                        if (_guestMemory.TryRead(guestBuffer.BaseAddress, liveSpan))
                        {
                            liveMatchesShadow = liveSpan.SequenceEqual(shadow);
                        }
                    }
                    finally
                    {
                        GuestDataPool.Shared.Return(live);
                    }
                }
            }

            var needsRefresh = ShouldRefreshGuestGlobalBuffer(
                guestBuffer.Writable,
                capturedMatchesShadow,
                liveMatchesShadow);
            var allocationInFlight = allocation.LastUseTimeline > _completedTimeline;
            var allocationInOpenBatch =
                IsGuestBufferAllocationReferencedByOpenBatch(allocation);
            if (ShouldVersionReadOnlyGuestGlobalBuffer(
                    guestBuffer.Writable,
                    needsRefresh,
                    allocationInFlight,
                    allocationInOpenBatch))
            {
                return CreateVersionedReadOnlyGlobalBufferResource(
                    guestBuffer,
                    expectedBias,
                    size);
            }

            if (needsRefresh)
            {
                // HOST_COHERENT does not permit a mapped CPU write while a
                // shader uses the allocation. Retire prior users first.
                WaitForGuestBufferAllocationForCpuVisibility(allocation);
                WriteBackAllDirtyGuestBuffers();
                // Populate the cached shadow copy first and write it out to the
                // mapped allocation in one pass. The mapped memory is
                // HOST_VISIBLE|HOST_COHERENT (write-combined on most drivers),
                // so CPU reads from it are uncached and orders of magnitude
                // slower than heap reads — never use it as a copy source.
                if (!guestBuffer.Writable)
                {
                    source.CopyTo(shadow);
                }
                else if (_guestMemory?.TryRead(guestBuffer.BaseAddress, shadow) != true)
                {
                    source.CopyTo(shadow);
                }

                shadow.CopyTo(new Span<byte>(
                    (void*)(allocation.Mapped + checked((nint)guestOffset)),
                    guestBuffer.Length));
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
                GuestDataPool.Shared.Return(guestBuffer.Data);
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

        private GuestBufferAllocation? FindGuestBufferAllocation(
            ulong baseAddress,
            ulong endAddress)
        {
            var lower = 0;
            var upper = _guestBufferAllocations.Count - 1;
            var candidateIndex = -1;
            while (lower <= upper)
            {
                var middle = lower + ((upper - lower) / 2);
                var candidate = _guestBufferAllocations[middle];
                if (candidate.BaseAddress <= baseAddress)
                {
                    candidateIndex = middle;
                    lower = middle + 1;
                }
                else
                {
                    upper = middle - 1;
                }
            }

            if (candidateIndex < 0)
            {
                return null;
            }

            var allocation = _guestBufferAllocations[candidateIndex];
            return endAddress - allocation.BaseAddress <= allocation.Size
                ? allocation
                : null;
        }

        private bool IsGuestBufferAllocationReferencedByOpenBatch(
            GuestBufferAllocation allocation)
        {
            if (!_batchOpen)
            {
                return false;
            }

            foreach (var resources in _batchResources)
            {
                foreach (var globalBuffer in resources.GlobalMemoryBuffers)
                {
                    if (ReferenceEquals(globalBuffer?.Allocation, allocation))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private GlobalBufferResource CreateVersionedReadOnlyGlobalBufferResource(
            GuestMemoryBuffer guestBuffer,
            ulong byteBias,
            ulong guestSize)
        {
            var descriptorSize = checked((guestSize + byteBias + 3) & ~3UL);
            var descriptorLength = checked((int)descriptorSize);
            var snapshot = GuestDataPool.Shared.Rent(descriptorLength);
            try
            {
                var snapshotData = snapshot.AsSpan(0, descriptorLength);
                snapshotData.Clear();
                guestBuffer.Data.AsSpan(0, guestBuffer.Length).CopyTo(
                    snapshotData[checked((int)byteBias)..]);

                var buffer = CreateHostBuffer(
                    snapshotData,
                    BufferUsageFlags.StorageBufferBit,
                    out var memory,
                    out var mapped);
                return new GlobalBufferResource
                {
                    BaseAddress = guestBuffer.BaseAddress,
                    Writable = false,
                    WriteBackToGuest = false,
                    Buffer = buffer,
                    Memory = memory,
                    Mapped = mapped + checked((nint)byteBias),
                    Offset = 0,
                    Size = descriptorSize,
                    GuestOffset = byteBias,
                    GuestSize = guestSize,
                };
            }
            finally
            {
                GuestDataPool.Shared.Return(snapshot);
                if (guestBuffer.Pooled)
                {
                    GuestDataPool.Shared.Return(guestBuffer.Data);
                }
            }
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
                GuestDataPool.Shared.Return(guestBuffer.Data);
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

            var ranges = new List<(ulong Start, ulong End)>(buffers.Count);
            foreach (var buffer in buffers)
            {
                if (buffer.BaseAddress == 0)
                {
                    continue;
                }

                var size = (ulong)Math.Max(buffer.Length, sizeof(uint));
                if (buffer.BaseAddress > ulong.MaxValue - size - 3)
                {
                    continue;
                }

                var alignedStart = buffer.BaseAddress &
                    ~(GuestStorageBufferOffsetAlignment - 1);
                var paddedEnd = (buffer.BaseAddress + size + 3) & ~3UL;
                ranges.Add((
                    alignedStart,
                    paddedEnd));
            }

            if (ranges.Count == 0)
            {
                return;
            }

            ranges.Sort(static (left, right) => left.Start.CompareTo(right.Start));
            var merged = new List<(ulong Start, ulong End)>(ranges.Count);
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
                    Math.Max(previous.End, range.End));
            }

            foreach (var range in merged)
            {
                EnsureGuestBufferAllocation(range.Start, range.End);
            }
        }

        private void EnsureGuestBufferAllocation(
            ulong requestedStart,
            ulong requestedEnd)
        {
            if (FindGuestBufferAllocation(requestedStart, requestedEnd) is not null)
            {
                return;
            }

            var start = requestedStart;
            var end = requestedEnd;
            List<GuestBufferAllocation> overlaps;
            do
            {
                overlaps = _guestBufferAllocations
                    .Where(allocation =>
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

            var replacement = CreateGuestBufferAllocation(start, end);
            foreach (var overlap in overlaps)
            {
                _guestBufferAllocations.Remove(overlap);
                DestroyGuestBufferAllocation(overlap);
            }

            _guestBufferAllocations.Add(replacement);
            _guestBufferAllocations.Sort(static (left, right) =>
                left.BaseAddress.CompareTo(right.BaseAddress));
            UpdateGuestBufferCacheMetric();
            TraceVulkanShader(
                $"vk.guest_buffer_allocation base=0x{start:X16} bytes={replacement.Size} " +
                $"merged={overlaps.Count}");
        }

        private void UpdateGuestBufferCacheMetric()
        {
            var bytes = 0UL;
            foreach (var allocation in _guestBufferAllocations)
            {
                bytes = checked(bytes + allocation.Size);
            }

            PerfOverlay.SetGuestBufferCacheBytes(bytes);
        }

        private GuestBufferAllocation CreateGuestBufferAllocation(
            ulong start,
            ulong end)
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
                out var memory,
                preferredMemoryFlags: MemoryPropertyFlags.HostCachedBit);
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
            ForgetDirtyGuestBuffer(allocation);
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
                PerInstance = guestBuffer.PerInstance,
                BaseRecord = guestBuffer.BaseRecord,
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
            PerInstance = guestBuffer.PerInstance,
            BaseRecord = guestBuffer.BaseRecord,
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

        private static PrimitiveTopology GetPrimitiveTopology(
            uint primitiveType,
            bool indexed,
            uint vertexCount,
            bool hasVertexBuffers) =>
            primitiveType switch
            {
                1 => PrimitiveTopology.PointList,
                2 => PrimitiveTopology.LineList,
                3 => PrimitiveTopology.LineStrip,
                5 => PrimitiveTopology.TriangleFan,
                6 => PrimitiveTopology.TriangleStrip,
                GuestPrimitiveRectListNgg or GuestPrimitiveRectList
                    when AgcPrimitiveHelpers.ShouldDrawRectListAsTriangleStrip(
                        primitiveType,
                        indexed,
                        vertexCount,
                        hasVertexBuffers) => PrimitiveTopology.TriangleStrip,
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
            uint componentCount)
        {
            var format = (dataFormat, numberFormat) switch
            {
                (1, 0) => Format.R8Unorm,
                (1, 1) => Format.R8SNorm,
                (1, 2) => Format.R8Uscaled,
                (1, 3) => Format.R8Sscaled,
                (1, 4) => Format.R8Uint,
                (1, 5) => Format.R8Sint,
                (1, 9) => Format.R8Srgb,
                (2, 0) => Format.R16Unorm,
                (2, 1) => Format.R16SNorm,
                (2, 2) => Format.R16Uscaled,
                (2, 3) => Format.R16Sscaled,
                (2, 4) => Format.R16Uint,
                (2, 5) => Format.R16Sint,
                (2, 7) => Format.R16Sfloat,
                (3, 0) => Format.R8G8Unorm,
                (3, 1) => Format.R8G8SNorm,
                (3, 2) => Format.R8G8Uscaled,
                (3, 3) => Format.R8G8Sscaled,
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
                // Prospero VertexAttribFormat quirks also seen as buffer formats.
                (113, _) => Format.R32G32B32A32Sfloat,
                (121, _) => Format.R16G16Sfloat,
                (16, 0) => Format.B5G6R5UnormPack16,
                (17, 0) => Format.R5G5B5A1UnormPack16,
                (19, 0) => Format.R4G4B4A4UnormPack16,
                (34, 7) => Format.E5B9G9R9UfloatPack32,
                _ => ToVkFloatVertexFormat(componentCount),
            };

            return NarrowVkVertexFormat(format, componentCount);
        }

        /// <summary>
        /// Narrow a sharp's full VkFormat to the component count the VS fetch
        /// actually consumes.
        /// </summary>
        private static Format NarrowVkVertexFormat(Format format, uint usedComponents)
        {
            if (usedComponents == 0)
            {
                return format;
            }

            return (format, usedComponents) switch
            {
                (Format.R32G32B32A32Sfloat, 1) => Format.R32Sfloat,
                (Format.R32G32B32A32Sfloat, 2) => Format.R32G32Sfloat,
                (Format.R32G32B32A32Sfloat, 3) => Format.R32G32B32Sfloat,
                (Format.R32G32B32Sfloat, 1) => Format.R32Sfloat,
                (Format.R32G32B32Sfloat, 2) => Format.R32G32Sfloat,
                (Format.R16G16B16A16Sfloat, 1) => Format.R16Sfloat,
                (Format.R16G16B16A16Sfloat, 2) => Format.R16G16Sfloat,
                (Format.R8G8B8A8Unorm, 1) => Format.R8Unorm,
                (Format.R8G8B8A8Unorm, 2) => Format.R8G8Unorm,
                (Format.R8G8B8A8SNorm, 2) => Format.R8G8SNorm,
                (Format.R8G8B8A8Uint, 1) => Format.R8Uint,
                (Format.R8G8B8A8Uint, 2) => Format.R8G8Uint,
                _ => format,
            };
        }

        private static Format ToVkFloatVertexFormat(uint componentCount) =>
            componentCount switch
            {
                1 => Format.R32Sfloat,
                2 => Format.R32G32Sfloat,
                3 => Format.R32G32B32Sfloat,
                4 => Format.R32G32B32A32Sfloat,
                _ => Format.R32Sfloat,
            };

        private static uint GetDrawVertexCount(
            uint primitiveType,
            uint vertexCount,
            GuestIndexBuffer? indexBuffer,
            bool hasVertexBuffers) =>
            AgcPrimitiveHelpers.GetRectListDrawVertexCount(
                primitiveType,
                vertexCount,
                indexed: indexBuffer is not null,
                hasVertexBuffers);

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
            if (scissor is not { } guestRect)
            {
                return new GuestRect(0, 0, extent.Width, extent.Height);
            }

            var rect = _renderResolutionScale == 1.0
                ? guestRect
                : new GuestRect(
                    (int)Math.Round(guestRect.X * _renderResolutionScale),
                    (int)Math.Round(guestRect.Y * _renderResolutionScale),
                    Math.Max(1u, (uint)Math.Round(guestRect.Width * _renderResolutionScale)),
                    Math.Max(1u, (uint)Math.Round(guestRect.Height * _renderResolutionScale)));

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
            if (viewport is not { } guestRect)
            {
                return new Viewport(0, 0, extent.Width, extent.Height, 0, 1);
            }

            var scale = (float)_renderResolutionScale;
            var rect = scale == 1f
                ? guestRect
                : guestRect with
                {
                    X = guestRect.X * scale,
                    Y = guestRect.Y * scale,
                    Width = guestRect.Width * scale,
                    Height = guestRect.Height * scale,
                };

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



        private bool SupportsColorAttachment(Format format)
        {
            _vk.GetPhysicalDeviceFormatProperties(_physicalDevice, format, out var properties);
            return (properties.OptimalTilingFeatures & FormatFeatureFlags.ColorAttachmentBit) != 0;
        }

        private bool SupportsStorageImage(Format format)
        {
            _vk.GetPhysicalDeviceFormatProperties(_physicalDevice, format, out var properties);
            return (properties.OptimalTilingFeatures & FormatFeatureFlags.StorageImageBit) != 0;
        }

        private static ImageUsageFlags GetNonStorageGuestImageViewUsage(uint type) =>
            ImageUsageFlags.SampledBit |
            ImageUsageFlags.TransferSrcBit |
            ImageUsageFlags.TransferDstBit |
            (IsGuestTexture3D(type)
                ? (ImageUsageFlags)0
                : ImageUsageFlags.ColorAttachmentBit);


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
                ReturnPooledGuestData(work);
                return;
            }

            PumpHostMovieFrame();

            if (_skipAllCompute ||
                AddressListContains("SHARPEMU_SKIP_COMPUTE_CS", work.ShaderAddress) ||
                (_skipTallComputeZ > 0 && work.GroupCountZ >= _skipTallComputeZ))
            {
                TraceVulkanShader(
                    $"vk.compute_skip cs=0x{work.ShaderAddress:X16} " +
                    $"groups={work.GroupCountX}x{work.GroupCountY}x{work.GroupCountZ} " +
                    $"textures={work.Textures.Count}");
                ReturnPooledGuestData(work);
                return;
            }

            if (!TryValidateComputeDispatch(work, out var validationError))
            {
                LogRejectedComputeDispatch(work, validationError);
                ReturnPooledGuestData(work);
                return;
            }

            if (!TryValidateStorageImageBindings(work, out validationError))
            {
                LogRejectedComputeDispatch(work, validationError);
                ReturnPooledGuestData(work);
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

                FlushBatchedGuestCommands();

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
                    MarkGlobalBufferShaderWrites(
                        resources,
                        work.WritesGlobalMemory);

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
                            GetTraceImages(resources, shaderAddress: work.ShaderAddress),
                            useComputeQueue: true);
                        submitted = true;
                    }
                    else
                    {
                        SubmitGuestCommandBuffer(
                            commandBuffer,
                            [],
                            [],
                            referencedResources: [resources],
                            useComputeQueue: true);
                        chunksSubmitted++;
                        commandBuffer = default;
                    }
                }

                MarkSampledImagesInitialized(resources);
                MarkStorageImagesInitialized(resources, traceContents: false);
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
                    Console.Error.WriteLine(
                        $"[LOADER][ERROR] Vulkan device lost during compute " +
                        $"cs=0x{work.ShaderAddress:X16} " +
                        $"groups={work.GroupCountX}x{work.GroupCountY}x{work.GroupCountZ} " +
                        $"textures={work.Textures.Count} " +
                        $"globals={work.GlobalMemoryBuffers.Count} " +
                        $"writes_global={(work.WritesGlobalMemory ? 1 : 0)} " +
                        $"indirect={(work.IsIndirect ? 1 : 0)} " +
                        $"spirv={work.ComputeSpirv.Length}");
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

            // Empty resource tables with non-trivial SPIR-V usually means the
            // SRT/EUD walk failed (scalar_pointer_fallback / srt=0). Binding
            // nothing while the module still declares descriptors is a common
            // device-loss trigger on the subsequent QueueSubmit.
            if (work.Textures.Count == 0 &&
                work.GlobalMemoryBuffers.Count == 0 &&
                work.ComputeSpirv.Length > 0)
            {
                error = "empty-resources";
                return false;
            }

            // Address-0 storage is host scratch for legitimate descriptors, but
            // after an empty SRT walk every binding can collapse to Address-0
            // fallbacks with no real globals — that path has lost the device
            // on Astro Bot right after the first presented frame.
            var hasUsableStorage = false;
            for (var i = 0; i < work.Textures.Count; i++)
            {
                var texture = work.Textures[i];
                if (texture.IsStorage && texture.Address != 0)
                {
                    hasUsableStorage = true;
                    break;
                }
            }

            var hasUsableGlobal = false;
            for (var i = 0; i < work.GlobalMemoryBuffers.Count; i++)
            {
                if (work.GlobalMemoryBuffers[i].BaseAddress != 0)
                {
                    hasUsableGlobal = true;
                    break;
                }
            }

            if (!hasUsableStorage && !hasUsableGlobal)
            {
                error = "no-usable-resources";
                return false;
            }

            error = string.Empty;
            return true;
        }

        private bool TryValidateStorageImageBindings(
            VulkanComputeGuestDispatch work,
            out string error)
        {
            var storageTextures = work.Textures
                .Where(static texture => texture.IsStorage)
                .ToArray();
            if (storageTextures.Length == 0)
            {
                error = string.Empty;
                return true;
            }

            if (!TryReadSpirvStorageImageContracts(
                    work.ComputeSpirv,
                    out var shaderContracts,
                    out error))
            {
                error = $"storage-contract-parse-failed({error})";
                return false;
            }

            if (shaderContracts.Length != storageTextures.Length)
            {
                error = $"storage-binding-count-mismatch(spirv={shaderContracts.Length}," +
                    $"guest={storageTextures.Length})";
                return false;
            }

            for (var index = 0; index < storageTextures.Length; index++)
            {
                var texture = storageTextures[index];
                var shaderContract = shaderContracts[index];
                var vulkanFormat = GetStorageImageFormat(
                    GetTextureFormat(texture.Format, texture.NumberType));
                if (!TryValidateStorageImageContract(
                        shaderContract,
                        texture.Format,
                        texture.NumberType,
                        texture.Type,
                        SupportsStorageImage(vulkanFormat),
                        out _,
                        out var bindingError))
                {
                    error = $"storage-binding[{index}]-invalid(" +
                        $"addr=0x{texture.Address:X16},reason={bindingError})";
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
            if (!ShouldRecordGlobalBufferVisibilityBarrier())
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

        private void MarkGuestBufferDirty(
            GuestBufferAllocation allocation,
            ulong offset,
            ulong length,
            string queueName,
            ulong timeline)
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
                if (!string.Equals(existing.QueueName, queueName, StringComparison.Ordinal))
                {
                    continue;
                }

                var existingEnd = existing.Offset + existing.Length;
                if (end < existing.Offset || existingEnd < start)
                {
                    continue;
                }

                start = Math.Min(start, existing.Offset);
                end = Math.Max(end, existingEnd);
                timeline = Math.Max(timeline, existing.Timeline);
                allocation.DirtyRanges.RemoveAt(index);
            }

            allocation.DirtyRanges.Add(
                new DirtyGuestBufferRange(start, end - start, queueName, timeline));
            IndexDirtyGuestBuffer(allocation, queueName);
        }

        private void WriteBackAllDirtyGuestBuffers(string? queueName = null)
        {
            var memory = _guestMemory;
            if (memory is null)
            {
                return;
            }

            var candidates = GetDirtyGuestBufferCandidates(queueName);
            foreach (var allocation in candidates)
            {
                for (var index = allocation.DirtyRanges.Count - 1; index >= 0; index--)
                {
                    var range = allocation.DirtyRanges[index];
                    if ((queueName is not null &&
                         !string.Equals(range.QueueName, queueName, StringComparison.Ordinal)) ||
                        range.Timeline > _completedTimeline)
                    {
                        continue;
                    }

                    if (range.Length == 0 || range.Length > int.MaxValue)
                    {
                        continue;
                    }

                    var rangeEnd = checked(range.Offset + range.Length);
                    var overlapsInFlightWrite = false;
                    for (var otherIndex = 0;
                         otherIndex < allocation.DirtyRanges.Count;
                         otherIndex++)
                    {
                        if (otherIndex == index)
                        {
                            continue;
                        }

                        var other = allocation.DirtyRanges[otherIndex];
                        if (other.Timeline <= _completedTimeline)
                        {
                            continue;
                        }

                        var otherEnd = checked(other.Offset + other.Length);
                        if (range.Offset < otherEnd && other.Offset < rangeEnd)
                        {
                            overlapsInFlightWrite = true;
                            break;
                        }
                    }

                    if (overlapsInFlightWrite)
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
                    var scanTicks = 0L;
                    var ioTicks = 0L;
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
                    const int FragmentationRunThreshold = 64;
                    var livePageBuffer = GuestDataPool.Shared.Rent(pageSize);
                    var mappedPageBuffer = GuestDataPool.Shared.Rent(pageSize);
                    var pageRuns = new List<(int Start, int Length)>(64);
                    var coalescedPageRun = new List<(int Start, int Length)>(1);
                    try
                    {
                        for (var pageStart = 0;
                             pageStart < mappedBytes.Length;
                             pageStart += pageSize)
                        {
                            var pageEnd = Math.Min(pageStart + pageSize, mappedBytes.Length);
                            var pageLength = pageEnd - pageStart;
                            var mappedPageSource = mappedBytes.Slice(pageStart, pageLength);
                            var shadowPage = shadowBytes.Slice(pageStart, pageLength);
                            if (mappedPageSource.SequenceEqual(shadowPage))
                            {
                                continue;
                            }

                            // HOST_COHERENT mappings are commonly uncached or
                            // write-combined on the CPU. Read each changed page
                            // once with a bulk copy, then perform the byte-level
                            // merge against ordinary cached memory.
                            var mappedPage = mappedPageBuffer.AsSpan(0, pageLength);
                            mappedPageSource.CopyTo(mappedPage);
                            pageRuns.Clear();
                            var scanStartTicks = _traceGlobalWritebackTiming
                                ? System.Diagnostics.Stopwatch.GetTimestamp()
                                : 0L;

                            const int coarseBlockSize = 128;
                            var coarseBlockCount = 0;
                            var coarseDiffBlockCount = 0;
                            for (var blockStart = 0; blockStart < pageLength; blockStart += coarseBlockSize)
                            {
                                var blockEnd = Math.Min(blockStart + coarseBlockSize, pageLength);
                                coarseBlockCount++;
                                if (!mappedPage.Slice(blockStart, blockEnd - blockStart).SequenceEqual(
                                        shadowPage.Slice(blockStart, blockEnd - blockStart)))
                                {
                                    coarseDiffBlockCount++;
                                }
                            }

                            if (coarseBlockCount > 0 && coarseDiffBlockCount * 4 >= coarseBlockCount)
                            {
                                pageRuns.Add((pageStart, pageLength));
                                changedRuns++;
                                changedBytes += (ulong)pageLength;
                                if (firstChangedOffset < 0)
                                {
                                    firstChangedOffset = pageStart;
                                }
                            }
                            else if (coarseDiffBlockCount > 0)
                            {
                                var cursor = 0;
                                while (cursor < pageLength)
                                {
                                    cursor = SkipEqualBytes(mappedPage, shadowPage, cursor, pageLength);

                                    if (cursor == pageLength)
                                    {
                                        break;
                                    }

                                    var runStart = cursor;
                                    cursor = SkipDifferentBytes(mappedPage, shadowPage, cursor, pageLength);

                                    var runLength = cursor - runStart;
                                    pageRuns.Add((pageStart + runStart, runLength));
                                    changedRuns++;
                                    changedBytes += (ulong)runLength;
                                    if (firstChangedOffset < 0)
                                    {
                                        firstChangedOffset = pageStart + runStart;
                                    }
                                }
                            }

                            if (_traceGlobalWritebackTiming)
                            {
                                scanTicks += System.Diagnostics.Stopwatch.GetTimestamp() - scanStartTicks;
                            }

                            if (pageRuns.Count == 0)
                            {
                                continue;
                            }

                            List<(int Start, int Length)> runsToWrite;
                            if (pageRuns.Count > FragmentationRunThreshold)
                            {
                                coalescedPageRun.Clear();
                                coalescedPageRun.Add((
                                    pageRuns[0].Start,
                                    pageRuns[^1].Start + pageRuns[^1].Length - pageRuns[0].Start));
                                runsToWrite = coalescedPageRun;
                            }
                            else
                            {
                                runsToWrite = pageRuns;
                            }

                            changedPages++;
                            var livePage = livePageBuffer.AsSpan(0, pageLength);
                            var ioStartTicks = _traceGlobalWritebackTiming
                                ? System.Diagnostics.Stopwatch.GetTimestamp()
                                : 0L;
                            var readOk = memory.TryRead(guestAddress + (ulong)pageStart, livePage);
                            if (_traceGlobalWritebackTiming)
                            {
                                ioTicks += System.Diagnostics.Stopwatch.GetTimestamp() - ioStartTicks;
                            }

                            if (readOk)
                            {
                                foreach (var run in runsToWrite)
                                {
                                    mappedPage.Slice(run.Start - pageStart, run.Length).CopyTo(
                                        livePage.Slice(run.Start - pageStart, run.Length));
                                }

                                var writeStartTicks = _traceGlobalWritebackTiming
                                    ? System.Diagnostics.Stopwatch.GetTimestamp()
                                    : 0L;
                                var writeOk = memory.TryWrite(guestAddress + (ulong)pageStart, livePage);
                                if (_traceGlobalWritebackTiming)
                                {
                                    ioTicks += System.Diagnostics.Stopwatch.GetTimestamp() - writeStartTicks;
                                }

                                if (writeOk)
                                {
                                    foreach (var run in runsToWrite)
                                    {
                                        mappedPage.Slice(run.Start - pageStart, run.Length).CopyTo(
                                            shadowBytes.Slice(run.Start, run.Length));
                                    }

                                    writtenPages++;
                                    writtenRuns += runsToWrite.Count;
                                    continue;
                                }

                                foreach (var run in runsToWrite)
                                {
                                    failedRuns++;
                                    MarkGuestBufferDirty(
                                        allocation,
                                        range.Offset + (ulong)run.Start,
                                        (ulong)run.Length,
                                        range.QueueName,
                                        range.Timeline);
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
                                        mappedPage.Slice(run.Start - pageStart, run.Length).CopyTo(
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
                                            mappedPage.Slice(run.Start - pageStart, run.Length).CopyTo(
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
                                    var changed = mappedPage.Slice(
                                        run.Start - pageStart,
                                        run.Length);
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
                                            (ulong)run.Length,
                                            range.QueueName,
                                            range.Timeline);
                                    }
                                }
                            }
                        }
                    }
                    finally
                    {
                        GuestDataPool.Shared.Return(livePageBuffer);
                        GuestDataPool.Shared.Return(mappedPageBuffer);
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

                    if (_traceGlobalWritebackTiming && changedRuns > 0)
                    {
                        var freq = (double)System.Diagnostics.Stopwatch.Frequency;
                        Console.Error.WriteLine(
                            $"[LOADER][ERROR] vk.global_writeback_timing base=0x{guestAddress:X16} " +
                            $"changed_runs={changedRuns} changed_pages={changedPages} " +
                            $"scan_ms={(scanTicks * 1000.0 / freq).ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} " +
                            $"io_ms={(ioTicks * 1000.0 / freq).ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}");
                    }
                }

                RefreshDirtyGuestBufferIndex(allocation, queueName);
            }
        }

        private static int SkipEqualBytes(
            ReadOnlySpan<byte> a,
            ReadOnlySpan<byte> b,
            int start,
            int end)
        {
            var cursor = start;
            var vectorSize = System.Numerics.Vector<byte>.Count;
            while (cursor + vectorSize <= end)
            {
                var va = new System.Numerics.Vector<byte>(a.Slice(cursor, vectorSize));
                var vb = new System.Numerics.Vector<byte>(b.Slice(cursor, vectorSize));
                if (va != vb)
                {
                    break;
                }

                cursor += vectorSize;
            }

            while (cursor < end && a[cursor] == b[cursor])
            {
                cursor++;
            }

            return cursor;
        }

        private static int SkipDifferentBytes(
            ReadOnlySpan<byte> a,
            ReadOnlySpan<byte> b,
            int start,
            int end)
        {
            var cursor = start;
            while (cursor < end && a[cursor] != b[cursor])
            {
                cursor++;
            }

            return cursor;
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
                if (!TryDecodeRenderTargetFormat(
                        target.Format,
                        target.NumberType,
                        target.ComponentSwap,
                        out targetFormats[index]) ||
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

            var normalizedBlends = GuestBlendStateNormalizer.NormalizeIntegerAttachments(
                work.Draw.RenderState.Blends,
                targetFormats.Select(static format => format.IsInteger).ToArray(),
                out var normalizedBlendCount);
            var draw = normalizedBlendCount == 0
                ? work.Draw
                : work.Draw with
                {
                    RenderState = work.Draw.RenderState with { Blends = normalizedBlends },
                };

            if (!_supportsIndependentBlend)
            {
                for (var index = 1; index < draw.RenderState.Blends.Count; index++)
                {
                    if (draw.RenderState.Blends[index] !=
                        draw.RenderState.Blends[0])
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

            if (!VulkanStorageFeedbackTargetResolver.TryResolve(
                    work.Targets,
                    draw.Textures,
                    draw.RenderState.Blends,
                    out var resolvedTargets,
                    out var usedStorageCompatibilityAttachment,
                    out var unsupportedStorageFeedbackAddress))
            {
                Console.Error.WriteLine(
                    $"[LOADER][WARN] Vulkan skipped storage render-target feedback loop " +
                    $"ps=0x{work.ShaderAddress:X16} " +
                    $"target=0x{unsupportedStorageFeedbackAddress:X16}; " +
                    "nonzero color writes or aliased MRT storage require pass splitting");
                ReturnPooledGuestData(work.Draw);
                return;
            }

            if (usedStorageCompatibilityAttachment)
            {
                TraceVulkanShader(
                    $"vk.storage_feedback_compat " +
                    $"ps=0x{work.ShaderAddress:X16} " +
                    $"storage=0x{work.Targets[0].Address:X16} " +
                    $"size={work.Targets[0].Width}x{work.Targets[0].Height}");
                work = work with
                {
                    Targets = resolvedTargets,
                    PublishTarget = false,
                };
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
                // A view-compatible alias accept can return an image whose
                // identity differs from the request (sRGB vs UNORM
                // counterpart). The render pass, framebuffer views, and
                // pipeline cache key must all follow the format that actually
                // backs the attachment; a pipeline keyed on the requested
                // format could later be replayed inside a render pass of the
                // other identity.
                formats[index] = targets[index].Format;

                // Guest colour attachments load their previous contents on
                // every pass after the first, so nothing resets one until the
                // guest clears it. Consume a pending clear here, before the
                // render pass is built, so the pass uses LoadOp.Clear.
                if (work.Targets[index].Address != 0 &&
                    _pendingGuestColorClears.TryRemove(work.Targets[index].Address, out _))
                {
                    targets[index].Initialized = false;
                }

                // CMASK meta-state: if the surface's metadata says "all clear",
                // start this pass from LoadOp.Clear and consume the state.
                // CPU-backed targets are skipped (their guest memory contents
                // are uploaded, not cleared) — same rule the flip-arm used.
                if (work.Targets[index].Address != 0 &&
                    !targets[index].IsCpuBacked &&
                    Agc.AgcExports.IsMetaClearedForSurface(work.Targets[index].Address))
                {
                    targets[index].Initialized = false;
                    Agc.AgcExports.ConsumeMetaClear(work.Targets[index].Address);
                }

                if (work.Targets[index].Address != 0 &&
                    TakeGuestImageInitialData(work.Targets[index].Address) is { } initialData &&
                    !targets[index].Initialized &&
                    (ulong)initialData.Length ==
                        GetTextureByteCount(
                            targetDescriptor.Format,
                            targets[index].Width,
                            targets[index].Height))
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
                var depthClearMode = GuestDepthClearMode.Resolve(
                    draw.RenderState.Depth,
                    work.DepthTarget);
                var clearDepthForDraw = depthClearMode.ClearAttachment;
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
                var clearDepthSeparately = false;
                if (ShouldAttachGuestDepth(
                        work.DepthTarget,
                        draw.RenderState.Depth) &&
                    work.DepthTarget is { } depthTarget)
                {
                    // Logical dims: GetOrCreateGuestDepth below scales itself.
                    var resolution = GuestDepthExtentResolver.Resolve(
                        depthTarget,
                        firstTarget.LogicalWidth,
                        firstTarget.LogicalHeight,
                        draw.Textures);
                    var effectiveDepthTarget = resolution.IsUsable &&
                        (resolution.Width != depthTarget.Width ||
                         resolution.Height != depthTarget.Height)
                            ? depthTarget with
                            {
                                Width = resolution.Width,
                                Height = resolution.Height,
                            }
                            : depthTarget;

                    depth = GetOrCreateGuestDepth(effectiveDepthTarget);
                    PrepareFirstUseDepth(depth, draw.RenderState.Depth);
                    if (clearDepthForDraw)
                    {
                        depth.GuestClearDepth = effectiveDepthTarget.ClearDepth;
                        depth.ClearDepth = effectiveDepthTarget.ClearDepth;
                    }
                    clearDepthSeparately = clearDepthForDraw &&
                        (depth.Width < firstTarget.Width ||
                         depth.Height < firstTarget.Height);
                    if (targets.Length == 1 && !clearDepthSeparately)
                    {
                        depthFramebuffer = GetOrCreateDepthFramebuffer(firstTarget, depth);
                    }
                }

                if (depth is not null && !clearDepthSeparately)
                {
                    // Guest color images may be allocated at their maximum
                    // resolution while the active viewport and DB surface use
                    // a smaller dynamic-rendering extent. Vulkan requires the
                    // framebuffer extent to fit every attachment.
                    extent = new Extent2D(
                        Math.Min(firstTarget.Width, depth.Width),
                        Math.Min(firstTarget.Height, depth.Height));
                }

                if (depthClearMode.SuppressDrawDepthState)
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

                var hasAttachedDepth = depth is not null && !clearDepthSeparately;
                var hasCompatibleDepthSample = depth is not null &&
                    draw.Textures.Any(texture =>
                        IsMatchingGuestDepthTexture(texture, depth));
                var directReadOnlyDepthFeedback =
                    VulkanReadOnlyDepthFeedbackPolicy.CanUse(
                        _directReadOnlyDepthFeedback,
                        hasAttachedDepth,
                        depth?.Initialized == true,
                        depth?.Layout is ImageLayout.ShaderReadOnlyOptimal or
                            ImageLayout.DepthStencilAttachmentOptimal or
                            ImageLayout.DepthStencilReadOnlyOptimal,
                        draw.RenderState.Depth.TestEnable,
                        draw.RenderState.Depth.WriteEnable,
                        clearDepthForDraw,
                        targets.Length,
                        hasCompatibleDepthSample);

                RenderPass renderPass;
                Framebuffer framebuffer;
                if (directReadOnlyDepthFeedback)
                {
                    renderPass = firstTarget.Initialized
                        ? depthFramebuffer!.ReadOnlyLoadRenderPass
                        : depthFramebuffer!.ReadOnlyColorClearRenderPass;
                    framebuffer = depthFramebuffer.ReadOnlyFramebuffer;
                }
                else
                {
                    renderPass = depthFramebuffer is null
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
                    framebuffer = depthFramebuffer?.Framebuffer ?? firstTarget.Framebuffer;
                    if (targets.Length > 1)
                    {
                        var attachedDepth = clearDepthSeparately ? null : depth;
                        (renderPass, framebuffer) = CreateRenderPassAndFramebuffer(
                            formats,
                            targets.Select(target => target.MipViews.Length > 0
                                ? target.MipViews[0]
                                : target.View).ToArray(),
                            extent.Width,
                            extent.Height,
                            targets.Select(target =>
                                target.Initialized || target.InitialUploadPending).ToArray(),
                            attachedDepth,
                            attachedDepth?.Initialized == true && !clearDepthForDraw);
                        transientRenderPass = renderPass;
                        transientFramebuffer = framebuffer;
                    }
                }

                resources = CreateTranslatedDrawResources(
                    draw,
                    renderPass,
                    formats,
                    extent,
                    targets,
                    hasDepthAttachment: hasAttachedDepth,
                    feedbackDepth: directReadOnlyDepthFeedback || clearDepthSeparately
                        ? null
                        : depth,
                    directReadOnlyDepthFeedback: directReadOnlyDepthFeedback
                        ? depth
                        : null);
                if (directReadOnlyDepthFeedback)
                {
                    RecordDirectReadOnlyDepthFeedback();
                }
                resources.TransientRenderPass = transientRenderPass;
                resources.TransientFramebuffer = transientFramebuffer;
                transientRenderPass = default;
                transientFramebuffer = default;
                resources.DebugName =
                    $"SharpEmu offscreen mrt={targets.Length} " +
                    $"ps=0x{work.ShaderAddress:X16} " +
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
                if (clearDepthSeparately && depth is not null)
                {
                    CloseOpenTranslatedRenderPass();
                    RecordStandaloneGuestDepthClear(depth);
                }
                var hasStorageImages = false;
                foreach (var texture in resources.Textures)
                {
                    if (texture is null)
                    {
                        continue;
                    }

                    hasStorageImages |= texture.IsStorage;
                }

                var hasDepthAttachment = depth is not null && !clearDepthSeparately;
                var usesInitializedColorLoad =
                    firstTarget.Initialized &&
                    !firstTarget.InitialUploadPending;
                var usesInitializedLoadPass =
                    targets.Length == 1 &&
                    usesInitializedColorLoad &&
                    (!hasDepthAttachment
                        ? renderPass.Handle == firstTarget.RenderPass.Handle &&
                          framebuffer.Handle == firstTarget.Framebuffer.Handle
                        : depth is not null &&
                          depthFramebuffer is not null &&
                          depth.Initialized &&
                          !clearDepthForDraw &&
                          renderPass.Handle == depthFramebuffer.LoadRenderPass.Handle &&
                          framebuffer.Handle == depthFramebuffer.Framebuffer.Handle);
                var needsGlobalBufferBarrier =
                    NeedsGlobalBufferVisibilityBarrier(resources);
                var reuseHazards = GetRenderPassReuseHazards(
                    targets,
                    resources,
                    usesInitializedLoadPass,
                    needsGlobalBufferBarrier);
                var passKey = new VulkanRenderPassReuseKey(
                    firstTarget.Image.Handle,
                    hasDepthAttachment ? depth!.Image.Handle : 0,
                    renderPass.Handle,
                    framebuffer.Handle,
                    extent.Width,
                    extent.Height);
                var continueOpenPass =
                    !directReadOnlyDepthFeedback &&
                    _reuseTranslatedRenderPasses &&
                    _openPassKey is { } openPassKey &&
                    VulkanRenderPassReusePolicy.CanContinue(
                        openPassKey,
                        passKey,
                        reuseHazards);
                if (!continueOpenPass)
                {
                    CloseOpenTranslatedRenderPass();
                }
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

                if (!continueOpenPass)
                {
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
                        directReadOnlyDepthFeedback &&
                        depth.Layout != ImageLayout.DepthStencilReadOnlyOptimal)
                    {
                        var fromDepthAttachment =
                            depth.Layout == ImageLayout.DepthStencilAttachmentOptimal;
                        var toReadOnlyDepth = new ImageMemoryBarrier
                        {
                            SType = StructureType.ImageMemoryBarrier,
                            SrcAccessMask = fromDepthAttachment
                                ? AccessFlags.DepthStencilAttachmentReadBit |
                                  AccessFlags.DepthStencilAttachmentWriteBit
                                : AccessFlags.ShaderReadBit,
                            DstAccessMask =
                                AccessFlags.DepthStencilAttachmentReadBit |
                                AccessFlags.ShaderReadBit,
                            OldLayout = depth.Layout,
                            NewLayout = ImageLayout.DepthStencilReadOnlyOptimal,
                            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                            Image = depth.Image,
                            SubresourceRange = new ImageSubresourceRange(
                                ImageAspectFlags.DepthBit, 0, 1, 0, 1),
                        };
                        _vk.CmdPipelineBarrier(
                            _commandBuffer,
                            fromDepthAttachment
                                ? PipelineStageFlags.EarlyFragmentTestsBit |
                                  PipelineStageFlags.LateFragmentTestsBit
                                : PipelineStageFlags.FragmentShaderBit |
                                  PipelineStageFlags.ComputeShaderBit,
                            PipelineStageFlags.EarlyFragmentTestsBit |
                            PipelineStageFlags.LateFragmentTestsBit |
                            PipelineStageFlags.FragmentShaderBit,
                            0,
                            0,
                            null,
                            0,
                            null,
                            1,
                            &toReadOnlyDepth);
                        depth.Layout = ImageLayout.DepthStencilReadOnlyOptimal;
                    }
                    else if (depth is not null &&
                        !clearDepthSeparately &&
                        (depth.Layout == ImageLayout.ShaderReadOnlyOptimal ||
                         depth.Layout == ImageLayout.DepthStencilReadOnlyOptimal))
                    {
                        var toDepthAttachment = new ImageMemoryBarrier
                        {
                            SType = StructureType.ImageMemoryBarrier,
                            SrcAccessMask = depth.Layout ==
                                ImageLayout.DepthStencilReadOnlyOptimal
                                    ? AccessFlags.DepthStencilAttachmentReadBit |
                                      AccessFlags.ShaderReadBit
                                    : AccessFlags.ShaderReadBit,
                            DstAccessMask =
                                AccessFlags.DepthStencilAttachmentReadBit |
                                AccessFlags.DepthStencilAttachmentWriteBit,
                            OldLayout = depth.Layout,
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
                            PipelineStageFlags.ComputeShaderBit |
                            PipelineStageFlags.EarlyFragmentTestsBit |
                            PipelineStageFlags.LateFragmentTestsBit,
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

                    ClearColorValue[]? metaClearValues = null;
                    for (var colorIndex = 0; colorIndex < targets.Length; colorIndex++)
                    {
                        if (!targets[colorIndex].Initialized &&
                            work.Targets[colorIndex].Address != 0)
                        {
                            var (clearWord0, clearWord1) = Agc.AgcExports.GetMetaClearValue(
                                work.Targets[colorIndex].Address);
                            if (clearWord0 != 0 || clearWord1 != 0)
                            {
                                metaClearValues ??= new ClearColorValue[targets.Length];
                                metaClearValues[colorIndex] = UnpackMetaClearValue(
                                    work.Targets[colorIndex].Format,
                                    clearWord0,
                                    clearWord1);
                            }
                        }
                    }

                    BeginTranslatedRenderPass(
                        renderPass,
                        framebuffer,
                        extent,
                        colorAttachmentCount: targets.Length,
                        hasDepthAttachment: hasDepthAttachment,
                        clearDepth: depth?.ClearDepth ?? 1f,
                        colorClearValues: metaClearValues);
                }

                RecordTranslatedDrawInPass(resources, extent);
                MarkGlobalBufferShaderWrites(resources);
                var keepPassOpen =
                    !directReadOnlyDepthFeedback &&
                    _reuseTranslatedRenderPasses &&
                    VulkanRenderPassReusePolicy.CanKeepOpen(reuseHazards);
                if (keepPassOpen)
                {
                    _openPassTarget = firstTarget;
                    _openPassKey = passKey;
                }
                else
                {
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
                        RecordStorageImagesForRead(
                            resources,
                            PipelineStageFlags.FragmentShaderBit);
                    }
                }

                RecordRenderPassReuseDecision(reuseHazards, continueOpenPass);

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
                    if (!clearDepthSeparately)
                    {
                        depth.Layout = directReadOnlyDepthFeedback
                            ? ImageLayout.DepthStencilReadOnlyOptimal
                            : ImageLayout.DepthStencilAttachmentOptimal;
                    }
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
                    Console.Error.WriteLine(
                        $"[LOADER][ERROR] Vulkan device lost during offscreen " +
                        $"vs=0x{work.ShaderAddress:X16} " +
                        $"mrt={work.Targets.Count} " +
                        $"textures={work.Draw.Textures.Count} " +
                        $"vertices={work.Draw.VertexCount}");
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

                // Non-Vulkan failures here are emulator bugs rather than device
                // state, and the message alone ("overflow", "index out of range")
                // names neither the guest draw nor the code that rejected it.
                Console.Error.WriteLine(
                    $"[LOADER][ERROR] Vulkan offscreen draw failed " +
                    $"mrt={work.Targets.Count} vs=0x{work.ShaderAddress:X16} " +
                    $"size={work.Targets[0].Width}x{work.Targets[0].Height} " +
                    $"format={work.Targets[0].Format}/{work.Targets[0].NumberType} " +
                    $"textures={work.Draw.Textures.Count} " +
                    $"vertices={work.Draw.VertexCount}: {exception}");
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
        private void ExecuteOffscreenColorClear(VulkanOffscreenColorClear work)
        {
            if (_deviceLost || work.Targets.Count == 0)
            {
                return;
            }

            Interlocked.Increment(ref _perfDrawCount);
            PerfOverlay.RecordDraw();

            var targetFormats = new VulkanRenderTargetFormat[work.Targets.Count];
            for (var index = 0; index < targetFormats.Length; index++)
            {
                var target = work.Targets[index];
                if (!TryDecodeRenderTargetFormat(
                        target.Format,
                        target.NumberType,
                        target.ComponentSwap,
                        out targetFormats[index]) ||
                    !SupportsColorAttachment(targetFormats[index].Format))
                {
                    Console.Error.WriteLine(
                        $"[LOADER][WARN] Vulkan skipped color clear for unsupported target " +
                        $"0x{target.Address:X16} format={target.Format} number_type={target.NumberType}.");
                    return;
                }
            }

            EnsureGuestSubmissionCapacity();
            var commandBuffer = BeginBatchedGuestCommands();
            CloseOpenTranslatedRenderPass();
            var logicalClear = stackalloc float[4]
            {
                work.Red,
                work.Green,
                work.Blue,
                work.Alpha,
            };

            for (var index = 0; index < work.Targets.Count; index++)
            {
                var targetDescriptor = work.Targets[index];
                var exportMapping = targetFormats[index].ExportMapping;
                var clearValue = new ClearColorValue(
                    logicalClear[exportMapping.Map(0)],
                    logicalClear[exportMapping.Map(1)],
                    logicalClear[exportMapping.Map(2)],
                    logicalClear[exportMapping.Map(3)]);
                var image = GetOrCreateGuestImage(
                    targetDescriptor,
                    targetFormats[index].Format);
                if (TakeGuestImageInitialData(targetDescriptor.Address) is { } initialData &&
                    !image.Initialized &&
                    (ulong)initialData.Length ==
                        (ulong)image.Width * image.Height * 4)
                {
                    UploadGuestImageInitialData(image, initialData);
                }

                var toTransferDst = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask = image.Initialized ? AccessFlags.ShaderReadBit : 0,
                    DstAccessMask = AccessFlags.TransferWriteBit,
                    OldLayout = image.Initialized
                        ? ImageLayout.ShaderReadOnlyOptimal
                        : ImageLayout.Undefined,
                    NewLayout = ImageLayout.TransferDstOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = image.Image,
                    SubresourceRange = ColorSubresourceRange(0, image.MipLevels),
                };
                _vk.CmdPipelineBarrier(
                    commandBuffer,
                    image.Initialized
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

                var range = ColorSubresourceRange(0, image.MipLevels);
                _vk.CmdClearColorImage(
                    commandBuffer,
                    image.Image,
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
                    Image = image.Image,
                    SubresourceRange = ColorSubresourceRange(0, image.MipLevels),
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
                image.Initialized = true;

                var guestTextureFormat = GetGuestTextureFormat(
                    targetDescriptor.Format,
                    targetDescriptor.NumberType);
                if (guestTextureFormat != 0)
                {
                    lock (_gate)
                    {
                        _availableGuestImages[image.Address] = guestTextureFormat;
                    }
                }
            }

            if (_traceVulkanShaderEnabled)
            {
                TraceVulkanShader(
                    $"vk.offscreen_color_clear mrt={work.Targets.Count} " +
                    $"ps=0x{work.ShaderAddress:X16} " +
                    $"rgba=({work.Red:0.###},{work.Green:0.###},{work.Blue:0.###},{work.Alpha:0.###})");
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
                    UploadGuestImageInitialData(target, pixels, work.RowOffset);
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

        // Returns the source row length in texels when the upload is a linear
        // image whose rows are padded to a wider hardware pitch, or 0 when the
        // data is an exact tightly packed match or not a recognisable padded
        // layout (in which case the caller keeps rejecting it). Only a pitch
        // equal to the width rounded up to a common alignment is accepted, so an
        // oversized buffer that still carries mip data is left rejected rather
        // than mis-copied.
        private static uint TryGetPaddedUploadRowLength(
            GuestImageResource target,
            ulong uploadByteCount,
            ulong expectedByteCount)
        {
            if (expectedByteCount == 0
                || target.Width == 0
                || target.Height == 0
                || uploadByteCount <= expectedByteCount)
            {
                return 0;
            }

            var rowsPerVolume = checked((ulong)target.Height * target.Depth);
            var texelCount = checked((ulong)target.Width * rowsPerVolume);
            if (texelCount == 0 || expectedByteCount % texelCount != 0)
            {
                // Block-compressed or otherwise non-linear: no per-texel pitch.
                return 0;
            }

            var bytesPerTexel = expectedByteCount / texelCount;
            if (bytesPerTexel == 0 || uploadByteCount % rowsPerVolume != 0)
            {
                return 0;
            }

            var rowBytes = uploadByteCount / rowsPerVolume;
            if (rowBytes % bytesPerTexel != 0)
            {
                return 0;
            }

            var rowTexels = rowBytes / bytesPerTexel;
            if (rowTexels < target.Width || rowTexels > uint.MaxValue)
            {
                return 0;
            }

            foreach (var alignment in (ReadOnlySpan<uint>)[8, 16, 32, 64, 128, 256])
            {
                if (AlignUp(target.Width, alignment) == rowTexels)
                {
                    return (uint)rowTexels;
                }
            }

            return 0;
        }

        private static uint AlignUp(uint value, uint alignment) =>
            (value + alignment - 1) / alignment * alignment;




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







        private void WaitForRenderWork()
        {
            using var profileScope = RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.Idle);
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
            finally
            {
                // EndFrame clears a completed capture. DiscardFrame only acts
                // when this render attempt ended before it presented a frame.
                RenderDocCapture.DiscardFrame();
            }
        }

        private void RenderCore()
        {
            RenderDocCapture.DiscardTimedOutFrame();
            RenderDocCapture.BeginFrame();

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
            bool frameSlotReady;
            using (RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.FrameSlotWait))
            {
                frameSlotReady = TryWaitFrameSlot(frameSlot, _frameSlotWaitBudgetNs);
            }

            if (!frameSlotReady)
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
                using var collectScope = RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.Collect);
                CollectCompletedGuestSubmissions(waitForOldest: false);
            }
            using (RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.Evict))
            {
                DrainGuestImageCpuSync();
            }

            var completedWork = 0;
            HashSet<string>? deferredOrderedQueues = null;
            var drainStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            var renderWorkDeadline = _renderWorkBudgetTicks > 0
                ? drainStartTicks + _renderWorkBudgetTicks
                : long.MaxValue;
            var followupDeadline = _guestWorkFollowupBudgetTicks > 0
                ? drainStartTicks + _guestWorkFollowupBudgetTicks
                : long.MinValue;
            var workLimit = _maxGuestWorkPerRender;
            // Prefer ordered sync / flip heads while the queue is elevated so
            // label wakeups are not starved behind fat compute/draw items on
            // sibling logical queues.
            var preferSyncWork =
                _pendingSyncGuestWorkCount >= (_maxPendingGuestWorkItems / 2) ||
                _pendingGuestWorkCount >= (_maxPendingGuestWorkItems / 2);
            while (completedWork < workLimit)
            {
                // Never block the macOS main thread waiting for in-flight GPU
                // work to drain. If submission is at capacity (a slow-compute
                // backlog), stop processing and let the event pump run; the
                // remaining queued work is picked up on later frames as the GPU
                // completions free up capacity (collected non-blockingly here).
                using (RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.Collect))
                {
                    CollectCompletedGuestSubmissions(waitForOldest: false);
                }

                if (OperatingSystem.IsMacOS() &&
                    _pendingGuestSubmissions.Count >= MaxInFlightGuestSubmissions)
                {
                    break;
                }

                PendingGuestWork pendingGuestWork;
                bool tookGuestWork;
                using (RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.TakeWork))
                {
                    tookGuestWork = TryTakeGuestWork(
                        out pendingGuestWork,
                        deferredOrderedQueues,
                        preferSyncWork);
                }

                if (!tookGuestWork)
                {
                    var nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                    if (completedWork == 0 ||
                        _guestWorkFollowupWaitMs <= 0 ||
                        nowTicks >= followupDeadline ||
                        nowTicks >= renderWorkDeadline ||
                        !WaitForFollowupGuestWork(_guestWorkFollowupWaitMs))
                    {
                        break;
                    }

                    continue;
                }

                if (!string.Equals(
                        _activeGuestQueue.Name,
                        pendingGuestWork.Queue.Name,
                        StringComparison.Ordinal))
                {
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
                using (RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.Describe))
                {
                    _activeGuestWorkLabel = DescribeGuestWork(
                        work,
                        pendingGuestWork.Queue,
                        pendingGuestWork.Sequence);
                }
                _lastGuestWorkLabel = _activeGuestWorkLabel;
                var deferGuestWork = false;
                var deferForFlipCapture = false;

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

                if (_traceOrderedActionLatency && work is VulkanOrderedGuestAction orderedActionForLatency)
                {
                    Console.Error.WriteLine(
                        $"[LOADER][WARN] vk.ordered_action_latency #{completedWork} " +
                        $"name='{orderedActionForLatency.DebugName}' " +
                        $"queue={pendingGuestWork.Queue.Name} " +
                        $"queued_ms={(System.Diagnostics.Stopwatch.GetTimestamp() - pendingGuestWork.EnqueuedTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F3} " +
                        $"pending={_pendingGuestWorkCount}");
                }
                try
                {

                    switch (work)
                    {
                        case VulkanOffscreenGuestDraw offscreenDraw:
                            using (RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.Draw))
                            {
                                ExecuteOffscreenDraw(offscreenDraw);
                            }

                            break;
                        case VulkanOffscreenColorClear colorClear:
                            using (RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.ColorClear))
                            {
                                ExecuteOffscreenColorClear(colorClear);
                            }

                            break;
                        case VulkanComputeGuestDispatch computeDispatch:
                            using (RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.Compute))
                            {
                                ExecuteComputeDispatch(computeDispatch);
                            }

                            break;
                        case VulkanGuestImageWrite guestImageWrite:
                            using (RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.ImageWrite))
                            {
                                ExecuteGuestImageWrite(guestImageWrite);
                            }

                            break;
                        case VulkanOrderedGuestAction orderedAction:
                            using (RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.OrderedAction))
                            {
                                deferGuestWork = !TryExecuteOrderedGuestAction(orderedAction);
                            }

                            break;
                        case VulkanGuestCacheOperation cacheOperation:
                            using (RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.OrderedAction))
                            {
                                ExecuteGuestCacheOperation(cacheOperation);
                            }

                            break;
                        case VulkanGpuLabelSignal gpuLabelSignal:
                            ExecuteGpuLabelSignal(gpuLabelSignal);
                            break;
                        case VulkanOrderedGuestFlip orderedFlip:
                            using (RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.Flip))
                            {
                                ExecuteOrderedGuestFlip(orderedFlip);
                            }

                            break;
                        case VulkanOrderedGuestFlipWait flipWait:
                            using (RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.Flip))
                            {
                                deferGuestWork = !TryExecuteOrderedGuestFlipWait(flipWait);
                                deferForFlipCapture = deferGuestWork;
                            }

                            break;
                    }
                }
                finally
                {
                    using (RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.CompleteWork))
                    {
                        if (!deferGuestWork || !RequeueGuestWorkFront(pendingGuestWork))
                        {
                            CompleteGuestWork(pendingGuestWork);
                        }
                    }

                    _enqueueAsImmediateQueueFollowup = false;
                    _immediateFollowupTail = null;
                    _activeGuestWorkLabel = string.Empty;
                    Volatile.Write(ref _executingGuestWorkSequence, 0);
                }

                if (deferGuestWork)
                {
                    // A flip can be on a different guest queue. Exclude the
                    // waiting queue so that the capture queue can progress.
                    if (deferForFlipCapture)
                    {
                        deferredOrderedQueues ??= new HashSet<string>(StringComparer.Ordinal);
                        deferredOrderedQueues.Add(pendingGuestWork.Queue.Name);
                        continue;
                    }

                    // macOS: non-blocking defer — exclude this logical queue for
                    // the rest of the tick so sibling queues can still progress.
                    // Windows/Linux already blocked in WaitForFences; excluding
                    // the only busy queue ends the drain immediately and leaves
                    // OrderedGuestAction stacked. Leave the item at the front and
                    // end this Render; the next tick retries after GPU progress.
                    if (OperatingSystem.IsMacOS())
                    {
                        deferredOrderedQueues ??= new HashSet<string>(StringComparer.Ordinal);
                        deferredOrderedQueues.Add(pendingGuestWork.Queue.Name);
                    }
                    else
                    {
                        break;
                    }
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

            using (RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.Flush))
            {
                FlushBatchedGuestCommands();
            }

            CollectAbandonedGuestImageVersions();

            Presentation presentation;
            if (_window.IsMinimized)
            {
                return;
            }

            var framebufferSize = GetFramebufferSize();
            var drawableSizeChanged =
                (uint)Math.Max(framebufferSize.X, 1) != _extent.Width ||
                (uint)Math.Max(framebufferSize.Y, 1) != _extent.Height;
            var hdrStateChanged = _window.ConsumeHdrStateChange();
            var guestHdrRequestChanged =
                _videoOptions.HdrMode == HostHdrMode.Auto &&
                _hdrRequestedForSwapchain !=
                    (_window.HdrState.Enabled && VideoOutExports.IsHdrOutputRequested);
            if (_window.ConsumeSurfaceRestore() ||
                drawableSizeChanged ||
                _swapchainRecreateDeferred ||
                hdrStateChanged && _videoOptions.HdrMode != HostHdrMode.Off ||
                guestHdrRequestChanged)
            {
                RecreateSwapchainResources(
                    guestHdrRequestChanged
                        ? "guest HDR output change"
                        : hdrStateChanged
                            ? "SDL HDR state change"
                            : drawableSizeChanged
                                ? "SDL drawable resize"
                            : "restored SDL window",
                    Result.SuboptimalKhr);
                return;
            }

            bool tookPresentation;
            using (RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.TakePresentation))
            {
                tookPresentation = TryTakePresentation(_presentedSequence, out presentation);
            }

            if (!tookPresentation &&
                TryTakeHostMovieOnlyPresentation(_presentedSequence, out presentation))
            {
                tookPresentation = true;
            }

            if (!tookPresentation)
            {
                // A render-loop tick with no newer flip is normal. Warn only when
                // an actual queued presentation is waiting on unfinished guest work.
                if (SharpEmu.Libs.Diagnostics.LoadProgressDiagnostics.IsActive ||
                    ShouldTracePresentedGuestImageContentsForDiagnostics())
                {
                    var hasPendingPresentation =
                        HasPendingGuestPresentation(_presentedSequence);
                    SharpEmu.Libs.Diagnostics.LoadProgressDiagnostics.TracePresentNotTaken(
                        _presentedSequence,
                        hasPendingPresentation);
                    SharpEmu.Libs.Diagnostics.LoadProgressDiagnostics.TraceGpuWaitSnapshot();
                    if (ShouldTracePresentedGuestImageContentsForDiagnostics() &&
                        hasPendingPresentation &&
                        _presentNotTakenLoggedSequence != _presentedSequence)
                    {
                        _presentNotTakenLoggedSequence = _presentedSequence;
                        Console.Error.WriteLine(
                            $"[LOADER][WARN] vk.present_not_taken seq={_presentedSequence} " +
                            "— presentation submitted but its required guest work isn't complete; nothing shown.");
                    }
                }

                return;
            }

            SharpEmu.Libs.Diagnostics.LoadProgressDiagnostics.TracePresentTaken(
                presentation.Sequence,
                presentation.GuestImageAddress,
                presentation.GuestImageVersion);
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
                _presentedSequence = presentation.Sequence;
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
                    _presentedSequence = presentation.Sequence;
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

                _presentedSequence = presentation.Sequence;
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
                        [PresentationTargetFormat],
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

                FlushBatchedGuestCommands();
            }

            uint imageIndex;
            Result acquireResult;
            using (RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.Acquire))
            {
                acquireResult = _swapchainApi.AcquireNextImage(
                    _device,
                    _swapchain,
                    SwapchainAcquireTimeoutNs,
                    _frameImageAvailable[frameSlot],
                    default,
                    &imageIndex);
            }
            if (acquireResult == Result.Timeout)
            {
                ReleaseUnsubmittedPresentationResources(
                    frameSlot,
                    translatedResources,
                    ownsPresentedGuestImageVersion,
                    presentedGuestImage);
                return;
            }

            if (acquireResult == Result.ErrorOutOfDateKhr)
            {
                RecreateSwapchainResources("vkAcquireNextImageKHR", acquireResult);
                ReleaseUnsubmittedPresentationResources(
                    frameSlot,
                    translatedResources,
                    ownsPresentedGuestImageVersion,
                    presentedGuestImage);

                _presentedSequence = presentation.Sequence;
                return;
            }

            CheckSwapchainResult(acquireResult, "vkAcquireNextImageKHR");
            var recreateAfterPresent = acquireResult == Result.SuboptimalKhr;

            if (pixels is not null)
            {
                var mapped = (void*)_frameUploadMapped[frameSlot];
                fixed (byte* source = pixels)
                {
                    System.Buffer.MemoryCopy(source, mapped, pixels.Length, pixels.Length);
                }
            }

            using var presentScope = RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.Present);
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
                RecordUpload(imageIndex, frameSlot);
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

            if (_hdrOutputActive)
            {
                RecordHdrPresentation(imageIndex, presentation.IsHdr);
                waitStage = PipelineStageFlags.ColorAttachmentOutputBit;
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
            using (RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.QueueSubmit))
            {
                Check(
                    _vk.QueueSubmit(_queue, 1, &submitInfo, _frameFences[frameSlot]),
                    "vkQueueSubmit");
            }

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
            Result presentResult;
            using (RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.QueuePresent))
            {
                presentResult = _swapchainApi.QueuePresent(_queue, &presentInfo);
            }
            RenderDocCapture.EndFrame();

            if (presentResult == Result.ErrorOutOfDateKhr)
            {
                // The submitted frame still executes; RecreateSwapchainResources
                // drains it (and every frame slot) before destroying anything.
                RecreateSwapchainResources("vkQueuePresentKHR", presentResult);
                _presentedSequence = presentation.Sequence;
                return;
            }

            CheckSwapchainResult(presentResult, "vkQueuePresentKHR");
            recreateAfterPresent |= presentResult == Result.SuboptimalKhr;
            VideoOutExports.ReportPresentedFrame();
            PerfOverlay.RecordPresent();
            RenderPhaseProfile.RecordFrame();
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
            MarkGlobalBufferShaderWrites(resources);
            RecordStorageImagesForRead(resources, PipelineStageFlags.FragmentShaderBit);
            EndDebugLabel(_commandBuffer);
        }




        private void RecordGuestImageForSampling(
            GuestImageResource guestImage,
            PipelineStageFlags shaderStage)
        {
            if (guestImage.Initialized || guestImage.InitialUploadPending)
            {
                return;
            }

            var range = ColorSubresourceRange(0, Math.Max(guestImage.MipLevels, 1));
            var toTransfer = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                DstAccessMask = AccessFlags.TransferWriteBit,
                OldLayout = ImageLayout.Undefined,
                NewLayout = ImageLayout.TransferDstOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = guestImage.Image,
                SubresourceRange = range,
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

            var clearValue = new ClearColorValue(0f, 0f, 0f, 0f);
            _vk.CmdClearColorImage(
                _commandBuffer,
                guestImage.Image,
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
                Image = guestImage.Image,
                SubresourceRange = range,
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

            guestImage.Initialized = true;
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
                    RecordFeedbackSnapshotCopy(
                        source.Format,
                        source.Width,
                        source.Height);
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
                    PipelineStageFlags.AllCommandsBit,
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
                        if (texture.WriteGeneration >= 0)
                        {
                            _cpuBackedUploadGenerations[guestImage.Address] =
                                texture.WriteGeneration;
                        }
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
        private static readonly double _renderResolutionScale =
            double.TryParse(
                Environment.GetEnvironmentVariable("SHARPEMU_RENDER_SCALE"),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var renderResolutionScale) &&
            renderResolutionScale > 0 &&
            renderResolutionScale <= 2.0
                ? renderResolutionScale
                : 1.0;

        private static uint ScaleGuestDimension(uint value) =>
            _renderResolutionScale == 1.0
                ? value
                : Math.Max(1u, (uint)Math.Round(value * _renderResolutionScale));

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

        /// <summary>
        /// Decodes the CB CLEAR_WORD0/1 pair into a float RGBA clear value
        /// according to the surface pixel format.  CLEAR_WORD holds the clear
        /// colour packed in the surface's native layout, so the two 32-bit
        /// words must be unpacked channel-by-channel; passing the raw word as
        /// a single float channel clears to a garbage colour.
        /// </summary>
        private static ClearColorValue UnpackMetaClearValue(
            uint format, uint cw0, uint cw1)
        {
            switch (format)
            {
                // Gen5 8_8_8_8 (R8G8B8A8): four UNORM bytes packed in WORD0,
                // little-endian channel order R,G,B,A.
                case Agc.AgcExports.Gen5TextureFormatR8G8B8A8Unorm:
                    return new ClearColorValue(
                        float32_0: ((cw0 >> 0) & 0xFF) / 255f,
                        float32_1: ((cw0 >> 8) & 0xFF) / 255f,
                        float32_2: ((cw0 >> 16) & 0xFF) / 255f,
                        float32_3: ((cw0 >> 24) & 0xFF) / 255f);

                // Gen5 16_16_16_16 float (R16G16B16A16F): R,G as halfs in
                // WORD0 and B,A as halfs in WORD1.
                case Agc.AgcExports.Gen5TextureFormatR16G16B16A16Float:
                    return new ClearColorValue(
                        float32_0: HalfToFloat((ushort)(cw0 >> 0)),
                        float32_1: HalfToFloat((ushort)(cw0 >> 16)),
                        float32_2: HalfToFloat((ushort)(cw1 >> 0)),
                        float32_3: HalfToFloat((ushort)(cw1 >> 16)));

                default:
                    // Unknown format: fall back to the common 8_8_8_8 layout.
                    return new ClearColorValue(
                        float32_0: ((cw0 >> 0) & 0xFF) / 255f,
                        float32_1: ((cw0 >> 8) & 0xFF) / 255f,
                        float32_2: ((cw0 >> 16) & 0xFF) / 255f,
                        float32_3: ((cw0 >> 24) & 0xFF) / 255f);
            }
        }

        private static float HalfToFloat(ushort halfBits) =>
            (float)BitConverter.UInt16BitsToHalf(halfBits);

        private void BeginTranslatedRenderPass(
            RenderPass renderPass,
            Framebuffer framebuffer,
            Extent2D extent,
            int colorAttachmentCount = 1,
            bool hasDepthAttachment = false,
            float clearDepth = 1f,
            ClearColorValue[]? colorClearValues = null)
        {
            colorAttachmentCount = Math.Max(colorAttachmentCount, 1);
            var clearValueCount = colorAttachmentCount + (hasDepthAttachment ? 1 : 0);
            var clearValues = stackalloc ClearValue[clearValueCount];
            for (var index = 0; index < colorAttachmentCount; index++)
            {
                clearValues[index] = colorClearValues is not null &&
                    index < colorClearValues.Length
                        ? new ClearValue { Color = colorClearValues[index] }
                        : default;
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
            // CB_BLEND_RED..ALPHA feed the CONSTANT_COLOR/CONSTANT_ALPHA factors.
            var blendConstants = stackalloc float[4]
            {
                resources.BlendConstant.Red,
                resources.BlendConstant.Green,
                resources.BlendConstant.Blue,
                resources.BlendConstant.Alpha,
            };
            _vk.CmdSetBlendConstants(_commandBuffer, blendConstants);
            if (resources.VertexBuffers.Length != 0)
            {
                var buffers = stackalloc VkBuffer[resources.VertexBuffers.Length];
                var offsets = stackalloc ulong[resources.VertexBuffers.Length];
                var handles = stackalloc ulong[resources.VertexBuffers.Length];
                var perInstance = stackalloc bool[resources.VertexBuffers.Length];
                var sourceIndices = stackalloc int[resources.VertexBuffers.Length];
                for (var index = 0; index < resources.VertexBuffers.Length; index++)
                {
                    handles[index] = resources.VertexBuffers[index].Buffer.Handle;
                    perInstance[index] = resources.VertexBuffers[index].PerInstance;
                }

                var bindingCount = VulkanVertexBindingPlanner.BuildUniqueSourceIndices(
                    new ReadOnlySpan<ulong>(handles, resources.VertexBuffers.Length),
                    new ReadOnlySpan<bool>(perInstance, resources.VertexBuffers.Length),
                    new Span<int>(sourceIndices, resources.VertexBuffers.Length));
                for (var bindingIndex = 0; bindingIndex < bindingCount; bindingIndex++)
                {
                    var sourceIndex = sourceIndices[bindingIndex];
                    buffers[bindingIndex] = resources.VertexBuffers[sourceIndex].Buffer;
                    // The pipeline attribute already contains OffsetBytes.
                    offsets[bindingIndex] = 0;
                }

                _vk.CmdBindVertexBuffers(
                    _commandBuffer,
                    0,
                    (uint)bindingCount,
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
                    // vertexOffset = ResolveVertexOffset(GE_INDX_OFFSET).
                    // GTA UI glyphs use relative indices + a nonzero base vertex.
                    _vk.CmdDrawIndexed(
                        _commandBuffer,
                        resources.VertexCount,
                        resources.InstanceCount,
                        0,
                        resources.BaseVertex,
                        0);
                }
                else
                {
                    _vk.CmdDraw(
                        _commandBuffer,
                        resources.VertexCount,
                        resources.InstanceCount,
                        (uint)Math.Max(resources.BaseVertex, 0),
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

                if (texture.FeedbackSnapshotKey is not null)
                {
                    RetireFeedbackSnapshot(texture);
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
                    RecycleHostBuffer(texture.StagingBuffer, texture.StagingMemory);
                }

                if (texture.NeedsUpload &&
                    texture.GuestImage is { Initialized: false } guestImage)
                {
                    guestImage.InitialUploadPending = false;
                }
            }

            foreach (var (buffer, memory) in resources.DeferredTextureStagingBuffers)
            {
                if (buffer.Handle != 0)
                {
                    RecycleHostBuffer(buffer, memory);
                }
            }
            resources.DeferredTextureStagingBuffers.Clear();

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
                var work = !string.IsNullOrEmpty(_activeGuestWorkLabel)
                    ? $"work={_activeGuestWorkLabel}"
                    : !string.IsNullOrEmpty(_lastGuestWorkLabel)
                        ? $"last_work={_lastGuestWorkLabel}"
                        : "work=<none>";
                var submit = string.IsNullOrEmpty(_lastSubmitDebugName)
                    ? string.Empty
                    : $" last_submit={_lastSubmitDebugName}";
                Console.Error.WriteLine(
                    "[LOADER][ERROR] Vulkan device lost; dropping subsequent guest GPU work. " +
                    $"{work}{submit} {exception.Message}");
            }

            return true;
        }

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
