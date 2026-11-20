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

    // A RIFF/WAVE header as an .at9 file has it: a WAVE_FORMAT_EXTENSIBLE format chunk with the ATRAC9 subformat and codec
    // configuration, a fact chunk (sample count, lead-in) and the data chunk header; the superframes follow.
    private static byte[] RiffPass(uint totalSamples, uint skipSamples, int superframes, byte[]? config = null, int superframeBytes = SuperframeBytes)
    {
        byte[] subFormat = [0xD2, 0x42, 0xE1, 0x47, 0xBA, 0x36, 0x8D, 0x4D, 0x88, 0xFC, 0x61, 0x65, 0x4F, 0x8C, 0x83, 0x6C];
        const int Header = 12 + 8 + 52 + 8 + 12 + 8;
        var pass = new byte[Header + superframeBytes * superframes];
        var span = pass.AsSpan();
        "RIFF"u8.CopyTo(span);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(span[4..], (uint)pass.Length - 8);
        "WAVE"u8.CopyTo(span[8..]);
        "fmt "u8.CopyTo(span[12..]);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(span[16..], 52);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(span[20..], 0xFFFE);
        subFormat.CopyTo(span[(20 + 24)..]);
        (config ?? MonoConfig).CopyTo(span[(20 + 44)..]);
        "fact"u8.CopyTo(span[72..]);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(span[76..], 12);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(span[80..], totalSamples);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(span[84..], skipSamples);
        "data"u8.CopyTo(span[92..]);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(span[96..], (uint)(superframeBytes * superframes));
        for (var superframe = 0; superframe < superframes; superframe++)
        {
            SilentFrame.CopyTo(pass, Header + superframe * superframeBytes);
        }

        return pass;
    }

    [Fact]
    public void AHeaderWithAnotherCodecConfigurationSwitchesTheDecoderToIt()
    {
        var decoder = CreateDecoder();
        byte[] smallFrames = [0xFE, 0x70, 0x0B, 0xE0];
        var pass = RiffPass(totalSamples: 0, skipSamples: 0, superframes: 2, smallFrames, superframeBytes: 96);

        var result = decoder.Decode(pass, new byte[SuperframeSamples * 4], Atrac9PcmEncoding.Signed16, requestedChannels: 0, multipleFrames: true);

        Assert.Equal(96, decoder.Config!.SuperframeBytes);
        Assert.Equal((pass.Length, SuperframeSamples * 2 * sizeof(short)), (result.InputConsumed, result.OutputWritten));
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
    public void AnAjmDecoderPlaysASuperframeItCannotDecodeAsSilenceAndGoesOn()
    {
        var decoder = new Atrac9DecodeState { ConcealsUndecodableSuperframes = true };
        Assert.True(decoder.TryInitialize(MonoConfig));
        var input = new byte[SuperframeBytes * 2];
        Array.Fill(input, (byte)0xFF, 0, SuperframeBytes);
        SilentFrame.CopyTo(input, SuperframeBytes);
        var output = new byte[SuperframeSamples * 2 * sizeof(short)];
        Array.Fill(output, (byte)0x55);

        var result = decoder.Decode(input, output, Atrac9PcmEncoding.Signed16, requestedChannels: 0, multipleFrames: true);

        Assert.Equal(0, result.Status & (Atrac9DecodeState.ResultCodecError | Atrac9DecodeState.ResultInvalidData));
        Assert.Equal((SuperframeBytes * 2, output.Length), (result.InputConsumed, result.OutputWritten));
        Assert.All(output.AsSpan(0, SuperframeSamples * sizeof(short)).ToArray(), sample => Assert.Equal(0, sample));
    }

    [Fact]
    public void ADecoderThatDoesNotConcealReportsASuperframeItCannotDecode()
    {
        var decoder = CreateDecoder();
        var input = new byte[SuperframeBytes];
        Array.Fill(input, (byte)0xFF);

        var result = decoder.Decode(input, new byte[SuperframeSamples * sizeof(short)], Atrac9PcmEncoding.Signed16, requestedChannels: 0, multipleFrames: true);

        Assert.Equal(
            Atrac9DecodeState.ResultCodecError | Atrac9DecodeState.ResultInvalidData,
            result.Status & (Atrac9DecodeState.ResultCodecError | Atrac9DecodeState.ResultInvalidData));
    }

    [Fact]
    public void ASuperframeWithoutRoomStaysInTheInput()
    {
        var decoder = CreateDecoder();

        var first = DecodeSuperframes(decoder, 2, SuperframeSamples);
        Assert.Equal((SuperframeBytes, SuperframeSamples * sizeof(short)), (first.InputConsumed, first.OutputWritten));
        Assert.NotEqual(0, first.Status & Atrac9DecodeState.ResultNotEnoughRoom);

        var second = DecodeSuperframes(decoder, 1, SuperframeSamples);
        Assert.Equal((SuperframeBytes, SuperframeSamples * sizeof(short)), (second.InputConsumed, second.OutputWritten));
        Assert.Equal((ulong)(SuperframeSamples * 2), second.TotalDecodedSamples);
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
