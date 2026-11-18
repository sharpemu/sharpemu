// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Ngs2;
using Xunit;

namespace SharpEmu.Libs.Tests.Audio;

public sealed class Ngs2VagDecoderTests
{
    private const int SamplesPerFrame = 28;

    [Fact]
    public void LoopStartComesFromTheFrameFlaggedSix()
    {
        // A typical looping BGM: intro frame, loop-start frame (0x06),
        // body, then the loop-end frame (0x03) closing the sample.
        var frames = BuildFrames(flags: [0x00, 0x06, 0x00, 0x03]);

        var waveform = Ngs2VagDecoder.Decode(frames, sampleRate: 48000);

        Assert.Equal(SamplesPerFrame, waveform.LoopStart);
        Assert.Equal(4 * SamplesPerFrame, waveform.LoopEnd);
    }

    [Fact]
    public void LoopEndFlagDoesNotTruncateTheWaveform()
    {
        // Regression: flag 0x03 used to be read as "loop start", which left the
        // loop covering only the tail frame and reduced a looping voice to a
        // 28-sample buzz.
        var frames = BuildFrames(flags: [0x06, 0x00, 0x03]);

        var waveform = Ngs2VagDecoder.Decode(frames, sampleRate: 48000);

        Assert.Equal(0, waveform.LoopStart);
        Assert.Equal(3 * SamplesPerFrame, waveform.LoopEnd);
    }

    [Fact]
    public void OneShotEndFlagStopsDecoding()
    {
        var frames = BuildFrames(flags: [0x00, 0x01, 0x00]);

        var waveform = Ngs2VagDecoder.Decode(frames, sampleRate: 48000);

        Assert.Equal(2 * SamplesPerFrame, waveform.Samples.Length);
        Assert.Equal(-1, waveform.LoopStart);
    }

    [Fact]
    public void DiscardEndFlagEmitsSilenceForItsOwnFrame()
    {
        // Flag 0x07 is "end marker, do not decode": the frame still occupies its
        // 28 sample slots but the SPU outputs silence rather than the nibbles.
        var frames = BuildFrames(flags: [0x00, 0x07]);

        var waveform = Ngs2VagDecoder.Decode(frames, sampleRate: 48000);

        Assert.Equal(2 * SamplesPerFrame, waveform.Samples.Length);
        Assert.All(
            waveform.Samples[SamplesPerFrame..].ToArray(),
            sample => Assert.Equal(0, sample));
    }

    [Fact]
    public void ReservedShiftFactorsDecodeAsNine()
    {
        // Shift factors 13-15 are out of range; the SPU treats them as 9, so a
        // frame using one must decode identically to the same frame at shift 9.
        var reserved = BuildFrames(flags: [0x00], shift: 15);
        var nine = BuildFrames(flags: [0x00], shift: 9);

        var reservedWaveform = Ngs2VagDecoder.Decode(reserved, sampleRate: 48000);
        var nineWaveform = Ngs2VagDecoder.Decode(nine, sampleRate: 48000);

        Assert.Equal(nineWaveform.Samples, reservedWaveform.Samples);
    }

    [Fact]
    public void ContainerDecodeCarriesTheLoopPointsThroughToTheWaveform()
    {
        // The production path arms a voice from a whole "VAGp" container, so the
        // loop markers have to survive the header/payload sizing too.
        var container = BuildVagContainer(
            BuildFrames(flags: [0x00, 0x06, 0x00, 0x03]),
            sampleRate: 44100);

        Assert.True(Ngs2VagDecoder.TryDecode(container, out var waveform));

        Assert.Equal(44100, waveform.SampleRate);
        Assert.Equal(SamplesPerFrame, waveform.LoopStart);
        Assert.Equal(4 * SamplesPerFrame, waveform.LoopEnd);
    }

    [Fact]
    public void NonVagBufferIsRejected()
    {
        Assert.False(Ngs2VagDecoder.TryDecode(new byte[Ngs2VagDecoder.VagHeaderSize + 16], out _));
    }

    // Wraps ADPCM frames in the 48-byte big-endian "VAGp" header the games use.
    private static byte[] BuildVagContainer(byte[] frames, int sampleRate)
    {
        var container = new byte[Ngs2VagDecoder.VagHeaderSize + frames.Length];
        BinaryPrimitives.WriteUInt32BigEndian(container, 0x56414770);
        BinaryPrimitives.WriteUInt32BigEndian(container.AsSpan(0x0C), (uint)frames.Length);
        BinaryPrimitives.WriteUInt32BigEndian(container.AsSpan(0x10), (uint)sampleRate);
        frames.CopyTo(container.AsSpan(Ngs2VagDecoder.VagHeaderSize));
        return container;
    }

    // Builds one 16-byte PS-ADPCM frame per flag, all sharing the same nibble
    // payload so tests can compare frames purely by header.
    private static byte[] BuildFrames(byte[] flags, int shift = 4, int filter = 0)
    {
        var frames = new byte[flags.Length * 16];
        for (var frame = 0; frame < flags.Length; frame++)
        {
            var offset = frame * 16;
            frames[offset] = (byte)((filter << 4) | shift);
            frames[offset + 1] = flags[frame];
            for (var i = 0; i < 14; i++)
            {
                frames[offset + 2 + i] = (byte)(0x19 + i);
            }
        }

        return frames;
    }
}
