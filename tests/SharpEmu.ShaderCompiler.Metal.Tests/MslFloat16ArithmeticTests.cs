// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Xunit;

namespace SharpEmu.ShaderCompiler.Metal.Tests;

public sealed class MslFloat16ArithmeticTests
{
    [Fact]
    public void CompactFloat16ArithmeticUsesHalfOperandsAndPreservesRegisterShape()
    {
        var fixture = new Gen5ComputeFixture(
            "compact-f16-arithmetic",
            [
                0x64000501,
                0x66060B04,
                0x680C1107,
                0x6A12170A,
                0x72181D0D,
                0x741E2310,
                0xBF810000,
            ],
            StoreScalarResourceBase: 0,
            StoreBackingBytes: 0);

        var shader = Gen5ComputeFixtures.CompileRequestOrThrow(fixture);

        Assert.Contains("as_type<half>", shader.Source, StringComparison.Ordinal);
        Assert.Contains("fmin(", shader.Source, StringComparison.Ordinal);
        Assert.Contains("fmax(", shader.Source, StringComparison.Ordinal);
        Assert.Contains("& 0xFFFF0000u", shader.Source, StringComparison.Ordinal);
    }
}
