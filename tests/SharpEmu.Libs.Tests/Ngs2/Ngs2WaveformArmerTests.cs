// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.Libs.Ngs2;
using Xunit;

namespace SharpEmu.Libs.Tests.Ngs2;

public sealed class Ngs2WaveformArmerTests
{
    [Fact]
    public void TryArm_VagContainer_DecodesMonoPcm()
    {
        var frame = BuildVagFrame(shift: 12, filter: 0, flags: 0x01, nibble: 1);
        var vag = BuildVagContainer(22050, frame);
        const ulong addr = 0x200000;
        var memory = new FakeCpuMemory(addr, vag.Length + 64);
        Assert.True(memory.TryWrite(addr, vag));

        Assert.True(Ngs2WaveformArmer.TryArmFromGuestAddress(memory, addr, out var armed));
        Assert.Equal("VAGp", armed.Format);
        Assert.Equal(22050, armed.SampleRate);
        Assert.Equal(28, armed.Samples.Length);
        Assert.All(armed.Samples, s => Assert.Equal((short)1, s));
    }

    [Fact]
    public void TryArm_Pcm16Riff_DecodesAndDownmixes()
    {
        var pcm = new short[] { 1000, 2000, 3000, 4000 };
        var wave = BuildPcm16Wave(sampleRate: 24000, channels: 1, pcm);
        const ulong addr = 0x300000;
        var memory = new FakeCpuMemory(addr, wave.Length + 64);
        Assert.True(memory.TryWrite(addr, wave));

        Assert.True(Ngs2WaveformArmer.TryArmFromGuestAddress(memory, addr, out var armed));
        Assert.Equal("PCM16", armed.Format);
        Assert.Equal(24000, armed.SampleRate);
        Assert.Equal(pcm, armed.Samples);
    }

    [Fact]
    public void TryArm_NestedPointerTable_FollowsChildWave()
    {
        var frame = BuildVagFrame(shift: 12, filter: 0, flags: 0x01, nibble: 2);
        var vag = BuildVagContainer(48000, frame);
        const ulong baseAddr = 0x400000;
        var memory = new FakeCpuMemory(baseAddr, 0x20000);
        const ulong waveAddr = 0x400000;
        const ulong tableAddr = 0x410000;
        Assert.True(memory.TryWrite(waveAddr, vag));

        var table = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(table.AsSpan(0, 8), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(table.AsSpan(8, 8), waveAddr);
        Assert.True(memory.TryWrite(tableAddr, table));

        Assert.True(Ngs2WaveformArmer.TryArmFromGuestAddress(memory, tableAddr, out var armed));
        Assert.Equal("VAGp", armed.Format);
        Assert.Equal(28, armed.Samples.Length);
        Assert.All(armed.Samples, s => Assert.Equal((short)2, s));
    }

    [Fact]
    public void TryArm_RejectsJunk()
    {
        var memory = new FakeCpuMemory(0x500000, 64);
        Assert.True(memory.TryWrite(0x500000, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
        Assert.False(Ngs2WaveformArmer.TryArmFromGuestAddress(memory, 0x500000, out _));
        Assert.False(Ngs2WaveformArmer.TryArmFromGuestAddress(memory, 0, out _));
    }

    [Fact]
    public void TryArm_WaveformBlocks_RawPcm16Stereo_Downmixes()
    {
        // Custom-sampler style: type 0x12, stereo 48 kHz, one block of raw PCM16.
        var left = new short[] { 1000, 2000, 3000, 4000 };
        var right = new short[] { 3000, 2000, 1000, 0 };
        var interleaved = new byte[left.Length * 4];
        for (var i = 0; i < left.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(interleaved.AsSpan(i * 4), left[i]);
            BinaryPrimitives.WriteInt16LittleEndian(interleaved.AsSpan(i * 4 + 2), right[i]);
        }

        const ulong dataAddr = 0x600000;
        const ulong blockAddr = 0x610000;
        var memory = new FakeCpuMemory(0x600000, 0x20000);
        Assert.True(memory.TryWrite(dataAddr, interleaved));

        var blockBytes = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(blockBytes.AsSpan(0), 0); // dataOffset
        BinaryPrimitives.WriteUInt32LittleEndian(blockBytes.AsSpan(4), (uint)interleaved.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(blockBytes.AsSpan(16), (uint)left.Length); // numSamples
        Assert.True(memory.TryWrite(blockAddr, blockBytes));

        Assert.True(Ngs2WaveformArmer.TryReadBlock(memory, blockAddr, out var block));
        var format = new Ngs2WaveformArmer.WaveformFormat(
            Ngs2WaveformArmer.WaveformTypePcmI16Alt,
            NumChannels: 2,
            SampleRate: 48000,
            ConfigData: 0,
            FrameOffset: 0,
            FrameMargin: 0);

        Assert.True(Ngs2WaveformArmer.TryArmFromWaveformBlocks(
            memory, dataAddr, format, new[] { block }, out var armed));
        Assert.Equal("PCM16", armed.Format);
        Assert.Equal(48000, armed.SampleRate);
        Assert.Equal(4, armed.Samples.Length);
        Assert.Equal((short)2000, armed.Samples[0]); // (1000+3000)/2
        Assert.Equal((short)2000, armed.Samples[1]);
        Assert.Equal((short)2000, armed.Samples[2]);
        Assert.Equal((short)2000, armed.Samples[3]);
    }

    [Fact]
    public void TryArm_WaveformBlocks_OffsetSlice_UsesDataOffset()
    {
        var pcm = new short[] { 111, 222, 333, 444 };
        var raw = new byte[16 + pcm.Length * 2];
        // 16-byte pad, then samples
        for (var i = 0; i < pcm.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(raw.AsSpan(16 + i * 2), pcm[i]);
        }

        const ulong dataAddr = 0x700000;
        var memory = new FakeCpuMemory(dataAddr, raw.Length + 64);
        Assert.True(memory.TryWrite(dataAddr, raw));

        var block = new Ngs2WaveformArmer.WaveformBlock(
            DataOffset: 16,
            DataSize: (uint)(pcm.Length * 2),
            NumRepeats: 0,
            NumSkipSamples: 0,
            NumSamples: (uint)pcm.Length,
            UserData: 0);
        var format = new Ngs2WaveformArmer.WaveformFormat(1, 1, 24000, 0, 0, 0);

        Assert.True(Ngs2WaveformArmer.TryArmFromWaveformBlocks(
            memory, dataAddr, format, new[] { block }, out var armed));
        Assert.Equal(pcm, armed.Samples);
    }

    [Fact]
    public void TryArm_WaveformBlocks_AllZeroPayload_Rejected()
    {
        var zeros = new byte[64];
        const ulong dataAddr = 0x800000;
        var memory = new FakeCpuMemory(dataAddr, zeros.Length + 64);
        Assert.True(memory.TryWrite(dataAddr, zeros));

        var block = new Ngs2WaveformArmer.WaveformBlock(0, 64, 0, 0, 32, 0);
        var format = new Ngs2WaveformArmer.WaveformFormat(0x12, 1, 48000, 0, 0, 0);
        Assert.False(Ngs2WaveformArmer.TryArmFromWaveformBlocks(
            memory, dataAddr, format, new[] { block }, out _));
    }

    [Fact]
    public void TryTrimActiveRegion_DropsLeadingTrailingSilence()
    {
        var samples = new short[200];
        for (var i = 50; i < 120; i++)
        {
            samples[i] = (short)(i * 20);
        }

        Assert.True(Ngs2WaveformArmer.TryTrimActiveRegion(samples, out var trimmed));
        Assert.True(trimmed.Length < samples.Length);
        Assert.True(trimmed.Length >= 70);
        Assert.All(trimmed, s => Assert.True(Math.Abs(s) >= 0));
    }

    [Fact]
    public void CrossfadeConcat_ShorterThanSumByCrossfade()
    {
        var a = new short[100];
        var b = new short[100];
        for (var i = 0; i < 100; i++)
        {
            a[i] = 1000;
            b[i] = 3000;
        }

        var joined = Ngs2WaveformArmer.CrossfadeConcat(a, b, crossfadeSamples: 20, maxTotalSamples: 1000);
        Assert.Equal(180, joined.Length);
        // Middle of crossfade should be between endpoints.
        Assert.InRange(joined[90], 1000, 3000);
    }

    [Fact]
    public void MakeSeamlessLoop_ShortensByCrossfade()
    {
        var pcm = new short[4000];
        for (var i = 0; i < pcm.Length; i++)
        {
            pcm[i] = (short)(Math.Sin(i * 0.01) * 10000);
        }

        var looped = Ngs2WaveformArmer.MakeSeamlessLoop(pcm, crossfadeSamples: 256);
        Assert.True(looped.Length < pcm.Length);
        Assert.True(looped.Length >= pcm.Length - 256);
    }

    [Fact]
    public void TryDecodeRawFloat32_ConvertsToPcm16()
    {
        var raw = new byte[8];
        BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(0), 0.5f);
        BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(4), -0.5f);
        Assert.True(Ngs2WaveformArmer.TryDecodeRawFloat32(raw, 1, 48000, 2, out var armed));
        Assert.Equal("F32", armed.Format);
        Assert.Equal(2, armed.Samples.Length);
        Assert.InRange(armed.Samples[0], 16000, 17000);
        Assert.InRange(armed.Samples[1], -17000, -16000);
    }

    [Fact]
    public void TryReadFormat_ReadsPublicWaveformFormatFields()
    {
        // OrbisNgs2WaveformFormat: type, channels, rate, config, frameOffset, margin
        var raw = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(0), 0x12);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(8), 48000);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(12), 0);
        const ulong addr = 0xA00000;
        var memory = new FakeCpuMemory(addr, 64);
        Assert.True(memory.TryWrite(addr, raw));

        Assert.True(Ngs2WaveformArmer.TryReadFormat(memory, addr, out var format));
        Assert.Equal(0x12u, format.WaveformType);
        Assert.Equal(2u, format.NumChannels);
        Assert.Equal(48000u, format.SampleRate);
        Assert.Equal(0u, format.ConfigData);
    }

    [Fact]
    public void TryReadFormat_RejectsZeroChannelsOrRate()
    {
        var raw = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(0), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(4), 0); // channels
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(8), 48000);
        const ulong addr = 0xA10000;
        var memory = new FakeCpuMemory(addr, 64);
        Assert.True(memory.TryWrite(addr, raw));
        Assert.False(Ngs2WaveformArmer.TryReadFormat(memory, addr, out _));
    }

    [Fact]
    public void TryReadBlock_RequiresPositiveDataSize()
    {
        var raw = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(0), 0); // offset
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(4), 0); // size 0 → reject
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(16), 100); // numSamples ignored without size
        const ulong addr = 0xA20000;
        var memory = new FakeCpuMemory(addr, 64);
        Assert.True(memory.TryWrite(addr, raw));
        Assert.False(Ngs2WaveformArmer.TryReadBlock(memory, addr, out _));

        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(4), 4096);
        Assert.True(memory.TryWrite(addr, raw));
        Assert.True(Ngs2WaveformArmer.TryReadBlock(memory, addr, out var block));
        Assert.Equal(4096u, block.DataSize);
    }

    [Fact]
    public void TryArm_WaveformBlocks_LoopTrue_SetsLoopStartZero()
    {
        var pcm = new short[64];
        for (var i = 0; i < pcm.Length; i++)
        {
            pcm[i] = (short)(500 + i);
        }

        var raw = new byte[pcm.Length * 2];
        for (var i = 0; i < pcm.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(raw.AsSpan(i * 2), pcm[i]);
        }

        const ulong dataAddr = 0xA30000;
        var memory = new FakeCpuMemory(dataAddr, raw.Length + 64);
        Assert.True(memory.TryWrite(dataAddr, raw));
        var block = new Ngs2WaveformArmer.WaveformBlock(0, (uint)raw.Length, 0, 0, (uint)pcm.Length, 0);
        var format = new Ngs2WaveformArmer.WaveformFormat(0x12, 1, 48000, 0, 0, 0);

        Assert.True(Ngs2WaveformArmer.TryArmFromWaveformBlocks(
            memory, dataAddr, format, new[] { block }, loop: true, out var armed));
        Assert.Equal(0, armed.LoopStart);
        Assert.Equal(pcm.Length, armed.LoopEnd);
    }

    private static byte[] BuildVagFrame(int shift, int filter, int flags, int nibble)
    {
        var frame = new byte[16];
        frame[0] = (byte)(((filter & 0x0F) << 4) | (shift & 0x0F));
        frame[1] = (byte)flags;
        var packed = (byte)(((nibble & 0x0F) << 4) | (nibble & 0x0F));
        for (var i = 2; i < 16; i++)
        {
            frame[i] = packed;
        }

        return frame;
    }

    private static byte[] BuildVagContainer(int sampleRate, byte[] frame)
    {
        var data = new byte[Ngs2VagDecoder.VagHeaderSize + frame.Length];
        BinaryPrimitives.WriteUInt32BigEndian(data, 0x56414770);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0x0C), (uint)frame.Length);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0x10), (uint)sampleRate);
        frame.CopyTo(data.AsSpan(Ngs2VagDecoder.VagHeaderSize));
        return data;
    }

    private static byte[] BuildPcm16Wave(int sampleRate, int channels, short[] monoOrInterleaved)
    {
        var dataBytes = monoOrInterleaved.Length * sizeof(short);
        var file = new byte[44 + dataBytes];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(file, 0);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(4), 36 + dataBytes);
        Encoding.ASCII.GetBytes("WAVE").CopyTo(file, 8);
        Encoding.ASCII.GetBytes("fmt ").CopyTo(file, 12);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(22), (ushort)channels);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(24), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(28), sampleRate * channels * 2);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(32), (ushort)(channels * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(34), 16);
        Encoding.ASCII.GetBytes("data").CopyTo(file, 36);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(40), dataBytes);
        for (var i = 0; i < monoOrInterleaved.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(file.AsSpan(44 + (i * 2)), monoOrInterleaved[i]);
        }

        return file;
    }
}
