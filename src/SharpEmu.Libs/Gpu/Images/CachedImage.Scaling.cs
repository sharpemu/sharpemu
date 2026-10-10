// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Scheduling;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Images;

// The guest-resolution twin of a scaled image and the blits that bridge the two resolutions.
//
// Guest memory layouts never change. Every path that moves guest bytes, and every copy that
// pairs this image with one at another host resolution, runs against the twin at the guest
// extent and then blits across, so callers keep describing rectangles in guest pixels.
public sealed unsafe partial class CachedImage
{
    private CachedImage? _guestSizedTwin;

    private CachedImage GuestSizedTwin()
    {
        if (_guestSizedTwin is { } existing)
        {
            return existing;
        }

        // The twin is host-owned: dropping the guest placement keeps the image cache from
        // tracking it and keeps RenderScalePolicy from scaling it in turn.
        var description = Description;
        description.Data = default;
        description.Stencil = default;
        description.Metadata = default;
        description.HtileClearMask = uint.MaxValue;
        _guestSizedTwin = new CachedImage(_device, _scheduler, _guestBacking, description);
        return _guestSizedTwin;
    }

    // The image at guest resolution: this image when it is not scaled, otherwise a twin
    // holding a downscaled copy of its current contents.
    internal CachedImage AtGuestResolution()
    {
        if (!IsScaled)
        {
            return this;
        }

        var twin = GuestSizedTwin();
        twin.BlitFrom(this);
        return twin;
    }

    private void BlitFrom(CachedImage source)
    {
        var aspect = ViewFormatRules.FullAspects(Backing.Format);
        if (source.Backing.ImageType != ImageType.Type2D || Backing.ImageType != ImageType.Type2D ||
            source.Backing.Samples != 1 || Backing.Samples != 1 ||
            source.Backing.Format != Backing.Format || (aspect & ImageAspectFlags.StencilBit) != 0)
        {
            throw SubmissionScheduler.Fatal(
                $"A scaled image blit needs single-sample 2D images of one format without stencil: " +
                $"sourceFormat={(int)source.Backing.Format} format={(int)Backing.Format} " +
                $"sourceType={(int)source.Backing.ImageType} type={(int)Backing.ImageType} " +
                $"sourceSamples={source.Backing.Samples} samples={Backing.Samples}.");
        }

        _scheduler.EndRendering();
        var command = new CommandBuffer(_scheduler.Current.Handle);
        source.Transition(ImageLayout.TransferSrcOptimal, AccessFlags.TransferReadBit, null, command);
        Transition(ImageLayout.TransferDstOptimal, AccessFlags.TransferWriteBit, null, command);
        var levels = Math.Min(source.Backing.MipLevels, Backing.MipLevels);
        var layers = Math.Min(source.Backing.Layers, Backing.Layers);
        var regions = new ImageBlit[levels];
        for (uint level = 0; level < levels; level++)
        {
            var subresource = new ImageSubresourceLayers(aspect, level, 0, layers);
            regions[level] = new ImageBlit
            {
                SrcSubresource = subresource,
                DstSubresource = subresource,
                SrcOffsets = new ImageBlit.SrcOffsetsBuffer
                {
                    Element1 = new Offset3D(
                        (int)Math.Max(source.Backing.Extent.Width >> (int)level, 1),
                        (int)Math.Max(source.Backing.Extent.Height >> (int)level, 1),
                        1),
                },
                DstOffsets = new ImageBlit.DstOffsetsBuffer
                {
                    Element1 = new Offset3D(
                        (int)Math.Max(Backing.Extent.Width >> (int)level, 1),
                        (int)Math.Max(Backing.Extent.Height >> (int)level, 1),
                        1),
                },
            };
        }

        // Depth values must not be interpolated, and Vulkan forbids a filtered depth blit.
        var filter = (aspect & ImageAspectFlags.DepthBit) != 0 ? Filter.Nearest : Filter.Linear;
        fixed (ImageBlit* blits = regions)
        {
            _device.Vk.CmdBlitImage(command, source.Backing.Handle, ImageLayout.TransferSrcOptimal,
                Backing.Handle, ImageLayout.TransferDstOptimal, (uint)regions.Length, blits, filter);
        }

        Transition(ReadyLayout, ReadyAccess, null, command);
    }

    // True when a copy between the two images would cross host resolutions. Host-owned
    // helpers are built to match the image they mirror, so only guest-placed pairs qualify.
    private bool CrossesRenderScale(CachedImage other) =>
        RenderScale != other.RenderScale && IsGuestPlaced && other.IsGuestPlaced;

    // Runs the copy at guest resolution: the scaled side contributes or receives its twin,
    // so the rectangles the caller describes keep their guest meaning.
    private bool TryCopyAcrossScales(CachedImage source, Action<CachedImage, CachedImage> copy)
    {
        if (!CrossesRenderScale(source))
        {
            return false;
        }

        var guestSource = source.AtGuestResolution();
        if (!IsScaled)
        {
            copy(this, guestSource);
            return true;
        }

        // The copy may touch only part of the image, so the twin starts from the current
        // contents and the result is blitted back over the whole image.
        var twin = GuestSizedTwin();
        twin.BlitFrom(this);
        copy(twin, guestSource);
        BlitFrom(twin);
        return true;
    }
}
