// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Core.Memory;

namespace SharpEmu.Core.Loader;

/// <summary>
/// Per-title rewrites of compiled display sizes. Some titles never ask sceVideoOut what the
/// display is: they register 3840x2160 output buffers, size their UI canvas to it and keep
/// their internal render size in a mode table, all as immediates and read-only data. Telling
/// such a title that the display is 1080p changes nothing, so when the host's display patch
/// resolution is below the compiled size, this rewrites the compiled constants to that
/// size. Every site is verified against the bytes the known build carries before anything is
/// written; a different build leaves the title untouched.
/// </summary>
internal static class TitleDisplayPatches
{
    public const string EnvironmentVariable = "SHARPEMU_TITLE_DISPLAY_PATCH";

    /// <summary>What a site encodes. 32-bit widths and heights are little-endian integers;
    /// a <see cref="Slot.WidthHeight"/> site packs width in the low and height in the high
    /// dword of one 64-bit immediate; the float slots are IEEE singles.</summary>
    internal enum Slot
    {
        Width,
        Height,
        WidthHeight,
        WidthF32,
        HeightF32,
    }

    internal enum Role
    {
        /// <summary>The output buffer, display size or UI canvas: becomes the display size.</summary>
        Output,

        /// <summary>The internal render size: never grows, shrinks to the display size.</summary>
        Internal,
    }

    internal readonly record struct Site(ulong Offset, Slot Slot, uint OldWidth, uint OldHeight, Role Role);

    internal static bool IsEnabled =>
        !string.Equals(Environment.GetEnvironmentVariable(EnvironmentVariable), "0", StringComparison.Ordinal);

    // Demon's Souls (PPSA01342, v01.004.000). The video-out setup creates its "DisplayBuffer"
    // textures and registers them at 3840x2160 (immediates at the call sites and in the two
    // display-size getters), the render configs in read-only data carry the output and
    // display size, the mode tables (index 2 performance, 3 cinematic) carry the display and
    // internal sizes, and the UI lays itself out on a 3840x2160 float canvas.
    private static readonly Site[] DemonsSouls =
    [
        new(0x94535C, Slot.Width, 3840, 2160, Role.Output),
        new(0x945361, Slot.Height, 3840, 2160, Role.Output),
        new(0x94542E, Slot.Width, 3840, 2160, Role.Output),
        new(0x945433, Slot.Height, 3840, 2160, Role.Output),
        new(0x944A48, Slot.WidthHeight, 3840, 2160, Role.Output),
        new(0x945004, Slot.WidthHeight, 3840, 2160, Role.Output),
        new(0x945579, Slot.WidthHeight, 3840, 2160, Role.Output),
        new(0x9E7F88, Slot.Width, 3840, 2160, Role.Output),
        new(0x9E7FB8, Slot.Height, 3840, 2160, Role.Output),
        new(0x20010AC, Slot.WidthHeight, 3840, 2160, Role.Output),
        new(0x20010B4, Slot.WidthHeight, 3840, 2160, Role.Output),
        new(0x20013EC, Slot.WidthHeight, 3840, 2160, Role.Output),
        new(0x20013F4, Slot.WidthHeight, 3840, 2160, Role.Output),
        new(0x20010A4, Slot.Width, 2560, 1440, Role.Internal),
        new(0x20010A8, Slot.Height, 2560, 1440, Role.Internal),
        new(0x20013E4, Slot.WidthHeight, 3840, 2160, Role.Internal),
        new(0x1FFA5D8, Slot.Width, 3840, 2160, Role.Output),
        new(0x1FFA5DC, Slot.Width, 3840, 2160, Role.Output),
        new(0x1FFBE38, Slot.Height, 3840, 2160, Role.Output),
        new(0x1FFBE3C, Slot.Height, 3840, 2160, Role.Output),
        new(0x1FFBE28, Slot.Width, 2560, 1440, Role.Internal),
        new(0x1FFBE2C, Slot.Width, 3840, 2160, Role.Internal),
        new(0x1FFD758, Slot.Height, 2560, 1440, Role.Internal),
        new(0x1FFD75C, Slot.Height, 3840, 2160, Role.Internal),
        new(0x1135E17, Slot.WidthF32, 3840, 2160, Role.Output),
        new(0x1135E1B, Slot.HeightF32, 3840, 2160, Role.Output),
        new(0x1FF9C60, Slot.WidthF32, 3840, 2160, Role.Output),
        new(0x1FF9C64, Slot.HeightF32, 3840, 2160, Role.Output),
        new(0x1FFEA50, Slot.WidthF32, 3840, 2160, Role.Output),
        new(0x1FFE588, Slot.HeightF32, 3840, 2160, Role.Output),
        new(0x2172C04, Slot.WidthF32, 3840, 2160, Role.Output),
        new(0x2172A0C, Slot.HeightF32, 3840, 2160, Role.Output),
    ];

    internal static IReadOnlyList<Site>? SitesFor(string? titleId) =>
        titleId switch
        {
            "PPSA01342" => DemonsSouls,
            _ => null,
        };

    /// <summary>Rewrites the title's compiled display sizes to <paramref name="width"/> x
    /// <paramref name="height"/>. Returns true when the sites were rewritten.</summary>
    public static bool Apply(
        IVirtualMemory memory,
        IReadOnlyList<ProgramHeader> programHeaders,
        ulong imageBase,
        string? titleId,
        uint width,
        uint height)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(programHeaders);
        if (!IsEnabled || width == 0 || height == 0)
        {
            return false;
        }

        var sites = SitesFor(titleId);
        if (sites is null)
        {
            return false;
        }

        var patches = BuildPatches(sites, width, height);
        if (patches.Count == 0)
        {
            return false;
        }

        // A build other than the one the sites were taken from keeps every byte: verify all
        // of them before the patcher (which verifies again) writes any.
        var current = new byte[8];
        foreach (var patch in patches)
        {
            var span = current.AsSpan(0, patch.New.Length);
            if (!memory.TryRead(imageBase + patch.Offset, span) || !span.SequenceEqual(patch.Old))
            {
                Console.Error.WriteLine(
                    $"[LOADER][WARN] {titleId}: display size patch skipped, the executable does not carry the " +
                    $"expected bytes at 0x{patch.Offset:X} (a different build?).");
                return false;
            }
        }

        GuestBytePatcher.Apply(memory, programHeaders, imageBase, patches);
        Console.Error.WriteLine(
            $"[LOADER][INFO] {titleId}: compiled display sizes rewritten to {width}x{height} at {patches.Count} sites " +
            $"(set {EnvironmentVariable}=0 to keep the title's own).");
        return true;
    }

    internal static List<GuestBytePatcher.Patch> BuildPatches(IReadOnlyList<Site> sites, uint width, uint height)
    {
        var patches = new List<GuestBytePatcher.Patch>(sites.Count);
        foreach (var site in sites)
        {
            var newWidth = site.Role == Role.Internal ? Math.Min(width, site.OldWidth) : width;
            var newHeight = site.Role == Role.Internal ? Math.Min(height, site.OldHeight) : height;
            if (newWidth == site.OldWidth && newHeight == site.OldHeight)
            {
                continue;
            }

            var old = Encode(site.Slot, site.OldWidth, site.OldHeight);
            var replacement = Encode(site.Slot, newWidth, newHeight);
            if (old.AsSpan().SequenceEqual(replacement))
            {
                continue;
            }

            patches.Add(new GuestBytePatcher.Patch(
                site.Offset,
                replacement,
                old,
                $"0x{site.Offset:X}={Convert.ToHexString(replacement).ToLowerInvariant()}?{Convert.ToHexString(old).ToLowerInvariant()}"));
        }

        return patches;
    }

    private static byte[] Encode(Slot slot, uint width, uint height)
    {
        switch (slot)
        {
            case Slot.Width:
            {
                var bytes = new byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(bytes, width);
                return bytes;
            }

            case Slot.Height:
            {
                var bytes = new byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(bytes, height);
                return bytes;
            }

            case Slot.WidthHeight:
            {
                var bytes = new byte[8];
                BinaryPrimitives.WriteUInt64LittleEndian(bytes, width | ((ulong)height << 32));
                return bytes;
            }

            case Slot.WidthF32:
            {
                var bytes = new byte[4];
                BinaryPrimitives.WriteSingleLittleEndian(bytes, width);
                return bytes;
            }

            case Slot.HeightF32:
            {
                var bytes = new byte[4];
                BinaryPrimitives.WriteSingleLittleEndian(bytes, height);
                return bytes;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(slot), slot, null);
        }
    }
}
