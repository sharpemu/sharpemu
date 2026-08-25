// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

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
