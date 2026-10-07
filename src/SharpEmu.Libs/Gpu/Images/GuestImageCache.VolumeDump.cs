// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Vulkan;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Images;

public sealed unsafe partial class GuestImageCache
{
    private static readonly bool VolumeUploadDumpsEnabled =
        Environment.GetEnvironmentVariable("SHARPEMU_DUMP_VOLUME_UPLOADS") == "1";
    private static readonly string? VolumeUploadExtent =
        Environment.GetEnvironmentVariable("SHARPEMU_DUMP_VOLUME_UPLOAD_EXTENT");
    private readonly Dictionary<ulong, int> _volumeUploadCounts = new();
    private readonly string _volumeDumpSession = Guid.NewGuid().ToString("N");

    private bool CanDumpVolumeUpload(ulong address) =>
        _volumeUploadCounts.TryGetValue(address, out var count) ? count < 4 : _volumeUploadCounts.Count < 8;

    internal static bool MatchesVolumeUploadExtent(string? filter, uint width, uint height, uint depth)
    {
        if (string.IsNullOrWhiteSpace(filter) || filter.Trim() == "0")
            return width <= 64 && height <= 64 && depth <= 64;
        var dimensions = filter.Split('x', StringSplitOptions.TrimEntries);
        if (dimensions.Length == 1 && uint.TryParse(dimensions[0], out var extent))
            return extent > 0 && extent <= 64 && width == extent && height == extent && depth == extent;
        return dimensions.Length == 3 &&
            uint.TryParse(dimensions[0], out var selectedWidth) && selectedWidth > 0 &&
            uint.TryParse(dimensions[1], out var selectedHeight) && selectedHeight > 0 &&
            uint.TryParse(dimensions[2], out var selectedDepth) && selectedDepth > 0 &&
            width == selectedWidth && height == selectedHeight && depth == selectedDepth;
    }

    private byte[]? SnapshotVolumeBacking(CachedImage image)
    {
        var description = image.Description;
        if (!VolumeUploadDumpsEnabled || !description.IsVolume ||
            !CanDumpVolumeUpload(description.Data.Address) ||
            description.Data.Size > 4 * 1024 * 1024 ||
            !MatchesVolumeUploadExtent(VolumeUploadExtent, description.Extent.Width,
                description.Extent.Height, description.Extent.Depth)) return null;
        var bytes = new byte[(int)description.Data.Size];
        return _backing.TryReadBacking(description.Data.Address, bytes) ? bytes : null;
    }

    private void DumpVolumeUpload(CachedImage image, GpuBuffer source, ulong sourceOffset,
        TilerBufferSpan linear, ColorTransferPlan plan, byte[]? backingBytes)
    {
        var description = image.Description;
        if (!VolumeUploadDumpsEnabled || !description.IsVolume ||
            !MatchesVolumeUploadExtent(VolumeUploadExtent, description.Extent.Width,
                description.Extent.Height, description.Extent.Depth) ||
            description.Data.Size > 4 * 1024 * 1024 || linear.Size > 4 * 1024 * 1024 ||
            !CanDumpVolumeUpload(description.Data.Address)) return;

        _volumeUploadCounts.TryGetValue(description.Data.Address, out var previousCount);
        var uploadNumber = previousCount + 1;
        _volumeUploadCounts[description.Data.Address] = uploadNumber;

        var sourceSize = description.Data.Size;
        var totalSize = sourceSize + linear.Size;
        var download = new GpuBuffer(_device, _scheduler, GpuBufferUsage.Download, 0,
            BufferUsageFlags.TransferDstBit, totalSize);
        var directory = Path.Combine(AppContext.BaseDirectory, "user", "logs", "volume-uploads", _volumeDumpSession);
        var stem = $"{description.Data.Address:X16}-{_scheduler.CurrentTick}-{uploadNumber}";
        var manifest = System.Text.Json.JsonSerializer.Serialize(new
        {
            Address = $"0x{description.Data.Address:X16}", UploadNumber = uploadNumber,
            SubmissionTick = _scheduler.CurrentTick, SourceSize = sourceSize,
            LinearSize = linear.Size, Format = description.PixelFormat.ToString(),
            BackingReadable = backingBytes != null, SourceUsage = source.Usage.ToString(),
            TileMode = description.TileMode.ToString(), description.Extent.Width,
            description.Extent.Height, description.Extent.Depth, description.Pitch,
            Tiles = plan.Tiles.Select(tile => new
            {
                Kind = tile.Kind.ToString(), tile.BytesPerElement, tile.TiledOffset,
                tile.LinearOffset, tile.LinearSize, tile.TiledSize, tile.LinearSliceStride,
                tile.Width, tile.Height, tile.Depth, tile.Pitch, tile.SurfaceZ,
                tile.TiledWidth, tile.TiledHeight, tile.Tail, tile.TailX, tile.TailY,
            }).ToArray(),
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

        _scheduler.EndRendering();
        var command = new CommandBuffer(_scheduler.Current.Handle);
        var barrier = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcAccessMask = AccessFlags2.MemoryWriteBit,
            DstAccessMask = AccessFlags2.TransferReadBit,
        };
        VulkanSynchronization.PipelineBarrier(_device.Vk, command, PipelineStageFlags.AllCommandsBit,
            PipelineStageFlags.TransferBit, 0, 1, &barrier, 0, null, 0, null);
        var sourceCopy = new BufferCopy(sourceOffset, 0, sourceSize);
        _device.Vk.CmdCopyBuffer(command, source.Handle, download.Handle, 1, &sourceCopy);
        var linearCopy = new BufferCopy(linear.Offset, sourceSize, linear.Size);
        _device.Vk.CmdCopyBuffer(command, linear.Buffer, download.Handle, 1, &linearCopy);
        barrier.SrcAccessMask = AccessFlags2.TransferReadBit | AccessFlags2.TransferWriteBit;
        barrier.DstAccessMask = AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit | AccessFlags2.HostReadBit;
        VulkanSynchronization.PipelineBarrier(_device.Vk, command, PipelineStageFlags.TransferBit,
            PipelineStageFlags.AllCommandsBit | PipelineStageFlags.HostBit, 0, 1, &barrier, 0, null, 0, null);
        // Keep the private readback alive until both GPU copies complete.
        _scheduler.QueueCompletionAction(() =>
        {
            try
            {
                download.Invalidate(0, totalSize);
                Directory.CreateDirectory(directory);
                if (backingBytes != null)
                    File.WriteAllBytes(Path.Combine(directory, stem + ".backing.bin"), backingBytes);
                File.WriteAllBytes(Path.Combine(directory, stem + ".tiled.bin"), download.Mapped[..(int)sourceSize]);
                File.WriteAllBytes(Path.Combine(directory, stem + ".linear.bin"), download.Mapped.Slice((int)sourceSize, (int)linear.Size));
                File.WriteAllText(Path.Combine(directory, stem + ".json"), manifest);
                Console.Error.WriteLine($"[GPU][TRACE] VolumeUpload saved={Path.Combine(directory, stem)}");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"[GPU][WARN] Volume upload dump failed: {exception.Message}");
            }
            finally
            {
                download.Dispose();
            }
        });
    }
}
