// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Reflection;
using SharpEmu.HLE;
using SharpEmu.Libs.Ngs2;
using Xunit;

namespace SharpEmu.Libs.Tests.Ngs2;

public sealed class Ngs2WaveformBlockTests
{
    private const ulong Base = 0x100000000;
    private const ulong Output = Base + 0x100;
    private readonly FakeCpuMemory _memory = new(Base, 0x1000);

    [Fact]
    public void ExportResolvesTheRequestedNid()
    {
        var export = typeof(Ngs2Exports).GetMethod(nameof(Ngs2Exports.Ngs2CalcWaveformBlock))!
            .GetCustomAttribute<SysAbiExportAttribute>()!;
        Assert.Equal("3pCNbVM11UA", export.Nid);
        Assert.Equal("sceNgs2CalcWaveformBlock", export.ExportName);
        Assert.Equal(Generation.Gen5, export.Target);
    }

    [Theory]
    [InlineData(0x10u, 1u)]
    [InlineData(0x11u, 1u)]
    [InlineData(0x12u, 2u)]
    [InlineData(0x13u, 2u)]
    [InlineData(0x14u, 3u)]
    [InlineData(0x15u, 3u)]
    [InlineData(0x16u, 4u)]
    [InlineData(0x17u, 4u)]
    [InlineData(0x18u, 4u)]
    [InlineData(0x19u, 4u)]
    [InlineData(0x1au, 8u)]
    [InlineData(0x1bu, 8u)]
    public void PcmBlockConvertsInterleavedSamplesToBytes(uint type, uint sampleBytes)
    {
        var context = Context(type);
        Assert.Equal(0, Ngs2Exports.Ngs2CalcWaveformBlock(context));
        AssertBlock(3 * 2 * sampleBytes, 7 * 2 * sampleBytes, 0, 7);
    }

    [Fact]
    public void VagBlockCoversWholeAdpcmUnitsAndSkipsLeadingSamples()
    {
        var context = Context(0x1c, position: 30, count: 28);
        Assert.Equal(0, Ngs2Exports.Ngs2CalcWaveformBlock(context));
        AssertBlock(32, 64, 2, 28);
    }

    [Theory]
    [InlineData(0xf00b74feu)]
    [InlineData(0xfe740bf0u)]
    public void Atrac9BlockCoversWholeSuperframes(uint config)
    {
        var context = Context(0x40, position: 1027, count: 1024, config: config);
        Assert.Equal(0, Ngs2Exports.Ngs2CalcWaveformBlock(context));
        AssertBlock(384, 768, 3, 1024);
    }

    [Fact]
    public void EmptyAtrac9RangeHasNoDataOrSkip()
    {
        var context = Context(0x40, position: 1027, count: 0, config: 0xfe740bf0);
        Assert.Equal(0, Ngs2Exports.Ngs2CalcWaveformBlock(context));
        AssertBlock(384, 0, 0, 0);
    }

    [Fact]
    public void LargePcmRangesDoNotOverflow32BitByteOffsets()
    {
        var context = Context(0x1a, position: uint.MaxValue, count: uint.MaxValue);
        Assert.Equal(0, Ngs2Exports.Ngs2CalcWaveformBlock(context));
        AssertBlock((ulong)uint.MaxValue * 16, (ulong)uint.MaxValue * 16, 0, uint.MaxValue);
    }

    [Theory]
    [InlineData(0x40u, 0u, 2u, 48000u)]
    [InlineData(0x40u, 0xfe740bf0u, 1u, 48000u)]
    [InlineData(0x40u, 0xfe740bf0u, 2u, 44100u)]
    [InlineData(0x12u, 0u, 0u, 48000u)]
    [InlineData(0x12u, 0u, 9u, 48000u)]
    [InlineData(0x12u, 0u, 2u, 0u)]
    public void InvalidFormatDoesNotPublishAPartialBlock(uint type, uint config, uint channels, uint rate)
    {
        var context = Context(type, config: config, channels: channels, rate: rate);
        Assert.Equal(unchecked((int)0x804A8431), Ngs2Exports.Ngs2CalcWaveformBlock(context));
        AssertUntouched();
    }

    [Fact]
    public void UnsupportedCodecDoesNotPretendToProduceABlock()
    {
        var context = Context(0x43);
        Assert.Equal(unchecked((int)0x804A8432), Ngs2Exports.Ngs2CalcWaveformBlock(context));
        AssertUntouched();
    }

    [Fact]
    public void InvalidAddressesReturnErrors()
    {
        var context = Context(0x12);
        context[CpuRegister.Rdi] = 0;
        Assert.Equal(unchecked((int)0x804A8431), Ngs2Exports.Ngs2CalcWaveformBlock(context));
        AssertUntouched();
        context[CpuRegister.Rdi] = Base;
        context[CpuRegister.Rcx] = 0;
        Assert.Equal(unchecked((int)0x804A8010), Ngs2Exports.Ngs2CalcWaveformBlock(context));
        context[CpuRegister.Rcx] = Base + 0xff0;
        Assert.Equal(unchecked((int)0x804A8010), Ngs2Exports.Ngs2CalcWaveformBlock(context));
    }

    private CpuContext Context(uint type, uint position = 3, uint count = 7,
        uint config = 0, uint channels = 2, uint rate = 48000)
    {
        var format = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(format, type);
        BinaryPrimitives.WriteUInt32LittleEndian(format.AsSpan(4), channels);
        BinaryPrimitives.WriteUInt32LittleEndian(format.AsSpan(8), rate);
        BinaryPrimitives.WriteUInt32LittleEndian(format.AsSpan(12), config);
        Assert.True(_memory.TryWrite(Base, format));
        Assert.True(_memory.TryWrite(Output, Enumerable.Repeat((byte)0xa5, 48).ToArray()));
        var context = new CpuContext(_memory, Generation.Gen5);
        context[CpuRegister.Rdi] = Base; context[CpuRegister.Rsi] = position;
        context[CpuRegister.Rdx] = count; context[CpuRegister.Rcx] = Output;
        return context;
    }

    private void AssertBlock(ulong offset, ulong size, uint skip, uint count)
    {
        var data = new byte[48];
        Assert.True(_memory.TryRead(Output, data));
        Assert.Equal(offset, BinaryPrimitives.ReadUInt64LittleEndian(data));
        Assert.Equal(size, BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(8)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16)));
        Assert.Equal(skip, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(20)));
        Assert.Equal(count, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(24)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(28)));
        Assert.Equal(0UL, BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(32)));
        Assert.All(data[40..], value => Assert.Equal((byte)0xa5, value));
    }

    private void AssertUntouched()
    {
        var data = new byte[48];
        Assert.True(_memory.TryRead(Output, data));
        Assert.All(data, value => Assert.Equal((byte)0xa5, value));
    }
}
