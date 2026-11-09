// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Patches;
using SharpEmu.GUI;
using System.Xml.Linq;
using Xunit;

namespace SharpEmu.Libs.Tests.GUI;

public sealed class GamePatchOptionTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("sharpemu-patch-options-").FullName;
    private GamePatchRepository Repository => new(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static GamePatch Patch => new("test-patch", "Game.xml", "Debug menu", "Test author",
        "First line\\nSecond line", "01.004.000", "eboot.bin", [new GamePatchWrite(0, [0x90])], null)
    {
        PatchVersion = "1.2",
    };

    [Fact]
    public void SelectionIsSavedAndReloadsWithoutChangingPatchMetadata()
    {
        var patch = Patch;
        var errors = new List<string>();
        var option = new GamePatchOption(patch, Repository, "PPSA00001", "01.004.000", false, false, errors.Add);
        option.IsChecked = true;
        Assert.True(option.IsChecked);
        Assert.Contains(patch.Id, Repository.LoadEnabled("PPSA00001"));
        Assert.Equal("Test author", option.Patch.Author);
        Assert.Equal("First line\nSecond line", option.Note);
        Assert.Equal("1.2", option.Patch.PatchVersion);

        var reopened = new GamePatchOption(patch, Repository, "PPSA00001", "01.004.000",
            Repository.LoadEnabled("PPSA00001").Contains(patch.Id), false, errors.Add);
        Assert.True(reopened.IsChecked);
        reopened.IsChecked = false;
        Assert.Empty(Repository.LoadEnabled("PPSA00001"));
        Assert.Empty(errors);
    }

    [Theory]
    [InlineData("debug", null, true)]
    [InlineData("AUTHOR", "Game.xml", true)]
    [InlineData("Second line", null, true)]
    [InlineData("01.004", null, true)]
    [InlineData("eboot", null, true)]
    [InlineData("1.2", null, true)]
    [InlineData("debug", "Other.xml", false)]
    [InlineData("nothing here", null, false)]
    [InlineData("   ", "Game.xml", true)]
    public void FilteringSearchesMetadataAndRespectsTheSelectedXmlFile(string query, string? fileName, bool matches)
    {
        var option = new GamePatchOption(Patch, Repository, "PPSA00001", "01.004.000", true, false, _ => { });
        Assert.Equal(matches, option.Matches(query, fileName));
        Assert.True(option.IsChecked);
    }

    [Theory]
    [InlineData("01.004.000", true, null)]
    [InlineData("01.007.000", false, null)]
    [InlineData("01.004.000", false, "Unsupported patch line")]
    public void RunningIncompatibleAndInvalidPatchesCannotBeEnabled(string version, bool readOnly, string? error)
    {
        var option = new GamePatchOption(Patch with { Error = error }, Repository, "PPSA00001", version,
            false, readOnly, _ => Assert.Fail("Disabled controls should not attempt a save."));
        Assert.False(option.CanToggle);
        option.IsChecked = true;
        Assert.False(option.IsChecked);
        Assert.Empty(Repository.LoadEnabled("PPSA00001"));
        Assert.NotEmpty(option.Status);
    }

    [Fact]
    public void PatternVersionCanBeSelectedAndEnabledForDifferentInstalledVersions()
    {
        var patch = GamePatch.Parse("Game.xml", XElement.Parse("""
            <Metadata Name="Pattern patch" AppVer="mask" AppElf="eboot.bin">
              <PatchList><Line Type="mask" Address="aa bb ?? cc" Value="90"/></PatchList>
            </Metadata>
            """));
        var option = new GamePatchOption(patch, Repository, "PPSA00001", "01.007.000", false, false,
            _ => Assert.Fail("Valid pattern patch should save."));
        Assert.True(option.CanToggle);
        Assert.False(option.HasStatus);
        Assert.Equal(Localization.Instance.Get("Library.Patches.PatternVersion"), option.AppVersionText);
        Assert.Equal(option.AppVersionText, option.TargetVersionText);
        option.IsChecked = true;
        Assert.Contains(patch.Id, Repository.LoadEnabled("PPSA00001"));
    }

    [Fact]
    public void FailedSaveKeepsThePreviousSelectionAndNotifiesTheBinding()
    {
        File.WriteAllText(Path.Combine(_directory, "PPSA00001.json"), "broken");
        var errors = new List<string>();
        var changed = new List<string?>();
        var option = new GamePatchOption(Patch, Repository, "PPSA00001", "01.004.000", false, false, errors.Add);
        option.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        option.IsChecked = true;
        Assert.False(option.IsChecked);
        Assert.Single(errors);
        Assert.Contains(nameof(GamePatchOption.IsChecked), changed);
        Assert.Equal("broken", File.ReadAllText(Path.Combine(_directory, "PPSA00001.json")));
    }
}
