// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.AvPlayer;
using Xunit;

namespace SharpEmu.Libs.Tests.AvPlayer;

public sealed class AvPlayerInitDataTests
{
    private const ulong InitDataAddress = 0x1_0000;
    private const ulong PostInitDataAddress = 0x1_1000;

    [Fact]
    public void Gen5InitExReadsAutoStartFromGen5Layout()
    {
        var memory = new FakeCpuMemory(InitDataAddress, 0x1000);
        Assert.True(memory.TryWrite(InitDataAddress + 0x74, [1]));
        Assert.True(memory.TryWrite(InitDataAddress + 0xA4, [0]));
        Assert.True(memory.TryWrite(InitDataAddress + 0xA8, [0]));
        var context = new CpuContext(memory, Generation.Gen5);

        Assert.True(
            AvPlayerExports.TryReadInitExAutoStart(
                context,
                InitDataAddress,
                out var autoStart));
        Assert.True(autoStart);

        Assert.True(memory.TryWrite(InitDataAddress + 0x74, [0]));
        Assert.True(memory.TryWrite(InitDataAddress + 0xA4, [1]));
        Assert.True(
            AvPlayerExports.TryReadInitExAutoStart(
                context,
                InitDataAddress,
                out autoStart));
        Assert.False(autoStart);
    }

    [Fact]
    public void Gen4InitExReadsAutoStartFromGen4Layout()
    {
        var memory = new FakeCpuMemory(InitDataAddress, 0x1000);
        Assert.True(memory.TryWrite(InitDataAddress + 0x74, [0]));
        Assert.True(memory.TryWrite(InitDataAddress + 0xA4, [1]));
        Assert.True(memory.TryWrite(InitDataAddress + 0xA8, [1]));
        var context = new CpuContext(memory, Generation.Gen4);

        Assert.True(
            AvPlayerExports.TryReadInitExAutoStart(
                context,
                InitDataAddress,
                out var autoStart));
        Assert.True(autoStart);

        Assert.True(memory.TryWrite(InitDataAddress + 0x74, [1]));
        Assert.True(memory.TryWrite(InitDataAddress + 0xA8, [0]));
        Assert.True(
            AvPlayerExports.TryReadInitExAutoStart(
                context,
                InitDataAddress,
                out autoStart));
        Assert.False(autoStart);
    }

    [Fact]
    public void UnreadableInitExAutoStartIsReportedAsFalse()
    {
        var memory = new FakeCpuMemory(InitDataAddress, 0x20);
        var context = new CpuContext(memory, Generation.Gen5);

        Assert.False(
            AvPlayerExports.TryReadInitExAutoStart(
                context,
                InitDataAddress,
                out var autoStart));
        Assert.False(autoStart);
    }

    [Fact]
    public void Gen5PostInitReadsSoftware2FromGen5Layout()
    {
        var memory = new FakeCpuMemory(InitDataAddress, 0x2000);
        WriteUInt32(memory, PostInitDataAddress + 4, 3);
        WriteUInt32(memory, PostInitDataAddress + 8, 1);
        var context = new CpuContext(memory, Generation.Gen5);

        Assert.True(
            AvPlayerExports.TryReadGen5PostInitVideoDecoder(
                context,
                PostInitDataAddress,
                out var decoderType,
                out var software2));
        Assert.Equal(1u, decoderType);
        Assert.True(software2);
    }

    [Fact]
    public void PostInitRejectsUnreadableGenerationSpecificDecoderField()
    {
        var memory = new FakeCpuMemory(PostInitDataAddress, 8);
        var context = new CpuContext(memory, Generation.Gen5);

        Assert.False(
            AvPlayerExports.TryReadGen5PostInitVideoDecoder(
                context,
                PostInitDataAddress,
                out var decoderType,
                out var software2));
        Assert.Equal(0u, decoderType);
        Assert.False(software2);
    }

    private static void WriteUInt32(FakeCpuMemory memory, ulong address, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        Assert.True(memory.TryWrite(address, bytes));
    }
}
