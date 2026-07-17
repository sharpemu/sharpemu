// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.AvPlayer;
using System.Buffers.Binary;
using Xunit;

namespace SharpEmu.Libs.Tests.AvPlayer;

public sealed class AvPlayerVideoFrameLayoutTests
{
    [Theory]
    [InlineData(Generation.Gen5, true, false, 1920, 1080, 2048, 0x32A000)]
    [InlineData(Generation.Gen5, false, true, 1920, 1088, 2048, 0x330000)]
    [InlineData(Generation.Gen4, false, false, 1920, 1088, 1920, 0x2FD000)]
    [InlineData(Generation.Gen4, true, true, 1920, 1080, 1920, 0x2FD000)]
    public void LayoutMatchesGenerationAndDecoderContract(
        Generation generation,
        bool software2,
        bool extended,
        int expectedWidth,
        int expectedHeight,
        int expectedPitch,
        int expectedSize)
    {
        var layout = AvPlayerExports.GetVideoFrameLayout(
            generation,
            sourceWidth: 1920,
            sourceHeight: 1080,
            useVideoDecoderSoftware2: software2,
            extended: extended);

        Assert.Equal(expectedWidth, layout.Width);
        Assert.Equal(expectedHeight, layout.Height);
        Assert.Equal(expectedPitch, layout.Pitch);
        Assert.Equal(expectedSize, layout.BufferSize);
    }

    [Fact]
    public void Gen5Nv12CopyPadsLumaAndChromaRowsToPitch()
    {
        var layout = AvPlayerExports.GetVideoFrameLayout(
            Generation.Gen5,
            sourceWidth: 4,
            sourceHeight: 2,
            useVideoDecoderSoftware2: true);
        var source = Enumerable.Range(1, 12).Select(value => (byte)value).ToArray();
        var destination = Enumerable.Repeat((byte)0xA5, layout.BufferSize).ToArray();

        Assert.True(
            AvPlayerExports.TryCopyNv12Frame(
                source,
                destination,
                sourceWidth: 4,
                sourceHeight: 2,
                layout: layout));

        Assert.Equal(source[0..4], destination[0..4]);
        Assert.Equal(source[4..8], destination[layout.Pitch..(layout.Pitch + 4)]);
        var chromaOffset = layout.Pitch * layout.Height;
        Assert.Equal(source[8..12], destination[chromaOffset..(chromaOffset + 4)]);
        Assert.All(destination[4..layout.Pitch], value => Assert.Equal(0, value));
        Assert.All(
            destination[(layout.Pitch + 4)..chromaOffset],
            value => Assert.Equal(0, value));
        Assert.All(
            destination[(chromaOffset + 4)..layout.BufferSize],
            value => Assert.Equal(0, value));
    }

    [Fact]
    public void DefaultDecoderPlacesChromaAfterAlignedLumaHeight()
    {
        var layout = AvPlayerExports.GetVideoFrameLayout(
            Generation.Gen4,
            sourceWidth: 4,
            sourceHeight: 2,
            useVideoDecoderSoftware2: false);
        var source = Enumerable.Range(1, 12).Select(value => (byte)value).ToArray();
        var destination = Enumerable.Repeat((byte)0xA5, layout.BufferSize).ToArray();

        Assert.True(
            AvPlayerExports.TryCopyNv12Frame(
                source,
                destination,
                sourceWidth: 4,
                sourceHeight: 2,
                layout: layout));

        var chromaOffset = layout.Pitch * layout.Height;
        Assert.Equal(source[8..12], destination[chromaOffset..(chromaOffset + 4)]);
        Assert.All(
            destination[(layout.Pitch * 2)..chromaOffset],
            value => Assert.Equal(0, value));
    }

    [Fact]
    public void Gen5ExtendedFrameInfoUsesExactLayoutAndPreservesTrailingMemory()
    {
        const ulong infoAddress = 0x2_0000;
        var memory = new FakeCpuMemory(infoAddress, 112);
        Assert.True(memory.TryWrite(infoAddress + 104, Enumerable.Repeat((byte)0xA5, 8).ToArray()));
        var context = new CpuContext(memory, Generation.Gen5);
        var layout = AvPlayerExports.GetVideoFrameLayout(
            Generation.Gen5,
            sourceWidth: 1920,
            sourceHeight: 1080,
            useVideoDecoderSoftware2: true);

        Assert.True(
            AvPlayerExports.TryWriteVideoFrameInfo(
                context,
                infoAddress,
                bufferAddress: 0x1234_5678,
                timestamp: 42,
                sourceWidth: 1920,
                sourceHeight: 1080,
                layout: layout,
                framesPerSecond: 29.97,
                extended: true));

        var info = new byte[112];
        Assert.True(memory.TryRead(infoAddress, info));
        Assert.Equal(0x1234_5678ul, BinaryPrimitives.ReadUInt64LittleEndian(info));
        Assert.Equal(42ul, BinaryPrimitives.ReadUInt64LittleEndian(info[16..]));
        Assert.Equal(1920u, BinaryPrimitives.ReadUInt32LittleEndian(info[24..]));
        Assert.Equal(1080u, BinaryPrimitives.ReadUInt32LittleEndian(info[28..]));
        Assert.Equal(128u, BinaryPrimitives.ReadUInt32LittleEndian(info[48..]));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(info[56..]));
        Assert.Equal(2048u, BinaryPrimitives.ReadUInt32LittleEndian(info[60..]));
        Assert.Equal(8, info[64]);
        Assert.Equal(8, info[65]);
        Assert.Equal(29.97, BinaryPrimitives.ReadDoubleLittleEndian(info[72..]), 3);
        Assert.All(info[104..], value => Assert.Equal(0xA5, value));
    }

    [Fact]
    public void Gen4ExtendedFrameInfoPreservesLegacySourceLayout()
    {
        const ulong infoAddress = 0x2_0000;
        var memory = new FakeCpuMemory(infoAddress, 104);
        var context = new CpuContext(memory, Generation.Gen4);
        var layout = AvPlayerExports.GetVideoFrameLayout(
            Generation.Gen4,
            sourceWidth: 1920,
            sourceHeight: 1080,
            useVideoDecoderSoftware2: false,
            extended: true);

        Assert.True(
            AvPlayerExports.TryWriteVideoFrameInfo(
                context,
                infoAddress,
                bufferAddress: 0x1234,
                timestamp: 7,
                sourceWidth: 1920,
                sourceHeight: 1080,
                layout: layout,
                framesPerSecond: 30,
                extended: true));

        var info = new byte[104];
        Assert.True(memory.TryRead(infoAddress, info));
        Assert.Equal(1920u, BinaryPrimitives.ReadUInt32LittleEndian(info[24..]));
        Assert.Equal(1080u, BinaryPrimitives.ReadUInt32LittleEndian(info[28..]));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(info[48..]));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(info[56..]));
        Assert.Equal(1920u, BinaryPrimitives.ReadUInt32LittleEndian(info[60..]));
        Assert.Equal(0d, BinaryPrimitives.ReadDoubleLittleEndian(info[72..]));
        Assert.Equal(0x2FD000, layout.BufferSize);
    }

    [Fact]
    public void Nv12CopyRejectsShortDestination()
    {
        var layout = AvPlayerExports.GetVideoFrameLayout(
            Generation.Gen5,
            sourceWidth: 4,
            sourceHeight: 2,
            useVideoDecoderSoftware2: true);

        Assert.False(
            AvPlayerExports.TryCopyNv12Frame(
                new byte[12],
                new byte[layout.BufferSize - 1],
                sourceWidth: 4,
                sourceHeight: 2,
                layout: layout));
    }
}
