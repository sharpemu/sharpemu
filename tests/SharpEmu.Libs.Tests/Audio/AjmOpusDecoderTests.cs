// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Audio;
using Xunit;
using static SharpEmu.Libs.Audio.Atrac9DecodeState;

namespace SharpEmu.Libs.Tests.Audio;

public sealed class NativeOpusFactAttribute : FactAttribute
{
    public NativeOpusFactAttribute()
    {
        var library = OperatingSystem.IsWindows() ? "swresample-5.dll" : "libswresample.so.5";
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "plugins", library)))
            Skip = "Native FFmpeg libraries are not present in the test output's plugins directory.";
    }
}

public sealed class AjmOpusDecoderTests
{
    // A generated 880 Hz stereo tone encoded with FFmpeg's native Opus encoder.
    internal static byte[] TonePacket()
    {
        var payload = Convert.FromHexString("FC7E3293CF2A221C563420DAF4E849D751B97EF68B0D385328B47DAC5256B92EDFA2532A162B3A4B9F6996B927109F16507B94DF85F5D8F100001618EE6874E3BA1948647EAA0BD8E35BD75AC5AFC9A0F5");
        var packet = new byte[payload.Length + 2];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, (ushort)payload.Length);
        payload.CopyTo(packet, 2);
        return packet;
    }

    internal static byte[] Parameters(uint channels = 2, uint rate = 48000, uint mapping = 0)
    {
        var parameters = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(parameters, channels);
        BinaryPrimitives.WriteUInt32LittleEndian(parameters.AsSpan(4), rate);
        BinaryPrimitives.WriteUInt32LittleEndian(parameters.AsSpan(8), mapping);
        return parameters;
    }

    [Fact]
    public void PacketFramingRejectsTruncatedAndInvalidPacketsWithoutReadingPastInput()
    {
        Assert.Equal(ResultPartialInput, AjmOpusDecoder.ReadPacket([], out _, out _));
        Assert.Equal(ResultPartialInput, AjmOpusDecoder.ReadPacket([3, 0, 0xF8], out _, out _));
        Assert.Equal(ResultCodecError | ResultInvalidData, AjmOpusDecoder.ReadPacket([0, 0], out _, out _));
        Assert.Equal(ResultCodecError | ResultInvalidData, AjmOpusDecoder.ReadPacket([1, 0, 0xFB], out _, out _));
        Assert.Equal(ResultCodecError | ResultInvalidData, AjmOpusDecoder.ReadPacket([2, 0, 0xFB, 0], out _, out _));
        var packet = TonePacket();
        Assert.Equal(0, AjmOpusDecoder.ReadPacket(packet, out var size, out var frames));
        Assert.Equal(packet.Length - 2, size);
        Assert.Equal(1u, frames);
    }

    [Fact]
    public void UninitializedOrDisposedDecoderDoesNotConsumeInput()
    {
        using var decoder = new AjmOpusDecoder(2, Atrac9PcmEncoding.Signed16);
        Assert.False(decoder.TryInitialize(Parameters(channels: 3)));
        Assert.False(decoder.TryInitialize(Parameters(mapping: 1)));
        Assert.False(decoder.TryInitialize(Parameters(rate: 44100)));
        Assert.False(decoder.TryInitialize([]));
        var result = decoder.Decode(TonePacket(), new byte[4096], true);
        Assert.Equal(ResultNotInitialized, result.Status);
        Assert.Equal(0, result.InputConsumed);
        decoder.Dispose();
        Assert.Equal(ResultNotInitialized, decoder.Decode(TonePacket(), new byte[4096], true).Status);
    }

    [NativeOpusFact]
    public void NativeDecodeProducesInterleavedPcmInAllEncodings()
    {
        foreach (var encoding in Enum.GetValues<Atrac9PcmEncoding>())
        {
            using var decoder = new AjmOpusDecoder(2, encoding);
            Assert.True(decoder.TryInitialize(Parameters()));
            var packet = TonePacket();
            var input = packet.Concat(packet).ToArray();
            var bytes = encoding == Atrac9PcmEncoding.Signed16 ? 2 : 4;
            var output = new byte[960 * 2 * bytes * 2];
            var result = decoder.Decode(input, output, true);
            Assert.Equal(0, result.Status);
            Assert.Equal(input.Length, result.InputConsumed);
            Assert.Equal(output.Length, result.OutputWritten);
            Assert.Equal(1920ul, result.TotalDecodedSamples);
            Assert.Equal(2u, result.Frames);
            Assert.Contains(output, value => value != 0);
        }
    }

    [NativeOpusFact]
    public void PendingPcmSurvivesSmallOutputBuffersAndResetDiscardsIt()
    {
        using var decoder = new AjmOpusDecoder(2, Atrac9PcmEncoding.Signed16);
        Assert.True(decoder.TryInitialize(Parameters()));
        var packet = TonePacket();
        var reference = new byte[3840];
        Assert.Equal(reference.Length, decoder.Decode(packet, reference, false).OutputWritten);
        decoder.Reset();
        var first = new byte[128];
        var result = decoder.Decode(packet, first, false);
        Assert.Equal(packet.Length, result.InputConsumed);
        Assert.Equal(first.Length, result.OutputWritten);
        var rest = new byte[3840 - first.Length];
        result = decoder.Decode([], rest, false);
        Assert.Equal(0, result.InputConsumed);
        Assert.Equal(rest.Length, result.OutputWritten);
        Assert.Equal(reference, first.Concat(rest).ToArray());
        decoder.Reset();
        Assert.Equal(0, decoder.Decode([], rest, false).OutputWritten);
    }

    [NativeOpusFact]
    public void GaplessSkipsLeadInAndHonorsSampleLimit()
    {
        using var decoder = new AjmOpusDecoder(2, Atrac9PcmEncoding.Signed16);
        Assert.True(decoder.TryInitialize(Parameters()));
        decoder.SetGapless(700, 100, true);
        var result = decoder.Decode(TonePacket(), new byte[4096], false);
        Assert.Equal(700 * 4, result.OutputWritten);
        Assert.Equal(700ul, result.TotalDecodedSamples);
        Assert.Equal(new Atrac9Gapless(0, 0, 100), decoder.Gapless);
        result = decoder.Decode(TonePacket(), new byte[4096], false);
        Assert.Equal(0, result.InputConsumed);
        Assert.Equal(0, result.OutputWritten);
    }

    [NativeOpusFact]
    public void ResamplingChangesOutputDurationAndFlushesTheTail()
    {
        foreach (var ratio in new[] { 0.5f, 0.8908987f, 2f })
        {
            using var decoder = new AjmOpusDecoder(2, Atrac9PcmEncoding.Signed16);
            Assert.True(decoder.TryInitialize(Parameters()));
            decoder.SetResampleParameters(ratio, 1);
            var packet = TonePacket();
            var input = Enumerable.Range(0, 10).SelectMany(_ => packet).ToArray();
            var output = new byte[960 * 4 * 24];
            var result = decoder.Decode(input, output, true);
            Assert.Equal(0, result.Status);
            Assert.Equal(input.Length, result.InputConsumed);
            decoder.SetResampleParameters(float.NaN, 2 | 8); // END without changing ratio.
            var tail = decoder.Decode([], output, true);
            var samples = (result.OutputWritten + tail.OutputWritten) / 4;
            Assert.InRange(samples, (int)(9600 / ratio) - 2, (int)(9600 / ratio) + 2);
            Assert.Equal(0, decoder.Decode([], output, true).OutputWritten);
        }
    }
}
