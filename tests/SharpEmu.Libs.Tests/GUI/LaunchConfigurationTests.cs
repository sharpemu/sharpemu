// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.GUI;
using Xunit;

namespace SharpEmu.Libs.Tests.GUI;

public sealed class LaunchConfigurationTests
{
    [Fact]
    public void DefaultLaunchSpecifiesEverySwitchAndLeavesAutomaticChoicesAutomatic()
    {
        var launch = new LaunchConfiguration(EffectiveLaunchSettings.Defaults, []);
        var environment = launch.BuildEnvironment(new GuiSettings());
        Assert.All(LaunchConfiguration.SwitchVariables, name => Assert.True(environment.ContainsKey(name)));
        Assert.All(EnvironmentSettings.All, setting => Assert.True(environment.ContainsKey(setting.Variable)));
        Assert.Equal("1", environment["SHARPEMU_WRITABLE_APP0"]);
        Assert.Equal("1", environment["SHARPEMU_DISABLE_IMPORT_LOOP_GUARD"]);
        Assert.Equal("1", environment["SHARPEMU_STRICT_COMPUTE"]);
        Assert.Equal("0", environment["SHARPEMU_SKIP_RT"]);
        Assert.Equal("0", environment["SHARPEMU_LOG_IO"]);
        Assert.Equal("1", environment["SHARPEMU_SHADER_PREWARM"]);
        Assert.Equal("1", environment["SHARPEMU_GNM_TESSELLATION"]);
        // Automatic leaves the platform's own pipeline-cache choice in place.
        Assert.Null(environment["SHARPEMU_VK_PIPELINE_CACHE"]);
        Assert.Equal("guest", environment["SHARPEMU_BINK_MODE"]);
        Assert.Equal("full", environment["SHARPEMU_GPU_READBACKS"]);
        Assert.Equal("Sharp", environment[LaunchConfiguration.DefaultProfileVariable]);
        Assert.Null(environment["SHARPEMU_GNM_TROPHY_KEY"]);
    }

    [Fact]
    public void DisabledGameSwitchesOverrideInheritedEnabledValues()
    {
        var game = new PerGameSettings
        {
            EnvironmentChanges = ["SHARPEMU_WRITABLE_APP0=0", "SHARPEMU_SHADER_PREWARM=0", "SHARPEMU_VK_PIPELINE_CACHE=1"],
            GpuReadbacks = "Off",
        };
        var launch = new LaunchConfiguration(EffectiveLaunchSettings.Resolve(game), []);
        var environment = launch.BuildEnvironment(new GuiSettings());
        Assert.Equal("0", environment["SHARPEMU_WRITABLE_APP0"]);
        Assert.Equal("0", environment["SHARPEMU_SHADER_PREWARM"]);
        Assert.Equal("1", environment["SHARPEMU_VK_PIPELINE_CACHE"]);
        Assert.Equal("off", environment["SHARPEMU_GPU_READBACKS"]);
    }

    [Fact]
    public void LauncherWideValuesComeFromTheLauncherSettings()
    {
        var launch = new LaunchConfiguration(EffectiveLaunchSettings.Defaults, []);
        var environment = launch.BuildEnvironment(new GuiSettings { DefaultProfile = "  Player  " });
        Assert.Equal("Player", environment[LaunchConfiguration.DefaultProfileVariable]);
    }

    [Fact]
    public void CustomVariablesWinWithinFileAndScriptVariablesWinForCli()
    {
        var launch = new LaunchConfiguration(EffectiveLaunchSettings.Defaults,
            ["SHARPEMU_LOG_IO=1", "EXTRA_VALUE=custom", "SHARPEMU_VK_PIPELINE_CACHE=0"]);
        var environment = launch.BuildEnvironment(new GuiSettings());
        Assert.Equal("1", environment["SHARPEMU_LOG_IO"]);
        Assert.Equal("custom", environment["EXTRA_VALUE"]);
        var cli = launch.BuildCliEnvironment(new GuiSettings(), name => name == "SHARPEMU_LOG_IO" ? "0" : null);
        Assert.False(cli.ContainsKey("SHARPEMU_LOG_IO"));
        Assert.Equal("custom", cli["EXTRA_VALUE"]);
        Assert.Equal("0", cli["SHARPEMU_VK_PIPELINE_CACHE"]);
    }

    [Fact]
    public void ArgumentsIncludeExplicitZeroAndFalseOverridesAndTheRefreshRate()
    {
        var launch = new LaunchConfiguration(EffectiveLaunchSettings.Defaults with { RefreshRate = 120 }, []);
        var arguments = launch.BuildArguments("test game.zar", "EnglishUS");
        Assert.Contains("--strict=off", arguments);
        Assert.Contains("--trace-imports=0", arguments);
        Assert.Contains("--no-log-file", arguments);
        Assert.Contains("--refresh-rate=120", arguments);
        Assert.Equal(Path.GetFullPath("test game.zar"), arguments[^1]);
    }

    [Fact]
    public void LogPathRespectsTheSharedLauncherPreference()
    {
        var launcher = new GuiSettings { LogFilePath = Path.Combine("logs", "custom.log"), OverrideLogFile = true };
        Assert.Equal(launcher.LogFilePath, LaunchConfiguration.ResolveLogFilePath(launcher, "PPSA01234", "default-logs"));
        launcher.OverrideLogFile = false;
        var timestamped = LaunchConfiguration.ResolveLogFilePath(launcher, "PPSA01234", "default-logs");
        Assert.Equal("logs", Path.GetDirectoryName(timestamped));
        Assert.StartsWith("custom-PPSA01234-", Path.GetFileName(timestamped));
        Assert.EndsWith(".log", timestamped);
        launcher.LogFilePath = null;
        Assert.Equal("default-logs", Path.GetDirectoryName(LaunchConfiguration.ResolveLogFilePath(launcher, "PPSA01234", "default-logs")));
    }

}
