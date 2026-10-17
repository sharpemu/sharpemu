// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcSubmittedVertexSnapshotTests
{
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
    public void SnapshotRemainderSubtractsEachCurrentPhaseAndClampsAtZero()
    {
        Assert.Equal(100, DcbSubmissionProfile.SnapshotRemainder(200, [10, 20, 30, 40]));
        Assert.Equal(0, DcbSubmissionProfile.SnapshotRemainder(50, [10, 20, 30, 40]));
        Assert.Equal(200, DcbSubmissionProfile.SnapshotRemainder(200, []));
    }
}
