// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Collections.Concurrent;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.VideoOut;
using SharpEmu.ShaderCompiler;

namespace SharpEmu.Libs.Agc;

// This partial transports guest texture descriptors into host draw resources.
public static partial class AgcExports
{
    private static readonly ConcurrentDictionary<ulong, byte> _arrayUploadUnsupported = new();

    // Escape hatch for the cached-texture copy skip (per-draw texel copies
    // are re-enabled unconditionally when set), for A/B-ing rendering issues.
    private static readonly bool _textureCopySkipDisabled = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_NO_TEXTURE_SKIP"),
        "1",
        StringComparison.Ordinal);
    // GPU deswizzle: ship raw tiled bytes + params to the backend instead of
    // detiling on the CPU. On by default; SHARPEMU_GPU_DETILE=0 forces the CPU
    // path. Backend-agnostic here (only inspects DetileParams); the Vulkan/Metal
    // backends detile on the GPU, others fall back to the CPU path.
    private static readonly bool _gpuDetileEnabled = !string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_GPU_DETILE"),
        "0",
        StringComparison.Ordinal);

    // Diagnostics (SHARPEMU_LOG_GPU_DETILE=1): one line per distinct texture tile
    // mode and per-gate decision, so we can see which swizzle modes/formats a
    // title uses and whether each takes the GPU or CPU path.
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

    private readonly record struct TextureDescriptor(
        ulong Address,
        uint Width,
        uint Height,
        uint Format,
        uint NumberType,
        uint TileMode,
        uint Type,
        uint BaseLevel,
        uint LastLevel,
        uint Pitch,
        uint DstSelect,
        uint Depth = 1,
        uint BaseArray = 0,
        uint ArrayPitch = 0,
        uint MaxMip = 0,
        uint MinLod = 0,
        uint MinLodWarn = 0,
        uint BcSwizzle = 0,
        ulong MetadataAddress = 0,
        uint DescriptorFlags = 0,
        bool HasExtendedDescriptor = false)
    {
        public uint ResourceMipLevels
        {
            get
            {
                // RDNA2 table 45 explicitly distinguishes MAX_MIP (the
                // resource allocation) from BASE_LEVEL/LAST_LEVEL (the
                // resource view). Do not size a Vulkan image from a view:
                // another descriptor for the same allocation may expose a
                // different subset of its mip chain.
                var maximumMipLevels = GetMaximumMipLevels();
                var resourceMipLevels = HasExtendedDescriptor
                    ? MaxMip + 1
                    : maximumMipLevels;
                return Math.Min(Math.Max(resourceMipLevels, 1u), maximumMipLevels);
            }
        }

        public uint MipLevels
        {
            get
            {
                var descriptorMipLevels = LastLevel >= ViewBaseLevel
                    ? LastLevel - ViewBaseLevel + 1
                    : 1;
                return Math.Min(
                    descriptorMipLevels,
                    ResourceMipLevels - ViewBaseLevel);
            }
        }

        public uint ViewBaseLevel
        {
            get
            {
                // Some single-mip Gen5 descriptors use the reserved/inverted
                // 15-0 range as a mip-disabled sentinel. The resource still
                // has exactly one addressable level (MAX_MIP=0). Treating 15
                // literally makes Vulkan reject an otherwise compatible GPU
                // image and falls back to stale guest-memory pixels. For any
                // malformed range, keep BASE_LEVEL's meaning and clamp it to
                // the allocation's last addressable mip. In particular, the
                // common 15-0/MAX_MIP=0 sentinel resolves to mip 0 without
                // making LAST_LEVEL the base of unrelated inverted views.
                return Math.Min(BaseLevel, ResourceMipLevels - 1);
            }
        }

        private uint GetMaximumMipLevels()
        {
            var largestDimension = Type == 10
                ? Math.Max(Math.Max(Width, Height), Depth)
                : Math.Max(Width, Height);
            uint maximumMipLevels = 1;
            while (largestDimension > 1)
            {
                largestDimension >>= 1;
                maximumMipLevels++;
            }

            return maximumMipLevels;
        }
    }

    private sealed record TranslatedImageBinding(
        TextureDescriptor Descriptor,
        bool IsStorage,
        uint MipLevel,
        IReadOnlyList<uint> SamplerDescriptor,
        bool IsArrayed = false);

    private readonly record struct GuestTextureSnapshotReuseKey(
        TextureDescriptor Descriptor,
        bool IsStorage,
        uint MipLevel,
        bool IsArrayed);

    private static IReadOnlyList<GuestDrawTexture> CreateGuestDrawTextures(
        CpuContext ctx,
        IReadOnlyList<TranslatedImageBinding> bindings,
        out int fallbackTextureCount)
    {
        var textures = new List<GuestDrawTexture>(bindings.Count);
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
            var reuseKey = new GuestTextureSnapshotReuseKey(
                binding.Descriptor,
                binding.IsStorage,
                binding.MipLevel,
                binding.IsArrayed);
            GuestDrawTexture texture;
            if (snapshots?.TryGetValue(reuseKey, out var snapshot) == true)
            {
                // The sampling state is unique to each binding. Multiple bindings
                // can share the decoded texture data. Keep one record for each binding.
                // Share the unchanged snapshot only during this translation.
                texture = snapshot with
                {
                    Sampler = ToGuestSampler(binding.SamplerDescriptor),
                };
            }
            else if (TryCreateGuestDrawTexture(
                    ctx,
                    binding.Descriptor,
                    binding.IsStorage,
                    binding.MipLevel,
                    binding.SamplerDescriptor,
                    binding.IsArrayed,
                    out texture))
            {
                // An empty non-fallback snapshot shows that this sampler-specific
                // texture is in the presenter cache. Another sampler can require
                // a different cache entry. Reuse only snapshots that contain
                // decoded pixels.
                if (!texture.IsFallback && texture.RgbaPixels.Length != 0)
                {
                    snapshots?.Add(reuseKey, texture);
                }
            }
            else
            {
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

    // BCn block-compressed guest formats and the bytes per 4x4 block.
    private static int GetBlockCompressedBlockBytes(uint format) => format switch
    {
        169 or 170 or 175 or 176 => 8,
        171 or 172 or 173 or 174 or 177 or 178 or 179 or 180 or 181 or 182 => 16,
        _ => 0,
    };

    /// <summary>
    /// Deswizzles a tiled texture source into linear layout when tiling is
    /// enabled and the format is understood; returns null to keep the raw
    /// bytes (linear surfaces, unknown modes, or non-power-of-two elements).
    /// </summary>
    // The GPU detile kernel implements these two equation families at 4/8/16 bpp
    // (one/two/four 32-bit words per element; 1/2 bpp are sub-word and stay on the
    // CPU). Keep in lockstep with VulkanDetilePass.Supports / MetalDetilePass.Supports.
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
            for (var y = 0; y < elementsHigh; y++)
            {
                var sourceOffset = (((long)tailElementY + y) * blockWidth + tailElementX) * bytesPerElement;
                blockLinear.AsSpan((int)sourceOffset, rowBytes)
                    .CopyTo(tailLinear.AsSpan(y * rowBytes, rowBytes));
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
        CpuContext ctx,
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
            sourceByteCount > MaxPresentedTextureBytes ||
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
        if (physicalSourceByteCount > MaxPresentedTextureBytes ||
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

        // Upload-known (not plain availability): the presenter's answer goes
        // generation-stale when the guest CPU rewrites a CPU-backed image
        // (video planes, streamed font atlases), which routes this draw back
        // through the texel copy below so the refresh path re-uploads.
        // With the write tracker off (Windows default), IsGuestImageUploadKnown
        // uses a cheap guest-memory probe so static UI can still skip (Dead
        // Cells menus) while changing CPU content (GTA Bink) forces a copy.
        if (!isStorage &&
            !wantsArrayUpload &&
            descriptor.Address != 0 &&
            GuestGpu.Current.IsGuestImageUploadKnown(
                descriptor.Address,
                descriptor.Format,
                descriptor.NumberType))
        {
            NoteSampledAddress(descriptor.Address, descriptor.Format, descriptor.NumberType);
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
            var uploadKnown = descriptor.Address != 0 &&
                GuestGpu.Current.IsGuestImageUploadKnown(
                    descriptor.Address,
                    descriptor.Format,
                    descriptor.NumberType);
            var readSucceeded = false;
            var linearNonzero = false;
            var storageSnapshot = default(SharpEmu.HLE.GuestImageWriteTracker.ReadSnapshot);
            if (descriptor.Address != 0 && !uploadKnown)
            {
                // Storage images can be pre-populated in tiled guest memory
                // just like sampled images. Reading only the logical linear
                // byte count both truncates 64 KiB swizzle blocks and uploads
                // tiled bytes as scanlines. Read the full physical footprint
                // and run the same AddrLib-derived detile path used below for
                // sampled textures before seeding the Vulkan image.
                storageSnapshot = SharpEmu.HLE.GuestImageWriteTracker.BeginReadSnapshot(
                    descriptor.Address,
                    checked(baseMipByteOffset + physicalSourceByteCount),
                    source: "agc.storage-image-snapshot");
                var storageSource = new byte[(int)physicalSourceByteCount];
                if (ctx.Memory.TryRead(descriptor.Address + baseMipByteOffset, storageSource))
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
                            ctx,
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

            NoteSampledAddress(descriptor.Address, descriptor.Format, descriptor.NumberType);
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

        // When the presenter already holds this exact texture identity in
        // its cache, the texel copy below would be discarded on arrival; for
        // scenes that sample large textures every draw this copy dominated
        // CPU time (Dead Cells menus). The cache records the write generation
        // that supplied its pixels. A later native or managed CPU write bumps
        // the tracker generation and makes IsTextureContentCached return false.
        // CPU-updated guest Bink planes are handled by the upload-known gate
        // above when the tracker cannot observe native writes.
        var sampler = ToGuestSampler(samplerDescriptor);
        // Capture the generation associated with these texels. The presenter
        // records it after upload, and a later tracked write makes the cache
        // generation differ so the next bind sends fresh texels.
        var hasWriteGeneration =
            SharpEmu.HLE.GuestImageWriteTracker.TryGetWriteGeneration(
                descriptor.Address,
                out var writeGeneration);
        if (!_textureCopySkipDisabled &&
            descriptor.Address != 0 &&
            GuestGpu.Current.IsTextureContentCached(
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
                    sampler)))
        {
            NoteSampledAddress(descriptor.Address, descriptor.Format, descriptor.NumberType);
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
                    ctx,
                    descriptor,
                    sampler,
                    arrayLayers,
                    elementsWide,
                    elementsHigh,
                    bytesPerElement,
                    hasWriteGeneration ? writeGeneration : -1,
                    out texture))
            {
                NoteSampledAddress(descriptor.Address, descriptor.Format, descriptor.NumberType);
                return FinalizeGuestTextureSnapshot(
                    ctx,
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

            // GPU detile for arrayed exact-XOR/4bpp textures: pack the tiled array
            // slices contiguously and hand them to the GPU pass (one dispatch-Z
            // layer per slice), mirroring the single-layer gate above. The backend
            // deswizzles every layer on the GPU; only unsupported cases fall to the
            // CPU per-layer detile below. Font/text atlases uploaded as 2D arrays
            // take this path.
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
                        if (!ctx.Memory.TryRead(
                                descriptor.Address + layer * chainSliceBytes + baseMipByteOffset,
                                tiledLayers.AsSpan(checked((int)(layer * (uint)sliceBytes)), sliceBytes)))
                        {
                            readAllLayers = false;
                            break;
                        }
                    }

                    if (readAllLayers)
                    {
                        NoteSampledAddress(descriptor.Address, descriptor.Format, descriptor.NumberType);
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
                            // Must match the identity the CPU path below ships, or
                            // the presenter caches this texture under a different
                            // key than IsTextureContentCached queries above and the
                            // texel-copy skip never hits for non-2D descriptors.
                            Type: descriptor.Type,
                            Depth: textureDepth,
                            TiledSource: tiledLayers,
                            Detile: gpuArrayParams,
                            SourceByteCount: trackedSourceByteCount);
                        return FinalizeGuestTextureSnapshot(
                            ctx,
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
                    if (!ctx.Memory.TryRead(
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
                    NoteSampledAddress(descriptor.Address, descriptor.Format, descriptor.NumberType);
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
                        ctx,
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
        if (!ctx.Memory.TryRead(descriptor.Address + baseMipByteOffset, source))
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

        if (_traceAgcShader)
        {
            var nonZero = 0;
            for (var i = 0; i < source.Length; i++)
            {
                if (source[i] != 0)
                {
                    nonZero++;
                    if (nonZero >= 64)
                    {
                        break;
                    }
                }
            }

            TraceAgcShader(
                $"agc.texture_source addr=0x{descriptor.Address:X16} " +
                $"fmt={descriptor.Format} num={descriptor.NumberType} tile={descriptor.TileMode} " +
                $"size={descriptor.Width}x{descriptor.Height} pitch={descriptor.Pitch} " +
                $"dst=0x{descriptor.DstSelect:X3} " +
                $"bytes={source.Length} logical_bytes={sourceByteCount} nonzero64={nonZero}");
        }
        DumpTextureSourceIfRequested(descriptor, sourceWidth, source);

        if (_gpuDetileLog && descriptor.TileMode != 0)
        {
            lock (_gpuDetileGateDiag)
            {
                if (_gpuDetileGateDiag.Add(descriptor.TileMode))
                {
                    var eq = hasElementLayout
                        ? GnmTiling.GetDetileParams(
                            descriptor.TileMode, bytesPerElement, elementsWide, elementsHigh).Equation
                        : DetileEquation.None;
                    Console.Error.WriteLine(
                        $"[GPU-DETILE] gate mode={descriptor.TileMode} fmt={descriptor.Format} " +
                        $"bpp={bytesPerElement} hasLayout={hasElementLayout} mipTail={baseMipInTail} " +
                        $"storage={isStorage} arrayed={isArrayed} eq={eq} -> " +
                        $"{(hasElementLayout && !baseMipInTail && IsGpuDetileBytesPerElement(bytesPerElement) && IsGpuDetileEquation(eq) ? "GPU" : "CPU")}");
                }
            }
        }

        // GPU detile: for the 4/8/16-bytes/element base-mip case the backend can
        // deswizzle on the GPU (exact-XOR and block-table equations, including
        // block-compressed formats), so ship the raw tiled bytes + params rather
        // than paying the CPU detile. Everything else keeps the CPU path below.
        //
        // Arrayed textures are handled by the arrayed branch above (they package
        // every layer's tiled slice); this branch is the single-layer case.
        if (_gpuDetileEnabled && hasElementLayout && !baseMipInTail &&
            IsGpuDetileBytesPerElement(bytesPerElement) && !isArrayed &&
            IsGpuDetileTextureType(descriptor.Type))
        {
            var gpuDetileParams = GnmTiling.GetDetileParams(
                descriptor.TileMode, bytesPerElement, elementsWide, elementsHigh);
            if (IsGpuDetileEquation(gpuDetileParams.Equation) &&
                (long)elementsWide * elementsHigh * bytesPerElement <= source.Length)
            {
                NoteSampledAddress(descriptor.Address, descriptor.Format, descriptor.NumberType);
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
                    ctx,
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
            ctx,
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
        CpuContext ctx,
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
                ctx,
                descriptor,
                isStorage,
                mipLevel,
                samplerDescriptor,
                isArrayed,
                out texture,
                snapshotAttempt + 1);
        }

        // Keep the descriptor but withhold bytes that crossed a guest write.
        // The backend can retain an older cached image and retry on a later
        // bind without publishing a torn atlas or video plane.
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
        CpuContext ctx,
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
            if (!ctx.Memory.TryRead(
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
                for (var y = 0; y < placement.ElementsHigh; y++)
                {
                    var sourceOffset = checked(
                        ((placement.TailElementY + y) * tailBlockWidth +
                         placement.TailElementX) * bytesPerElement);
                    tailLinear.AsSpan(sourceOffset, rowBytes)
                        .CopyTo(destination.Slice(y * rowBytes, rowBytes));
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



    /// <summary>
    /// On PS5 render targets alias guest memory, so pixels the game wrote with
    /// the CPU are visible before the first GPU draw (Chowdren pre-fills its
    /// fog/overlay layers that way). Seed newly created Vulkan guest images
    /// with the current guest memory contents to preserve that base layer.
    /// </summary>
    private static void ProvideRenderTargetInitialData(
        CpuContext ctx,
        RenderTargetDescriptor target)
    {
        if (!GuestGpu.Current.GuestImageWantsInitialData(target.Address))
        {
            return;
        }

        var byteCount = VulkanVideoPresenter.GetGuestImageByteCount(
            target.Format,
            target.Width,
            target.Height);
        if (byteCount == 0 || byteCount > MaxPresentedTextureBytes)
        {
            return;
        }

        var initialData = new byte[byteCount];
        var readOk = ctx.Memory.TryRead(target.Address, initialData);
        var nonZero = readOk && initialData.AsSpan().IndexOfAnyExcept((byte)0) >= 0;
        if (_traceDraws && _rtSeedTraced.Add(target.Address))
        {
            Console.Error.WriteLine(
                $"[RTSEED] addr=0x{target.Address:X} {target.Width}x{target.Height} " +
                $"read={readOk} nonZero={nonZero}");
        }

        if (nonZero)
        {
            GuestGpu.Current.ProvideGuestImageInitialData(target.Address, initialData);
        }
    }

    private static readonly HashSet<ulong> _rtSeedTraced = new();

    private static int _textureDumpCount;
    private static readonly ConcurrentDictionary<string, int> _textureDumpKeys = new();

    /// <summary>
    /// Writes raw sampled-texture bytes (as read from guest memory) when
    /// SHARPEMU_TEXTURE_DUMP_DIR is set, so upload-time content can be
    /// inspected offline. File name records size and effective pitch.
    /// </summary>
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
        // First uses plus periodic later snapshots (the game reuses the same
        // allocation for successive full-screen images).
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

    /// <summary>
    /// Writes the bytes after detiling when SHARPEMU_TEXTURE_LINEAR_DUMP_DIR is
    /// set. Keeping this separate from the raw-source dump makes AddrLib
    /// equation changes directly inspectable with ordinary image tools.
    /// </summary>
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

    private static GuestSampler ToGuestSampler(IReadOnlyList<uint> descriptor) =>
        descriptor.Count >= 4
            ? new GuestSampler(
                descriptor[0],
                descriptor[1],
                descriptor[2],
                descriptor[3])
            : default;

    internal static IReadOnlyList<uint> NormalizeSamplerDescriptorForImageOperation(
        IReadOnlyList<uint> descriptor)
    {
        const uint depthCompareMask = 0x7u << 12;
        if (descriptor.Count < 4 ||
            (descriptor[0] & depthCompareMask) == 0)
        {
            return descriptor;
        }

        // The shader translators perform guest depth comparisons after a
        // normal sample. The native sampler must not compare the value first.
        return
        [
            descriptor[0] & ~depthCompareMask,
            descriptor[1],
            descriptor[2],
            descriptor[3],
        ];
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

    private static ulong GetTextureBytesPerTexel(uint format) =>
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
            _ => 0UL,
        };

    internal static ulong GetTextureByteCount(
        uint format,
        uint width,
        uint height,
        uint depth = 1)
    {
        var bytesPerTexel = GetTextureBytesPerTexel(format);
        if (bytesPerTexel != 0)
        {
            return checked(
                (ulong)width *
                height *
                Math.Max(depth, 1u) *
                bytesPerTexel);
        }

        var blockBytes = (ulong)GetBlockCompressedBlockBytes(format);
        return blockBytes == 0
            ? 0
            : checked(
                ((ulong)width + 3) / 4 *
                (((ulong)height + 3) / 4) *
                Math.Max(depth, 1u) *
                blockBytes);
    }

    internal static uint GetTextureVolumeDepth(uint type, uint depth) =>
        type == Gen5TextureType3D
            ? Math.Max(depth, 1u)
            : 1u;

    private static uint GetLinearTexturePitch(uint pitch, uint height, uint format)
    {
        var bytesPerTexel = GetTextureBytesPerTexel(format);
        if (bytesPerTexel == 0 || height == 0)
        {
            return pitch;
        }

        // GNM linear surfaces align the row pitch to 256 bytes, so a 32px
        // RGBA8 texture is stored with a 64px (256-byte) pitch and a 288px
        // one with 320px. Reading at the unpadded width made every padded
        // tail land on the next row, which showed as transparent gaps every
        // other row on small tiles and diagonal dashes on wider surfaces.
        var pitchBytes = AlignUp((ulong)pitch * bytesPerTexel, 256UL);
        return checked((uint)(pitchBytes / bytesPerTexel));
    }

    private static ulong AlignUp(ulong value, ulong alignment) =>
        (value + alignment - 1) & ~(alignment - 1);

    private static bool TryReadTextureDescriptor(
        CpuContext ctx,
        ulong packetAddress,
        uint packetLength,
        out TextureDescriptor descriptor)
    {
        descriptor = default;
        if (packetLength < 10 ||
            !TryReadUInt32(ctx, packetAddress + 4, out var startRegister))
        {
            return false;
        }

        var valueCount = packetLength - 2;
        if (startRegister > PsTextureUserDataRegister ||
            startRegister + valueCount < PsTextureUserDataRegister + 8)
        {
            return false;
        }

        var descriptorAddress =
            packetAddress +
            8 +
            ((ulong)(PsTextureUserDataRegister - startRegister) * sizeof(uint));
        Span<uint> fields = stackalloc uint[8];
        for (var i = 0; i < fields.Length; i++)
        {
            if (!TryReadUInt32(ctx, descriptorAddress + ((ulong)i * sizeof(uint)), out fields[i]))
            {
                return false;
            }
        }

        return TryDecodeTextureDescriptor(fields.ToArray(), out descriptor);
    }

    private static bool TryDecodeTextureDescriptor(
        IReadOnlyList<uint> fields,
        out TextureDescriptor descriptor)
    {
        descriptor = default;
        if (fields.Count < 4)
        {
            return false;
        }

        // RDNA2 ISA table 45: BASE_ADDRESS is addr[47:8], WIDTH is the full
        // 16-bit field split across word1/word2, and HEIGHT is word2[29:14].
        // Keeping the high base byte is required for legal guest VAs above
        // 1 TiB; it is not descriptor metadata.
        var address = (((ulong)(fields[1] & 0xFFu) << 32) | fields[0]) << 8;
        var width = (((fields[1] >> 30) & 0x3u) | ((fields[2] & 0x3FFFu) << 2)) + 1;
        var height = ((fields[2] >> 14) & 0xFFFFu) + 1;
        var unifiedFormat = (fields[1] >> 20) & 0x1FFu;
        if (unifiedFormat == 0 ||
            !Gfx10UnifiedFormat.TryDecode(
                unifiedFormat,
                out var format,
                out var numberType))
        {
            return false;
        }
        var tileMode = (fields[3] >> 20) & 0x1Fu;
        var type = (fields[3] >> 28) & 0xFu;
        var baseLevel = (fields[3] >> 12) & 0xFu;
        var lastLevel = (fields[3] >> 16) & 0xFu;
        var bcSwizzle = (fields[3] >> 25) & 0x7u;
        var hasExtendedDescriptor = fields.Count >= 8;
        var word4 = fields.Count >= 5 ? fields[4] : 0u;
        var depthOrLastSlice = (word4 & 0x1FFFu) + 1;
        var baseArray = (word4 >> 16) & 0x1FFFu;
        // In a 256-bit 1D/2D/2D-MSAA descriptor word4[13:0] is
        // (pitch-1). A zeroed upper half denotes the common 128-bit resource,
        // where pitch is implicit; use width rather than inventing pitch=1.
        var pitch = type is 8u or 9u or 14u && word4 != 0
            ? (word4 & 0x3FFFu) + 1
            : width;
        var depth = type is 10u or 11u or 12u or 13u or 15u
            ? depthOrLastSlice
            : 1u;
        var word5 = fields.Count >= 6 ? fields[5] : 0u;
        var arrayPitch = word5 & 0xFu;
        var maxMip = (word5 >> 4) & 0xFu;
        var minLod = (fields[1] >> 8) & 0xFFFu;
        var minLodWarn = (word5 >> 8) & 0xFFFu;
        var word6 = fields.Count >= 7 ? fields[6] : 0u;
        var word7 = fields.Count >= 8 ? fields[7] : 0u;
        var metadataAddress = ((((ulong)word7 << 8) | (word6 >> 24)) << 8);
        var descriptorFlags = word6 & 0x00FF_FFFFu;
        var dstSelect = fields[3] & 0xFFFu;
        if (address == 0 || width == 0 || height == 0 || type is >= 1 and <= 7)
        {
            return false;
        }

        descriptor = new TextureDescriptor(
            address,
            width,
            height,
            format,
            numberType,
            tileMode,
            type,
            baseLevel,
            lastLevel,
            pitch,
            dstSelect,
            depth,
            baseArray,
            arrayPitch,
            maxMip,
            minLod,
            minLodWarn,
            bcSwizzle,
            metadataAddress,
            descriptorFlags,
            hasExtendedDescriptor);
        return true;
    }

    private static TextureDescriptor CreateFallbackTextureDescriptor(
        IReadOnlyList<uint> fields,
        uint instructionDimension)
    {
        var format = Gen5TextureFormatR8G8B8A8Unorm;
        var numberType = 0u;
        var tileMode = 0u;
        if (fields.Count >= 4)
        {
            var unifiedFormat = (fields[1] >> 20) & 0x1FFu;
            if (!Gfx10UnifiedFormat.TryDecode(
                    unifiedFormat,
                    out format,
                    out numberType))
            {
                format = Gen5TextureFormatR8G8B8A8Unorm;
                numberType = 0;
            }
            tileMode = (fields[3] >> 20) & 0x1Fu;
            if (format == 0)
            {
                format = Gen5TextureFormatR8G8B8A8Unorm;
            }
        }

        return new TextureDescriptor(
            Address: 0,
            Width: 1,
            Height: 1,
            Format: format,
            NumberType: numberType,
            TileMode: tileMode,
            Type: GetFallbackTextureType(instructionDimension),
            BaseLevel: 0,
            LastLevel: 0,
            Pitch: 1,
            DstSelect: 0xFAC);
    }

    internal static uint GetFallbackTextureType(uint instructionDimension) =>
        instructionDimension switch
        {
            0 => Gen5TextureType1D,
            2 => Gen5TextureType3D,
            3 => Gen5TextureTypeCube,
            4 => Gen5TextureType1DArray,
            5 or 7 => Gen5TextureType2DArray,
            _ => Gen5TextureType2D,
        };
}
