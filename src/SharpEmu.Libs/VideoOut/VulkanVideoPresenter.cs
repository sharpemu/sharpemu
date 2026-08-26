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
        private readonly HashSet<ulong> _tracedGuestImageContents = new();
        private readonly Dictionary<ulong, int> _tracedGuestWriteCounts = new();
        private readonly Dictionary<int, int> _pixelSpirvWriteCounts = new();
        private readonly Queue<PendingGuestSubmission> _pendingGuestSubmissions = new();
        // Submissions whose fence timed out. Keep GPU objects alive until the
        // fence signals (or the device is lost) so a single hung compute
        // dispatch cannot re-block every subsequent capacity wait for the full
        // fence timeout (~3s → ~0.3 FPS).
        private readonly Queue<PendingGuestSubmission> _abandonedGuestSubmissions = new();
        private readonly Dictionary<string, ulong> _lastSubmittedTimelineByGuestQueue =
            new(StringComparer.Ordinal);
        private VulkanGuestQueueIdentity _activeGuestQueue =
            VulkanGuestQueueIdentity.Default;
        private long _activeGuestWorkSequence;



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
