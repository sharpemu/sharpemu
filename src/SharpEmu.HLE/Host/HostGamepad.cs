// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.Host.DualSense;
using SharpEmu.HLE.Host.Windows;

namespace SharpEmu.HLE.Host;

/// <summary>
/// The launcher's view of host controllers: one call that answers "is a pad
/// connected, what is it, and what is it doing", plus the feedback sinks the
/// controller tester drives. Which readers exist per platform is a detail
/// this facade hides — the DualSense is read on Windows, Linux and macOS,
/// while XInput pads are Windows-only.
/// </summary>
public static class HostGamepad
{
    /// <summary>Starts every background reader this host supports; safe to call repeatedly.</summary>
    public static void EnsureStarted()
    {
        DualSenseReader.EnsureStarted();
        WindowsXInputReader.EnsureStarted(); // no-ops off Windows
    }

    /// <summary>
    /// Snapshot of the active pad. The DualSense wins when both are present;
    /// <paramref name="name"/> is a display name and null when nothing is
    /// connected.
    /// </summary>
    public static bool TryGetState(out HostGamepadState state, out string? name)
    {
        if (DualSenseReader.TryGetState(out state))
        {
            name = "DualSense";
            return true;
        }

        if (WindowsXInputReader.TryGetState(out state))
        {
            name = "Xbox controller";
            return true;
        }

        name = null;
        return false;
    }

    /// <summary>
    /// A user-actionable explanation for why a connected pad cannot be read
    /// (Linux hidraw permissions, macOS Input Monitoring), or null when the
    /// plain answer is "nothing is plugged in".
    /// </summary>
    public static string? UnavailableReason => DualSenseReader.UnavailableReason;

    /// <summary>Sets rumble on every connected pad; large = strong/left motor.</summary>
    public static void SetRumble(byte largeMotor, byte smallMotor)
    {
        DualSenseReader.SetRumble(largeMotor, smallMotor);
        WindowsXInputReader.SetRumble(largeMotor, smallMotor);
    }
}
