// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Ngs2;
using Xunit;

namespace SharpEmu.Libs.Tests.Ngs2;

// sceNgs2ParseWaveformData against the SceNgs2WaveformInfo layout the libNgs2
// ABI defines. Void Terrarium calls this on its audio thread for every
// sound it loads, so a fabricated or mis-sized description is felt immediately.
public sealed class Ngs2ParseWaveformExportsTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong DataAddress = MemoryBase + 0x100;
    private const ulong InfoAddress = MemoryBase + 0x800;

    // libNgs2 waveform types.
    private const uint WaveformTypePcmI16L = 0x12;
    private const uint WaveformTypeVag = 0x1C;
    // sizeof(SceNgs2WaveformInfo).
    private const int WaveformInfoSize = 0xE8;

    private const int InvalidOutAddress = unchecked((int)0x804A8010);
    private const int InvalidWaveformAddress = unchecked((int)0x804A8055);
    private const int InvalidWaveformData = unchecked((int)0x804A8430);

    private static CpuContext CreateContext(out FakeCpuMemory memory)
    {
        memory = new FakeCpuMemory(MemoryBase, 0x2000);
        return new CpuContext(memory, Generation.Gen5);
    }

    private static byte[] BuildPcm16Wave(int frames, int channels, uint sampleRate)
    {
        var dataBytes = frames * channels * 2;
        var file = new byte[12 + 8 + 16 + 8 + dataBytes];
        var span = file.AsSpan();
        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], (uint)(file.Length - 8));
        "WAVE"u8.CopyTo(span[8..]);
        "fmt "u8.CopyTo(span[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(span[20..], 1); // WAVE_FORMAT_PCM
        BinaryPrimitives.WriteUInt16LittleEndian(span[22..], (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(span[24..], sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(span[28..], sampleRate * (uint)channels * 2);
        BinaryPrimitives.WriteUInt16LittleEndian(span[32..], (ushort)(channels * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(span[34..], 16);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[40..], (uint)dataBytes);
        return file;
    }

    private static byte[] BuildVag(int units, uint sampleRate)
    {
        var file = new byte[0x30 + (units * 16)];
        var span = file.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(span, 0x56414770); // "VAGp"
        BinaryPrimitives.WriteUInt32BigEndian(span[0x0C..], (uint)(units * 16));
        BinaryPrimitives.WriteUInt32BigEndian(span[0x10..], sampleRate);
        span[0x1E] = 1;
        return file;
    }

    private static int Parse(CpuContext ctx, FakeCpuMemory memory, byte[] image)
    {
        Assert.True(memory.TryWrite(DataAddress, image));
        ctx[CpuRegister.Rdi] = DataAddress;
        ctx[CpuRegister.Rsi] = (ulong)image.Length;
        ctx[CpuRegister.Rdx] = InfoAddress;
        return Ngs2Exports.Ngs2ParseWaveformData(ctx);
    }

    private static uint Field(FakeCpuMemory memory, int offset)
    {
        Span<byte> bytes = stackalloc byte[4];
        Assert.True(memory.TryRead(InfoAddress + (ulong)offset, bytes));
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    [Fact]
    public void ParseWaveformData_DescribesAPcm16RiffWave()
    {
        var ctx = CreateContext(out var memory);

        var result = Parse(ctx, memory, BuildPcm16Wave(frames: 64, channels: 2, sampleRate: 44100));

        Assert.Equal(0, result);
        // SceNgs2WaveformFormat: waveformType@0, numChannels@4, sampleRate@8.
        Assert.Equal(WaveformTypePcmI16L, Field(memory, 0x00));
        Assert.Equal(2u, Field(memory, 0x04));
        Assert.Equal(44100u, Field(memory, 0x08));
        // dataOffset@0x18 / dataSize@0x1C.
        Assert.Equal(44u, Field(memory, 0x18));
        Assert.Equal(64u * 2 * 2, Field(memory, 0x1C));
        // numSamples@0x28 = dataSize / audioUnitSize.
        Assert.Equal(64u, Field(memory, 0x28));
        // audioUnitSize@0x2C = blockAlign, numAudioUnitSamples@0x30 = 1 for PCM,
        // numAudioUnitPerFrame@0x34 = 1.
        Assert.Equal(4u, Field(memory, 0x2C));
        Assert.Equal(1u, Field(memory, 0x30));
        Assert.Equal(1u, Field(memory, 0x34));
        // audioFrameSize@0x38 = audioUnitSize x numAudioUnitPerFrame.
        Assert.Equal(4u, Field(memory, 0x38));
        Assert.Equal(1u, Field(memory, 0x3C));
        // numDelaySamples@0x40 is 0 for PCM, numBlocks@0x44 = 1.
        Assert.Equal(0u, Field(memory, 0x40));
        Assert.Equal(1u, Field(memory, 0x44));
    }

    [Fact]
    public void ParseWaveformData_PublishesTheFirstBlockOverTheWholeDataChunk()
    {
        var ctx = CreateContext(out var memory);

        Assert.Equal(0, Parse(ctx, memory, BuildPcm16Wave(frames: 32, channels: 1, sampleRate: 48000)));

        // aBlock[0] starts at 0x48: dataOffset (uintptr), dataSize (size_t),
        // numRepeats, numSkipSamples, numSamples.
        Span<byte> block = stackalloc byte[0x28];
        Assert.True(memory.TryRead(InfoAddress + 0x48, block));
        Assert.Equal(44UL, BinaryPrimitives.ReadUInt64LittleEndian(block));
        Assert.Equal(64UL, BinaryPrimitives.ReadUInt64LittleEndian(block[8..]));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(block[0x10..]));
        Assert.Equal(32u, BinaryPrimitives.ReadUInt32LittleEndian(block[0x18..]));
    }

    [Fact]
    public void ParseWaveformData_WritesExactlySizeofWaveformInfo()
    {
        var ctx = CreateContext(out var memory);
        Span<byte> paint = stackalloc byte[WaveformInfoSize + 0x10];
        paint.Fill(0x9C);
        Assert.True(memory.TryWrite(InfoAddress, paint));

        Assert.Equal(0, Parse(ctx, memory, BuildPcm16Wave(frames: 8, channels: 2, sampleRate: 48000)));

        Span<byte> tail = stackalloc byte[0x10];
        Assert.True(memory.TryRead(InfoAddress + WaveformInfoSize, tail));
        foreach (var value in tail)
        {
            Assert.Equal(0x9C, value);
        }
    }

    [Fact]
    public void ParseWaveformData_DescribesAVagContainer()
    {
        var ctx = CreateContext(out var memory);

        var result = Parse(ctx, memory, BuildVag(units: 10, sampleRate: 22050));

        Assert.Equal(0, result);
        Assert.Equal(WaveformTypeVag, Field(memory, 0x00));
        Assert.Equal(1u, Field(memory, 0x04));
        Assert.Equal(22050u, Field(memory, 0x08));
        Assert.Equal(0x30u, Field(memory, 0x18));
        Assert.Equal(160u, Field(memory, 0x1C));
        // 28 samples per 16-byte HE-VAG unit.
        Assert.Equal(280u, Field(memory, 0x28));
        Assert.Equal(16u, Field(memory, 0x2C));
        Assert.Equal(28u, Field(memory, 0x30));
    }

    [Fact]
    public void ParseWaveformData_RejectsANullOutInfo()
    {
        var ctx = CreateContext(out _);
        ctx[CpuRegister.Rdi] = DataAddress;
        ctx[CpuRegister.Rsi] = 0x100;
        ctx[CpuRegister.Rdx] = 0;

        Assert.Equal(InvalidOutAddress, Ngs2Exports.Ngs2ParseWaveformData(ctx));
    }

    [Fact]
    public void ParseWaveformData_RejectsANullData()
    {
        var ctx = CreateContext(out _);
        ctx[CpuRegister.Rdi] = 0;
        ctx[CpuRegister.Rsi] = 0x100;
        ctx[CpuRegister.Rdx] = InfoAddress;

        Assert.Equal(InvalidWaveformAddress, Ngs2Exports.Ngs2ParseWaveformData(ctx));
    }

    [Fact]
    public void ParseWaveformData_RejectsAnUnknownContainer()
    {
        var ctx = CreateContext(out var memory);
        var junk = new byte[0x40];
        junk.AsSpan().Fill(0x5A);

        Assert.Equal(InvalidWaveformData, Parse(ctx, memory, junk));
    }
}
