// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using SharpEmu.Libs.Gpu;
using Silk.NET.Vulkan;

internal enum VulkanFeedbackSnapshotKind
{
    Color,
    Depth,
}

internal readonly record struct VulkanFeedbackSnapshotKey(
    VulkanFeedbackSnapshotKind Kind,
    Format ImageFormat,
    Format ViewFormat,
    uint Width,
    uint Height,
    uint MipLevels,
    uint DstSelect);

internal readonly record struct VulkanFeedbackSnapshotStatistics(
    long Created,
    long ColorCreated,
    long DepthCreated,
    long Allocations,
    ulong AllocatedBytes,
    long PoolHits,
    long Copies,
    ulong CopiedBytes,
    long Retired,
    long PoolReturns,
    long Destroyed,
    long Live,
    ulong LiveBytes,
    long PeakLive,
    ulong PeakLiveBytes);

internal sealed class VulkanFeedbackSnapshotTelemetry
{
    private long _created;
    private long _colorCreated;
    private long _depthCreated;
    private long _allocations;
    private ulong _allocatedBytes;
    private long _poolHits;
    private long _copies;
    private ulong _copiedBytes;
    private long _retired;
    private long _poolReturns;
    private long _destroyed;
    private long _live;
    private ulong _liveBytes;
    private long _peakLive;
    private ulong _peakLiveBytes;

    internal bool RecordAcquired(
        VulkanFeedbackSnapshotKind kind,
        ulong allocationBytes,
        bool allocated)
    {
        _created++;
        if (kind == VulkanFeedbackSnapshotKind.Color)
        {
            _colorCreated++;
        }
        else
        {
            _depthCreated++;
        }

        if (allocated)
        {
            _allocations++;
            _allocatedBytes = checked(_allocatedBytes + allocationBytes);
        }
        else
        {
            _poolHits++;
        }
        _live++;
        _liveBytes = checked(_liveBytes + allocationBytes);
        _peakLive = Math.Max(_peakLive, _live);
        _peakLiveBytes = Math.Max(_peakLiveBytes, _liveBytes);
        return ShouldReport(_created);
    }

    internal void RecordCopy(ulong copiedBytes)
    {
        _copies++;
        _copiedBytes = checked(_copiedBytes + copiedBytes);
    }

    internal void RecordRetired(ulong allocationBytes, bool returnedToPool)
    {
        _retired++;
        if (returnedToPool)
        {
            _poolReturns++;
        }
        else
        {
            _destroyed++;
        }
        _live = Math.Max(_live - 1, 0);
        _liveBytes = allocationBytes >= _liveBytes
            ? 0
            : _liveBytes - allocationBytes;
    }

    internal VulkanFeedbackSnapshotStatistics GetStatistics() =>
        new(
            _created,
            _colorCreated,
            _depthCreated,
            _allocations,
            _allocatedBytes,
            _poolHits,
            _copies,
            _copiedBytes,
            _retired,
            _poolReturns,
            _destroyed,
            _live,
            _liveBytes,
            _peakLive,
            _peakLiveBytes);

    internal static bool ShouldReport(long created) =>
        created is > 0 and <= 4 ||
        (created > 0 && (created & (created - 1)) == 0);

    internal void RecordPooledStorageDestroyed()
    {
        _destroyed++;
    }
}

internal static class VulkanFeedbackSnapshotPoolPolicy
{
    internal static bool IsEnabled(string? value) =>
        !string.Equals(value, "0", StringComparison.Ordinal);

    internal static bool CanStore(
        int entriesForKey,
        ulong pooledBytes,
        ulong allocationBytes,
        int maxEntriesPerKey,
        ulong maxBytes) =>
        maxEntriesPerKey > 0 &&
        entriesForKey < maxEntriesPerKey &&
        allocationBytes <= maxBytes &&
        pooledBytes <= maxBytes - allocationBytes;

    internal static ulong ResolveCopyByteCount(
        Format format,
        uint width,
        uint height,
        ulong estimatedBytes) =>
        estimatedBytes != 0
            ? estimatedBytes
            : format switch
            {
                Format.D16Unorm => checked((ulong)width * height * 2),
                Format.D32Sfloat => checked((ulong)width * height * 4),
                _ => 0,
            };
}

internal static class VulkanReadOnlyDepthFeedbackPolicy
{
    internal static bool IsEnabled(string? value) =>
        !string.Equals(value, "0", StringComparison.Ordinal);

    internal static bool CanUse(
        bool featureEnabled,
        bool hasDepthAttachment,
        bool depthInitialized,
        bool depthLayoutSupported,
        bool depthTestEnabled,
        bool depthWriteEnabled,
        bool depthClearEnabled,
        int renderTargetCount,
        bool hasCompatibleSample) =>
        featureEnabled &&
        hasDepthAttachment &&
        depthInitialized &&
        depthLayoutSupported &&
        depthTestEnabled &&
        !depthWriteEnabled &&
        !depthClearEnabled &&
        renderTargetCount == 1 &&
        hasCompatibleSample;
}

internal static unsafe partial class VulkanVideoPresenter
{
    // This partial owns feedback snapshot creation, pooling, and telemetry.

    private static readonly bool _logFeedbackSnapshots =
        string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_LOG_FEEDBACK_SNAPSHOTS"),
            "1",
            StringComparison.Ordinal);

    private static readonly bool _poolFeedbackSnapshots =
        VulkanFeedbackSnapshotPoolPolicy.IsEnabled(
            Environment.GetEnvironmentVariable("SHARPEMU_POOL_FEEDBACK_SNAPSHOTS"));

    private static readonly bool _directReadOnlyDepthFeedback =
        VulkanReadOnlyDepthFeedbackPolicy.IsEnabled(
            Environment.GetEnvironmentVariable("SHARPEMU_DIRECT_READONLY_DEPTH_FEEDBACK"));

    private const int FeedbackSnapshotPoolMaxEntriesPerKey = 2;
    private static readonly ulong _feedbackSnapshotPoolMaxBytes =
        ulong.TryParse(
            Environment.GetEnvironmentVariable("SHARPEMU_FEEDBACK_SNAPSHOT_POOL_MB"),
            out var feedbackPoolMegabytes)
            ? checked(feedbackPoolMegabytes * 1024UL * 1024UL)
            : 256UL * 1024 * 1024;

    private sealed partial class Presenter
    {
        private readonly record struct FeedbackSnapshotStorage(
            Image Image,
            DeviceMemory Memory,
            ImageView View,
            ulong AllocationBytes);

        private readonly VulkanFeedbackSnapshotTelemetry _feedbackSnapshotTelemetry = new();
        private readonly Dictionary<VulkanFeedbackSnapshotKey, Stack<FeedbackSnapshotStorage>>
            _feedbackSnapshotPool = [];
        private int _feedbackSnapshotPoolCount;
        private ulong _feedbackSnapshotPoolBytes;
        private int _feedbackSnapshotPoolPeakCount;
        private ulong _feedbackSnapshotPoolPeakBytes;
        private long _directReadOnlyDepthFeedbackUses;

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

        private bool TryRentFeedbackSnapshot(
            VulkanFeedbackSnapshotKey key,
            out FeedbackSnapshotStorage storage)
        {
            if (!_poolFeedbackSnapshots ||
                !_feedbackSnapshotPool.TryGetValue(key, out var entries) ||
                !entries.TryPop(out storage))
            {
                storage = default;
                return false;
            }

            _feedbackSnapshotPoolCount--;
            _feedbackSnapshotPoolBytes -= storage.AllocationBytes;
            if (entries.Count == 0)
            {
                _feedbackSnapshotPool.Remove(key);
            }

            return true;
        }

        private void RetireFeedbackSnapshot(TextureResource texture)
        {
            var key = texture.FeedbackSnapshotKey!.Value;
            var storage = new FeedbackSnapshotStorage(
                texture.Image,
                texture.ImageMemory,
                texture.View,
                texture.FeedbackAllocationBytes);
            _feedbackSnapshotPool.TryGetValue(key, out var entries);
            var returnToPool =
                _poolFeedbackSnapshots &&
                VulkanFeedbackSnapshotPoolPolicy.CanStore(
                    entries?.Count ?? 0,
                    _feedbackSnapshotPoolBytes,
                    storage.AllocationBytes,
                    FeedbackSnapshotPoolMaxEntriesPerKey,
                    _feedbackSnapshotPoolMaxBytes);
            if (returnToPool)
            {
                entries ??= new Stack<FeedbackSnapshotStorage>();
                entries.Push(storage);
                _feedbackSnapshotPool[key] = entries;
                _feedbackSnapshotPoolCount++;
                _feedbackSnapshotPoolBytes += storage.AllocationBytes;
                _feedbackSnapshotPoolPeakCount = Math.Max(
                    _feedbackSnapshotPoolPeakCount,
                    _feedbackSnapshotPoolCount);
                _feedbackSnapshotPoolPeakBytes = Math.Max(
                    _feedbackSnapshotPoolPeakBytes,
                    _feedbackSnapshotPoolBytes);
            }
            else
            {
                DestroyFeedbackSnapshotStorage(storage);
            }

            RecordFeedbackSnapshotRetired(
                storage.AllocationBytes,
                returnToPool);
        }

        private void DestroyFeedbackSnapshotStorage(FeedbackSnapshotStorage storage)
        {
            if (storage.View.Handle != 0)
            {
                _vk.DestroyImageView(_device, storage.View, null);
            }
            if (storage.Image.Handle != 0)
            {
                _vk.DestroyImage(_device, storage.Image, null);
            }
            if (storage.Memory.Handle != 0)
            {
                _vk.FreeMemory(_device, storage.Memory, null);
            }
        }

        private void DestroyFeedbackSnapshotPool()
        {
            foreach (var entries in _feedbackSnapshotPool.Values)
            {
                while (entries.TryPop(out var storage))
                {
                    DestroyFeedbackSnapshotStorage(storage);
                    if (_logFeedbackSnapshots)
                    {
                        _feedbackSnapshotTelemetry.RecordPooledStorageDestroyed();
                    }
                }
            }

            _feedbackSnapshotPool.Clear();
            _feedbackSnapshotPoolCount = 0;
            _feedbackSnapshotPoolBytes = 0;
        }

        private void RecordFeedbackSnapshotAcquired(
            VulkanFeedbackSnapshotKind kind,
            ulong allocationBytes,
            bool allocated)
        {
            if (!_logFeedbackSnapshots)
            {
                return;
            }

            var shouldReport = _feedbackSnapshotTelemetry.RecordAcquired(
                kind,
                allocationBytes,
                allocated);
            if (shouldReport)
            {
                ReportFeedbackSnapshotTelemetry(final: false);
            }
        }

        private void RecordFeedbackSnapshotCopy(Format format, uint width, uint height)
        {
            if (!_logFeedbackSnapshots)
            {
                return;
            }

            var copiedBytes = VulkanFeedbackSnapshotPoolPolicy.ResolveCopyByteCount(
                format,
                width,
                height,
                GetVulkanImageByteCount(format, width, height));

            _feedbackSnapshotTelemetry.RecordCopy(copiedBytes);
        }

        private void RecordFeedbackSnapshotRetired(
            ulong allocationBytes,
            bool returnedToPool)
        {
            if (!_logFeedbackSnapshots)
            {
                return;
            }

            _feedbackSnapshotTelemetry.RecordRetired(
                allocationBytes,
                returnedToPool);
        }

        private void ReportFeedbackSnapshotTelemetry(bool final)
        {
            if (!_logFeedbackSnapshots)
            {
                return;
            }

            var statistics = _feedbackSnapshotTelemetry.GetStatistics();
            Console.Error.WriteLine(
                $"[LOADER][INFO] vk.feedback_snapshot_telemetry " +
                $"final={(final ? 1 : 0)} created={statistics.Created} " +
                $"color={statistics.ColorCreated} depth={statistics.DepthCreated} " +
                $"allocations={statistics.Allocations} " +
                $"allocated_bytes={statistics.AllocatedBytes} " +
                $"pool_hits={statistics.PoolHits} " +
                $"copies={statistics.Copies} copied_bytes={statistics.CopiedBytes} " +
                $"retired={statistics.Retired} pool_returns={statistics.PoolReturns} " +
                $"destroyed={statistics.Destroyed} live={statistics.Live} " +
                $"live_bytes={statistics.LiveBytes} peak_live={statistics.PeakLive} " +
                $"peak_live_bytes={statistics.PeakLiveBytes} " +
                $"pooled={_feedbackSnapshotPoolCount} " +
                $"pooled_bytes={_feedbackSnapshotPoolBytes} " +
                $"peak_pooled={_feedbackSnapshotPoolPeakCount} " +
                $"peak_pooled_bytes={_feedbackSnapshotPoolPeakBytes} " +
                $"direct_depth={_directReadOnlyDepthFeedbackUses}");
        }

        private void RecordDirectReadOnlyDepthFeedback()
        {
            var count = Interlocked.Increment(
                ref _directReadOnlyDepthFeedbackUses);
            if (_logFeedbackSnapshots &&
                VulkanFeedbackSnapshotTelemetry.ShouldReport(count))
            {
                Console.Error.WriteLine(
                    $"[LOADER][INFO] vk.depth_feedback_direct count={count}");
            }
        }
    }
}
