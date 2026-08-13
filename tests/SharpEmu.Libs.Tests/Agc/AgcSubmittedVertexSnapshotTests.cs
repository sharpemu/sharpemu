// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcSubmittedVertexSnapshotTests
{
    [Fact]
    public void CopyKeepsSubmissionBytesAndSharedStorage()
    {
        var source = Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray();
        var inputs = new[]
        {
            CreateBinding(location: 0, source, dataLength: 32),
            CreateBinding(location: 1, source, dataLength: 16),
        };

        Assert.True(AgcExports.TryCopySubmittedVertexInputs(
            inputs,
            maximumBytes: 32,
            out var retained,
            out var retainedBytes));

        Assert.Equal(32, retainedBytes);
        Assert.Equal(2, retained.Length);
        Assert.NotSame(source, retained[0].Data);
        Assert.Same(retained[0].Data, retained[1].Data);
        Assert.Equal(32, retained[0].DataLength);
        Assert.Equal(16, retained[1].DataLength);
        Assert.False(retained[0].DataPooled);
        Assert.False(retained[1].DataPooled);

        source[0] = 0xFF;
        Assert.Equal(0, retained[0].Data[0]);
    }

    [Fact]
    public void CopyRejectsDataAboveSubmissionLimit()
    {
        var source = new byte[33];
        var inputs = new[] { CreateBinding(location: 0, source, dataLength: source.Length) };

        Assert.False(AgcExports.TryCopySubmittedVertexInputs(
            inputs,
            maximumBytes: 32,
            out var retained,
            out var retainedBytes));

        Assert.Empty(retained);
        Assert.Equal(0, retainedBytes);
    }

    private static Gen5VertexInputBinding CreateBinding(
        uint location,
        byte[] data,
        int dataLength) =>
        new(
            Pc: 0x40 + location,
            Location: location,
            ComponentCount: 2,
            DataFormat: 11,
            NumberFormat: 7,
            BaseAddress: 0x6_1000_0000,
            Stride: 20,
            OffsetBytes: location * 12,
            Data: data,
            DataLength: dataLength,
            DataPooled: true);
}
