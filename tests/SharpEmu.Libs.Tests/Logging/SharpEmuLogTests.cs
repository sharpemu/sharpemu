// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Logging;
using Xunit;

namespace SharpEmu.Libs.Tests.Logging;

public sealed class SharpEmuLogTests
{
    [Theory]
    [InlineData("Trace", LogLevel.Trace)]
    [InlineData("debug", LogLevel.Debug)]
    [InlineData(" Info ", LogLevel.Info)]
    [InlineData("WARNING", LogLevel.Warning)]
    [InlineData("Error", LogLevel.Error)]
    [InlineData("critical", LogLevel.Critical)]
    [InlineData("None", LogLevel.None)]
    [InlineData("warn", LogLevel.Warning)]
    [InlineData("fatal", LogLevel.Critical)]
    public void TryParseLevelAcceptsDefinedNamesAndAliases(string text, LogLevel expected)
    {
        Assert.True(SharpEmuLog.TryParseLevel(text, out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("unknown")]
    [InlineData("999")]
    [InlineData("-1")]
    public void TryParseLevelRejectsInvalidValues(string? text)
    {
        Assert.False(SharpEmuLog.TryParseLevel(text, out var level));
        Assert.Equal(default, level);
    }
}
