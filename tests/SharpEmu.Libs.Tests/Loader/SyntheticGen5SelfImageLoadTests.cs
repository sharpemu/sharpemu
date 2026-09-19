// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;
using Xunit;

namespace SharpEmu.Libs.Tests.Loader;

/// <summary>
/// End-to-end loader coverage for the minimal PS5 SELF segment-table fixture.
/// </summary>
public sealed class SyntheticGen5SelfImageLoadTests
{
    [Fact]
    public void MinimalSelfSegmentTableImage_UsesPhysicalOffsetAndZeroFillsMappedExtent()
    {
        ReadOnlySpan<byte> payload = [0x31, 0xC0, 0xC3];
        var imageData = SyntheticGen5SelfImageBuilder.BuildMinimalSelfSegmentTableImage(payload);
        var memory = new VirtualMemory();

        var image = new SelfLoader().Load(imageData, memory);

        Assert.True(image.IsSelf);
        Assert.Equal((byte)2, image.ElfHeader.AbiVersion);
        Assert.Equal(
            SyntheticGen5SelfImageBuilder.ImageBase + SyntheticGen5SelfImageBuilder.PayloadVirtualAddress,
            image.EntryPoint);

        var programHeader = Assert.Single(image.ProgramHeaders);
        Assert.Equal((ulong)SyntheticGen5SelfImageBuilder.ElfPayloadFileOffset, programHeader.Offset);
        Assert.Equal(ProgramHeaderType.Load, programHeader.HeaderType);
        Assert.Equal((ulong)payload.Length, programHeader.FileSize);
        Assert.Equal(SyntheticGen5SelfImageBuilder.DefaultMemorySize, programHeader.MemorySize);

        var mappedRegion = Assert.Single(image.MappedRegions);
        Assert.Equal(image.EntryPoint, mappedRegion.VirtualAddress);
        Assert.Equal((ulong)SyntheticGen5SelfImageBuilder.SelfPayloadFileOffset, mappedRegion.FileOffset);
        Assert.NotEqual(programHeader.Offset, mappedRegion.FileOffset);
        Assert.Equal((ulong)payload.Length, mappedRegion.FileSize);
        Assert.Equal(SyntheticGen5SelfImageBuilder.DefaultMemorySize, mappedRegion.MemorySize);
        Assert.Equal(ProgramHeaderFlags.Read | ProgramHeaderFlags.Execute, mappedRegion.Protection);

        Span<byte> mappedBytes = stackalloc byte[8];
        Assert.True(memory.TryRead(image.EntryPoint, mappedBytes));
        Assert.Equal(payload.ToArray(), mappedBytes[..payload.Length].ToArray());
        Assert.Equal(new byte[mappedBytes.Length - payload.Length], mappedBytes[payload.Length..].ToArray());

        Span<byte> tailBytes = stackalloc byte[16];
        var tailAddress =
            image.EntryPoint + SyntheticGen5SelfImageBuilder.DefaultMemorySize - (ulong)tailBytes.Length;
        Assert.True(memory.TryRead(tailAddress, tailBytes));
        Assert.Equal(new byte[tailBytes.Length], tailBytes.ToArray());
    }
}
