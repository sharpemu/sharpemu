// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.ShaderCache;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.ShaderCache;

public sealed class ShaderCacheSettingsTests
{
    [Theory]
    [InlineData("on", true)]
    [InlineData("1", true)]
    [InlineData("True", true)]
    [InlineData("OFF", false)]
    [InlineData("0", false)]
    [InlineData("disabled", false)]
    public void KnownValuesParse(string value, bool expected)
    {
        Assert.True(ShaderCacheSettings.TryParse(value, out var enabled));
        Assert.Equal(expected, enabled);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sometimes")]
    public void UnknownValuesDoNotParse(string? value) => Assert.False(ShaderCacheSettings.TryParse(value, out _));

    [Fact]
    public void TheCacheIsOnUnlessAValueOrTheLegacySwitchTurnsItOff()
    {
        Assert.True(ShaderCacheSettings.Resolve(null));
        Assert.False(ShaderCacheSettings.Resolve(null, "0"));
        Assert.True(ShaderCacheSettings.Resolve("on", "0"));
        Assert.False(ShaderCacheSettings.Resolve("off"));
    }

    [Fact]
    public void TheFormatRoundTrips()
    {
        Assert.True(ShaderCacheSettings.TryParse(ShaderCacheSettings.Format(true), out var on) && on);
        Assert.True(ShaderCacheSettings.TryParse(ShaderCacheSettings.Format(false), out var off) && !off);
    }

    [Fact]
    public void TheCacheFileLivesUnderTheTitleFolder()
    {
        var path = ShaderCacheSettings.CacheFilePath("PPSA01341");
        Assert.Equal(ShaderCacheFile.FileName, Path.GetFileName(path));
        Assert.Equal("PPSA01341", Path.GetFileName(Path.GetDirectoryName(path)));
    }
}
