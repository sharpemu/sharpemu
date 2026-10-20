// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using LibAtrac9;
using Xunit;

namespace SharpEmu.Libs.Tests.Audio;

public sealed class Atrac9CorruptStreamTests
{
    // A corrupt block must surface as a data error the AJM layer reports to the guest,
    // never as a NullReferenceException from an undefined spectrum codebook.
    [Theory]
    [InlineData(new byte[] { 0xFE, 0x70, 0x17, 0xE0 })]
    [InlineData(new byte[] { 0xFE, 0x74, 0x0B, 0xF0 })]
    public void RandomBlocksNeverEscapeAsNullReferences(byte[] configData)
    {
        var decoder = new Atrac9Decoder();
        decoder.Initialize(configData);
        var config = decoder.Config;
        var pcm = new short[config.ChannelCount][];
        for (var channel = 0; channel < pcm.Length; channel++)
            pcm[channel] = new short[config.SuperframeSamples];

        var random = new System.Random(0x5EED);
        var data = new byte[config.SuperframeBytes];
        for (var attempt = 0; attempt < 20000; attempt++)
        {
            random.NextBytes(data);
            data[0] &= 0x7F; // keep the sync bit clear so the header parses further
            try
            {
                decoder.Decode(data, pcm);
            }
            catch (Exception exception) when (
                exception is ArgumentException or InvalidDataException or InvalidOperationException or IndexOutOfRangeException)
            {
            }
        }
    }
}
