// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.GUI;
using Xunit;

namespace SharpEmu.Libs.Tests.GUI;

public sealed class GameSettingsKeyTests
{
    [Fact]
    public void TitleIdKeepsExistingSettingsFile()
    {
        Assert.Equal("PPSA01234", PerGameSettings.KeyFor("PPSA01234", "unused.zar"));
    }

    [Fact]
    public void MissingTitleIdUsesNormalizedFullPathAndSeparatesGames()
    {
        var path = Path.Combine(Path.GetTempPath(), "first.zar");
        var key = PerGameSettings.KeyFor(null, path);
        Assert.StartsWith("PATH-", key);
        Assert.Equal(key, PerGameSettings.KeyFor("", Path.Combine(Path.GetTempPath(), "sub", "..", "first.zar")));
        Assert.NotEqual(key, PerGameSettings.KeyFor(null, Path.Combine(Path.GetTempPath(), "second.zar")));
        Assert.Equal(key + ".json", Path.GetFileName(PerGameSettings.PathFor(key)));
    }
}
