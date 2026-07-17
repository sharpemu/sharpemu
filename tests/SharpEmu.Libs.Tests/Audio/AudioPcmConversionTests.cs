// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Audio;
using Xunit;

namespace SharpEmu.Libs.Tests.Audio;

public sealed class AudioPcmConversionTests
{
    [Fact]
    public void FloatFullScaleMapsToSignedPcmEndpoints()
    {
        Span<byte> source = stackalloc byte[sizeof(float) * 2];
        WriteFloat(source, 0, -1.0f);
        WriteFloat(source, 1, 1.0f);
        Span<byte> destination = stackalloc byte[AudioPcmConversion.OutputFrameSize];

        AudioPcmConversion.ConvertToStereoPcm16(
            source,
            destination,
            frames: 1,
            channels: 2,
            bytesPerSample: sizeof(float),
            isFloat: true,
            volume: 1.0f);

        Assert.Equal(short.MinValue, BinaryPrimitives.ReadInt16LittleEndian(destination));
        Assert.Equal(short.MaxValue, BinaryPrimitives.ReadInt16LittleEndian(destination[2..]));
    }

    [Fact]
    public void FloatNaNMapsToSilence()
    {
        Span<byte> source = stackalloc byte[sizeof(float)];
        WriteFloat(source, 0, float.NaN);
        Span<byte> destination = stackalloc byte[AudioPcmConversion.OutputFrameSize];

        AudioPcmConversion.ConvertToStereoPcm16(
            source,
            destination,
            frames: 1,
            channels: 1,
            bytesPerSample: sizeof(float),
            isFloat: true,
            volume: 1.0f);

        Assert.Equal(0, BinaryPrimitives.ReadInt16LittleEndian(destination));
        Assert.Equal(0, BinaryPrimitives.ReadInt16LittleEndian(destination[2..]));
    }

    private static void WriteFloat(Span<byte> destination, int sample, float value) =>
        BinaryPrimitives.WriteInt32LittleEndian(
            destination[(sample * sizeof(float))..],
            BitConverter.SingleToInt32Bits(value));
}
