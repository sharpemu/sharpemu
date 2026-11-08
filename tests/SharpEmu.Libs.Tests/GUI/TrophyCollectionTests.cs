// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.GUI;
using SharpEmu.Libs.Np;
using Xunit;

namespace SharpEmu.Libs.Tests.GUI;

public sealed class TrophyCollectionTests
{
    [Fact]
    public void MergeVersions_CountsSharedTrophiesOnceAndUsesTheLatestInstallation()
    {
        var first = Summary("old", "01.000.000", [Trophy(1), Trophy(2, unlocked: true)]);
        var latest = Summary("latest", "02.000.000", [Trophy(2, unlocked: true), Trophy(1)]);

        var merged = TrophyHubView.MergeVersions([first, latest]);

        Assert.Same(latest, Assert.Single(merged));
        Assert.Equal(1, merged.Sum(summary => summary.Earned));
        Assert.Equal(2, merged.Sum(summary => summary.Trophies.Count));
    }

    [Fact]
    public void MergeVersions_KeepsDifferentTrophySetsWithTheSameCountSeparate()
    {
        var first = Summary("first", "01.000.000", [Trophy(1)]);
        var different = Summary("different", "02.000.000", [Trophy(2)]);

        Assert.Equal(2, TrophyHubView.MergeVersions([first, different]).Count);
    }

    [Fact]
    public void MergeVersions_KeepsDifferentTitleIdsSeparate()
    {
        var first = Summary("first", "01.000.000", [Trophy(1)]);
        var otherRegion = new TrophyHubView.GameSummary(
            new GameEntry("Same game", "PPSA00002", "01.000.000", "/games/region/eboot.bin", 0, null, null),
            "/games/region", [Trophy(1)]);

        Assert.Equal(2, TrophyHubView.MergeVersions([first, otherRegion]).Count);
    }

    private static TrophyHubView.GameSummary Summary(string folder, string version, IReadOnlyList<TrophyEntry> trophies) =>
        new(new GameEntry("Same game", "PPSA00001", version, $"/games/{folder}/eboot.bin", 0, null, null),
            $"/games/{folder}", trophies);

    private static TrophyEntry Trophy(int id, bool unlocked = false) =>
        new(id, 1, $"Trophy {id}", "Description", false, unlocked, null, null);
}
