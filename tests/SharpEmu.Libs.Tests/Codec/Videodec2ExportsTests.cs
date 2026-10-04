// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Codec;
using Xunit;

namespace SharpEmu.Libs.Tests.Codec;

public sealed class Videodec2ExportsTests
{
    private const ulong MemoryBase = 0x6_0000_0000UL;
    private const ulong ComputeInfoAddress = MemoryBase + 0x100;
    private const ulong ComputeConfigAddress = MemoryBase + 0x200;
    private const ulong ComputeQueueAddress = MemoryBase + 0x300;
    private const ulong DecoderConfigAddress = MemoryBase + 0x400;
    private const ulong DecoderInfoAddress = MemoryBase + 0x500;
    private const ulong FrameBufferAddress = MemoryBase + 0x600;
    private const ulong OutputInfoAddress = MemoryBase + 0x700;
    private const ulong ComputeMemoryAddress = MemoryBase + 0x900;
    private const ulong PixelBufferAddress = MemoryBase + 0x1000;
    private const ulong PictureInfoAddress = MemoryBase + 0x1800;
    private const ulong MinimumMemoryBytes = 16UL * 1024 * 1024;

    [Fact]
    public void QueryComputeMemoryInfo_PreservesSizeAndPopulatesFields()
    {
        var (memory, context) = CreateContext();
        var actual = Sentinel(0x28);
        BinaryPrimitives.WriteUInt64LittleEndian(actual, 0x18);
        Assert.True(memory.TryWrite(ComputeInfoAddress, actual));

        context[CpuRegister.Rdi] = ComputeInfoAddress;
        Assert.Equal(0, Videodec2Exports.Videodec2QueryComputeMemoryInfo(context));

        var expected = Sentinel(0x28);
        BinaryPrimitives.WriteUInt64LittleEndian(expected, 0x18);
        BinaryPrimitives.WriteUInt64LittleEndian(expected.AsSpan(0x08), MinimumMemoryBytes);
        BinaryPrimitives.WriteUInt64LittleEndian(expected.AsSpan(0x10), 0);
        Assert.Equal(expected, Read(memory, ComputeInfoAddress, expected.Length));
    }

    [Fact]
    public void AllocateComputeQueue_WritesThirdArgumentOnly()
    {
        var (memory, context) = CreateContext();
        var config = Sentinel(0x20);
        var memoryInfo = Sentinel(0x28);
        var queue = Sentinel(0x18);
        BinaryPrimitives.WriteUInt64LittleEndian(config, 0x10);
        BinaryPrimitives.WriteUInt64LittleEndian(memoryInfo, 0x18);
        BinaryPrimitives.WriteUInt64LittleEndian(memoryInfo.AsSpan(0x10), ComputeMemoryAddress);
        Assert.True(memory.TryWrite(ComputeConfigAddress, config));
        Assert.True(memory.TryWrite(ComputeInfoAddress, memoryInfo));
        Assert.True(memory.TryWrite(ComputeQueueAddress, queue));

        context[CpuRegister.Rdi] = ComputeConfigAddress;
        context[CpuRegister.Rsi] = ComputeInfoAddress;
        context[CpuRegister.Rdx] = ComputeQueueAddress;
        Assert.Equal(0, Videodec2Exports.Videodec2AllocateComputeQueue(context));

        Assert.Equal(config, Read(memory, ComputeConfigAddress, config.Length));
        Assert.Equal(memoryInfo, Read(memory, ComputeInfoAddress, memoryInfo.Length));
        var expectedQueue = Sentinel(0x18);
        BinaryPrimitives.WriteUInt64LittleEndian(expectedQueue, ComputeMemoryAddress);
        Assert.Equal(expectedQueue, Read(memory, ComputeQueueAddress, expectedQueue.Length));
    }

    [Fact]
    public void QueryDecoderMemoryInfo_InitializesEveryOutputField()
    {
        var (memory, context) = CreateContext();
        var config = Sentinel(0x48);
        var info = Sentinel(0x50);
        BinaryPrimitives.WriteUInt64LittleEndian(config, 0x48);
        BinaryPrimitives.WriteUInt64LittleEndian(info, 0x48);
        Assert.True(memory.TryWrite(DecoderConfigAddress, config));
        Assert.True(memory.TryWrite(DecoderInfoAddress, info));

        context[CpuRegister.Rdi] = DecoderConfigAddress;
        context[CpuRegister.Rsi] = DecoderInfoAddress;
        Assert.Equal(0, Videodec2Exports.Videodec2QueryDecoderMemoryInfo(context));

        var expected = Sentinel(0x50);
        BinaryPrimitives.WriteUInt64LittleEndian(expected, 0x48);
        BinaryPrimitives.WriteUInt64LittleEndian(expected.AsSpan(0x08), MinimumMemoryBytes);
        BinaryPrimitives.WriteUInt64LittleEndian(expected.AsSpan(0x10), 0);
        BinaryPrimitives.WriteUInt64LittleEndian(expected.AsSpan(0x18), MinimumMemoryBytes);
        BinaryPrimitives.WriteUInt64LittleEndian(expected.AsSpan(0x20), 0);
        BinaryPrimitives.WriteUInt64LittleEndian(expected.AsSpan(0x28), MinimumMemoryBytes);
        BinaryPrimitives.WriteUInt64LittleEndian(expected.AsSpan(0x30), 0);
        BinaryPrimitives.WriteUInt64LittleEndian(expected.AsSpan(0x38), MinimumMemoryBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(0x40), 0x100);
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(0x44), 0);
        Assert.Equal(expected, Read(memory, DecoderInfoAddress, expected.Length));
    }

    [Fact]
    public void DecodeNoPicture_PreservesStructSizesAndClearsStatusFields()
    {
        var (memory, context) = CreateContext();
        var frameBuffer = Sentinel(0x28);
        var outputInfo = Sentinel(0x40);
        BinaryPrimitives.WriteUInt64LittleEndian(frameBuffer, 0x20);
        BinaryPrimitives.WriteUInt64LittleEndian(outputInfo, 0x38);
        Assert.True(memory.TryWrite(FrameBufferAddress, frameBuffer));
        Assert.True(memory.TryWrite(OutputInfoAddress, outputInfo));

        context[CpuRegister.Rdi] = 0xDEAD;
        context[CpuRegister.Rdx] = FrameBufferAddress;
        context[CpuRegister.Rcx] = OutputInfoAddress;
        Assert.Equal(0, Videodec2Exports.Videodec2Decode(context));

        frameBuffer[0x18] = 0;
        outputInfo[0x08] = 0;
        outputInfo[0x09] = 0;
        outputInfo[0x0A] = 0;
        outputInfo[0x0B] = 0;
        Assert.Equal(frameBuffer, Read(memory, FrameBufferAddress, frameBuffer.Length));
        Assert.Equal(outputInfo, Read(memory, OutputInfoAddress, outputInfo.Length));
    }

    [Fact]
    public void FlushNoPicture_UsesSecondAndThirdArguments()
    {
        var (memory, context) = CreateContext();
        var frameBuffer = Sentinel(0x28);
        var outputInfo = Sentinel(0x40);
        BinaryPrimitives.WriteUInt64LittleEndian(frameBuffer, 0x20);
        BinaryPrimitives.WriteUInt64LittleEndian(outputInfo, 0x38);
        Assert.True(memory.TryWrite(FrameBufferAddress, frameBuffer));
        Assert.True(memory.TryWrite(OutputInfoAddress, outputInfo));

        context[CpuRegister.Rdi] = 0xDEAD;
        context[CpuRegister.Rsi] = FrameBufferAddress;
        context[CpuRegister.Rdx] = OutputInfoAddress;
        Assert.Equal(0, Videodec2Exports.Videodec2Flush(context));

        frameBuffer[0x18] = 0;
        outputInfo[0x08] = 0;
        outputInfo[0x09] = 0;
        outputInfo[0x0A] = 0;
        outputInfo[0x0B] = 0;
        Assert.Equal(frameBuffer, Read(memory, FrameBufferAddress, frameBuffer.Length));
        Assert.Equal(outputInfo, Read(memory, OutputInfoAddress, outputInfo.Length));
    }

    [Fact]
    public void WriteDecodedFrame_CopiesPitchedNv12AndPublishesAbiFields()
    {
        var (memory, context) = CreateContext();
        const ulong slotBytes = 0x800;
        var frameBuffer = Sentinel(0x28);
        var outputInfo = Sentinel(0x40);
        BinaryPrimitives.WriteUInt64LittleEndian(frameBuffer, 0x20);
        BinaryPrimitives.WriteUInt64LittleEndian(frameBuffer.AsSpan(0x08), PixelBufferAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(frameBuffer.AsSpan(0x10), slotBytes);
        BinaryPrimitives.WriteUInt64LittleEndian(outputInfo, 0x38);
        Assert.True(memory.TryWrite(FrameBufferAddress, frameBuffer));
        Assert.True(memory.TryWrite(OutputInfoAddress, outputInfo));

        var pixels = Enumerable.Range(0, 1280).Select(index => (byte)(index * 31)).ToArray();
        var decoded = new Videodec2DecodedFrame(pixels, 17, 3, 256, ErrorFrame: false);

        Assert.True(Videodec2Exports.TryWriteDecodedFrame(
            context, FrameBufferAddress, OutputInfoAddress, decoded));
        Assert.Equal(pixels, Read(memory, PixelBufferAddress, pixels.Length));

        frameBuffer[0x18] = 1;
        Assert.Equal(frameBuffer, Read(memory, FrameBufferAddress, frameBuffer.Length));

        outputInfo[0x08] = 1;
        outputInfo[0x09] = 0;
        outputInfo[0x0A] = 1;
        outputInfo[0x0B] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(outputInfo.AsSpan(0x0C), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(outputInfo.AsSpan(0x10), 17);
        BinaryPrimitives.WriteUInt32LittleEndian(outputInfo.AsSpan(0x14), 256);
        BinaryPrimitives.WriteUInt32LittleEndian(outputInfo.AsSpan(0x18), 3);
        BinaryPrimitives.WriteUInt64LittleEndian(outputInfo.AsSpan(0x20), PixelBufferAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(outputInfo.AsSpan(0x28), slotBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(outputInfo.AsSpan(0x30), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(outputInfo.AsSpan(0x34), 256);
        Assert.Equal(outputInfo, Read(memory, OutputInfoAddress, outputInfo.Length));
    }

    [Fact]
    public void GetPictureInfo_ReturnsMetadataForPublishedFrame()
    {
        var (memory, context) = CreateContext();
        const ulong slotBytes = 0x800;
        var frameBuffer = Sentinel(0x28);
        var outputInfo = Sentinel(0x40);
        var pictureInfo = Sentinel(120);
        BinaryPrimitives.WriteUInt64LittleEndian(frameBuffer, 0x20);
        BinaryPrimitives.WriteUInt64LittleEndian(frameBuffer.AsSpan(0x08), PixelBufferAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(frameBuffer.AsSpan(0x10), slotBytes);
        BinaryPrimitives.WriteUInt64LittleEndian(outputInfo, 0x38);
        BinaryPrimitives.WriteUInt64LittleEndian(pictureInfo, 120);
        Assert.True(memory.TryWrite(FrameBufferAddress, frameBuffer));
        Assert.True(memory.TryWrite(OutputInfoAddress, outputInfo));
        Assert.True(memory.TryWrite(PictureInfoAddress, pictureInfo));

        var decoded = new Videodec2DecodedFrame(new byte[1280], 17, 3, 256, ErrorFrame: false)
        {
            Pts = 0x1122,
            Dts = 0x3344,
            AttachedData = 0x5566,
            KeyFrame = true,
            Profile = 100,
            Level = 41,
        };
        Assert.True(Videodec2Exports.TryWriteDecodedFrame(
            context, FrameBufferAddress, OutputInfoAddress, decoded));

        context[CpuRegister.Rdi] = OutputInfoAddress;
        context[CpuRegister.Rsi] = PictureInfoAddress;
        context[CpuRegister.Rdx] = 0;
        Assert.Equal(0, Videodec2Exports.Videodec2GetPictureInfo(context));

        var expected = new byte[120];
        BinaryPrimitives.WriteUInt64LittleEndian(expected, 120);
        expected[0x08] = 1;
        BinaryPrimitives.WriteUInt64LittleEndian(expected.AsSpan(0x10), 0x1122);
        BinaryPrimitives.WriteUInt64LittleEndian(expected.AsSpan(0x18), 0x3344);
        BinaryPrimitives.WriteUInt64LittleEndian(expected.AsSpan(0x20), 0x5566);
        expected[0x28] = 1;
        expected[0x29] = 100;
        expected[0x2A] = 41;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(0x2C), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(0x30), 0);
        expected[0x34] = 1;
        expected[0x4E] = 1;
        expected[0x4F] = 5;
        Assert.Equal(expected, Read(memory, PictureInfoAddress, expected.Length));
    }

    private static (FakeCpuMemory Memory, CpuContext Context) CreateContext()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x2000);
        return (memory, new CpuContext(memory, Generation.Gen5));
    }

    private static byte[] Sentinel(int size)
    {
        var bytes = new byte[size];
        bytes.AsSpan().Fill(0xA5);
        return bytes;
    }

    private static byte[] Read(FakeCpuMemory memory, ulong address, int size)
    {
        var bytes = new byte[size];
        Assert.True(memory.TryRead(address, bytes));
        return bytes;
    }
}
