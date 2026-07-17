// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Gpu.Metal;

// Draw textures decoded from guest memory are cached across draws keyed by
// their full descriptor identity, mirroring the Vulkan presenter's texture
// cache: once an identity is marked cached, the AGC submit thread skips the
// guest-memory read/detile/copy entirely (shipping empty texels) and the
// render thread serves the cached MTLTexture — for scenes that sample large
// textures every draw, that per-draw copy dominated both allocation churn
// and CPU time. GuestImageWriteTracker write-protects the source pages, so
// a guest CPU write dirties the address and the entry is evicted at the next
// drain; the following draw ships fresh texels and re-populates the cache.
internal static partial class MetalVideoPresenter
{
    private const int MaxCachedDrawTextures = 2048;

    /// <summary>Render-thread-only cache of decoded draw textures; each value
    /// holds one retain. Committed command buffers retain the textures they
    /// reference, so eviction releases immediately without a GPU drain.</summary>
    private static readonly Dictionary<TextureContentIdentity, nint> _drawTextureCache = new();

    /// <summary>Identities the AGC submit thread may skip texel copies for.
    /// Read from the submit thread, written by the render thread.</summary>
    private static readonly ConcurrentDictionary<TextureContentIdentity, byte> _cachedDrawTextureIdentities = new();

    internal static bool IsTextureContentCached(in TextureContentIdentity identity) =>
        _cachedDrawTextureIdentities.ContainsKey(identity);

    /// <summary>Builds the same identity the AGC layer checks before skipping
    /// a texel copy; the two must agree field-for-field or skips and cache
    /// entries would never line up.</summary>
    private static TextureContentIdentity GetDrawTextureIdentity(GuestDrawTexture texture) => new(
        texture.Address,
        texture.Width,
        texture.Height,
        texture.Format,
        texture.NumberType,
        texture.DstSelect,
        texture.TileMode,
        texture.Pitch,
        texture.Sampler);

    /// <summary>Caching requires the write tracker: without page protection a
    /// guest CPU write would never evict the entry and draws would sample
    /// stale texels forever. Storage textures are shader-writable on the GPU,
    /// so their content identity is not stable either.</summary>
    private static bool IsCacheableDrawTexture(GuestDrawTexture texture) =>
        GuestImageWriteTracker.Enabled &&
        texture.Address != 0 &&
        !texture.IsStorage &&
        !texture.IsFallback;

    private static bool TryGetCachedDrawTexture(GuestDrawTexture texture, out nint handle) =>
        _drawTextureCache.TryGetValue(GetDrawTextureIdentity(texture), out handle);

    private static void CacheDrawTexture(GuestDrawTexture texture, nint handle)
    {
        var key = GetDrawTextureIdentity(texture);
        if (_drawTextureCache.Remove(key, out var previous))
        {
            MetalNative.SendVoid(previous, MetalNative.Selector("release"));
        }

        _ = MetalNative.Send(handle, MetalNative.Selector("retain"));
        _drawTextureCache[key] = handle;
        _cachedDrawTextureIdentities[key] = 0;
        GuestImageWriteTracker.Track(
            texture.Address,
            (ulong)texture.RgbaPixels.Length,
            Volatile.Read(ref _executingGuestWorkSequence),
            "metal.texture-cache");
    }

    /// <summary>Runs once per drain, before any queued draw executes: a draw
    /// whose texels the submit thread skipped must never resolve to an entry
    /// the guest has since rewritten.</summary>
    private static void EvictDirtyCachedDrawTextures()
    {
        if (_drawTextureCache.Count == 0)
        {
            return;
        }

        // Evict by address rather than by identity: several identities can
        // share one source address (same texels, different samplers), and
        // ConsumeDirty clears the flag on first read — evicting only the
        // first identity would leave the others sampling stale texels.
        HashSet<ulong>? dirtyAddresses = null;
        foreach (var entry in _drawTextureCache)
        {
            if (dirtyAddresses is not null && dirtyAddresses.Contains(entry.Key.Address))
            {
                continue;
            }

            if (GuestImageWriteTracker.ConsumeDirty(entry.Key.Address))
            {
                (dirtyAddresses ??= []).Add(entry.Key.Address);
            }
        }

        if (dirtyAddresses is null && _drawTextureCache.Count <= MaxCachedDrawTextures)
        {
            return;
        }

        if (_drawTextureCache.Count > MaxCachedDrawTextures)
        {
            foreach (var entry in _drawTextureCache)
            {
                MetalNative.SendVoid(entry.Value, MetalNative.Selector("release"));
            }

            _drawTextureCache.Clear();
            _cachedDrawTextureIdentities.Clear();
            return;
        }

        List<TextureContentIdentity>? evicted = null;
        foreach (var entry in _drawTextureCache)
        {
            if (dirtyAddresses!.Contains(entry.Key.Address))
            {
                (evicted ??= []).Add(entry.Key);
            }
        }

        if (evicted is not null)
        {
            foreach (var key in evicted)
            {
                if (_drawTextureCache.Remove(key, out var handle))
                {
                    _cachedDrawTextureIdentities.TryRemove(key, out _);
                    MetalNative.SendVoid(handle, MetalNative.Selector("release"));
                }
            }
        }

        foreach (var address in dirtyAddresses!)
        {
            GuestImageWriteTracker.Rearm(address);
        }
    }

    /// <summary>Self-heal for the skip/eviction race: the submit thread saw a
    /// cached identity and skipped the copy, but the entry was evicted before
    /// this draw executed. Read the texels directly rather than rendering a
    /// fallback texture for the frame, sized with the same block-aware math
    /// the draw path expects.</summary>
    private static byte[]? TryReadGuestDrawTexturePixels(GuestDrawTexture texture)
    {
        var memory = _guestMemory;
        if (memory is null || texture.Address == 0)
        {
            return null;
        }

        var width = Math.Max(texture.Width, 1u);
        var height = Math.Max(texture.Height, 1u);
        var rowLength = texture.TileMode == 0
            ? Math.Max(texture.Pitch, width)
            : width;
        var format = MetalGuestFormats.DecodeTextureFormat(texture.Format, texture.NumberType);
        var byteCount = MetalGuestFormats.GetTextureByteCount(format, rowLength, height);
        if (byteCount == 0 || byteCount > int.MaxValue)
        {
            return null;
        }

        var pixels = new byte[(int)byteCount];
        return memory.TryRead(texture.Address, pixels) ? pixels : null;
    }
}
