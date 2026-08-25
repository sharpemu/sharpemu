// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using SharpEmu.Libs.Gpu;
using Silk.NET.Vulkan;

internal static unsafe partial class VulkanVideoPresenter
{
    // This partial owns sampled-texture aliases for guest color images.

    internal enum GuestImageVariantStoreAction
    {
        Add,
        KeepExisting,
        FlushAndReplace,
    }

    internal static GuestImageVariantStoreAction DecideGuestImageVariantStore(
        object? stored,
        object incoming) =>
        stored is null
            ? GuestImageVariantStoreAction.Add
            : ReferenceEquals(stored, incoming)
                ? GuestImageVariantStoreAction.KeepExisting
                : GuestImageVariantStoreAction.FlushAndReplace;

    // A guest image accepts a request in a different Vulkan format without
    // being recreated when the two formats are the same texel layout read
    // through different transfer functions (sRGB vs UNORM counterparts).
    // Both must also be legal alias views of each other so the shared
    // mutable-format image can serve either identity. Same-class numeric
    // reinterpretation (R32Uint over R8G8B8A8Unorm, packed 10:10:10:2 over
    // 8:8:8:8) is excluded: the attachment keeps the existing image's
    // format, and a fragment shader translated for the other numeric type
    // would no longer match it.
    internal static bool IsAliasableGuestImageFormat(
        Format existingFormat,
        Format requestedFormat) =>
        existingFormat != requestedFormat &&
        Presenter.IsCompatibleViewFormat(existingFormat, requestedFormat) &&
        Presenter.GetStorageImageFormat(existingFormat) ==
            Presenter.GetStorageImageFormat(requestedFormat);

    internal static bool IsCompatibleGuestImageViewFormat(
        Format imageFormat,
        Format viewFormat) =>
        Presenter.IsCompatibleViewFormat(imageFormat, viewFormat);

    internal static bool HasCompatibleGuestImageTileMode(
        uint requestedTileMode,
        uint existingTileMode) =>
        requestedTileMode == existingTileMode;

    private sealed partial class Presenter
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<GuestImageResource> _pendingAliasImageDumps = new();
        private readonly record struct GuestImageVariantKey(
            ulong Address,
            uint Width,
            uint Height,
            uint Depth,
            uint Type,
            uint MipLevels,
            uint TileMode,
            uint GuestFormat,
            Format Format);

        // A single guest allocation may be rebound through several RT descriptors.
        // Keep the inactive Vulkan images instead of destroying their contents each
        // time the guest switches size or format at the same address.
        private readonly Dictionary<GuestImageVariantKey, GuestImageResource>
            _guestImageVariants = new();
        // A conflict can retain a displaced image until teardown when a
        // diagnostic queue still owns it or the device is lost.
        private readonly List<GuestImageResource> _retiredGuestImageVariants = [];
        private readonly Queue<(GuestImageResource Image, ulong RetireTimeline)>
            _deferredGuestImageVariantDestroys = new();
        private long _guestImageVariantConflictCount;
        private long _guestImageVariantSameResourceCount;
        private long _guestImageVariantReplacementCount;
        private long _guestImageVariantDeferredDestroyCount;

        private bool TryResolveGuestImageAlias(
            GuestDrawTexture texture,
            Format viewFormat,
            out GuestImageResource guestImage)
        {
            GuestImageResource? best = null;
            var bestScore = int.MinValue;
            var guestFormat = GetGuestTextureFormat(texture.Format, texture.NumberType);

            void Consider(GuestImageResource candidate, bool isActive)
            {
                if (!IsUsableGuestImageAlias(texture, viewFormat, candidate))
                {
                    return;
                }

                // Prefer an exact descriptor match. Initialization is important,
                // but it must not make a differently sized alias outrank the image
                // that the texture descriptor actually names.
                var score = 0;
                if (candidate.LogicalWidth == texture.Width &&
                    candidate.LogicalHeight == texture.Height)
                {
                    score += 32;
                }
                if (candidate.Format == viewFormat)
                {
                    score += 16;
                }
                if (candidate.GuestFormat == guestFormat)
                {
                    score += 8;
                }
                if (candidate.Initialized)
                {
                    score += 4;
                }
                if (candidate.MipLevels == texture.ResourceMipLevels)
                {
                    score += 2;
                }
                if (isActive)
                {
                    score += 1;
                }

                if (score > bestScore)
                {
                    best = candidate;
                    bestScore = score;
                }
            }

            if (_guestImages.TryGetValue(texture.Address, out var active))
            {
                Consider(active, isActive: true);
            }

            foreach (var (key, candidate) in _guestImageVariants)
            {
                if (key.Address == texture.Address)
                {
                    Consider(candidate, isActive: false);
                }
            }

            if (best is not null)
            {
                guestImage = best;
                if (ShouldTraceVulkanResources())
                {
                    Console.Error.WriteLine(
                        $"[LOADER][TRACE] vk.texture_variant_hit " +
                        $"addr=0x{texture.Address:X16} " +
                        $"tex={texture.Width}x{texture.Height}/{viewFormat} " +
                        $"image={best.Width}x{best.Height}/{best.Format} " +
                        $"initialized={best.Initialized}");
                }
                return true;
            }

            guestImage = null!;
            return false;
        }

        private static bool IsUsableGuestImageAlias(
            GuestDrawTexture texture,
            Format viewFormat,
            GuestImageResource guestImage) =>
            texture.BaseMipLevel < guestImage.MipLevels &&
            IsCompatibleGuestImageAlias(texture, guestImage) &&
            IsCompatibleViewFormat(guestImage.Format, viewFormat);

        private static bool IsCompatibleGuestImageAlias(
            GuestDrawTexture texture,
            GuestImageResource guestImage)
        {
            if (!HasCompatibleGuestImageTileMode(
                    texture.TileMode,
                    guestImage.TileMode))
            {
                return false;
            }

            var textureIs3D = IsGuestTexture3D(texture.Type);
            if (textureIs3D != IsGuestTexture3D(guestImage.Type))
            {
                return false;
            }

            if (textureIs3D)
            {
                return texture.Width == guestImage.LogicalWidth &&
                    texture.Height == guestImage.LogicalHeight &&
                    GetGuestTextureDepth(texture.Type, texture.Depth) ==
                        guestImage.LogicalDepth;
            }

            if (guestImage.LogicalWidth == texture.Width &&
                guestImage.LogicalHeight == texture.Height)
            {
                return true;
            }

            if (texture.TileMode == 0 ||
                texture.Width == 0 ||
                texture.Height == 0)
            {
                return false;
            }

            return texture.Width <= guestImage.LogicalWidth &&
                texture.Height <= guestImage.LogicalHeight;
        }

        private void StoreGuestImageVariant(
            GuestImageVariantKey key,
            GuestImageResource resource)
        {
            _guestImageVariants.TryGetValue(key, out var stored);
            switch (DecideGuestImageVariantStore(stored, resource))
            {
                case GuestImageVariantStoreAction.Add:
                    _guestImageVariants.Add(key, resource);
                    return;

                case GuestImageVariantStoreAction.KeepExisting:
                    _guestImageVariantSameResourceCount++;
                    TraceGuestImageVariantConflict(
                        "same-resource",
                        key,
                        resource,
                        resource,
                        flushed: false,
                        deferred: false);
                    return;

                case GuestImageVariantStoreAction.FlushAndReplace:
                    break;

                default:
                    throw new InvalidOperationException("Unknown guest image variant action.");
            }

            _guestImageVariantConflictCount++;
            // This method runs on the presenter thread. Submit all recorded
            // work before the dictionary stops owning the old image. Its
            // destruction waits for that submission timeline to retire.
            FlushBatchedGuestCommands();

            _guestImageVariants[key] = resource;
            _guestImageVariantReplacementCount++;

            var storedOwnedElsewhere = _guestImages.Values.Any(
                    candidate => ReferenceEquals(candidate, stored)) ||
                _guestImageVariants.Values.Any(
                    candidate => ReferenceEquals(candidate, stored));
            var storedPendingDiagnostic = _pendingAliasImageDumps.Any(
                candidate => ReferenceEquals(candidate, stored));
            var deferred = false;
            if (!storedOwnedElsewhere && !storedPendingDiagnostic && !_deviceLost)
            {
                _deferredGuestImageVariantDestroys.Enqueue((stored!, _submitTimeline));
                deferred = true;
            }
            else if (!storedOwnedElsewhere)
            {
                _retiredGuestImageVariants.Add(stored!);
            }

            TraceGuestImageVariantConflict(
                "replace",
                key,
                stored!,
                resource,
                flushed: true,
                deferred);
            ProcessDeferredTextureDestroys();
        }

        private void TraceGuestImageVariantConflict(
            string action,
            GuestImageVariantKey key,
            GuestImageResource stored,
            GuestImageResource incoming,
            bool flushed,
            bool deferred)
        {
            var count = action == "same-resource"
                ? _guestImageVariantSameResourceCount
                : _guestImageVariantConflictCount;
            if (count > 8 && (count & (count - 1)) != 0)
            {
                return;
            }

            Console.Error.WriteLine(
                $"[LOADER][WARN] vk.guest_image_variant_conflict " +
                $"action={action} count={count} seq={CurrentGuestWorkSequenceForDiagnostics} " +
                $"addr=0x{key.Address:X16} size={key.Width}x{key.Height}x{key.Depth} " +
                $"type={key.Type} mips={key.MipLevels} tile={key.TileMode} " +
                $"guest_fmt=0x{key.GuestFormat:X8} " +
                $"vk_fmt={key.Format} stored_image=0x{stored.Image.Handle:X16} " +
                $"incoming_image=0x{incoming.Image.Handle:X16} " +
                $"flushed={(flushed ? 1 : 0)} deferred={(deferred ? 1 : 0)} " +
                $"device_lost={(_deviceLost ? 1 : 0)}");
        }

        private bool TryGetOrCreateGuestImageView(
            GuestImageResource resource,
            Format format,
            uint mipLevel,
            uint levelCount,
            uint dstSelect,
            out ImageView view,
            bool arrayedView = false)
        {
            try
            {
                view = GetOrCreateGuestImageView(resource, format, mipLevel, levelCount, dstSelect, arrayedView);
                return true;
            }
            catch (Exception exception)
            {
                view = default;
                TraceVulkanShader(
                    $"vk.texture_alias_view_failed addr=0x{resource.Address:X16} " +
                    $"image_format={resource.Format} view_format={format}: {exception.Message}");
                return false;
            }
        }

        private ImageView GetOrCreateGuestImageView(
            GuestImageResource resource,
            Format format,
            uint mipLevel,
            uint levelCount,
            uint dstSelect = 0xFAC,
            bool arrayedView = false)
        {
            if (mipLevel >= resource.MipLevels)
            {
                throw new InvalidOperationException(
                    $"View mip {mipLevel} exceeds image mip count {resource.MipLevels}.");
            }

            levelCount = Math.Max(levelCount, 1);
            levelCount = Math.Min(levelCount, resource.MipLevels - mipLevel);
            if (format == resource.Format && dstSelect == 0xFAC && !arrayedView)
            {
                if (mipLevel == 0 && levelCount == resource.MipLevels)
                {
                    return resource.View;
                }

                if (levelCount == 1)
                {
                    return resource.MipViews[mipLevel];
                }
            }

            if (!IsCompatibleViewFormat(resource.Format, format))
            {
                throw new InvalidOperationException(
                    $"Incompatible image view format {format} for image {resource.Format}.");
            }

            var key = (format, mipLevel + (arrayedView ? 0x100u : 0), levelCount, dstSelect);
            if (resource.FormatViews.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var viewUsageInfo = new ImageViewUsageCreateInfo
            {
                SType = StructureType.ImageViewUsageCreateInfo,
                Usage = GetNonStorageGuestImageViewUsage(resource.Type),
            };
            var restrictViewUsage = resource.SupportsStorageUsage &&
                !SupportsStorageImage(format);
            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                PNext = restrictViewUsage ? &viewUsageInfo : null,
                Image = resource.Image,
                ViewType = GetGuestTextureViewType(resource.Type, arrayedView),
                Format = format,
                Components = ToVkComponentMapping(dstSelect),
                SubresourceRange = ColorSubresourceRange(mipLevel, levelCount),
            };
            ImageView view;
            Check(
                _vk.CreateImageView(_device, &viewInfo, null, out view),
                "vkCreateImageView(guest alias)");
            resource.FormatViews.Add(key, view);
            SetDebugName(
                ObjectType.ImageView,
                view.Handle,
                $"SharpEmu guest 0x{resource.Address:X16} alias {format} mip{mipLevel}+{levelCount}");
            TraceVulkanShader(
                $"vk.texture_alias_view addr=0x{resource.Address:X16} " +
                $"image_format={resource.Format} view_format={format} " +
                $"mip={mipLevel} levels={levelCount} dst=0x{dstSelect:X3}");
            return view;
        }

        private ImageView GetOrCreateGuestImageIdentityView(
            GuestImageResource resource,
            Format format,
            uint mipLevel,
            uint levelCount) =>
            GetOrCreateGuestImageView(
                resource,
                format,
                mipLevel,
                levelCount,
                dstSelect: 0xFAC);
    }
}
