// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Collections.Concurrent;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.VideoOut;
using SharpEmu.ShaderCompiler;
using static SharpEmu.Libs.Agc.AgcExports;

namespace SharpEmu.Libs.Gpu.Metal;

internal static class MetalTextureSnapshots
{
    private const ulong MaximumSnapshotBytes = 128UL * 1024UL * 1024UL;
    private static readonly ConcurrentDictionary<ulong, byte> _arrayUploadUnsupported = new();

    // Disable cache reuse to check snapshot freshness.
    private static readonly bool _textureCopySkipDisabled = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_NO_TEXTURE_SKIP"),
        "1",
        StringComparison.Ordinal);

    // Send supported tiled data to the GPU; decode other layouts on the CPU.
    private static readonly bool _gpuDetileEnabled = !string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_GPU_DETILE"),
        "0",
        StringComparison.Ordinal);

    // Report each tile mode and decode decision once.
    private static readonly bool _gpuDetileLog = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_LOG_GPU_DETILE"),
        "1",
        StringComparison.Ordinal);

    private static readonly HashSet<uint> _seenTextureTileModes = new();

    private static readonly HashSet<uint> _gpuDetileGateDiag = new();

    private static readonly bool _reuseGuestTextureSnapshots = !string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_REUSE_GUEST_TEXTURE_SNAPSHOTS"),
        "0",
        StringComparison.Ordinal);

    private static int _guestTextureSnapshotReuseLogged;

    private static int _textureFallbackTraceCount;

    private readonly record struct GuestTextureSnapshotReuseKey(
        TextureDescriptor Descriptor,
        bool IsStorage,
        uint MipLevel,
        bool IsArrayed);

    internal static IReadOnlyList<GuestDrawTexture> CreateTextureSnapshots(
        CpuContext context,
        IReadOnlyList<TranslatedImageBinding> bindings,
        out int fallbackTextureCount)
    {
        var textures = new List<GuestDrawTexture>(bindings.Count);
        fallbackTextureCount = 0;
        Dictionary<GuestTextureSnapshotReuseKey, GuestDrawTexture>? snapshots = null;
        if (_reuseGuestTextureSnapshots)
        {
            snapshots = new Dictionary<GuestTextureSnapshotReuseKey, GuestDrawTexture>();
            if (Interlocked.Exchange(ref _guestTextureSnapshotReuseLogged, 1) == 0)
            {
                Console.Error.WriteLine(
                    "[LOADER][INFO] Per-work guest texture snapshot reuse enabled.");
            }
        }
        fallbackTextureCount = 0;
        foreach (var binding in bindings)
        {
            var profileStart = TexturePreparationProfile.Begin();
            var reuseKey = new GuestTextureSnapshotReuseKey(
                binding.Descriptor,
                binding.IsStorage,
                binding.MipLevel,
                binding.IsArrayed);
            GuestDrawTexture texture;
            if (snapshots?.TryGetValue(reuseKey, out var snapshot) == true)
            {
                // Keep each binding's sampling state separate; share unchanged pixel data within this draw.
                texture = snapshot with
                {
                    Sampler = ToGuestSampler(binding.SamplerDescriptor),
                };
                TexturePreparationProfile.RecordBinding(
                    TexturePreparationProfile.BindingOutcome.Reused,
                    profileStart);
            }
            else if (TryCreateGuestDrawTexture(
                    context,
                    binding.Descriptor,
                    binding.IsStorage,
                    binding.MipLevel,
                    binding.SamplerDescriptor,
                    binding.IsArrayed,
                    out texture))
            {
                // Reuse only decoded pixels; an empty snapshot can refer to a sampler-specific cache entry.
                if (!texture.IsFallback && texture.RgbaPixels.Length != 0)
                {
                    snapshots?.Add(reuseKey, texture);
                }

                var outcome = texture.IsFallback
                    ? TexturePreparationProfile.BindingOutcome.Fallback
                    : texture.TiledSource is not null
                        ? TexturePreparationProfile.BindingOutcome.Tiled
                        : texture.RgbaPixels.Length != 0
                            ? TexturePreparationProfile.BindingOutcome.Linear
                            : TexturePreparationProfile.BindingOutcome.Empty;
                var payloadBytes = texture.TiledSource?.LongLength ?? texture.RgbaPixels.LongLength;
                TexturePreparationProfile.RecordBinding(outcome, profileStart, payloadBytes);
            }
            else
            {
                TexturePreparationProfile.RecordBinding(
                    TexturePreparationProfile.BindingOutcome.Skipped,
                    profileStart);
                continue;
            }

            textures.Add(texture);
            if (texture.IsFallback)
            {
                fallbackTextureCount++;
            }
        }

        return textures;
    }

    // Keep equation support consistent with MetalDetilePass.Supports.
    // The GPU handles 4-, 8-, and 16-byte elements; the CPU handles smaller elements.
    private static bool IsGpuDetileEquation(DetileEquation equation) =>
        equation == DetileEquation.ExactXor || equation == DetileEquation.BlockTable;

    private static bool IsGpuDetileBytesPerElement(int bytesPerElement) =>
        bytesPerElement is 4 or 8 or 16;

    private static bool IsGpuDetileTextureType(uint type) =>
        type != Gen5TextureType3D;

    private static bool TryGetTextureElementLayout(
        TextureDescriptor descriptor,
        uint sourceWidth,
        out int elementsWide,
        out int elementsHigh,
        out int bytesPerElement)
    {
        var blockBytes = GetBlockCompressedBlockBytes(descriptor.Format);
        if (blockBytes != 0)
        {
            bytesPerElement = blockBytes;
            elementsWide = (int)((sourceWidth + 3) / 4);
            elementsHigh = (int)((descriptor.Height + 3) / 4);
        }
        else
        {
            bytesPerElement = (int)GetTextureBytesPerTexel(descriptor.Format);
            if (bytesPerElement == 0)
            {
                elementsWide = 0;
                elementsHigh = 0;
                return false;
            }

            elementsWide = (int)sourceWidth;
            elementsHigh = (int)descriptor.Height;
        }

        return true;
    }

    private static byte[]? TryDetileTextureSource(
        TextureDescriptor descriptor,
        uint sourceWidth,
        int logicalByteCount,
        byte[] source,
        bool baseMipInTail = false,
        int tailElementX = 0,
        int tailElementY = 0)
    {
        if (!GnmTiling.NeedsDetile(descriptor.TileMode) ||
            !TryGetTextureElementLayout(
                descriptor,
                sourceWidth,
                out var elementsWide,
                out var elementsHigh,
                out var bytesPerElement))
        {
            return null;
        }

        if (baseMipInTail)
        {
            if (!GnmTiling.TryGetBlockElementDimensions(
                    descriptor.TileMode,
                    bytesPerElement,
                    out var blockWidth,
                    out var blockHeight))
            {
                return null;
            }

            var blockByteCount = (long)blockWidth * blockHeight * bytesPerElement;
            if (source.Length < blockByteCount ||
                (long)elementsWide * elementsHigh * bytesPerElement > logicalByteCount)
            {
                return null;
            }

            var blockLinear = new byte[blockByteCount];
            if (!GnmTiling.TryDetile(
                    source,
                    blockLinear,
                    descriptor.TileMode,
                    blockWidth,
                    blockHeight,
                    bytesPerElement))
            {
                return null;
            }

            var tailLinear = new byte[logicalByteCount];
            var rowBytes = elementsWide * bytesPerElement;
            for (var rowIndex = 0; rowIndex < elementsHigh; rowIndex++)
            {
                var sourceOffset = (((long)tailElementY + rowIndex) * blockWidth + tailElementX) * bytesPerElement;
                blockLinear.AsSpan((int)sourceOffset, rowBytes)
                    .CopyTo(tailLinear.AsSpan(rowIndex * rowBytes, rowBytes));
            }

            return tailLinear;
        }

        var volumeDepth = checked((int)GetTextureVolumeDepth(
            descriptor.Type,
            descriptor.Depth));
        if (logicalByteCount % volumeDepth != 0 ||
            source.Length % volumeDepth != 0)
        {
            return null;
        }

        var logicalSliceByteCount = logicalByteCount / volumeDepth;
        var physicalSliceByteCount = source.Length / volumeDepth;
        var linear = new byte[logicalByteCount];
        for (var slice = 0; slice < volumeDepth; slice++)
        {
            if (!GnmTiling.TryDetile(
                    source.AsSpan(slice * physicalSliceByteCount, physicalSliceByteCount),
                    linear.AsSpan(slice * logicalSliceByteCount, logicalSliceByteCount),
                    descriptor.TileMode,
                    elementsWide,
                    elementsHigh,
                    bytesPerElement))
            {
                return null;
            }
        }

        return linear;
    }

    private static void TraceTextureFallback(TextureDescriptor descriptor, string reason)
    {
        var mode = Environment.GetEnvironmentVariable("SHARPEMU_TRACE_GUEST_IMAGES");
        if ((!string.Equals(mode, "1", StringComparison.Ordinal) &&
             !string.Equals(mode, "present", StringComparison.OrdinalIgnoreCase)) ||
            Interlocked.Increment(ref _textureFallbackTraceCount) > 64)
        {
            return;
        }

        Console.Error.WriteLine(
            $"[LOADER][TRACE] agc.texture_fallback reason={reason} " +
            $"addr=0x{descriptor.Address:X16} type={descriptor.Type} " +
            $"size={descriptor.Width}x{descriptor.Height} pitch={descriptor.Pitch} " +
            $"fmt={descriptor.Format} num={descriptor.NumberType} " +
            $"tile={descriptor.TileMode} mip={descriptor.MipLevels} " +
            $"dst=0x{descriptor.DstSelect:X3}");
    }

    private static bool TryCreateGuestDrawTexture(
        CpuContext context,
        TextureDescriptor descriptor,
        bool isStorage,
        uint mipLevel,
        IReadOnlyList<uint> samplerDescriptor,
        bool isArrayed,
        out GuestDrawTexture texture,
        int snapshotAttempt = 0)
    {
        texture = default!;
        var textureDepth = GetTextureVolumeDepth(
            descriptor.Type,
            descriptor.Depth);
        if ((descriptor.Type != Gen5TextureType1D &&
             descriptor.Type != Gen5TextureType2D &&
             descriptor.Type != Gen5TextureType3D &&
             descriptor.Type != Gen5TextureTypeCube &&
             descriptor.Type != Gen5TextureType1DArray &&
             descriptor.Type != Gen5TextureType2DArray) ||
            descriptor.Width == 0 ||
            descriptor.Height == 0 ||
            descriptor.Width > 8192 ||
            descriptor.Height > 8192)
        {
            TraceTextureFallback(descriptor, "invalid-descriptor");
            texture = CreateFallbackGuestDrawTexture(
                isStorage,
                descriptor.Format,
                descriptor.NumberType,
                isArrayed,
                descriptor.Type,
                textureDepth);
            return true;
        }

        if (_gpuDetileLog)
        {
            lock (_seenTextureTileModes)
            {
                if (_seenTextureTileModes.Add(descriptor.TileMode))
                {
                    Console.Error.WriteLine(
                        $"[GPU-DETILE] texture tile_mode={descriptor.TileMode} fmt={descriptor.Format} " +
                        $"{descriptor.Width}x{descriptor.Height} " +
                        $"(0=linear; GPU covers exact-XOR 5/9/24/27 @ 4bpp).");
                }
            }
        }

        var sourceWidth = descriptor.TileMode == 0
            ? GetLinearTexturePitch(
                Math.Max(descriptor.Width, descriptor.Pitch),
                descriptor.Height,
                descriptor.Format)
            : descriptor.Width;
        var sourceSliceByteCount = GetTextureByteCount(
            descriptor.Format,
            sourceWidth,
            descriptor.Height);
        var sourceByteCount = GetTextureByteCount(
            descriptor.Format,
            sourceWidth,
            descriptor.Height,
            textureDepth);
        if (sourceByteCount == 0 ||
            sourceByteCount > MaximumSnapshotBytes ||
            sourceByteCount > int.MaxValue)
        {
            TraceTextureFallback(
                descriptor,
                $"invalid-byte-count:{sourceByteCount}");
            texture = CreateFallbackGuestDrawTexture(
                isStorage,
                descriptor.Format,
                descriptor.NumberType,
                isArrayed,
                descriptor.Type,
                textureDepth);
            return true;
        }

        var physicalSourceByteCount = sourceSliceByteCount;
        var elementsWide = 0;
        var elementsHigh = 0;
        var bytesPerElement = 0;
        var hasElementLayout = GnmTiling.NeedsDetile(descriptor.TileMode) &&
            TryGetTextureElementLayout(
                descriptor,
                sourceWidth,
                out elementsWide,
                out elementsHigh,
                out bytesPerElement);
        if (hasElementLayout &&
            GnmTiling.TryGetTiledByteCount(
                descriptor.TileMode,
                elementsWide,
                elementsHigh,
                bytesPerElement,
                out var tiledByteCount))
        {
            physicalSourceByteCount = tiledByteCount;
        }

        var resourceMipLevels = descriptor.HasExtendedDescriptor
            ? descriptor.ResourceMipLevels
            : 1u;
        var baseMipByteOffset = 0UL;
        var baseMipInTail = false;
        var mipTailElementX = 0;
        var mipTailElementY = 0;
        var chainSliceBytes = physicalSourceByteCount;
        if (hasElementLayout && resourceMipLevels > 1 &&
            GnmTiling.TryGetBaseMipPlacement(
                descriptor.TileMode,
                elementsWide,
                elementsHigh,
                bytesPerElement,
                resourceMipLevels,
                out baseMipByteOffset,
                out baseMipInTail,
                out mipTailElementX,
                out mipTailElementY,
                out var placedChainSliceBytes))
        {
            chainSliceBytes = placedChainSliceBytes;
        }

        physicalSourceByteCount = checked(physicalSourceByteCount * textureDepth);
        if (physicalSourceByteCount > MaximumSnapshotBytes ||
            physicalSourceByteCount > int.MaxValue)
        {
            texture = CreateFallbackGuestDrawTexture(
                isStorage,
                descriptor.Format,
                descriptor.NumberType,
                isArrayed,
                descriptor.Type,
                textureDepth);
            return true;
        }

        var wantsArrayUpload = isArrayed &&
            !isStorage &&
            descriptor.Address != 0 &&
            (descriptor.Type == Gen5TextureType2DArray ||
             descriptor.Type == Gen5TextureType1DArray) &&
            descriptor.Depth > 1 &&
            !_arrayUploadUnsupported.ContainsKey(descriptor.Address);
        var arrayUploadLayers = wantsArrayUpload ? descriptor.Depth : 1u;
        var isVideoBuffer =
            SharpEmu.Libs.AvPlayer.AvPlayerExports.IsVideoBufferAddress(
                descriptor.Address);

        // Check content freshness before reusing a cached upload.
        var sampledUploadKnown = false;
        if (!isStorage &&
            !wantsArrayUpload &&
            !isVideoBuffer &&
            descriptor.Address != 0)
        {
            var lookupStart = TexturePreparationProfile.Begin();
            sampledUploadKnown = GuestGpu.Current is IGuestImageSnapshotBackend uploadSnapshots &&
                uploadSnapshots.IsGuestImageUploadKnown(
                    descriptor.Address,
                    descriptor.Format,
                    descriptor.NumberType);
            TexturePreparationProfile.RecordUploadKnown(lookupStart, sampledUploadKnown);
        }

        if (sampledUploadKnown)
        {
            texture = new GuestDrawTexture(
                descriptor.Address,
                descriptor.Width,
                descriptor.Height,
                descriptor.Format,
                descriptor.NumberType,
                [],
                IsFallback: false,
                IsStorage: false,
                MipLevels: descriptor.MipLevels,
                MipLevel: mipLevel,
                BaseMipLevel: descriptor.ViewBaseLevel,
                ResourceMipLevels: descriptor.ResourceMipLevels,
                Pitch: sourceWidth,
                TileMode: descriptor.TileMode,
                DstSelect: descriptor.DstSelect,
                Sampler: ToGuestSampler(samplerDescriptor),
                ArrayedView: isArrayed,
                Type: descriptor.Type,
                Depth: textureDepth);
            return true;
        }

        if (isStorage)
        {
            var initialPixels = Array.Empty<byte>();
            var uploadKnownStart = TexturePreparationProfile.Begin();
            var uploadKnown = descriptor.Address != 0 &&
                GuestGpu.Current is IGuestImageSnapshotBackend storageSnapshots &&
                storageSnapshots.IsGuestImageUploadKnown(
                    descriptor.Address,
                    descriptor.Format,
                    descriptor.NumberType);
            TexturePreparationProfile.RecordUploadKnown(uploadKnownStart, uploadKnown);
            var readSucceeded = false;
            var linearNonzero = false;
            var storageSnapshot = default(SharpEmu.HLE.GuestImageWriteTracker.ReadSnapshot);
            if (descriptor.Address != 0 && !uploadKnown)
            {
                // Read and decode the full tiled storage footprint before upload.
                storageSnapshot = SharpEmu.HLE.GuestImageWriteTracker.BeginReadSnapshot(
                    descriptor.Address,
                    checked(baseMipByteOffset + physicalSourceByteCount),
                    source: "agc.storage-image-snapshot");
                var storageSource = new byte[(int)physicalSourceByteCount];
                if (context.Memory.TryRead(descriptor.Address + baseMipByteOffset, storageSource))
                {
                    readSucceeded = true;
                    var linearStorage = TryDetileTextureSource(
                        descriptor,
                        sourceWidth,
                        checked((int)sourceByteCount),
                        storageSource,
                        baseMipInTail,
                        mipTailElementX,
                        mipTailElementY) ?? storageSource
                            .AsSpan(0, checked((int)sourceByteCount))
                            .ToArray();
                    if (linearStorage.AsSpan().IndexOfAnyExcept((byte)0) >= 0)
                    {
                        linearNonzero = true;
                        initialPixels = linearStorage;
                    }
                }

                if (readSucceeded &&
                    !SharpEmu.HLE.GuestImageWriteTracker.IsReadSnapshotStable(storageSnapshot))
                {
                    if (snapshotAttempt < 2)
                    {
                        return TryCreateGuestDrawTexture(
                            context,
                            descriptor,
                            isStorage,
                            mipLevel,
                            samplerDescriptor,
                            isArrayed,
                            out texture,
                            snapshotAttempt + 1);
                    }

                    initialPixels = [];
                }
            }

            if (ParseOptionalHexAddress(
                    Environment.GetEnvironmentVariable(
                        "SHARPEMU_TRACE_STORAGE_IMAGE_INIT_ADDRESS")) ==
                descriptor.Address)
            {
                Console.Error.WriteLine(
                    $"[LOADER][TRACE] agc.storage_initial_data " +
                    $"addr=0x{descriptor.Address:X16} op_storage={isStorage} " +
                    $"upload_known={uploadKnown} read={readSucceeded} " +
                    $"nonzero={linearNonzero} initial_bytes={initialPixels.Length} " +
                    $"logical_bytes={sourceByteCount} physical_bytes={physicalSourceByteCount} " +
                    $"size={descriptor.Width}x{descriptor.Height} pitch={sourceWidth} " +
                    $"fmt={descriptor.Format} num={descriptor.NumberType} " +
                    $"tile={descriptor.TileMode} mip={mipLevel}");
            }

            texture = new GuestDrawTexture(
                descriptor.Address,
                descriptor.Width,
                descriptor.Height,
                descriptor.Format,
                descriptor.NumberType,
                initialPixels,
                IsFallback: descriptor.Address == 0,
                IsStorage: true,
                MipLevels: descriptor.MipLevels,
                MipLevel: mipLevel,
                BaseMipLevel: descriptor.ViewBaseLevel,
                ResourceMipLevels: descriptor.ResourceMipLevels,
                Pitch: sourceWidth,
                TileMode: descriptor.TileMode,
                DstSelect: descriptor.DstSelect,
                Sampler: ToGuestSampler(samplerDescriptor),
                WriteGeneration: storageSnapshot.Active ? storageSnapshot.Generation : -1,
                Type: descriptor.Type,
                Depth: textureDepth,
                SourceByteCount: checked(baseMipByteOffset + physicalSourceByteCount),
                CpuSnapshotStable:
                    !readSucceeded ||
                    SharpEmu.HLE.GuestImageWriteTracker.IsReadSnapshotStable(storageSnapshot));
            return true;
        }

        // Reuse cached texels only while their tracked write generation is unchanged.
        var sampler = ToGuestSampler(samplerDescriptor);
        // Keep the write generation with the pixels so later writes invalidate this upload.
        var hasWriteGeneration =
            SharpEmu.HLE.GuestImageWriteTracker.TryGetWriteGeneration(
                descriptor.Address,
                out var writeGeneration);
        // Keep this draw's decoded pixels when a rotating video buffer can be reused.
        var contentCached = false;
        if (!_textureCopySkipDisabled &&
            !isVideoBuffer &&
            descriptor.Address != 0)
        {
            var lookupStart = TexturePreparationProfile.Begin();
            contentCached = GuestGpu.Current is IGuestImageSnapshotBackend contentSnapshots &&
                contentSnapshots.IsTextureContentCached(
                new TextureCacheLookupIdentity(
                    new TextureContentIdentity(
                        descriptor.Address,
                        descriptor.Width,
                        descriptor.Height,
                        descriptor.Format,
                        descriptor.NumberType,
                        descriptor.DstSelect,
                        descriptor.TileMode,
                        sourceWidth,
                        isArrayed,
                        arrayUploadLayers,
                        descriptor.Type,
                        textureDepth,
                        descriptor.ResourceMipLevels),
                    sampler));
            TexturePreparationProfile.RecordContentCache(lookupStart, contentCached);
        }

        if (contentCached)
        {
            texture = new GuestDrawTexture(
                descriptor.Address,
                descriptor.Width,
                descriptor.Height,
                descriptor.Format,
                descriptor.NumberType,
                [],
                IsFallback: false,
                IsStorage: false,
                MipLevels: descriptor.MipLevels,
                MipLevel: mipLevel,
                BaseMipLevel: descriptor.ViewBaseLevel,
                ResourceMipLevels: descriptor.ResourceMipLevels,
                Pitch: sourceWidth,
                TileMode: descriptor.TileMode,
                DstSelect: descriptor.DstSelect,
                Sampler: sampler,
                ArrayedView: isArrayed,
                ArrayLayers: arrayUploadLayers,
                Type: descriptor.Type,
                Depth: textureDepth);
            return true;
        }

        var trackedSourceByteCount = wantsArrayUpload
            ? checked(chainSliceBytes * arrayUploadLayers)
            : checked(baseMipByteOffset + physicalSourceByteCount);
        var readSnapshot = SharpEmu.HLE.GuestImageWriteTracker.BeginReadSnapshot(
            descriptor.Address,
            trackedSourceByteCount,
            source: "agc.sampled-texture-snapshot");
        if (readSnapshot.Active)
        {
            hasWriteGeneration = true;
            writeGeneration = readSnapshot.Generation;
        }

        if (wantsArrayUpload)
        {
            var arrayLayers = arrayUploadLayers;
            var layerBytes = checked((int)sourceSliceByteCount);
            var totalBytes = (long)layerBytes * arrayLayers;

            if (hasElementLayout && resourceMipLevels > 1 &&
                TryCreateTiledArrayMipChain(
                    context,
                    descriptor,
                    sampler,
                    arrayLayers,
                    elementsWide,
                    elementsHigh,
                    bytesPerElement,
                    hasWriteGeneration ? writeGeneration : -1,
                    out texture))
            {
                return FinalizeGuestTextureSnapshot(
                    context,
                    descriptor,
                    isStorage,
                    mipLevel,
                    samplerDescriptor,
                    isArrayed,
                    readSnapshot,
                    snapshotAttempt,
                    texture,
                    out texture);
            }

            // Pack tiled array slices for one GPU decode per layer; decode unsupported layouts on the CPU.
            if (_gpuDetileEnabled && hasElementLayout && !baseMipInTail &&
                IsGpuDetileBytesPerElement(bytesPerElement) &&
                IsGpuDetileTextureType(descriptor.Type) &&
                (long)physicalSourceByteCount * arrayLayers <= int.MaxValue)
            {
                var gpuArrayParams = GnmTiling.GetDetileParams(
                    descriptor.TileMode, bytesPerElement, elementsWide, elementsHigh);
                if (IsGpuDetileEquation(gpuArrayParams.Equation) &&
                    (long)elementsWide * elementsHigh * bytesPerElement <= (long)physicalSourceByteCount)
                {
                    var sliceBytes = checked((int)physicalSourceByteCount);
                    var tiledLayers = new byte[(long)sliceBytes * arrayLayers];
                    var readAllLayers = true;
                    for (var layer = 0u; layer < arrayLayers; layer++)
                    {
                        if (!context.Memory.TryRead(
                                descriptor.Address + layer * chainSliceBytes + baseMipByteOffset,
                                tiledLayers.AsSpan(checked((int)(layer * (uint)sliceBytes)), sliceBytes)))
                        {
                            readAllLayers = false;
                            break;
                        }
                    }

                    if (readAllLayers)
                    {
            texture = new GuestDrawTexture(
                            descriptor.Address,
                            descriptor.Width,
                            descriptor.Height,
                            descriptor.Format,
                            descriptor.NumberType,
                            [],
                            IsFallback: false,
                            IsStorage: false,
                            MipLevels: descriptor.MipLevels,
                            MipLevel: mipLevel,
                            BaseMipLevel: descriptor.ViewBaseLevel,
                            ResourceMipLevels: descriptor.ResourceMipLevels,
                            Pitch: sourceWidth,
                            TileMode: descriptor.TileMode,
                            DstSelect: descriptor.DstSelect,
                            Sampler: sampler,
                            WriteGeneration: hasWriteGeneration ? writeGeneration : -1,
                            ArrayedView: true,
                            ArrayLayers: arrayLayers,
                            // Use the same texture identity for cache lookup and upload.
                            Type: descriptor.Type,
                            Depth: textureDepth,
                            TiledSource: tiledLayers,
                            Detile: gpuArrayParams,
                            SourceByteCount: trackedSourceByteCount);
                        return FinalizeGuestTextureSnapshot(
                            context,
                            descriptor,
                            isStorage,
                            mipLevel,
                            samplerDescriptor,
                            isArrayed,
                            readSnapshot,
                            snapshotAttempt,
                            texture,
                            out texture);
                    }
                }
            }

            if (totalBytes <= int.MaxValue)
            {
                var layered = new byte[totalBytes];
                var uploadedLayers = 0u;
                for (var layer = 0u; layer < arrayLayers; layer++)
                {
                    var sliceSource = new byte[(int)chainSliceBytes];
                    if (!context.Memory.TryRead(
                            descriptor.Address + layer * chainSliceBytes + baseMipByteOffset,
                            sliceSource))
                    {
                        break;
                    }

                    var sliceLinear = TryDetileTextureSource(
                        descriptor,
                        sourceWidth,
                        layerBytes,
                        sliceSource,
                        baseMipInTail,
                        mipTailElementX,
                        mipTailElementY) ?? sliceSource.AsSpan(0, layerBytes).ToArray();
                    sliceLinear.AsSpan(0, layerBytes)
                        .CopyTo(layered.AsSpan(checked((int)(layer * layerBytes))));
                    uploadedLayers++;
                }

                if (uploadedLayers == arrayLayers)
                {
            texture = new GuestDrawTexture(
                        descriptor.Address,
                        descriptor.Width,
                        descriptor.Height,
                        descriptor.Format,
                        descriptor.NumberType,
                        layered,
                        IsFallback: false,
                        IsStorage: false,
                        MipLevels: descriptor.MipLevels,
                        MipLevel: mipLevel,
                        BaseMipLevel: descriptor.ViewBaseLevel,
                        ResourceMipLevels: descriptor.ResourceMipLevels,
                        Pitch: sourceWidth,
                        TileMode: descriptor.TileMode,
                        DstSelect: descriptor.DstSelect,
                        Sampler: sampler,
                        WriteGeneration: hasWriteGeneration ? writeGeneration : -1,
                        ArrayedView: true,
                        ArrayLayers: arrayLayers,
                        Type: descriptor.Type,
                        Depth: textureDepth,
                        SourceByteCount: checked(chainSliceBytes * arrayLayers));
                    return FinalizeGuestTextureSnapshot(
                        context,
                        descriptor,
                        isStorage,
                        mipLevel,
                        samplerDescriptor,
                        isArrayed,
                        readSnapshot,
                        snapshotAttempt,
                        texture,
                        out texture);
                }
            }

            _arrayUploadUnsupported.TryAdd(descriptor.Address, 0);
        }

        var source = new byte[(int)physicalSourceByteCount];
        if (!context.Memory.TryRead(descriptor.Address + baseMipByteOffset, source))
        {
            TraceTextureFallback(
                descriptor,
                $"guest-read-failed:{sourceByteCount}");
            texture = CreateFallbackGuestDrawTexture(
                isStorage,
                descriptor.Format,
                descriptor.NumberType,
                isArrayed,
                descriptor.Type,
                textureDepth);
            return true;
        }

        DumpTextureSourceIfRequested(descriptor, sourceWidth, source);

        if (_gpuDetileLog && descriptor.TileMode != 0)
        {
            lock (_gpuDetileGateDiag)
            {
                if (_gpuDetileGateDiag.Add(descriptor.TileMode))
                {
                    var equation = hasElementLayout
                        ? GnmTiling.GetDetileParams(
                            descriptor.TileMode, bytesPerElement, elementsWide, elementsHigh).Equation
                        : DetileEquation.None;
                    Console.Error.WriteLine(
                        $"[GPU-DETILE] gate mode={descriptor.TileMode} fmt={descriptor.Format} " +
                        $"bpp={bytesPerElement} hasLayout={hasElementLayout} mipTail={baseMipInTail} " +
                        $"storage={isStorage} arrayed={isArrayed} eq={equation} -> " +
                        $"{(hasElementLayout && !baseMipInTail && IsGpuDetileBytesPerElement(bytesPerElement) && IsGpuDetileEquation(equation) ? "GPU" : "CPU")}");
                }
            }
        }

        // Send supported single-layer base-mip layouts to the GPU; decode the rest on the CPU.
        if (_gpuDetileEnabled && hasElementLayout && !baseMipInTail &&
            IsGpuDetileBytesPerElement(bytesPerElement) && !isArrayed &&
            IsGpuDetileTextureType(descriptor.Type))
        {
            var gpuDetileParams = GnmTiling.GetDetileParams(
                descriptor.TileMode, bytesPerElement, elementsWide, elementsHigh);
            if (IsGpuDetileEquation(gpuDetileParams.Equation) &&
                (long)elementsWide * elementsHigh * bytesPerElement <= source.Length)
            {
            texture = new GuestDrawTexture(
                    descriptor.Address,
                    descriptor.Width,
                    descriptor.Height,
                    descriptor.Format,
                    descriptor.NumberType,
                    [],
                    IsFallback: false,
                    IsStorage: isStorage,
                    MipLevels: descriptor.MipLevels,
                    MipLevel: mipLevel,
                    BaseMipLevel: descriptor.ViewBaseLevel,
                    ResourceMipLevels: descriptor.ResourceMipLevels,
                    Pitch: sourceWidth,
                    TileMode: descriptor.TileMode,
                    DstSelect: descriptor.DstSelect,
                    Sampler: ToGuestSampler(samplerDescriptor),
                    WriteGeneration: hasWriteGeneration ? writeGeneration : -1,
                    ArrayedView: isArrayed,
                    Type: descriptor.Type,
                    Depth: textureDepth,
                    TiledSource: source,
                    Detile: gpuDetileParams,
                    SourceByteCount: (ulong)source.Length);
                return FinalizeGuestTextureSnapshot(
                    context,
                    descriptor,
                    isStorage,
                    mipLevel,
                    samplerDescriptor,
                    isArrayed,
                    readSnapshot,
                    snapshotAttempt,
                    texture,
                    out texture);
            }
        }

        var rgba = TryDetileTextureSource(
            descriptor,
            sourceWidth,
            checked((int)sourceByteCount),
            source,
            baseMipInTail,
            mipTailElementX,
            mipTailElementY) ?? source.AsSpan(0, checked((int)sourceByteCount)).ToArray();
        DumpLinearTextureIfRequested(descriptor, sourceWidth, rgba);
        texture = new GuestDrawTexture(
            descriptor.Address,
            descriptor.Width,
            descriptor.Height,
            descriptor.Format,
            descriptor.NumberType,
            rgba,
            IsFallback: false,
            IsStorage: isStorage,
            MipLevels: descriptor.MipLevels,
            MipLevel: mipLevel,
            BaseMipLevel: descriptor.ViewBaseLevel,
            ResourceMipLevels: descriptor.ResourceMipLevels,
            Pitch: sourceWidth,
            TileMode: descriptor.TileMode,
            DstSelect: descriptor.DstSelect,
            Sampler: ToGuestSampler(samplerDescriptor),
            WriteGeneration: hasWriteGeneration ? writeGeneration : -1,
            ArrayedView: isArrayed,
            Type: descriptor.Type,
            Depth: textureDepth,
            SourceByteCount: physicalSourceByteCount);
        return FinalizeGuestTextureSnapshot(
            context,
            descriptor,
            isStorage,
            mipLevel,
            samplerDescriptor,
            isArrayed,
            readSnapshot,
            snapshotAttempt,
            texture,
            out texture);
    }

    private static bool FinalizeGuestTextureSnapshot(
        CpuContext context,
        TextureDescriptor descriptor,
        bool isStorage,
        uint mipLevel,
        IReadOnlyList<uint> samplerDescriptor,
        bool isArrayed,
        SharpEmu.HLE.GuestImageWriteTracker.ReadSnapshot snapshot,
        int snapshotAttempt,
        GuestDrawTexture candidate,
        out GuestDrawTexture texture)
    {
        if (SharpEmu.HLE.GuestImageWriteTracker.IsReadSnapshotStable(snapshot))
        {
            texture = snapshot.Active
                ? candidate with
                {
                    WriteGeneration = snapshot.Generation,
                    CpuSnapshotStable = true,
                }
                : candidate;
            return true;
        }

        if (snapshotAttempt < 2)
        {
            return TryCreateGuestDrawTexture(
                context,
                descriptor,
                isStorage,
                mipLevel,
                samplerDescriptor,
                isArrayed,
                out texture,
                snapshotAttempt + 1);
        }

        // Withhold bytes read across a guest write to prevent a torn upload.
        texture = candidate with
        {
            RgbaPixels = [],
            TiledSource = null,
            MipUploads = null,
            WriteGeneration = -1,
            CpuSnapshotStable = false,
        };
        return true;
    }

    private static bool TryCreateTiledArrayMipChain(
        CpuContext context,
        TextureDescriptor descriptor,
        GuestSampler sampler,
        uint arrayLayers,
        int elementsWide,
        int elementsHigh,
        int bytesPerElement,
        long writeGeneration,
        out GuestDrawTexture texture)
    {
        texture = default!;
        if (!GnmTiling.TryGetMipChainPlacement(
                descriptor.TileMode,
                elementsWide,
                elementsHigh,
                bytesPerElement,
                descriptor.ResourceMipLevels,
                out var placements,
                out var chainSliceBytes) ||
            chainSliceBytes == 0 ||
            chainSliceBytes > int.MaxValue)
        {
            return false;
        }

        var uploads = new GuestTextureMipUpload[placements.Length];
        var mipByteCounts = new ulong[placements.Length];
        ulong totalLinearBytes = 0;
        for (var mip = 0; mip < placements.Length; mip++)
        {
            var width = Math.Max(descriptor.Width >> mip, 1u);
            var height = Math.Max(descriptor.Height >> mip, 1u);
            var mipBytes = GetTextureByteCount(descriptor.Format, width, height);
            if (mipBytes == 0 || mipBytes > int.MaxValue)
            {
                return false;
            }

            uploads[mip] = new GuestTextureMipUpload(
                totalLinearBytes,
                (uint)mip,
                width,
                height,
                width);
            mipByteCounts[mip] = mipBytes;
            try
            {
                totalLinearBytes = checked(totalLinearBytes + mipBytes * arrayLayers);
            }
            catch (OverflowException)
            {
                return false;
            }
        }

        if (totalLinearBytes > int.MaxValue)
        {
            return false;
        }

        var linear = new byte[(int)totalLinearBytes];
        var tiledSlice = new byte[(int)chainSliceBytes];
        byte[]? tailLinear = null;
        var tailBlockWidth = 0;
        var tailBlockHeight = 0;
        if (placements.Any(static placement => placement.InMipTail))
        {
            if (!GnmTiling.TryGetBlockElementDimensions(
                    descriptor.TileMode,
                    bytesPerElement,
                    out tailBlockWidth,
                    out tailBlockHeight))
            {
                return false;
            }

            tailLinear = new byte[checked(tailBlockWidth * tailBlockHeight * bytesPerElement)];
        }

        for (var layer = 0u; layer < arrayLayers; layer++)
        {
            if (!context.Memory.TryRead(
                    descriptor.Address + layer * chainSliceBytes,
                    tiledSlice))
            {
                return false;
            }

            if (tailLinear is not null &&
                !GnmTiling.TryDetile(
                    tiledSlice.AsSpan(0, (int)placements.First(static placement => placement.InMipTail).ByteCount),
                    tailLinear,
                    descriptor.TileMode,
                    tailBlockWidth,
                    tailBlockHeight,
                    bytesPerElement))
            {
                return false;
            }

            for (var mip = 0; mip < placements.Length; mip++)
            {
                var placement = placements[mip];
                var mipBytes = mipByteCounts[mip];
                var destinationOffset = checked(
                    uploads[mip].BufferOffset + mipBytes * layer);
                var destination = linear.AsSpan((int)destinationOffset, (int)mipBytes);
                if (!placement.InMipTail)
                {
                    if (!GnmTiling.TryDetile(
                            tiledSlice.AsSpan((int)placement.ByteOffset, (int)placement.ByteCount),
                            destination,
                            descriptor.TileMode,
                            placement.ElementsWide,
                            placement.ElementsHigh,
                            bytesPerElement))
                    {
                        return false;
                    }

                    continue;
                }

                var rowBytes = placement.ElementsWide * bytesPerElement;
                for (var rowIndex = 0; rowIndex < placement.ElementsHigh; rowIndex++)
                {
                    var sourceOffset = checked(
                        ((placement.TailElementY + rowIndex) * tailBlockWidth +
                         placement.TailElementX) * bytesPerElement);
                    tailLinear.AsSpan(sourceOffset, rowBytes)
                        .CopyTo(destination.Slice(rowIndex * rowBytes, rowBytes));
                }
            }
        }

        texture = new GuestDrawTexture(
            descriptor.Address,
            descriptor.Width,
            descriptor.Height,
            descriptor.Format,
            descriptor.NumberType,
            linear,
            IsFallback: false,
            IsStorage: false,
            MipLevels: descriptor.MipLevels,
            MipLevel: 0,
            BaseMipLevel: descriptor.ViewBaseLevel,
            ResourceMipLevels: descriptor.ResourceMipLevels,
            Pitch: descriptor.Width,
            TileMode: descriptor.TileMode,
            DstSelect: descriptor.DstSelect,
            Sampler: sampler,
            WriteGeneration: writeGeneration,
            ArrayedView: true,
            ArrayLayers: arrayLayers,
            Type: descriptor.Type,
            Depth: 1,
            MipUploads: uploads,
            SourceByteCount: checked(chainSliceBytes * arrayLayers));
        return true;
    }

    private static int _textureDumpCount;

    private static readonly ConcurrentDictionary<string, int> _textureDumpKeys = new();

    private static void DumpTextureSourceIfRequested(
        in TextureDescriptor descriptor,
        uint sourcePitch,
        byte[] source)
    {
        var directory = Environment.GetEnvironmentVariable("SHARPEMU_TEXTURE_DUMP_DIR");
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        var key = $"0x{descriptor.Address:X}-{descriptor.Width}x{descriptor.Height}";
        var occurrence = _textureDumpKeys.AddOrUpdate(key, 1, static (_, count) => count + 1);
        // Capture initial and later uses of recycled image storage.
        if ((occurrence > 3 && occurrence % 500 >= 3) ||
            Interlocked.Increment(ref _textureDumpCount) > 200)
        {
            return;
        }

        var index = _textureDumpCount;

        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(
                directory,
                $"{index:D3}-0x{descriptor.Address:X}-{descriptor.Width}x{descriptor.Height}" +
                $"-p{sourcePitch}-f{descriptor.Format}-t{descriptor.TileMode}.bin");
            File.WriteAllBytes(path, source);
        }
        catch (IOException)
        {
        }
    }

    private static void DumpLinearTextureIfRequested(
        in TextureDescriptor descriptor,
        uint sourcePitch,
        byte[] source)
    {
        var directory = Environment.GetEnvironmentVariable("SHARPEMU_TEXTURE_LINEAR_DUMP_DIR");
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        var key = $"linear-0x{descriptor.Address:X}-{descriptor.Width}x{descriptor.Height}";
        var occurrence = _textureDumpKeys.AddOrUpdate(key, 1, static (_, count) => count + 1);
        if ((occurrence > 3 && occurrence % 500 >= 3) ||
            Interlocked.Increment(ref _textureDumpCount) > 200)
        {
            return;
        }

        var index = _textureDumpCount;
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(
                directory,
                $"{index:D3}-0x{descriptor.Address:X}-{descriptor.Width}x{descriptor.Height}" +
                $"-p{sourcePitch}-f{descriptor.Format}-t{descriptor.TileMode}.linear.bin");
            File.WriteAllBytes(path, source);
        }
        catch (IOException)
        {
        }
    }

    private static GuestDrawTexture CreateFallbackGuestDrawTexture(
        bool isStorage,
        uint format,
        uint numberType,
        bool isArrayed = false,
        uint type = Gen5TextureType2D,
        uint depth = 1)
    {
        var fallbackFormat = format == 0 ? 10u : format;
        var fallbackNumberType = numberType;
        return new(
            0,
            1,
            1,
            fallbackFormat,
            fallbackNumberType,
            [0, 0, 0, 255],
            IsFallback: true,
            IsStorage: isStorage,
            MipLevels: 1,
            MipLevel: 0,
            ArrayedView: isArrayed,
            Type: type,
            Depth: GetTextureVolumeDepth(type, depth));
    }

    private static byte[] ConvertRgba16FloatToRgba8(ReadOnlySpan<byte> source, uint width, uint height)
    {
        var destination = new byte[checked((int)((ulong)width * height * 4))];
        var pixelCount = destination.Length / 4;
        for (var pixel = 0; pixel < pixelCount; pixel++)
        {
            var sourceOffset = pixel * 8;
            var destinationOffset = pixel * 4;
            destination[destinationOffset + 0] = HalfToByte(BinaryPrimitives.ReadUInt16LittleEndian(source[sourceOffset..]));
            destination[destinationOffset + 1] = HalfToByte(BinaryPrimitives.ReadUInt16LittleEndian(source[(sourceOffset + 2)..]));
            destination[destinationOffset + 2] = HalfToByte(BinaryPrimitives.ReadUInt16LittleEndian(source[(sourceOffset + 4)..]));
            destination[destinationOffset + 3] = HalfToByte(BinaryPrimitives.ReadUInt16LittleEndian(source[(sourceOffset + 6)..]));
        }

        return destination;
    }

    private static byte HalfToByte(ushort bits)
    {
        var value = (float)BitConverter.UInt16BitsToHalf(bits);
        if (!float.IsFinite(value))
        {
            return 0;
        }

        return (byte)Math.Clamp((int)MathF.Round(value * 255.0f), 0, 255);
    }

    private static uint GetLinearTexturePitch(uint pitch, uint height, uint format)
    {
        var bytesPerTexel = GetTextureBytesPerTexel(format);
        if (bytesPerTexel == 0 || height == 0)
        {
            return pitch;
        }

        // Use the padded row pitch so each row starts at its stored offset.
        var pitchBytes = AlignUp((ulong)pitch * bytesPerTexel, 256UL);
        return checked((uint)(pitchBytes / bytesPerTexel));
    }

    private static ulong AlignUp(ulong value, ulong alignment) =>
        (value + alignment - 1) & ~(alignment - 1);

}
