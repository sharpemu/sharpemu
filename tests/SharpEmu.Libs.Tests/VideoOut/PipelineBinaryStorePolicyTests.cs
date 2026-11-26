// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class PipelineBinaryStorePolicyTests
{
    [Fact]
    public void UnsetStoresEveryBuildOf20MillisecondsOrMore()
    {
        Assert.Equal(20, PipelineBinaryStorePolicy.DefaultMilliseconds);
        Assert.Equal(PipelineBinaryStorePolicy.DefaultMilliseconds, PipelineBinaryStorePolicy.Parse(null));
        Assert.Equal(PipelineBinaryStorePolicy.DefaultMilliseconds, PipelineBinaryStorePolicy.Parse(""));
    }

    [Theory]
    [InlineData("20", 20)]
    [InlineData("0", 0)]
    [InlineData("12.5", 12.5)]
    public void ANumberOfMillisecondsBecomesTheThreshold(string value, double expected) =>
        Assert.Equal(expected, PipelineBinaryStorePolicy.Parse(value));

    [Theory]
    [InlineData("-5")]
    [InlineData("abc")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1,5")]
    public void AnythingElseKeepsTheDefault(string value) =>
        Assert.Equal(PipelineBinaryStorePolicy.DefaultMilliseconds, PipelineBinaryStorePolicy.Parse(value));
}
