// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Core.Loader;
using Xunit;

namespace SharpEmu.Libs.Tests.Loader;

public sealed class TitleDisplayPatchesTests
{
    private static readonly TitleDisplayPatches.Site[] Sites =
    [
        new(0x100, TitleDisplayPatches.Slot.Width, 3840, 2160, TitleDisplayPatches.Role.Output),
        new(0x104, TitleDisplayPatches.Slot.Height, 3840, 2160, TitleDisplayPatches.Role.Output),
        new(0x108, TitleDisplayPatches.Slot.WidthHeight, 3840, 2160, TitleDisplayPatches.Role.Output),
        new(0x110, TitleDisplayPatches.Slot.WidthF32, 3840, 2160, TitleDisplayPatches.Role.Output),
        new(0x114, TitleDisplayPatches.Slot.HeightF32, 3840, 2160, TitleDisplayPatches.Role.Output),
        new(0x118, TitleDisplayPatches.Slot.Width, 2560, 1440, TitleDisplayPatches.Role.Internal),
        new(0x11C, TitleDisplayPatches.Slot.Height, 2560, 1440, TitleDisplayPatches.Role.Internal),
    ];

    [Fact]
    public void BuildsEveryEncodingForA1080pDisplayAndShrinksTheInternalSize()
    {
        var patches = TitleDisplayPatches.BuildPatches(Sites, 1920, 1080);

        Assert.Equal(7, patches.Count);
        Assert.Equal(Le32(3840), patches[0].Old);
        Assert.Equal(Le32(1920), patches[0].New);
        Assert.Equal(Le32(2160), patches[1].Old);
        Assert.Equal(Le32(1080), patches[1].New);
        Assert.Equal(Le64(3840 | (2160UL << 32)), patches[2].Old);
        Assert.Equal(Le64(1920 | (1080UL << 32)), patches[2].New);
        Assert.Equal(F32(3840f), patches[3].Old);
        Assert.Equal(F32(1920f), patches[3].New);
        Assert.Equal(F32(2160f), patches[4].Old);
        Assert.Equal(F32(1080f), patches[4].New);
        Assert.Equal(Le32(2560), patches[5].Old);
        Assert.Equal(Le32(1920), patches[5].New);
        Assert.Equal(Le32(1440), patches[6].Old);
        Assert.Equal(Le32(1080), patches[6].New);
    }

    [Fact]
    public void A1440pDisplayLeavesA1440pInternalSizeAlone()
    {
        var patches = TitleDisplayPatches.BuildPatches(Sites, 2560, 1440);

        Assert.Equal(5, patches.Count);
        Assert.DoesNotContain(patches, patch => patch.Offset >= 0x118);
    }

    [Fact]
    public void ADisplayAtTheCompiledSizeNeedsNoPatch()
    {
        Assert.Empty(TitleDisplayPatches.BuildPatches(Sites, 3840, 2160));
    }

    [Fact]
    public void KnownTitlesHaveSitesAndOthersDoNot()
    {
        Assert.NotNull(TitleDisplayPatches.SitesFor("PPSA01342"));
        Assert.Null(TitleDisplayPatches.SitesFor("PPSA00000"));
        Assert.Null(TitleDisplayPatches.SitesFor(null));
    }

    [Fact]
    public void ApplyRewritesVerifiedSitesAndSkipsEverythingOnAMismatch()
    {
        using var memory = new SharpEmu.Core.Memory.PhysicalVirtualMemory();
        Assert.True(memory.TryAllocateAtOrAbove(0x200000000UL, 0x10000, executable: false, 0x4000, out var imageBase));
        ProgramHeader[] headers = [CreateLoad(0, 0x4000)];
        var sites = TitleDisplayPatches.SitesFor("PPSA01342")!;
        foreach (var site in sites)
        {
            Assert.True(memory.TryWrite(imageBase + (site.Offset & 0x3FFF), Encode(site, site.OldWidth, site.OldHeight)));
        }

        // The real sites lie beyond this small image, so apply through relocated copies.
        var relocated = sites.Select(site => site with { Offset = site.Offset & 0x3FFF }).ToArray();
        var patches = TitleDisplayPatches.BuildPatches(relocated, 1920, 1080);
        GuestBytePatcher.Apply(memory, headers, imageBase, patches);
        var first = new byte[4];
        Assert.True(memory.TryRead(imageBase + (relocated[0].Offset), first));
        Assert.Equal(Le32(1920), first);

        // A second application sees 1920 where 3840 was expected and touches nothing.
        Assert.False(TitleDisplayPatches.Apply(memory, headers, imageBase, "PPSA00000", 1920, 1080));
        Assert.False(TitleDisplayPatches.Apply(memory, headers, imageBase, "PPSA01342", 1920, 1080));
    }

    private static byte[] Encode(TitleDisplayPatches.Site site, uint width, uint height) =>
        site.Slot switch
        {
            TitleDisplayPatches.Slot.Width => Le32(width),
            TitleDisplayPatches.Slot.Height => Le32(height),
            TitleDisplayPatches.Slot.WidthHeight => Le64(width | ((ulong)height << 32)),
            TitleDisplayPatches.Slot.WidthF32 => F32(width),
            TitleDisplayPatches.Slot.HeightF32 => F32(height),
            _ => throw new ArgumentOutOfRangeException(nameof(site)),
        };

    private static byte[] Le32(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] Le64(ulong value)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] F32(float value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(bytes, value);
        return bytes;
    }

    private static ProgramHeader CreateLoad(ulong virtualAddress, ulong size) =>
        GuestBytePatcherTests.CreateLoad(virtualAddress, size);
}
