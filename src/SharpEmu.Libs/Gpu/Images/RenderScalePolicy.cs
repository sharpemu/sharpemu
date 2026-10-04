// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Images;

// Picks the host resolution of one guest image.
//
// The choice is a pure function of the guest description, so every view, alias, copy and
// descriptor of the same guest memory reaches the same answer without consulting the cache.
// Guest memory layouts never change: a scaled image only differs in the size of its host
// backing, and the transfer paths route guest bytes through a guest-sized intermediate.
public static class RenderScalePolicy
{
    public const string VariableName = "SHARPEMU_RENDER_SCALE";

    // Below this a target is a shadow map, a LUT, a probe atlas or an eye-adaptation
    // readback, and scaling it changes what the game computes rather than how it looks.
    public const uint MinimumScaledWidth = 1280;
    public const uint MinimumScaledHeight = 720;

    private static readonly float? _environmentScale = ReadEnvironmentScale();
    private static float _scale = _environmentScale ?? 1.0f;

    public static float Scale => Volatile.Read(ref _scale);

    public static bool Enabled => Scale != 1.0f;

    // The environment variable wins so scripted runs do not depend on stored settings.
    public static void Configure(float scale)
    {
        if (_environmentScale is not null)
        {
            return;
        }

        Volatile.Write(ref _scale, VideoOut.HostVideoOptions.NormalizeRenderScale(scale));
    }

    private static float? ReadEnvironmentScale()
    {
        var text = Environment.GetEnvironmentVariable(VariableName);
        if (string.IsNullOrWhiteSpace(text) ||
            !float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return null;
        }

        return VideoOut.HostVideoOptions.NormalizeRenderScale(parsed);
    }

    public static uint ScaleLength(uint length, float scale)
    {
        if (scale == 1.0f || length <= 1)
        {
            return length;
        }

        var scaled = (uint)Math.Ceiling(length * (double)scale);
        return Math.Clamp(scaled, 1u, 32768u);
    }

    // The guest-pixel extent the clip-disable vertex transform maps onto. Dividing the device
    // limit by an upscale keeps the host viewport (reference x scale) inside that limit while
    // the transform the vertex program bakes stays scale independent.
    public static uint ClipSpaceReferenceExtent(uint limit)
    {
        var scale = Scale;
        return scale <= 1.0f ? limit : Math.Max(1u, (uint)(limit / scale));
    }

    public static Extent3D ScaleExtent(Extent3D extent, float scale) => scale == 1.0f
        ? extent
        : new Extent3D(ScaleLength(extent.Width, scale), ScaleLength(extent.Height, scale), extent.Depth);

    private static Func<Format, bool>? _formatSupport;

    // Installed once with the device: a scaled image is resized with vkCmdBlitImage whenever
    // guest bytes or a differently scaled alias cross into it, so a format that cannot blit
    // must keep its guest resolution.
    public static void ConfigureFormatSupport(Func<Format, bool> supportsScaledResize) =>
        Volatile.Write(ref _formatSupport, supportsScaledResize);

    public static float ScaleFor(in ImageDescription description) => ScaleFor(description, Scale);

    // Only screen-class, single-level, single-sample, uncompressed 2D render and depth
    // targets scale. Everything the host cannot reproduce at another size - block grids,
    // multisample footprints, HTile and DCC block coverage, mip chains whose levels would
    // round apart, cube and array faces - keeps its guest resolution.
    public static float ScaleFor(in ImageDescription description, float scale)
    {
        if (scale == 1.0f || !IsScalable(description))
        {
            return 1.0f;
        }

        return scale;
    }

    public static bool IsScalable(in ImageDescription description)
    {
        // A stencil-association entry carries no host image, and an image without guest
        // memory is an internal helper whose extent must track whatever it mirrors.
        if (description.PixelFormat == Format.Undefined || !ImageDescription.IsValidRange(description.Data))
        {
            return false;
        }

        if (description.Type != GuestImageType.Color2D || description.Samples != 1 ||
            description.Resources.Levels != 1 || description.Resources.Layers != 1)
        {
            return false;
        }

        // DCC only describes guest bytes the host never stores, so a scaled backing keeps it
        // meaningful. HTile encodes guest depth blocks the renderer reads back as clear state.
        if (description.IsBlock || description.HasStencil || description.Metadata.Kind == MetadataKind.Htile)
        {
            return false;
        }

        // Render and depth tilings mark the surfaces a game draws into. Asset textures use
        // the standard tilings and must stay at their authored size.
        if (description.TileMode is not (GuestTileMode.RenderTarget or GuestTileMode.Depth))
        {
            return false;
        }

        if (description.Extent.Width < MinimumScaledWidth || description.Extent.Height < MinimumScaledHeight)
        {
            return false;
        }

        // A square target at screen size is a cube face or a shadow atlas far more often
        // than it is a scene buffer.
        if (description.Extent.Width == description.Extent.Height)
        {
            return false;
        }

        // Only a backend that installed the predicate (the Vulkan device) can resize an image.
        return Volatile.Read(ref _formatSupport) is { } supported && supported(description.PixelFormat);
    }
}
