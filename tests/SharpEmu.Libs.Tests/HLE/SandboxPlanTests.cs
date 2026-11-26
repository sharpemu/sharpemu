// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

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
        var plan = SandboxPlan.Build(Inputs(Folder("emulator"), Folder("data"), null, variables: new()
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

    [Fact]
    public void GrantRecord_IsKeptInTheUserFolder()
    {
        Assert.Equal(Path.Combine(_root, "user", "sandbox-grants.json"), SandboxGrantRecord.FilePath(_root));
    }
}
