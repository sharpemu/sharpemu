// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.Host.Windows;

/// <summary>
/// Whether a GUI launch runs in the sandbox. Precedence: an explicit <c>SHARPEMU_SANDBOX=0</c> / <c>=1</c> in the
/// environment wins, then the GUI setting (default on). The command line does not use this: for it the sandbox
/// stays opt-in (<see cref="WindowsAppContainerSandbox.IsRequested"/>), so only the GUI defaults to on.
/// </summary>
public static class SandboxLaunchPolicy
{
    /// <summary>The default of the GUI setting "Run games in a sandbox".</summary>
    public const bool GuiDefault = true;

    /// <summary>The variable that overrides the setting (and, on the command line, the opt-in switch).</summary>
    public const string Variable = "SHARPEMU_SANDBOX";

    public static bool ShouldSandbox(bool guiSetting, string? environmentValue) =>
        environmentValue switch
        {
            "1" => true,
            "0" => false,
            _ => guiSetting,
        };

    /// <summary>True when the environment forces the sandbox on, so no setting or one-off choice can turn it off.</summary>
    public static bool IsForcedOn(string? environmentValue) => environmentValue == "1";
}
