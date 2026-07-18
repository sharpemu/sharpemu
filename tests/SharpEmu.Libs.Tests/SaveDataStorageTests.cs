// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.IO;
using SharpEmu.Libs.SaveData;
using Xunit;

namespace SharpEmu.Libs.Tests;

/// <summary>
/// Save data lives under ~/SharpEmu/Saves/&lt;titleId&gt;/&lt;dirName&gt;/ with UI
/// metadata in &lt;slot&gt;/sce_sys/param.json. These guard the pure path and
/// metadata logic that the SaveData HLE exports build on.
/// </summary>
public sealed class SaveDataStorageTests
{
    [Fact]
    public void RootHonorsOverrideAndFallsBackToUserProfile()
    {
        Assert.Equal(Path.GetFullPath("/tmp/custom-saves"), SaveDataStorage.Root("/tmp/custom-saves"));

        var home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        Assert.Equal(Path.Combine(home, "SharpEmu", "Saves"), SaveDataStorage.Root());
    }

    [Fact]
    public void LayoutNestsTitleThenSlotThenSceSys()
    {
        var root = SaveDataStorage.Root("/saves");
        var titleRoot = SaveDataStorage.TitleRoot(root, "PPSA15552");
        var slot = SaveDataStorage.SlotDir(titleRoot, "SAVE0000");

        Assert.Equal(Path.Combine(Path.GetFullPath("/saves"), "PPSA15552"), titleRoot);
        Assert.Equal(Path.Combine(titleRoot, "SAVE0000"), slot);
        Assert.Equal(Path.Combine(slot, "sce_sys", "param.json"), SaveDataStorage.ParamPath(slot));
        Assert.Equal(Path.Combine(slot, "sce_sys", "icon0.png"), SaveDataStorage.IconPath(slot));
        Assert.Equal(Path.Combine(titleRoot, "sce_sdmemory", "memory.dat"), SaveDataStorage.MemoryPath(titleRoot));
    }

    [Theory]
    [InlineData("SAVE0000", "SAVE0000")]
    [InlineData("../../etc/passwd", ".._.._etc_passwd")] // '/' separators -> '_', collapsing to one segment
    [InlineData("a/b", "a_b")]
    [InlineData("", "default")]
    [InlineData("   ", "default")]
    public void SanitizeNeutralizesPathSeparatorsAndEmpties(string input, string expected)
    {
        Assert.Equal(expected, SaveDataStorage.Sanitize(input));
    }

    [Fact]
    public void SanitizedSlotStaysUnderTheTitleRoot()
    {
        // The dangerous part of a traversal is the separator; sanitizing it to a
        // single segment keeps the slot a direct child of the title root.
        var titleRoot = SaveDataStorage.TitleRoot(SaveDataStorage.Root("/saves"), "PPSA15552");
        var slot = SaveDataStorage.SlotDir(titleRoot, "../escape");
        Assert.Equal(titleRoot, Path.GetDirectoryName(slot));
        Assert.DoesNotContain(Path.DirectorySeparatorChar, Path.GetFileName(slot));
    }

    [Fact]
    public void MetadataRoundTripsThroughParamJson()
    {
        var slot = Path.Combine(Path.GetTempPath(), "sharpemu-savetest-" + Path.GetRandomFileName());
        try
        {
            var written = new SaveDataMetadata
            {
                Title = "Dead Cells",
                SubTitle = "The Prisoners' Quarters",
                Detail = "Cell 1 - 3h 12m",
                UserParam = 42,
            };
            SaveDataStorage.WriteMetadata(slot, written);

            Assert.True(File.Exists(SaveDataStorage.ParamPath(slot)));
            var read = SaveDataStorage.ReadMetadata(slot);
            Assert.Equal(written.Title, read.Title);
            Assert.Equal(written.SubTitle, read.SubTitle);
            Assert.Equal(written.Detail, read.Detail);
            Assert.Equal(written.UserParam, read.UserParam);
        }
        finally
        {
            if (Directory.Exists(slot))
            {
                Directory.Delete(slot, recursive: true);
            }
        }
    }

    [Fact]
    public void ReadMetadataDefaultsWhenMissingOrCorrupt()
    {
        var slot = Path.Combine(Path.GetTempPath(), "sharpemu-savetest-" + Path.GetRandomFileName());
        try
        {
            var missing = SaveDataStorage.ReadMetadata(slot);
            Assert.Equal(Path.GetFileName(slot), missing.Title);

            Directory.CreateDirectory(Path.GetDirectoryName(SaveDataStorage.ParamPath(slot))!);
            File.WriteAllText(SaveDataStorage.ParamPath(slot), "{ not valid json");
            var corrupt = SaveDataStorage.ReadMetadata(slot);
            Assert.Equal(Path.GetFileName(slot), corrupt.Title);
        }
        finally
        {
            if (Directory.Exists(slot))
            {
                Directory.Delete(slot, recursive: true);
            }
        }
    }
}
