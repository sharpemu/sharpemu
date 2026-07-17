// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Agc;
using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcRuntimeScalarMetadataTests
{
    [Fact]
    public void BufferBiasAndCountAreInterleavedByAbsoluteBinding()
    {
        var registers = new uint[] { 0x12345678 };
        var bindings = new Gen5GlobalMemoryBinding[]
        {
            CreateBinding(0x1003, 5),
            CreateBinding(0x2040, 9),
            CreateBinding(0x30FF, 4),
            CreateBinding(0x4000, 1),
            CreateBinding(0x5008, 16),
        };
        var bytes = new byte[
            Gen5RuntimeScalarLayout.GetDwordLength(bindings.Length) *
            sizeof(uint)];

        AgcExports.PackRuntimeScalarStateInto(bytes, registers, bindings);

        Assert.Equal(0x12345678u, ReadDword(bytes, 0));
        AssertMetadata(bytes, 0, expectedBias: 3, expectedCount: 2);
        AssertMetadata(bytes, 1, expectedBias: 64, expectedCount: 19);
        AssertMetadata(bytes, 2, expectedBias: 255, expectedCount: 65);
        AssertMetadata(bytes, 3, expectedBias: 0, expectedCount: 1);
        AssertMetadata(bytes, 4, expectedBias: 8, expectedCount: 6);
    }

    private static Gen5GlobalMemoryBinding CreateBinding(
        ulong baseAddress,
        int dataLength) =>
        new(0, baseAddress, [], new byte[dataLength], dataLength, false);

    private static void AssertMetadata(
        byte[] bytes,
        int binding,
        uint expectedBias,
        uint expectedCount)
    {
        Assert.Equal(
            expectedBias,
            ReadDword(
                bytes,
                Gen5RuntimeScalarLayout.GetByteBiasDwordIndex(binding)));
        Assert.Equal(
            expectedCount,
            ReadDword(
                bytes,
                Gen5RuntimeScalarLayout.GetBufferDwordCountDwordIndex(binding)));
    }

    private static uint ReadDword(byte[] bytes, int dwordIndex) =>
        BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(dwordIndex * sizeof(uint), sizeof(uint)));
}
