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
}
