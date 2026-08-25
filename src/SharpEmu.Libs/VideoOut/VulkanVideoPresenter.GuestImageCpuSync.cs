// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using SharpEmu.Libs.Gpu;
using Silk.NET.Vulkan;

internal static unsafe partial class VulkanVideoPresenter
{
    // This partial owns the lifecycle of CPU-written guest image content.

    // Write-tracker generation last uploaded for a CPU-backed guest image.
    // A newer generation in the tracker means the guest CPU rewrote the
    // memory (video frames, streamed atlases) and the upload-known skip in
    // draw translation must ship fresh texels instead of reusing the image.
    private static readonly Dictionary<ulong, long> _cpuBackedUploadGenerations = new();
    // Sparse guest-memory fingerprints used when the write tracker is off so
    // upload-known can still skip static UI atlases without missing CPU-updated
    // video planes (see IsGuestImageUploadKnown).
    private static readonly Dictionary<ulong, ulong> _untrackedGuestImageContentProbes = new();
    private static readonly Dictionary<ulong, byte[]> _pendingGuestImageInitialData = new();

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

    /// <summary>
    /// Upper bound on the backing extent that guest CPU-write tracking is
    /// armed over. Deliberately equal to the presenter-side re-upload budget
    /// used by the AGC flip/acquire sync path: arming a range larger than the
    /// sync path is willing to read back would fault and dirty forever without
    /// ever producing a re-upload, so it is pure cost.
    /// </summary>
    internal const ulong MaxTrackedGuestImageBytes = 128UL * 1024UL * 1024UL;

    /// <summary>
    /// Decides whether a guest surface is eligible for CPU-write tracking.
    /// The predicate is byte-based on purpose: the cost that actually scales
    /// with surface size is the dirty re-upload (one allocation plus a guest
    /// memory read of the whole extent per dirty flip), not the arming itself
    /// (one mprotect and, per write burst, one fault for the whole range).
    /// A resolution cap was the wrong proxy — it ignored bytes-per-texel and
    /// volume depth while excluding the 4K UI sheets that most need
    /// invalidation.
    /// </summary>
    internal static bool ShouldTrackGuestImageWrites(ulong byteCount) =>
        byteCount != 0 && byteCount <= MaxTrackedGuestImageBytes;

    /// <summary>
    /// Decides whether a sampled guest image whose backing memory the parse
    /// thread just re-read should be re-uploaded from those bytes.
    /// <para>
    /// <paramref name="isCpuBacked"/> is a latch that flips false the first
    /// time an address is used as a render target and never flips back, so it
    /// cannot be the sole gate: a font atlas or UI sheet that was also
    /// rendered into is permanently frozen at its first upload afterwards.
    /// The write tracker answers the real question. A parse-time generation
    /// above zero means a guest CPU store was observed on the backing range,
    /// and a generation the last upload does not already cover means those
    /// bytes are newer than the host image.
    /// </para>
    /// <para>
    /// Requiring a positive generation keeps the pure GPU-feedback case
    /// (render into an image, then sample it) safe: such a surface is tracked
    /// but never CPU-written, so its generation stays zero and the live image
    /// is preserved instead of being overwritten with guest memory.
    /// </para>
    /// </summary>
    internal static bool ShouldRefreshGuestImageFromCpu(
        bool isCpuBacked,
        long textureWriteGeneration,
        bool hasUploadedGeneration,
        long uploadedGeneration) =>
        isCpuBacked ||
        (textureWriteGeneration > 0 &&
            (!hasUploadedGeneration || uploadedGeneration != textureWriteGeneration));

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

    // Records an acquire/flip synchronization request. CPU-written textures
    // are validated at their next guest bind, where AGC supplies correctly
    // decoded pixels. The render loop must not upload raw tiled guest bytes.
    private static int _cpuWrittenGuestImageSyncRequested;

    internal static void RequestCpuWrittenGuestImageSync(
        ulong scopeAddress = 0,
        ulong scopeByteCount = ulong.MaxValue)
    {
        _ = scopeAddress;
        if (scopeByteCount == 0 ||
            !SharpEmu.HLE.GuestImageWriteTracker.Enabled)
        {
            return;
        }

        Volatile.Write(ref _cpuWrittenGuestImageSyncRequested, 1);
    }

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

        ulong probeByteCount = 0;
        lock (_gate)
        {
            var known =
                _availableGuestImages.TryGetValue(address, out var availableFormat) &&
                    availableFormat == guestFormat ||
                _pendingGuestImageUploads.ContainsKey((address, guestFormat));
            if (!known)
            {
                return false;
            }

            // CPU-backed images (video frames, streamed atlases) go stale when
            // the guest CPU rewrites the memory after the recorded upload; a
            // changed write-tracker generation forces a fresh texel copy so
            // the refresh path can re-upload. GPU-rendered images have no
            // generation entry and keep the plain availability answer.
            if (_cpuBackedUploadGenerations.TryGetValue(address, out var uploadedGeneration) &&
                SharpEmu.HLE.GuestImageWriteTracker.TryGetWriteGeneration(
                    address,
                    out var currentGeneration) &&
                currentGeneration != uploadedGeneration)
            {
                return false;
            }

            if (SharpEmu.HLE.GuestImageWriteTracker.Enabled)
            {
                return true;
            }

            if (_guestImageExtents.TryGetValue(address, out var extent))
            {
                probeByteCount = extent.ByteCount;
            }
        }

        // Tracker off: availability never goes generation-stale. A sparse
        // guest-memory probe lets static upload-known textures keep skipping
        // (Dead Cells menus) while CPU-rewritten planes (GTA Bink) fall through
        // to a full texel copy when the probe changes.
        return IsUntrackedGuestImageContentUnchanged(address, probeByteCount);
    }

    private static bool IsUntrackedGuestImageContentUnchanged(ulong address, ulong byteCount)
    {
        var memory = _guestMemory;
        if (memory is null || byteCount == 0)
        {
            // No probe possible — preserve the historical skip so UI stays
            // cheap; video planes normally have extents registered.
            return true;
        }

        var probe = ComputeSparseGuestContentProbe(memory, address, byteCount);
        lock (_gate)
        {
            if (!_untrackedGuestImageContentProbes.TryGetValue(address, out var previous))
            {
                _untrackedGuestImageContentProbes[address] = probe;
                return true;
            }

            if (previous == probe)
            {
                return true;
            }

            _untrackedGuestImageContentProbes[address] = probe;
            return false;
        }
    }

    private static ulong ComputeSparseGuestContentProbe(
        SharpEmu.HLE.ICpuMemory memory,
        ulong address,
        ulong byteCount)
    {
        Span<byte> sample = stackalloc byte[64];
        ulong hash = 14695981039346656037UL;
        Span<ulong> offsets = stackalloc ulong[3];
        var offsetCount = 0;
        offsets[offsetCount++] = 0;
        if (byteCount > 128)
        {
            offsets[offsetCount++] = byteCount / 2;
        }

        if (byteCount > 64)
        {
            offsets[offsetCount++] = byteCount - 64;
        }

        for (var o = 0; o < offsetCount; o++)
        {
            var offset = offsets[o];
            if (offset >= byteCount)
            {
                continue;
            }

            var length = (int)Math.Min(64UL, byteCount - offset);
            if (!memory.TryRead(address + offset, sample[..length]))
            {
                hash ^= 0x9E3779B97F4A7C15UL + offset;
                continue;
            }

            for (var i = 0; i < length; i++)
            {
                hash ^= sample[i];
                hash *= 1099511628211UL;
            }

            hash ^= (ulong)length + offset;
        }

        return hash ^ byteCount;
    }

    private sealed partial class Presenter
    {
        private void TrackSampledTextureSource(GuestDrawTexture texture)
        {
            var sourceByteCount = texture.SourceByteCount != 0
                ? texture.SourceByteCount
                : (ulong)(texture.TiledSource?.Length ?? texture.RgbaPixels.Length);
            SharpEmu.HLE.GuestImageWriteTracker.Track(
                texture.Address,
                sourceByteCount,
                CurrentGuestWorkSequenceForDiagnostics,
                "vulkan.texture-cache",
                protect: SharpEmu.HLE.GuestImageWriteTracker.Enabled);
        }

        /// <summary>
        /// Applies texture-cache capacity maintenance. CPU-written images are
        /// refreshed at their next bind, after AGC has decoded the guest
        /// tiling and format. Reading raw guest bytes here is incorrect for
        /// tiled images and can replace a live image between related draws.
        /// </summary>
        private void DrainGuestImageCpuSync()
        {
            _ = Interlocked.Exchange(ref _cpuWrittenGuestImageSyncRequested, 0);
            if (_textureCache.Count <= 2048)
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
            var watchedAddresses = _textureCache.Keys
                .Select(static key => key.Address)
                .Distinct()
                .ToArray();
            foreach (var entry in _textureCache)
            {
                _deferredTextureDestroys.Enqueue((entry.Value, retireTimeline));
            }

            _textureCache.Clear();
            ClearCachedTextureIdentities();
            foreach (var address in watchedAddresses)
            {
                SharpEmu.HLE.GuestImageWriteTracker.UntrackWatchOnly(address);
            }
        }

        private void UploadGuestImageInitialData(
            GuestImageResource target,
            byte[] pixels,
            uint rowOffset = 0)
        {
            var guestDataFormat = (target.GuestFormat & 0x8000_0000u) != 0
                ? (target.GuestFormat >> 8) & 0x1FFu
                : 0;
            var uploadPixels = guestDataFormat == 13
                ? ExpandRgb32Pixels(pixels)
                : pixels;
            var expectedByteCount = GetVulkanImageByteCount(
                target.Format,
                target.Width,
                target.Height,
                target.Depth);

            // A band upload only makes sense on an image that already holds the
            // rest of the surface. Uninitialized images enter the barrier below
            // with OldLayout=Undefined, which discards every existing texel — a
            // partial upload there would leave everything outside the band black
            // and, because only rewritten rows are ever sent afterwards, it would
            // stay that way. Refuse the band and take the full path instead.
            if (rowOffset != 0 && !target.Initialized)
            {
                return;
            }

            // A band upload covers rows [rowOffset, rowOffset + rowCount) of an
            // otherwise-correct image, so validate it against one row's worth of
            // the surface rather than the whole thing.
            var uploadHeight = target.Height;
            if (rowOffset != 0 || (ulong)uploadPixels.Length != expectedByteCount)
            {
                var rowBytes = target.Height == 0 ? 0 : expectedByteCount / target.Height;
                if (rowBytes != 0 &&
                    target.Depth <= 1 &&
                    (ulong)uploadPixels.Length % rowBytes == 0)
                {
                    var rows = (uint)((ulong)uploadPixels.Length / rowBytes);
                    if (rows != 0 && rowOffset + rows <= target.Height)
                    {
                        uploadHeight = rows;
                        expectedByteCount = (ulong)uploadPixels.Length;
                    }
                }
            }

            // The guest can hand us linear pixel data whose rows are padded out
            // to a hardware pitch wider than the image, so the byte count runs
            // past the tightly packed width*height*bpp we compute. Recover the
            // real source row length and copy with it instead of dropping the
            // upload, which would otherwise leave the texture blank.
            var uploadRowLengthTexels = TryGetPaddedUploadRowLength(
                target,
                (ulong)uploadPixels.Length,
                expectedByteCount);
            if (expectedByteCount == 0
                || ((ulong)uploadPixels.Length != expectedByteCount
                    && uploadRowLengthTexels == 0))
            {
                if (_rejectedGuestImageUploads.Add(
                        (target.Address, uploadPixels.Length, expectedByteCount, target.Format)))
                {
                    Console.Error.WriteLine(
                        $"[LOADER][WARN] Vulkan rejected incompatible guest image upload " +
                        $"addr=0x{target.Address:X16} size={target.Width}x{target.Height} " +
                        $"format={target.Format} bytes={uploadPixels.Length} expected={expectedByteCount}");
                }

                return;
            }

            var byteCount = (ulong)uploadPixels.Length;
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
                fixed (byte* source = uploadPixels)
                {
                    System.Buffer.MemoryCopy(
                        source,
                        mapped,
                        uploadPixels.Length,
                        uploadPixels.Length);
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
                    BufferRowLength = uploadRowLengthTexels,
                    BufferImageHeight = 0,
                    ImageSubresource = new ImageSubresourceLayers(
                        ImageAspectFlags.ColorBit,
                        0,
                        0,
                        1),
                    ImageOffset = new Offset3D(0, (int)rowOffset, 0),
                    ImageExtent = new Extent3D(
                        target.Width,
                        uploadHeight,
                        target.Depth),
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

        private void TrackCpuBackedGuestImage(GuestImageResource image)
        {
            if (image.Width == 0 || image.Height == 0)
            {
                return;
            }

            var depth = Math.Max(image.Depth, 1u);
            var byteCount = GetVulkanImageByteCount(
                image.Format,
                image.Width,
                image.Height,
                depth);
            if (!ShouldTrackGuestImageWrites(byteCount))
            {
                return;
            }

            SharpEmu.HLE.GuestImageWriteTracker.Track(
                image.Address,
                byteCount,
                CurrentGuestWorkSequenceForDiagnostics,
                "vulkan.render-target");
        }
    }
}
