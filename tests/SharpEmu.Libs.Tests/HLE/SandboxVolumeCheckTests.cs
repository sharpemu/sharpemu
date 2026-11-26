// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.Host.Windows;
using SharpEmu.Libs.Tests.FileSystem;
using Xunit;

namespace SharpEmu.Libs.Tests.HLE;

public sealed class SandboxVolumeCheckTests
{
    private static readonly SandboxVolume Ntfs = new("NTFS", true);
    private static readonly SandboxVolume ExFat = new("exFAT", false);

    private static SandboxVolume? ByDrive(string path) => char.ToUpperInvariant(path[0]) switch
    {
        'C' => Ntfs,
        'D' => ExFat,
        'E' => new SandboxVolume("FAT32", false),
        _ => null,
    };

    [Fact]
    public void AllFoldersOnAclVolumesPass()
    {
        var folders = new[]
        {
            new SandboxFolder(@"C:\SharpEmu", SandboxAccess.ReadExecute, "emulator folder"),
            new SandboxFolder(@"C:\Games\G", SandboxAccess.ReadExecute, "game folder (app0)", IsGame: true),
        };

        Assert.Empty(SandboxVolumeCheck.Find(folders, ByDrive));
    }

    [Fact]
    public void AGameFolderOnExFatFailsWithTheGameAdvice()
    {
        var folders = new[]
        {
            new SandboxFolder(@"C:\SharpEmu\user", SandboxAccess.ReadWrite, "user data"),
            new SandboxFolder(@"D:\jeux\game", SandboxAccess.ReadExecute, "game folder (app0)", IsGame: true),
        };

        var problem = Assert.Single(SandboxVolumeCheck.Find(folders, ByDrive));

        Assert.Equal(@"D:\jeux\game", problem.Folder.Path);
        Assert.Equal(
            @"The sandbox cannot access D:\jeux\game (game folder (app0)) on an exFAT drive. " +
            "Move it to an NTFS drive, convert the game to .zar, or launch without the sandbox.",
            problem.Message);
    }

    [Fact]
    public void AnotherFolderOnFatGetsAdviceWithoutTheZarOption()
    {
        var folders = new[] { new SandboxFolder(@"E:\logs", SandboxAccess.ReadWrite, "log file") };

        var problem = Assert.Single(SandboxVolumeCheck.Find(folders, ByDrive));

        Assert.Contains("on a FAT32 drive", problem.Message);
        Assert.DoesNotContain(".zar", problem.Message);
        Assert.Contains("launch without the sandbox", problem.Message);
    }

    [Fact]
    public void EveryBadFolderIsReportedAndAnUnknownVolumeIsSkipped()
    {
        var folders = new[]
        {
            new SandboxFolder(@"D:\a", SandboxAccess.ReadExecute, "x"),
            new SandboxFolder(@"E:\b", SandboxAccess.ReadWrite, "y"),
            new SandboxFolder(@"Z:\c", SandboxAccess.ReadExecute, "unknown volume"),
        };

        var problems = SandboxVolumeCheck.Find(folders, ByDrive);

        Assert.Equal([@"D:\a", @"E:\b"], problems.Select(problem => problem.Folder.Path));
        Assert.Contains(@"D:\a", SandboxVolumeCheck.Format(problems));
        Assert.Contains(@"E:\b", SandboxVolumeCheck.Format(problems));
    }

    [Fact]
    public void ADecisionRestsOnAclSupportNotOnTheName()
    {
        var folders = new[] { new SandboxFolder(@"C:\x", SandboxAccess.ReadExecute, "x") };

        Assert.Empty(SandboxVolumeCheck.Find(folders, _ => new SandboxVolume("exFAT", true)));
        Assert.Single(SandboxVolumeCheck.Find(folders, _ => new SandboxVolume("", false)));
    }

    [Fact]
    public void AZarGameIsPassedByHandleSoItsDriveIsNotChecked()
    {
        var root = Directory.CreateTempSubdirectory("sharpemu-volume-").FullName;
        try
        {
            var executable = Directory.CreateDirectory(Path.Combine(root, "emu")).FullName;
            var data = Directory.CreateDirectory(Path.Combine(root, "data")).FullName;
            var zar = Path.Combine(root, "game.zar");
            File.WriteAllBytes(zar, ZArchiveTestWriter.Build([("eboot.bin", new byte[16])]));

            var plan = SandboxPlan.Build(new SandboxInputs(executable, data, zar, null, [], _ => null));

            Assert.Equal([Path.GetFullPath(zar)], plan.InheritedFiles);
            Assert.DoesNotContain(plan.Folders, folder => folder.Path.EndsWith(".zar", StringComparison.OrdinalIgnoreCase));
            // Only the zar's own folder is on exFAT here: the plan grants nothing there.
            Assert.Empty(SandboxVolumeCheck.Find(plan.Folders, _ => Ntfs));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AMissingZarIsReportedAsMissingNotAsAnExFatFolder()
    {
        var root = Directory.CreateTempSubdirectory("sharpemu-missing-").FullName;
        try
        {
            var missing = Path.Combine(root, "demon.zar");
            var inputs = new SandboxInputs(root, root, missing, null, [], _ => null);

            Assert.Equal($"Game file not found: {missing}", SandboxPlan.FindMissingGame(inputs));
            Assert.Equal(
                $"Game file not found: {Path.Combine(root, "eboot.bin")}",
                SandboxPlan.FindMissingGame(inputs with { EbootPath = Path.Combine(root, "eboot.bin") }));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AnExistingGameOrNoGameIsNotReportedMissing()
    {
        var root = Directory.CreateTempSubdirectory("sharpemu-present-").FullName;
        try
        {
            var eboot = Path.Combine(root, "eboot.bin");
            File.WriteAllBytes(eboot, new byte[4]);

            Assert.Null(SandboxPlan.FindMissingGame(new SandboxInputs(root, root, eboot, null, [], _ => null)));
            Assert.Null(SandboxPlan.FindMissingGame(new SandboxInputs(root, root, null, null, [], _ => null)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TheArticleFollowsTheFileSystemName()
    {
        var folder = new[] { new SandboxFolder(@"X:\x", SandboxAccess.ReadExecute, "x") };

        Assert.Contains("on an exFAT drive", SandboxVolumeCheck.Find(folder, _ => ExFat)[0].Message);
        Assert.Contains("on a FAT32 drive", SandboxVolumeCheck.Find(folder, _ => new SandboxVolume("FAT32", false))[0].Message);
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void TheRealVolumeOfTheTempFolderIsAnswered()
    {
        // Windows handles and Windows path rules.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var volume = SandboxVolumeCheck.QueryVolume(Path.GetTempPath());

        Assert.NotNull(volume);
        Assert.False(string.IsNullOrEmpty(volume.FileSystem));
    }
}
