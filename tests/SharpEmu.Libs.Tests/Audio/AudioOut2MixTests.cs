// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Audio;
using Xunit;

namespace SharpEmu.Libs.Tests.Audio;

public sealed class AudioOut2MixTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ushort ObjectMain = 0x100;
    private const float HalfPower = 0.70710677f;

    [Theory]
    [InlineData((ushort)0, true)]
    [InlineData((ushort)1, true)]
    [InlineData((ushort)2, true)]
    [InlineData(ObjectMain, true)]
    [InlineData((ushort)3, false)]
    [InlineData((ushort)4, false)]
    [InlineData((ushort)5, false)]
    [InlineData((ushort)6, false)]
    public void OnlyMainBgmAndVoicePortsReachTheSpeakers(ushort portType, bool expected)
    {
        Assert.Equal(expected, AudioOut2Exports.RoutesToSpeakers(portType));
        Assert.Equal(expected, AudioOut2Exports.TryGetStereoGains(portType, 2, 1f, AudioOut2Exports.AmbisonicsNone, 0, out _, out _));
    }

    [Theory]
    [InlineData(64u, HalfPower, HalfPower)]
    [InlineData(65u, HalfPower, -HalfPower)]
    [InlineData(0u, 1f, 1f)]
    [InlineData(2u, HalfPower, -HalfPower)]
    public void FirstOrderAmbisonicsDecodeToStereoCardioids(uint ambisonics, float left, float right)
    {
        Assert.True(AudioOut2Exports.TryGetStereoGains(ObjectMain, 1, 0.5f, ambisonics, 0, out var leftGain, out var rightGain));
        Assert.Equal(left * 0.5f, leftGain, 5);
        Assert.Equal(right * 0.5f, rightGain, 5);
    }

    [Theory]
    [InlineData(66u)]
    [InlineData(67u)]
    [InlineData(99u)]
    [InlineData(1u)]
    [InlineData(3u)]
    [InlineData(15u)]
    public void HigherAmbisonicComponentsDoNotFeedStereo(uint ambisonics)
    {
        Assert.False(AudioOut2Exports.TryGetStereoGains(ObjectMain, 1, 1f, ambisonics, 0, out _, out _));
    }

    [Fact]
    public void PlainObjectsPlayCentredOrPassThroughToOneSide()
    {
        Assert.True(AudioOut2Exports.TryGetStereoGains(ObjectMain, 1, 1f, AudioOut2Exports.AmbisonicsNone, 0, out var left, out var right));
        Assert.Equal(HalfPower, left, 5);
        Assert.Equal(HalfPower, right, 5);
        Assert.True(AudioOut2Exports.TryGetStereoGains(ObjectMain, 1, 1f, AudioOut2Exports.AmbisonicsNone, AudioOut2Exports.PassthroughLeft, out left, out right));
        Assert.Equal((1f, 0f), (left, right));
        Assert.True(AudioOut2Exports.TryGetStereoGains(ObjectMain, 1, 1f, AudioOut2Exports.AmbisonicsNone, AudioOut2Exports.PassthroughRight, out left, out right));
        Assert.Equal((0f, 1f), (left, right));
        Assert.False(AudioOut2Exports.TryGetStereoGains(ObjectMain, 1, 0f, AudioOut2Exports.AmbisonicsNone, 0, out _, out _));
    }

    [Fact]
    public void FifthOrderSceneDecodesASourceToItsSide()
    {
        const int frames = 4;
        var leftSource = DecodeScene(frames, azimuthSine: 1f);
        var centreSource = DecodeScene(frames, azimuthSine: 0f);

        Assert.Equal(2f * HalfPower, leftSource[0], 4);
        Assert.Equal(0f, leftSource[1], 4);
        Assert.Equal(HalfPower, centreSource[0], 4);
        Assert.Equal(HalfPower, centreSource[1], 4);
    }

    [Fact]
    public void SetAttributesStoresGainAmbisonicsAndPassthrough()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var param = new byte[0x40];
        BinaryPrimitives.WriteUInt16LittleEndian(param, ObjectMain);
        BinaryPrimitives.WriteUInt32LittleEndian(param.AsSpan(4), 0x100);
        Assert.True(memory.TryWrite(MemoryBase, param));
        ctx[CpuRegister.Rdi] = 0;
        ctx[CpuRegister.Rsi] = MemoryBase;
        ctx[CpuRegister.Rdx] = MemoryBase + 0x80;
        Assert.Equal(0, AudioOut2Exports.AudioOut2PortCreate(ctx));
        var handle = BinaryPrimitives.ReadUInt64LittleEndian(Read(memory, MemoryBase + 0x80, 8));

        var values = new byte[12];
        BinaryPrimitives.WriteSingleLittleEndian(values, 0.25f);
        BinaryPrimitives.WriteUInt32LittleEndian(values.AsSpan(4), 65);
        BinaryPrimitives.WriteUInt32LittleEndian(values.AsSpan(8), AudioOut2Exports.PassthroughLeft);
        Assert.True(memory.TryWrite(MemoryBase + 0x100, values));
        var attributes = new byte[0x18 * 3];
        WriteAttribute(attributes, 0, 1, MemoryBase + 0x100);
        WriteAttribute(attributes, 1, 8, MemoryBase + 0x104);
        WriteAttribute(attributes, 2, 5, MemoryBase + 0x108);
        Assert.True(memory.TryWrite(MemoryBase + 0x200, attributes));
        ctx[CpuRegister.Rdi] = handle;
        ctx[CpuRegister.Rsi] = MemoryBase + 0x200;
        ctx[CpuRegister.Rdx] = 3;

        Assert.Equal(0, AudioOut2Exports.AudioOut2PortSetAttributes(ctx));

        Assert.Equal((0.25f, 65u, AudioOut2Exports.PassthroughLeft), AudioOut2Exports.PortMixAttributes(handle));
        ctx[CpuRegister.Rdi] = handle;
        AudioOut2Exports.AudioOut2PortDestroy(ctx);
    }

    private static float[] DecodeScene(int frames, float azimuthSine)
    {
        var mix = new float[frames * 2];
        var source = new byte[frames * sizeof(float)];
        for (var acn = 0u; acn < 36; acn++)
        {
            var component = acn switch
            {
                0 => 1f,
                1 => azimuthSine,
                _ => 0.6f,
            };
            for (var frame = 0; frame < frames; frame++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(source.AsSpan(frame * sizeof(float)), component);
            }

            if (AudioOut2Exports.TryGetStereoGains(ObjectMain, 1, 1f, AudioOut2Exports.AmbisonicsAcnBase + acn, 0, out var left, out var right))
            {
                AudioOut2Exports.MixPortIntoStereo(source, mix, frames, 1, sizeof(float), true, left, right);
            }
        }

        return mix;
    }

    private static void WriteAttribute(byte[] attributes, int index, uint id, ulong value)
    {
        var entry = attributes.AsSpan(index * 0x18, 0x18);
        BinaryPrimitives.WriteUInt32LittleEndian(entry, id);
        BinaryPrimitives.WriteUInt64LittleEndian(entry[0x08..], value);
        BinaryPrimitives.WriteUInt64LittleEndian(entry[0x10..], sizeof(uint));
    }

    private static byte[] Read(FakeCpuMemory memory, ulong address, int length)
    {
        var bytes = new byte[length];
        Assert.True(memory.TryRead(address, bytes));
        return bytes;
    }
}
