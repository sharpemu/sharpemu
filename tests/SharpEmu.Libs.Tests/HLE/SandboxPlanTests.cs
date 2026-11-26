// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.FileSystem;
using SharpEmu.HLE.Host.Windows;
using Xunit;

namespace SharpEmu.Libs.Tests.HLE;

public sealed class SandboxPlanTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sharpemu-sandbox-plan-{Guid.NewGuid():N}");

    public SandboxPlanTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Folder(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    private static SandboxInputs Inputs(
        string executable, string dataRoot, string? eboot, string? log = null, string[]? arguments = null,
        Dictionary<string, string>? variables = null) =>
        new(executable, dataRoot, eboot, log, arguments ?? [], name => variables?.GetValueOrDefault(name));

    private static SandboxFolder Find(SandboxPlan plan, string path) =>
        Assert.Single(plan.Folders, folder => string.Equals(folder.Path, path, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void Build_GivesTheEmulatorAndGameReadAccessAndTheUserFolderWriteAccess()
    {
        var executable = Folder("emulator");
        var data = Folder("data");
        var game = Folder("games", "CUSA00001");
        var plan = SandboxPlan.Build(Inputs(executable, data, Path.Combine(game, "eboot.bin")));

        Assert.Equal(SandboxAccess.ReadExecute, Find(plan, executable).Access);
        Assert.Equal(SandboxAccess.ReadExecute, Find(plan, game).Access);
        Assert.Equal(SandboxAccess.ReadWrite, Find(plan, Path.Combine(data, "user")).Access);
        Assert.Equal(3, plan.Folders.Count);
    }

    [Fact]
    public void Build_PointsTheChildAtTheGrantedFolders()
    {
        var data = Folder("data");
        var plan = SandboxPlan.Build(Inputs(Folder("emulator"), data, null));

        var temp = Path.Combine(data, "user", "sandbox-temp");
        Assert.Equal(temp, plan.TempDirectory);
        Assert.Equal(temp, plan.Environment["TEMP"]);
        Assert.Equal(temp, plan.Environment["TMP"]);
        Assert.Equal(data, plan.Environment["SHARPEMU_USER_DIR"]);
        Assert.Equal(Path.Combine(data, "user", "ampr-index"), plan.Environment["SHARPEMU_AMPR_INDEX_CACHE"]);
    }

    [Fact]
    public void Build_KeepsAConfiguredAprIndexCacheAndGrantsIt()
    {
        var cache = Folder("elsewhere", "ampr");
        var plan = SandboxPlan.Build(Inputs(Folder("emulator"), Folder("data"), null,
            variables: new() { ["SHARPEMU_AMPR_INDEX_CACHE"] = cache }));

        Assert.False(plan.Environment.ContainsKey("SHARPEMU_AMPR_INDEX_CACHE"));
        Assert.Equal(SandboxAccess.ReadWrite, Find(plan, cache).Access);
    }

    [Fact]
    public void Build_GrantsEachConfiguredWritableFolderAndThePipelineCacheFolder()
    {
        var saves = Folder("saves");
        var temp0 = Folder("temp0");
        var download = Folder("download0");
        var caches = Folder("caches");
        var plan = SandboxPlan.Build(Inputs(Folder("emulator"), Folder("data"), null, variables: new()
        {
            ["SHARPEMU_SAVEDATA_DIR"] = saves,
            ["SHARPEMU_TEMP0_DIR"] = temp0,
            ["SHARPEMU_DOWNLOAD0_DIR"] = download,
            ["SHARPEMU_VK_PIPELINE_CACHE_PATH"] = Path.Combine(caches, "vulkan-pipeline-cache.bin"),
            ["SHARPEMU_SHADER_ANALYSIS_CACHE_PATH"] = "0",
        }));

        foreach (var folder in new[] { saves, temp0, download, caches })
        {
            Assert.Equal(SandboxAccess.ReadWrite, Find(plan, folder).Access);
        }

        // "0" turns the analysis cache off; it is not a folder.
        Assert.DoesNotContain(plan.Folders, folder => Path.GetFileName(folder.Path) == "0");
    }

    [Fact]
    public void Build_SkipsAFolderAlreadyInsideAWritableOne()
    {
        var data = Folder("data");
        var saves = Folder("data", "user", "savedata");
        var plan = SandboxPlan.Build(Inputs(Folder("emulator"), data, null,
            variables: new() { ["SHARPEMU_SAVEDATA_DIR"] = saves }));

        Assert.DoesNotContain(plan.Folders, folder => string.Equals(folder.Path, saves, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Build_KeepsTheEmulatorFolderReadOnlyWhenItHoldsTheUserFolder()
    {
        var executable = Folder("portable");
        var plan = SandboxPlan.Build(Inputs(executable, executable, null));

        Assert.Equal(SandboxAccess.ReadExecute, Find(plan, executable).Access);
        Assert.Equal(SandboxAccess.ReadWrite, Find(plan, Path.Combine(executable, "user")).Access);
    }

    [Fact]
    public void Build_GrantsTheLogFolderAndTheGnmSessionFolder()
    {
        var logs = Folder("logs");
        var sessions = Folder("temp", "loadexec");
        var plan = SandboxPlan.Build(Inputs(
            Folder("emulator"), Folder("data"), null,
            log: Path.Combine(logs, "game.log"),
            arguments: ["--cpu-engine=native", "--gnm-session=" + Path.Combine(sessions, "abc.json")]));

        Assert.Equal(SandboxAccess.ReadWrite, Find(plan, logs).Access);
        Assert.Equal(SandboxAccess.ReadWrite, Find(plan, sessions).Access);
    }

    [Fact]
    public void Build_MountsTheParentOfADecryptedSidecarAndFindsAddonContent()
    {
        var game = Folder("games", "CUSA00002");
        Folder("games", "CUSA00002", "Media");
        var decrypted = Folder("games", "CUSA00002", "decrypted");
        var addcont = Folder("games", "addcont");
        var plan = SandboxPlan.Build(Inputs(Folder("emulator"), Folder("data"), Path.Combine(decrypted, "eboot.bin")));

        Assert.Equal(SandboxAccess.ReadExecute, Find(plan, game).Access);
        Assert.DoesNotContain(plan.Folders, folder => string.Equals(folder.Path, decrypted, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(SandboxAccess.ReadExecute, Find(plan, addcont).Access);
    }

    [Fact]
    public void Build_WarnsAboutSettingsTheSandboxCannotHonor()
    {
        var game = GameWithTitle("G", "PPSA00001");
        var plan = SandboxPlan.Build(Inputs(Folder("emulator"), Folder("data"), Path.Combine(game, "eboot.bin"), variables: new()
        {
            ["SHARPEMU_WRITABLE_APP0"] = "1",
            ["SHARPEMU_CRASH_CAPTURE"] = "1",
        }));

        Assert.Equal(2, plan.Warnings.Count);
        Assert.Contains("SHARPEMU_WRITABLE_APP0", plan.Describe());
    }

    [Fact]
    public void Describe_ListsEveryFolderWithItsAccess()
    {
        var executable = Folder("emulator");
        var data = Folder("data");
        var text = SandboxPlan.Build(Inputs(executable, data, null)).Describe();

        Assert.Contains($"RX {executable} (emulator folder)", text);
        Assert.Contains($"RW+Low {Path.Combine(data, "user")}", text);
        Assert.Contains("TEMP=TMP=", text);
    }

    [Fact]
    public void GrantRecord_RoundTripsThroughJson()
    {
        var record = new SandboxGrantRecord("SharpEmu.Sandbox",
        [
            new SandboxGrantEntry(@"C:\Games\A", SandboxAccess.ReadExecute, IsFile: false, Labeled: false),
            new SandboxGrantEntry(@"C:\SharpEmu\user", SandboxAccess.ReadWrite, IsFile: false, Labeled: true),
            new SandboxGrantEntry(@"C:\Games\b.zar", SandboxAccess.ReadExecute, IsFile: true, Labeled: false),
        ]);

        var json = record.ToJson();
        var restored = SandboxGrantRecord.FromJson(json);

        Assert.Contains("\"ReadWrite\"", json);
        Assert.Equal(record.ProfileName, restored.ProfileName);
        Assert.Equal(record.Entries, restored.Entries);
    }

    [Fact]
    public void GrantRecord_MergeKeepsTheWiderAccessAndTheLabel()
    {
        var record = new SandboxGrantRecord("SharpEmu.Sandbox",
        [
            new SandboxGrantEntry(@"C:\Data\user", SandboxAccess.ReadExecute, false, false),
        ]);

        var merged = record.Merge(
        [
            new SandboxGrantEntry(@"c:\data\USER", SandboxAccess.ReadWrite, false, true),
            new SandboxGrantEntry(@"C:\Games\A", SandboxAccess.ReadExecute, false, false),
        ]);

        Assert.Equal(2, merged.Entries.Count);
        var user = merged.Entries[0];
        Assert.Equal(SandboxAccess.ReadWrite, user.Access);
        Assert.True(user.Labeled);
    }

    private string GameWithTitle(string folderName, string? titleId)
    {
        var game = Folder("games", folderName, "sce_sys");
        if (titleId is not null)
        {
            File.WriteAllText(Path.Combine(game, "param.json"), "{ \"titleId\": \"" + titleId + "\" }");
        }

        return Path.GetDirectoryName(game)!;
    }

    private static bool Reaches(SandboxPlan plan, string path) =>
        plan.Folders.Any(folder => !folder.IsFile && folder.Access != SandboxAccess.AttributesOnly &&
            (string.Equals(folder.Path, path, StringComparison.OrdinalIgnoreCase) || SandboxPlan.IsUnder(folder.Path, path)));

    [Fact]
    public void Build_GrantsTheSavesOfTheLaunchedGameOnly()
    {
        var data = Folder("data");
        var user = Path.Combine(data, "user");
        var game = GameWithTitle("PPSA00001-app", "PPSA00001");
        var plan = SandboxPlan.Build(Inputs(Folder("emulator"), data, Path.Combine(game, "eboot.bin")));

        Assert.Equal(SandboxAccess.ReadWrite, Find(plan, Path.Combine(user, "savedata", "PPSA00001")).Access);
        Assert.Equal(SandboxAccess.ReadWrite, Find(plan, Path.Combine(user, "pipeline_cache", "PPSA00001")).Access);
        Assert.Equal(SandboxAccess.ReadWrite, Find(plan, Path.Combine(user, "game_logs", "PPSA00001")).Access);
        Assert.Equal(SandboxAccess.ReadWrite, Find(plan, Path.Combine(user, "temp", "PPSA00001-app")).Access);
        foreach (var shared in new[] { "logs", "sandbox-temp", "ampr-index", "redzone_cache" })
        {
            Assert.Equal(SandboxAccess.ReadWrite, Find(plan, Path.Combine(user, shared)).Access);
        }

        Assert.False(Reaches(plan, Path.Combine(user, "savedata", "PPSA00002")));
        Assert.False(Reaches(plan, Path.Combine(user, "pipeline_cache", "PPSA00002")));
        Assert.False(Reaches(plan, Path.Combine(user, "game_logs", "PPSA00002")));
        Assert.DoesNotContain(plan.Folders, folder => string.Equals(folder.Path, user, StringComparison.OrdinalIgnoreCase));
        // The save root answers "exists" (sceSaveDataInitialize creates it) but gives no listing and nothing below it.
        Assert.Equal(SandboxAccess.AttributesOnly, Find(plan, Path.Combine(user, "savedata")).Access);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void Build_GrantsTheModsPatchesAndEntitlementsThatExistForReading()
    {
        var data = Folder("data");
        var mods = Folder("data", "user", "mods");
        File.WriteAllText(Path.Combine(data, "user", "entitlements.json"), "{}");
        var game = GameWithTitle("G", "PPSA00001");
        var plan = SandboxPlan.Build(Inputs(Folder("emulator"), data, Path.Combine(game, "eboot.bin")));

        Assert.Equal(SandboxAccess.ReadExecute, Find(plan, mods).Access);
        Assert.Contains(plan.Folders, folder => folder.IsFile && folder.Path.EndsWith("entitlements.json", StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Folders, folder => folder.Path.EndsWith("patches", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_FallsBackToTheWholeUserFolderAndWarnsWhenTheTitleIdIsUnknown()
    {
        var data = Folder("data");
        var user = Path.Combine(data, "user");
        foreach (var game in new[] { GameWithTitle("A", null), GameWithTitle("B", "..") })
        {
            var plan = SandboxPlan.Build(Inputs(Folder("emulator"), data, Path.Combine(game, "eboot.bin")));

            Assert.Equal(SandboxAccess.ReadWrite, Find(plan, user).Access);
            Assert.Contains(plan.Warnings, warning => warning.Contains("saves of other games are reachable", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Build_LeavesTheDefaultSaveAndPipelineCacheFoldersOutWhenTheyAreRedirected()
    {
        var data = Folder("data");
        var saves = Folder("saves");
        var caches = Folder("caches");
        var game = GameWithTitle("G", "PPSA00001");
        var plan = SandboxPlan.Build(Inputs(Folder("emulator"), data, Path.Combine(game, "eboot.bin"), variables: new()
        {
            ["SHARPEMU_SAVEDATA_DIR"] = saves,
            ["SHARPEMU_VK_PIPELINE_CACHE_PATH"] = Path.Combine(caches, "cache.bin"),
        }));

        Assert.Equal(SandboxAccess.ReadWrite, Find(plan, saves).Access);
        Assert.Equal(SandboxAccess.ReadWrite, Find(plan, caches).Access);
        Assert.False(Reaches(plan, Path.Combine(data, "user", "savedata", "PPSA00001")));
        Assert.False(Reaches(plan, Path.Combine(data, "user", "pipeline_cache", "PPSA00001")));
    }

    [Fact]
    public void Build_ReadsTheTitleIdOfAnArchiveWithoutExtractingIt()
    {
        var data = Folder("data");
        var source = GameWithTitle("src", "PPSA00077");
        File.WriteAllText(Path.Combine(source, "eboot.bin"), "x");
        var archive = Path.Combine(_root, "src.zar");
        ZArchiveWriter.Pack(source, archive);
        var plan = SandboxPlan.Build(Inputs(Folder("emulator"), data, archive));

        Assert.Equal(SandboxAccess.ReadWrite, Find(plan, Path.Combine(data, "user", "savedata", "PPSA00077")).Access);
        Assert.Equal(SandboxAccess.ReadWrite, Find(plan, Path.Combine(data, "user", "temp", "src")).Access);
        Assert.False(Reaches(plan, Path.Combine(data, "user", "savedata", "PPSA00001")));
    }

    [Fact]
    public void TitleId_ReadsParamJsonAndRejectsUnusableNames()
    {
        Assert.Equal("PPSA00001", SandboxTitleId.FromJson("﻿{\"titleId\":\" PPSA00001 \"}"u8.ToArray()));
        Assert.Null(SandboxTitleId.FromJson("{\"titleId\":\"..\"}"u8.ToArray()));
        Assert.Null(SandboxTitleId.FromJson("{\"titleId\":\"\"}"u8.ToArray()));
        Assert.Null(SandboxTitleId.FromJson("{\"other\":1}"u8.ToArray()));
        Assert.Null(SandboxTitleId.FromJson("not json"u8.ToArray()));
        Assert.Equal("ppsa_1", SandboxTitleId.SaveDataSegment(" ppsa_1 "));
        Assert.Equal("PPSA_1_", SandboxTitleId.CacheSegment("ppsa.1:"));
    }

    [Fact]
    public void GrantRecord_WithoutForgetsOneFolderEntry()
    {
        var record = new SandboxGrantRecord("SharpEmu.Sandbox",
        [
            new SandboxGrantEntry(@"C:\Data\user", SandboxAccess.ReadWrite, false, true),
            new SandboxGrantEntry(@"C:\Data\user\logs", SandboxAccess.ReadWrite, false, true),
        ]);

        var without = record.Without(@"c:\data\USER");

        Assert.Equal([@"C:\Data\user\logs"], without.Entries.Select(entry => entry.Path));
    }

    [Fact]
    public void GrantRecord_IsKeptInTheUserFolder()
    {
        Assert.Equal(Path.Combine(_root, "user", "sandbox-grants.json"), SandboxGrantRecord.FilePath(_root));
    }
}
