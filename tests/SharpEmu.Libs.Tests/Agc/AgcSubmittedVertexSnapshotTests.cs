// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcSubmittedVertexSnapshotTests
{
    [Fact]
    public void StageInstructionMetadataReusesOnlyTheSameDecodedProgram()
    {
        var instruction = new Gen5ShaderInstruction(4, default, "SXorB32", [], [], [], null);
        var program = new Gen5ShaderProgram(0x1000, [instruction]);
        var metadata = AgcExports.GetStageInstructionMetadata(program);
        Assert.Same(metadata, AgcExports.GetStageInstructionMetadata(program));
        Assert.Same(instruction, metadata.InstructionsByAddress[4]);
        Assert.True(metadata.HasBitwiseExclusiveOr);

        var replacement = new Gen5ShaderProgram(0x1000, []);
        var replacementMetadata = AgcExports.GetStageInstructionMetadata(replacement);
        Assert.NotSame(metadata, replacementMetadata);
        Assert.Empty(replacementMetadata.InstructionsByAddress);
        Assert.False(replacementMetadata.HasBitwiseExclusiveOr);
    }

    [Theory]
    [InlineData(0x69u, 0u, false)]
    [InlineData(0x9fu, 0u, false)]
    [InlineData(0x10u, 0x12u, false)]
    [InlineData(0x76u, 0u, true)]
    [InlineData(0x63u, 0u, true)]
    [InlineData(0x79u, 0u, true)]
    [InlineData(0x7au, 0u, true)]
    [InlineData(0x64u, 0u, true)]
    [InlineData(0x10u, 0x11u, true)]
    [InlineData(0x10u, 0x13u, true)]
    [InlineData(0x10u, 0u, true)]
    public void GeometryRegisterUpdatesExcludeOnlyContextPackets(uint opcode, uint register, bool expected)
    {
        Assert.Equal(expected, AgcExports.NeedsGeometryRegisterUpdate(opcode, register));
    }

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

    [Fact]
    public void DiscardedByteCountUsesLargestSharedExtentAndClampsLengths()
    {
        var shared = new byte[32];
        var separate = new byte[12];
        var inputs = new[]
        {
            CreateBinding(0, shared, 16),
            CreateBinding(1, shared, 48),
            CreateBinding(2, shared, 8),
            CreateBinding(3, separate, -1),
            CreateBinding(4, separate, 10),
        };

        Assert.Equal(42, DcbSubmissionProfile.CountUniqueVertexBytes(inputs));
        Assert.Equal(42, DcbSubmissionProfile.CountUniqueVertexBytes(inputs.Reverse().ToArray()));
        Assert.Equal(0, DcbSubmissionProfile.CountUniqueVertexBytes([]));
    }

    [Fact]
    public void SnapshotRemainderExcludesNestedPayloadTime()
    {
        Assert.Equal(50, DcbSubmissionProfile.SnapshotRemainder(200, [10, 20, 30, 40, 50, 35]));
        Assert.Equal(50, DcbSubmissionProfile.SnapshotRemainder(200, [10, 20, 30, 40, 50, 0]));
        Assert.Equal(0, DcbSubmissionProfile.SnapshotRemainder(100, [10, 20, 30, 40, 50, 35]));
        Assert.Equal(20, DcbSubmissionProfile.SnapshotRemainder(200, [10, 20, 30, 40, 50, 35, 25, 5]));
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
