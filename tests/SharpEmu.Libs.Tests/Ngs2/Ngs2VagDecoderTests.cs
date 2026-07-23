// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Ngs2;
using Xunit;

namespace SharpEmu.Libs.Tests.Ngs2;

/// <summary>
/// Contract tests for the clean-room PS-ADPCM (VAGp) decoder.
/// These assert real decode outputs (sample counts, rates, loop markers, reject paths),
/// not just "returns true".
/// </summary>
public sealed class Ngs2VagDecoderTests
{
    [Fact]
    public void IsVag_RequiresMagicAndFullHeader()
    {
        Assert.False(Ngs2VagDecoder.IsVag(ReadOnlySpan<byte>.Empty));
        Assert.False(Ngs2VagDecoder.IsVag(new byte[Ngs2VagDecoder.VagHeaderSize - 1]));

        var badMagic = new byte[Ngs2VagDecoder.VagHeaderSize];
        BinaryPrimitives.WriteUInt32BigEndian(badMagic, 0x41424344); // "ABCD"
        Assert.False(Ngs2VagDecoder.IsVag(badMagic));

        var good = BuildVagContainer(sampleRate: 22050, frames: [BuildFrame(shift: 12, filter: 0, flags: 0x01, nibble: 0)]);
        Assert.True(Ngs2VagDecoder.IsVag(good));
    }

    [Fact]
    public void TryDecode_RejectsMissingOrEmptyPayload()
    {
        Assert.False(Ngs2VagDecoder.TryDecode(ReadOnlySpan<byte>.Empty, out _));

        // Valid magic/header but no ADPCM body.
        var headerOnly = new byte[Ngs2VagDecoder.VagHeaderSize];
        BinaryPrimitives.WriteUInt32BigEndian(headerOnly, 0x56414770); // "VAGp"
        BinaryPrimitives.WriteUInt32BigEndian(headerOnly.AsSpan(0x0C), 0);
        BinaryPrimitives.WriteUInt32BigEndian(headerOnly.AsSpan(0x10), 48000);
        Assert.False(Ngs2VagDecoder.TryDecode(headerOnly, out _));
    }

    [Fact]
    public void TryDecode_OneShotFrame_Produces28SamplesAndDefaultLoop()
    {
        // filter=0, shift=12, nibble=1 -> sample = (1<<12)>>12 = 1, all 28 samples.
        // flags=0x01 ends one-shot at end of this frame.
        var frame = BuildFrame(shift: 12, filter: 0, flags: 0x01, nibble: 1);
        var container = BuildVagContainer(sampleRate: 22050, frames: [frame]);

        Assert.True(Ngs2VagDecoder.TryDecode(container, out var waveform));
        Assert.Equal(22050, waveform.SampleRate);
        Assert.Equal(28, waveform.Samples.Length);
        Assert.All(waveform.Samples, sample => Assert.Equal((short)1, sample));
        Assert.Equal(-1, waveform.LoopStart);
        Assert.Equal(-1, waveform.LoopEnd);
    }

    [Fact]
    public void TryDecode_ZeroSampleRate_FallsBackTo48k()
    {
        var frame = BuildFrame(shift: 12, filter: 0, flags: 0x01, nibble: 0);
        var container = BuildVagContainer(sampleRate: 0, frames: [frame]);

        Assert.True(Ngs2VagDecoder.TryDecode(container, out var waveform));
        Assert.Equal(48000, waveform.SampleRate);
        Assert.Equal(28, waveform.Samples.Length);
    }

    [Fact]
    public void Decode_LoopStartAndEndFlags_AreCaptured()
    {
        // Frame 0: loop start (0x03). Frame 1: loop end (0x06). No one-shot end.
        var frames = ConcatFrames(
            BuildFrame(shift: 12, filter: 0, flags: 0x03, nibble: 2),
            BuildFrame(shift: 12, filter: 0, flags: 0x06, nibble: 3));

        var waveform = Ngs2VagDecoder.Decode(frames, sampleRate: 44100);
        Assert.Equal(44100, waveform.SampleRate);
        Assert.Equal(56, waveform.Samples.Length);
        Assert.Equal(0, waveform.LoopStart);
        Assert.Equal(56, waveform.LoopEnd);
        Assert.Equal((short)2, waveform.Samples[0]);
        Assert.Equal((short)3, waveform.Samples[28]);
    }

    [Fact]
    public void Decode_OneShotEnd_TruncatesRemainingFrames()
    {
        // First frame ends one-shot; second frame must not be decoded.
        var frames = ConcatFrames(
            BuildFrame(shift: 12, filter: 0, flags: 0x07, nibble: 4),
            BuildFrame(shift: 12, filter: 0, flags: 0x00, nibble: 5));

        var waveform = Ngs2VagDecoder.Decode(frames, sampleRate: 48000);
        Assert.Equal(28, waveform.Samples.Length);
        Assert.All(waveform.Samples, sample => Assert.Equal((short)4, sample));
    }

    [Fact]
    public void Decode_PredictorFilter1_UsesHistory()
    {
        // filter=1, f0=60/64: second sample depends on first.
        // nibble=1, shift=12 -> base s=1 each step.
        // sample0 = 1; sample1 = 1 + (1*60)>>6 = 1 + 0 = 1 with integer hist.
        // Use shift=0 so s is large enough for history to matter: s = (1<<12) = 4096.
        // sample0 = 4096
        // sample1 = 4096 + (4096*60)>>6 = 4096 + 3840 = 7936
        var frame = BuildFrame(shift: 0, filter: 1, flags: 0x01, nibble: 1);
        var waveform = Ngs2VagDecoder.Decode(frame, sampleRate: 48000);
        Assert.Equal(28, waveform.Samples.Length);
        Assert.Equal((short)4096, waveform.Samples[0]);
        Assert.Equal((short)7936, waveform.Samples[1]);
    }

    private static byte[] BuildVagContainer(int sampleRate, byte[][] frames)
    {
        var body = ConcatFrames(frames);
        var data = new byte[Ngs2VagDecoder.VagHeaderSize + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(data, 0x56414770); // "VAGp"
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0x0C), (uint)body.Length);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0x10), (uint)sampleRate);
        body.CopyTo(data.AsSpan(Ngs2VagDecoder.VagHeaderSize));
        return data;
    }

    // One 16-byte ADPCM frame: header byte = (filter<<4)|shift, flag byte, then 14
    // packed nibble pairs all equal to the same 4-bit value.
    private static byte[] BuildFrame(int shift, int filter, int flags, int nibble)
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

    private static byte[] ConcatFrames(params byte[][] frames)
    {
        var total = frames.Sum(static f => f.Length);
        var buffer = new byte[total];
        var offset = 0;
        foreach (var frame in frames)
        {
            frame.CopyTo(buffer.AsSpan(offset));
            offset += frame.Length;
        }

        return buffer;
    }
}
