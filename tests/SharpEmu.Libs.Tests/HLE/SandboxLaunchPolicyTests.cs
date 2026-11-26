// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.Host.Windows;
using Xunit;

namespace SharpEmu.Libs.Tests.HLE;

public sealed class SandboxLaunchPolicyTests
{
    [Fact]
    public void TheGuiSettingDefaultsToOn()
    {
        Assert.True(SandboxLaunchPolicy.GuiDefault);
        Assert.True(new SharpEmu.GUI.GuiSettings().RunInSandbox);
    }

    [Theory]
    [InlineData(true, null, true)]
    [InlineData(false, null, false)]
    [InlineData(true, "", true)]
    [InlineData(false, "yes", false)]
    [InlineData(false, "1", true)]
    [InlineData(true, "0", false)]
    [InlineData(true, "1", true)]
    [InlineData(false, "0", false)]
    public void TheEnvironmentOverridesTheSetting(bool setting, string? environment, bool expected) =>
        Assert.Equal(expected, SandboxLaunchPolicy.ShouldSandbox(setting, environment));

    [Fact]
    public void OnlyOneForcesTheSandboxOn()
    {
        Assert.True(SandboxLaunchPolicy.IsForcedOn("1"));
        Assert.False(SandboxLaunchPolicy.IsForcedOn("0"));
        Assert.False(SandboxLaunchPolicy.IsForcedOn(null));
    }

    [Fact]
    public void AGameOnExFatThrowsTheTypedVolumeFailure()
    {
        var folders = new[]
        {
            new SandboxFolder(@"C:\SharpEmu\user", SandboxAccess.ReadWrite, "user data"),
            new SandboxFolder(@"D:\Games\G", SandboxAccess.ReadExecute, "game folder (app0)", IsGame: true),
        };

        var exception = Assert.Throws<SandboxVolumeException>(() => SandboxVolumeCheck.ThrowIfUnusable(
            folders, path => path[0] == 'D' ? new SandboxVolume("exFAT", false) : new SandboxVolume("NTFS", true)));

        var problem = Assert.Single(exception.Problems);
        Assert.Equal("exFAT", problem.Volume.FileSystem);
        Assert.Same(problem, exception.GameProblem);
        Assert.Equal(SandboxVolumeCheck.Format(exception.Problems), exception.Message);
    }

    [Fact]
    public void OnlyANonGameFolderOnExFatHasNoGameProblem()
    {
        var folders = new[] { new SandboxFolder(@"D:\SharpEmu\user", SandboxAccess.ReadWrite, "user data") };

        var exception = Assert.Throws<SandboxVolumeException>(() => SandboxVolumeCheck.ThrowIfUnusable(
            folders, _ => new SandboxVolume("FAT32", false)));

        Assert.Null(exception.GameProblem);
    }

    [Fact]
    public void NtfsFoldersDoNotThrow() =>
        SandboxVolumeCheck.ThrowIfUnusable(
            [new SandboxFolder(@"C:\G", SandboxAccess.ReadExecute, "game folder (app0)", IsGame: true)],
            _ => new SandboxVolume("NTFS", true));
}
