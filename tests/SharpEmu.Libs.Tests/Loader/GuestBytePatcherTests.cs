// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Loader;
using Xunit;

namespace SharpEmu.Libs.Tests.Loader;

public sealed class GuestBytePatcherTests
{
    [Fact]
    public void ParsesEntriesWithOptionalOldBytes()
    {
        var patches = GuestBytePatcher.Parse("853cdb=ffffffff?fef5ffff; 0x10 = 0x90 90 ;;20=cc");

        Assert.Equal(3, patches.Count);
        Assert.Equal(0x853cdbUL, patches[0].Offset);
        Assert.Equal(new byte[] { 0xff, 0xff, 0xff, 0xff }, patches[0].New);
        Assert.Equal(new byte[] { 0xfe, 0xf5, 0xff, 0xff }, patches[0].Old);
        Assert.Equal(0x10UL, patches[1].Offset);
        Assert.Equal(new byte[] { 0x90, 0x90 }, patches[1].New);
        Assert.Null(patches[1].Old);
        Assert.Equal(0x20UL, patches[2].Offset);
    }

    [Theory]
    [InlineData("853cdb")]
    [InlineData("=ff")]
    [InlineData("zz=ff")]
    [InlineData("10=f")]
    [InlineData("10=")]
    [InlineData("10=ff?")]
    [InlineData("10=ff?f")]
    [InlineData("10=ffff?ff")]
    public void RejectsMalformedEntries(string entry)
    {
        var exception = Assert.Throws<InvalidDataException>(() => GuestBytePatcher.Parse(entry));
        Assert.Contains(entry, exception.Message);
    }

    [Fact]
    public void ApplyChecksOldBytesAndSegmentBounds()
    {
        using var memory = new SharpEmu.Core.Memory.PhysicalVirtualMemory();
        Assert.True(memory.TryAllocateAtOrAbove(0x200000000UL, 0x10000, executable: false, 0x4000, out var imageBase));
        Assert.True(memory.TryWrite(imageBase + 0x100, new byte[] { 0xfe, 0xf5, 0xff, 0xff }));
        ProgramHeader[] headers = [CreateLoad(0, 0x4000)];

        GuestBytePatcher.Apply(memory, headers, imageBase, GuestBytePatcher.Parse("100=ffffffff?fef5ffff"));
        var actual = new byte[4];
        Assert.True(memory.TryRead(imageBase + 0x100, actual));
        Assert.Equal(new byte[] { 0xff, 0xff, 0xff, 0xff }, actual);

        // Old bytes no longer match, and nothing else is applied.
        var mismatch = Assert.Throws<InvalidDataException>(() =>
            GuestBytePatcher.Apply(memory, headers, imageBase, GuestBytePatcher.Parse("104=11;100=00000000?fef5ffff")));
        Assert.Contains("100=00000000?fef5ffff", mismatch.Message);
        Assert.True(memory.TryRead(imageBase + 0x104, actual.AsSpan(0, 1)));
        Assert.Equal(0, actual[0]);

        Assert.Throws<InvalidDataException>(() =>
            GuestBytePatcher.Apply(memory, headers, imageBase, GuestBytePatcher.Parse("3fff=0000")));
    }

    internal static ProgramHeader CreateLoad(ulong virtualAddress, ulong memorySize)
    {
        var bytes = new byte[56];
        BitConverter.TryWriteBytes(bytes.AsSpan(0), (uint)ProgramHeaderType.Load);
        BitConverter.TryWriteBytes(bytes.AsSpan(4), (uint)(ProgramHeaderFlags.Read | ProgramHeaderFlags.Execute));
        BitConverter.TryWriteBytes(bytes.AsSpan(16), virtualAddress);
        BitConverter.TryWriteBytes(bytes.AsSpan(40), memorySize);
        return System.Runtime.InteropServices.MemoryMarshal.Read<ProgramHeader>(bytes);
    }
}
