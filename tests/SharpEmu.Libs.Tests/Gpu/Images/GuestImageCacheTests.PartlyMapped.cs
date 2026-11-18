// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Buffers;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Images;

// The shape of the Ghost of Yotei loading failure: a streamed texture's range is mapped at its start
// only (the resident levels) and its last bytes are unmapped. The upload reads the bytes of the levels
// the image holds and nothing else; it never invents the bytes of a level the image holds.
public sealed partial class GuestImageCacheTests
{
    private static (ulong Address, ulong Resident, byte[] Fill) MapStreamedPrefix(CacheHarness harness, byte value)
    {
        var layout = StreamedTexture(0x1_0000_0000, 1).Description;
        // The tiled chain keeps level 0 at its end: everything before it is the resident prefix.
        Assert.True(layout.MipLayout[0].Offset > layout.MipLayout[1].Offset);
        var resident = layout.MipLayout[0].Offset / harness.Granularity * harness.Granularity;
        Assert.True(resident >= layout.MipLayout[1].Offset + layout.MipLayout[1].Size);
        var address = harness.MapBackedPrefix(layout.Data.Size, resident);
        var fill = new byte[resident];
        Array.Fill(fill, value);
        harness.Write(address, fill);
        return (address, resident, fill);
    }

    // Names no resident-level API, so it also runs against a cache that reads the whole chain, where
    // the unmapped tail fails the upload.
    [Fact]
    public void PartlyMappedStreamedTexture_UploadsItsMappedLevels()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var (address, _, _) = MapStreamedPrefix(harness, 0x5A);

        var request = StreamedTexture(address, 1);
        var identifier = harness.Acquire(ref request);

        // Every mapped byte is one value, so the first host level reads it whatever the tiling.
        Assert.All(harness.ReadImageBytes(harness.Images.Owner(identifier)!), value => Assert.Equal(0x5A, value));
    }

    [Fact]
    public void StreamedTexture_HoldsOnlyItsResidentLevels()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var (address, _, _) = MapStreamedPrefix(harness, 0x5A);

        var request = StreamedTexture(address, 1);
        var identifier = harness.Acquire(ref request);
        var image = harness.Images.Owner(identifier)!;

        Assert.Equal(1u, image.Description.FirstLevel);
        Assert.Equal(new Extent3D(StreamedSize / 2, StreamedSize / 2, 1), image.Backing.Extent);
        Assert.Equal(StreamedLevels - 1, image.Backing.MipLevels);
        Assert.Equal(0u, request.View.BaseLevel);
        // A quarter of the chain's level 0 alone: the unmapped level 0 has no host memory.
        Assert.True(image.AccountedSize < (ulong)StreamedSize * StreamedSize * 4 / 2);
        // The image reads the bytes that hold its levels, not the unmapped end of the range.
        Assert.True(image.GuestReadSize < image.Description.Data.Size);
        Assert.Equal(0x5A, harness.ReadImageBytes(image)[0]);
        Assert.All(harness.ReadImageBytes(image), value => Assert.Equal(0x5A, value));
    }

    // The image hashes the head and tail of the guest bytes it reads; the unmapped end of the range
    // is not among them.
    [Fact]
    public void StreamedTexture_HashesOnlyTheGuestBytesItReads()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var (address, _, _) = MapStreamedPrefix(harness, 0x5A);

        var request = StreamedTexture(address, 1);
        var image = harness.Images.Owner(harness.Acquire(ref request))!;

        _ = image.HashGuestEdges();
    }

    // A byte of a level the image holds is never invented as zero: when the mapping stops inside the
    // held levels the upload fails as it always has.
    [Fact]
    public void StreamedTexture_AnUnmappedByteOfAHeldLevelFailsTheUpload()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var fatal = new FatalScope();
        using var harness = new CacheHarness(_vulkan);
        var layout = StreamedTexture(0x1_0000_0000, 1).Description;
        var held = layout.MipLayout[1].Offset + layout.MipLayout[1].Size;
        Assert.True(held > harness.Granularity);
        var address = harness.MapBackedPrefix(layout.Data.Size, harness.Granularity);

        var request = StreamedTexture(address, 1);
        Assert.ThrowsAny<Exception>(() => harness.Acquire(ref request));
        Assert.Contains(fatal.Messages, message => message.Contains("Could not read the mapped guest image backing", StringComparison.Ordinal));
    }
}