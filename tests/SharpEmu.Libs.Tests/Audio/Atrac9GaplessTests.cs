// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Audio;
using Xunit;

namespace SharpEmu.Libs.Tests.Audio;

public sealed class Atrac9GaplessTests
{
    private static readonly byte[] MonoConfig = [0xFE, 0x70, 0x17, 0xE0];
    private const int SuperframeBytes = 192;
    private const int SuperframeSamples = 256;
    private static readonly byte[] SilentFrame = [0x00, 0x84, 0x00, 0x40];

    private static Atrac9DecodeState CreateDecoder()
    {
        var decoder = new Atrac9DecodeState();
        Assert.True(decoder.TryInitialize(MonoConfig));
        return decoder;
    }

    private static Atrac9DecodeResult DecodeSuperframes(Atrac9DecodeState decoder, int superframes, int outputSamples)
    {
        var input = new byte[SuperframeBytes * superframes];
        for (var superframe = 0; superframe < superframes; superframe++)
        {
            SilentFrame.CopyTo(input, superframe * SuperframeBytes);
        }

        var output = new byte[outputSamples * sizeof(short)];
        return decoder.Decode(input, output, Atrac9PcmEncoding.Signed16, requestedChannels: 0, multipleFrames: true);
    }

    // A RIFF/WAVE header with a fact chunk (sample count, lead-in) and the data chunk header; the superframes follow.
    private static byte[] RiffPass(uint totalSamples, uint skipSamples, int superframes)
    {
        var pass = new byte[40 + SuperframeBytes * superframes];
        "RIFF"u8.CopyTo(pass);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(pass.AsSpan(4), (uint)pass.Length - 8);
        "WAVE"u8.CopyTo(pass.AsSpan(8));
        "fact"u8.CopyTo(pass.AsSpan(12));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(pass.AsSpan(16), 12);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(pass.AsSpan(20), totalSamples);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(pass.AsSpan(24), skipSamples);
        "data"u8.CopyTo(pass.AsSpan(32));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(pass.AsSpan(36), (uint)(SuperframeBytes * superframes));
        for (var superframe = 0; superframe < superframes; superframe++)
        {
            SilentFrame.CopyTo(pass, 40 + superframe * SuperframeBytes);
        }

        return pass;
    }

    [Fact]
    public void AStreamBackAtItsHeaderRestartsTheWindowItsLastPassUsedUp()
    {
        var decoder = CreateDecoder();
        var pass = RiffPass(totalSamples: 300, skipSamples: 100, superframes: 2);

        var first = decoder.Decode(pass, new byte[SuperframeSamples * 4], Atrac9PcmEncoding.Signed16, requestedChannels: 0, multipleFrames: true);
        Assert.Equal((pass.Length, 300 * sizeof(short), 300ul), (first.InputConsumed, first.OutputWritten, first.TotalDecodedSamples));

        var spent = DecodeSuperframes(decoder, 1, SuperframeSamples);
        Assert.Equal((0, 0), (spent.InputConsumed, spent.OutputWritten));

        var again = decoder.Decode(pass, new byte[SuperframeSamples * 4], Atrac9PcmEncoding.Signed16, requestedChannels: 0, multipleFrames: true);
        Assert.Equal((pass.Length, 300 * sizeof(short), 300ul), (again.InputConsumed, again.OutputWritten, again.TotalDecodedSamples));
    }

    [Fact]
    public void AJobWithoutInputReportsPartialInput()
    {
        var decoder = CreateDecoder();

        var result = decoder.Decode(ReadOnlySpan<byte>.Empty, new byte[SuperframeSamples * 2], Atrac9PcmEncoding.Signed16, requestedChannels: 0, multipleFrames: true);

        Assert.Equal((Atrac9DecodeState.ResultPartialInput, 0, 0), (result.Status, result.InputConsumed, result.OutputWritten));
    }

    [Fact]
    public void WithoutAWindowEverySuperframeSampleIsWritten()
    {
        var decoder = CreateDecoder();

        var result = DecodeSuperframes(decoder, 2, SuperframeSamples * 2);

        Assert.Equal(SuperframeSamples * 2 * sizeof(short), result.OutputWritten);
        Assert.Equal((ulong)(SuperframeSamples * 2), result.TotalDecodedSamples);
        Assert.Equal(default, decoder.Gapless);
    }

    [Fact]
    public void TheWindowSkipsTheLeadInAndStopsAtTheTotal()
    {
        var decoder = CreateDecoder();
        decoder.SetGapless(totalSamples: 500, skipSamples: 100, reset: false);

        var first = DecodeSuperframes(decoder, 1, SuperframeSamples);
        Assert.Equal(156 * sizeof(short), first.OutputWritten);
        Assert.Equal(new Atrac9Gapless(344, 0, 100), decoder.Gapless);

        var rest = DecodeSuperframes(decoder, 3, SuperframeSamples * 3);
        Assert.Equal((256 + 88) * sizeof(short), rest.OutputWritten);
        Assert.Equal(SuperframeBytes * 2, rest.InputConsumed);
        Assert.Equal(new Atrac9Gapless(0, 0, 100 + 256 - 88), decoder.Gapless);
        Assert.Equal(500ul, rest.TotalDecodedSamples);

        var ended = DecodeSuperframes(decoder, 1, SuperframeSamples);
        Assert.Equal(0, ended.OutputWritten);
        Assert.Equal(0, ended.InputConsumed);
    }

    [Fact]
    public void ALeadInLongerThanASuperframeWritesNothingForIt()
    {
        var decoder = CreateDecoder();
        decoder.SetGapless(totalSamples: 0, skipSamples: 300, reset: false);

        var first = DecodeSuperframes(decoder, 1, 0);
        Assert.Equal(0, first.OutputWritten);
        Assert.Equal(SuperframeBytes, first.InputConsumed);
        Assert.Equal(new Atrac9Gapless(0, 44, 256), decoder.Gapless);

        var second = DecodeSuperframes(decoder, 1, SuperframeSamples);
        Assert.Equal((256 - 44) * sizeof(short), second.OutputWritten);
        Assert.Equal(new Atrac9Gapless(0, 0, 300), decoder.Gapless);
    }

    [Fact]
    public void TheRoomCheckUsesTheTrimmedSize()
    {
        var decoder = CreateDecoder();
        decoder.SetGapless(totalSamples: 100, skipSamples: 0, reset: false);

        var result = DecodeSuperframes(decoder, 1, 100);

        Assert.Equal(100 * sizeof(short), result.OutputWritten);
        Assert.Equal(0, result.Status & Atrac9DecodeState.ResultNotEnoughRoom);
    }

    [Fact]
    public void ResetRestoresTheWindowAndASetWithoutResetKeepsProgress()
    {
        var decoder = CreateDecoder();
        decoder.SetGapless(totalSamples: 500, skipSamples: 100, reset: false);
        DecodeSuperframes(decoder, 1, SuperframeSamples);

        decoder.SetGapless(totalSamples: 500, skipSamples: 100, reset: false);
        Assert.Equal(new Atrac9Gapless(344, 0, 100), decoder.Gapless);

        decoder.Reset();
        Assert.Equal(new Atrac9Gapless(500, 100, 0), decoder.Gapless);

        DecodeSuperframes(decoder, 1, SuperframeSamples);
        decoder.SetGapless(totalSamples: 2000, skipSamples: 10, reset: true);
        Assert.Equal(new Atrac9Gapless(2000, 10, 0), decoder.Gapless);
    }

    [Fact]
    public void ANewWindowAfterInitializingForTheNextSoundTakesEffectWithoutAReset()
    {
        // The next sound on a voice: the instance is initialized again, then its window is sent without a reset.
        var decoder = CreateDecoder();
        decoder.SetGapless(totalSamples: 300, skipSamples: 100, reset: false);
        DecodeSuperframes(decoder, 2, 2 * SuperframeSamples);
        Assert.Equal(new Atrac9Gapless(0, 0, 212), decoder.Gapless);

        Assert.True(decoder.TryInitialize(MonoConfig));
        decoder.SetGapless(totalSamples: 1000, skipSamples: 50, reset: false);
        Assert.Equal(new Atrac9Gapless(1000, 50, 0), decoder.Gapless);

        var result = DecodeSuperframes(decoder, 1, SuperframeSamples);
        Assert.Equal((SuperframeSamples - 50) * sizeof(short), result.OutputWritten);
        Assert.Equal((ulong)(SuperframeSamples - 50), result.TotalDecodedSamples);
    }

    [Fact]
    public void ASetWithoutResetMovesTheLimitsAndKeepsTheProgress()
    {
        var decoder = CreateDecoder();
        decoder.SetGapless(totalSamples: 500, skipSamples: 100, reset: false);
        DecodeSuperframes(decoder, 1, SuperframeSamples);

        decoder.SetGapless(totalSamples: 1000, skipSamples: 100, reset: false);
        Assert.Equal(new Atrac9Gapless(1000 - 156, 0, 100), decoder.Gapless);
    }
}
