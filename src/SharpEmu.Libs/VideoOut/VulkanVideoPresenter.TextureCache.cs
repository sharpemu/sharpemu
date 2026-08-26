// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using Silk.NET.Vulkan;

internal static unsafe partial class VulkanVideoPresenter
{
    // This partial owns sampled-texture cache identity and refresh state.

    // Mirror of the render thread's texture-cache identities, readable from
    // the guest submit thread. The AGC translator used to allocate and copy
    // every referenced texture's texels out of guest memory on every draw,
    // only for the presenter to discard the bytes on a cache hit — for a
    // scene sampling large textures this was by far the dominant CPU cost
    // (gigabytes/second of allocation, page faults and GC pressure).
    // The value is the guest-write generation uploaded into the cached image.
    // Reads happen per texture per draw on the guest submit thread and must
    // not contend with render-thread mutations.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<
        TextureCacheLookupIdentity, long> _cachedTextureIdentities = new();

    internal static bool IsTextureContentCached(in TextureCacheLookupIdentity identity)
    {
        if (!_cachedTextureIdentities.TryGetValue(identity, out var uploadedGeneration))
        {
            return false;
        }

        return !SharpEmu.HLE.GuestImageWriteTracker.TryGetWriteGeneration(
                identity.Content.Address,
                out var currentGeneration) ||
            currentGeneration == uploadedGeneration;
    }

    private static void MarkTextureContentCached(
        in TextureContentIdentity content,
        in GuestSampler sampler,
        long uploadedGeneration)
    {
        if (uploadedGeneration < 0 &&
            !SharpEmu.HLE.GuestImageWriteTracker.TryGetWriteGeneration(
                content.Address,
                out uploadedGeneration))
        {
            uploadedGeneration = -1;
        }

        _cachedTextureIdentities[new TextureCacheLookupIdentity(content, sampler)] =
            uploadedGeneration;
    }

    private static void UnmarkTextureContentCached(in TextureContentIdentity content)
    {
        foreach (var identity in _cachedTextureIdentities.Keys)
        {
            if (identity.Content == content)
            {
                _cachedTextureIdentities.TryRemove(identity, out _);
            }
        }
    }

    private static void ClearCachedTextureIdentities() =>
        _cachedTextureIdentities.Clear();

    private sealed partial class Presenter
    {
        private readonly Queue<(TextureResource Texture, ulong RetireTimeline)>
            _deferredTextureDestroys = new();

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
                texture.ArrayedView,
                Math.Max(texture.ArrayLayers, 1),
                Type: texture.Type,
                Depth: GetGuestTextureDepth(texture.Type, texture.Depth),
                ResourceMipLevels: texture.ResourceMipLevels);
            if (_textureCache.TryGetValue(key, out var cached))
            {
                if (!texture.CpuSnapshotStable)
                {
                    return CreateCachedTextureBindingResource(cached, texture.Sampler);
                }

                var hasSubmittedPixels =
                    TryGetSubmittedTextureFingerprint(texture, out var fingerprint);
                if (!hasSubmittedPixels || cached.CpuContentFingerprint == fingerprint)
                {
                    if (hasSubmittedPixels)
                    {
                        TrackSampledTextureSource(texture);
                        MarkTextureContentCached(
                            key,
                            texture.Sampler,
                            texture.WriteGeneration);
                    }

                    return CreateCachedTextureBindingResource(cached, texture.Sampler);
                }

                // A new sampler binding carried newer bytes for the same guest
                // image. Preserve command order: earlier draws keep the old
                // image, while this and later draws use the replacement.
                if (_batchOpen)
                {
                    FlushBatchedGuestCommands();
                }

                if (TryRefreshCachedTextureResource(
                        texture,
                        cached,
                        fingerprint,
                        out var refreshed))
                {
                    cached.CpuContentFingerprint = fingerprint;
                    cached.WriteGeneration = texture.WriteGeneration;
                    TrackSampledTextureSource(texture);
                    MarkTextureContentCached(
                        key,
                        texture.Sampler,
                        texture.WriteGeneration);
                    return refreshed;
                }

                _textureCache.Remove(key);
                _deferredTextureDestroys.Enqueue((cached, _submitTimeline));
            }

            // Empty pixels mean the submit thread skipped the guest-memory
            // copy because this identity was marked cached; a miss here is
            // an invalidation race (eviction, cache clear). Self-heal by
            // reading the texels directly rather than rendering a fallback.
            // An unstable snapshot is different: another read outside the
            // generation transaction can publish the same torn content.
            if (!texture.CpuSnapshotStable)
            {
                return CreateTextureResource(texture);
            }

            if (texture.RgbaPixels.Length == 0 &&
                texture.TiledSource is not { Length: > 0 })
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
                TrackSampledTextureSource(texture);
                // Publish the cache hit only after write observation is live.
                // Otherwise, the submit thread can skip a copy before the
                // observer is registered.
                MarkTextureContentCached(key, texture.Sampler, texture.WriteGeneration);
            }

            return resource;
        }

        /// <summary>
        /// Uploads changed guest content into an identity-stable cached image.
        /// The existing replacement path handles unsupported upload layouts.
        /// </summary>
        private bool TryRefreshCachedTextureResource(
            GuestDrawTexture texture,
            TextureResource cached,
            ulong fingerprint,
            out TextureResource resource)
        {
            resource = null!;
            var width = Math.Max(texture.Width, 1);
            var height = Math.Max(texture.Height, 1);
            var depth = GetGuestTextureDepth(texture.Type, texture.Depth);
            var layers = IsGuestTexture3D(texture.Type)
                ? 1u
                : Math.Max(texture.ArrayLayers, 1);
            var mipLevels = texture.MipUploads is { Length: > 0 }
                ? Math.Max(texture.ResourceMipLevels, 1)
                : 1u;
            if (cached.Width != width ||
                cached.Height != height ||
                cached.Depth != depth ||
                cached.Type != texture.Type ||
                cached.Layers != layers ||
                cached.MipLevels != mipLevels)
            {
                return false;
            }

            var rowLength = texture.TileMode == 0
                ? Math.Max(texture.Pitch, width)
                : width;
            var expectedSize = GetTextureByteCount(
                texture.Format,
                rowLength,
                height,
                depth);

            if (_gpuDetileEnabled &&
                texture.MipUploads is not { Length: > 0 } &&
                texture.Detile is { } detileParameters &&
                texture.TiledSource is { Length: > 0 } tiledSource &&
                VulkanDetilePass.Supports(detileParameters) &&
                !IsGuestTexture3D(texture.Type) &&
                detileParameters.ElementsWide > 0 &&
                detileParameters.ElementsHigh > 0 &&
                (long)tiledSource.Length >=
                    (long)detileParameters.ElementsWide * detileParameters.ElementsHigh *
                    detileParameters.BytesPerElement * layers &&
                tiledSource.Length %
                    (int)(layers * (uint)detileParameters.BytesPerElement) == 0)
            {
                try
                {
                    var commandBuffer = BeginBatchedGuestCommands();
                    CloseOpenTranslatedRenderPass();
                    if (EnsureDetilePass().RecordDetile(
                            commandBuffer,
                            cached.Image,
                            ImageLayout.ShaderReadOnlyOptimal,
                            width,
                            height,
                            layers,
                            tiledSource,
                            detileParameters,
                            out var detileTransients))
                    {
                        _batchRetireDetile.Add(detileTransients);
                        resource = CreateCachedTextureBindingResource(
                            cached,
                            texture.Sampler);
                        return true;
                    }
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine(
                        $"[LOADER][WARN] Cached GPU texture refresh failed for " +
                        $"addr=0x{texture.Address:X16}: {exception.Message}");
                }
            }

            var pixels = texture.RgbaPixels;
            if (pixels.Length == 0 &&
                layers == 1 &&
                texture.TiledSource is { Length: > 0 } fallbackTiled &&
                texture.Detile is { } fallbackParameters &&
                expectedSize > 0 &&
                expectedSize <= int.MaxValue)
            {
                var linear = new byte[expectedSize];
                if (GnmTiling.TryDetile(
                        fallbackTiled,
                        linear,
                        texture.TileMode,
                        fallbackParameters.ElementsWide,
                        fallbackParameters.ElementsHigh,
                        fallbackParameters.BytesPerElement))
                {
                    pixels = linear;
                }
            }

            var expectedUploadSize = texture.MipUploads is { Length: > 0 } mipUploads
                ? GetMipUploadByteCount(texture.Format, mipUploads, layers)
                : expectedSize * layers;
            if (expectedUploadSize == 0 ||
                expectedUploadSize > int.MaxValue ||
                pixels.Length != (int)expectedUploadSize)
            {
                return false;
            }

            DumpTextureUpload(texture, pixels, rowLength, width, height);
            TraceTextureUploadContents(
                texture,
                pixels,
                rowLength,
                width,
                height,
                GetTextureFormat(texture.Format, texture.NumberType),
                "refresh");
            var uploadPixels = texture.Format == 13
                ? ExpandRgb32Pixels(pixels)
                : pixels;
            var (stagingBuffer, stagingMemory) = CreateTextureStagingBuffer(
                uploadPixels,
                $"{TextureDebugName(texture, GetTextureFormat(texture.Format, texture.NumberType))} " +
                "refresh staging");
            resource = new TextureResource
            {
                Address = cached.Address,
                StagingBuffer = stagingBuffer,
                StagingMemory = stagingMemory,
                Image = cached.Image,
                View = cached.View,
                Width = cached.Width,
                Height = cached.Height,
                Depth = cached.Depth,
                Type = cached.Type,
                RowLength = cached.RowLength,
                DstSelect = cached.DstSelect,
                Layers = cached.Layers,
                MipLevel = cached.MipLevel,
                MipLevels = cached.MipLevels,
                MipUploads = texture.MipUploads,
                NeedsUpload = true,
                RefreshesExistingImage = true,
                Cached = true,
                CpuContentFingerprint = fingerprint,
                SamplerState = texture.Sampler,
                WriteGeneration = texture.WriteGeneration,
            };
            return true;
        }

        private static bool TryGetSubmittedTextureFingerprint(
            GuestDrawTexture texture,
            out ulong fingerprint)
        {
            if (texture.TiledSource is { Length: > 0 } tiled)
            {
                fingerprint = ComputeTextureContentFingerprint(tiled);
                return true;
            }

            if (texture.RgbaPixels.Length > 0)
            {
                fingerprint = ComputeTextureContentFingerprint(texture.RgbaPixels);
                return true;
            }

            fingerprint = 0;
            return false;
        }

        private static TextureResource CreateCachedTextureBindingResource(
            TextureResource cached,
            GuestSampler sampler) =>
            new()
            {
                Address = cached.Address,
                Image = cached.Image,
                View = cached.View,
                Width = cached.Width,
                Height = cached.Height,
                Depth = cached.Depth,
                Type = cached.Type,
                RowLength = cached.RowLength,
                DstSelect = cached.DstSelect,
                Layers = cached.Layers,
                MipLevel = cached.MipLevel,
                MipLevels = cached.MipLevels,
                IsStorage = cached.IsStorage,
                Cached = true,
                SamplerState = sampler,
            };

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
                RecycleHostBuffer(texture.StagingBuffer, texture.StagingMemory);
            }
        }
    }
}
