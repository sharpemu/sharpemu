// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Images;

public sealed partial class GuestImageCache
{
    private readonly RecentImageTrace _volumeTraceStates = new(2048);

    private void TraceVolumeState(string operation, CachedImage image, in ImageRequest request, string detail = "")
    {
        RecordResourceHistory(operation, image.Description.Data.Address, image.Description.Data.Size,
            image.Description.PixelFormat, image.Description.Extent, request.Role);
        if (!ImageClearTrace.Enabled || (!IsTracedImage(image.Description) && !IsTracedImage(request.Description))) return;
        ref readonly var description = ref image.Description;
        var message = $"operation={operation} image=0x{description.Data.Address:X16} size=0x{description.Data.Size:X} " +
            $"host=0x{image.Backing.Handle.Handle:X} format={description.PixelFormat} " +
            $"extent={description.Extent.Width}x{description.Extent.Height}x{description.Extent.Depth} " +
            $"pitch={description.Pitch} tile={description.TileMode} levels={description.Resources.Levels} " +
            $"request=0x{request.Description.Data.Address:X16} role={request.Role} view={request.View.Type} " +
            $"viewFormat={request.View.Format} mip={request.View.BaseLevel} layer={request.View.BaseLayer} layers={request.View.LayerCount} " +
            $"cpuDirty={image.IsCpuDirty} bufferDirty={image.IsBufferModified} gpuDirty={image.IsGpuModified} " +
            $"lastCpuWrite=0x{image.LastCpuWriteAddress:X16}+0x{image.LastCpuWriteSize:X} {detail}";
        _volumeTraceStates.Record(message, $"[GPU][TRACE] VolumeImage time={DateTime.UtcNow:O} tick={_scheduler.CurrentTick} {message}");
    }
}
