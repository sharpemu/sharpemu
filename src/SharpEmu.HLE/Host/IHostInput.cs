// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.Host;

/// <summary>
/// Host input devices: gamepad state snapshots, force-feedback/lightbar sinks, and the
/// keyboard-fallback queries. Which physical readers exist (DualSense over raw HID,
/// XInput, evdev, ...) is a backend detail; merge policy between devices and the
/// keyboard lives in the HLE pad exports.
/// </summary>
public interface IHostInput
{
    /// <summary>Starts the background device readers once; safe to call repeatedly.</summary>
    void EnsureStarted();

    /// <summary>
    /// Fills <paramref name="destination"/> with snapshots of currently connected
    /// gamepads and returns how many were written (0 when none are connected).
    /// </summary>
    int GetGamepadStates(Span<HostGamepadState> destination);

    /// <summary>Human-readable name of the first connected gamepad, or null.</summary>
    string? DescribeConnectedGamepad();

    /// <summary>Sets rumble on all connected gamepads; large = strong/left motor.</summary>
    void SetRumble(byte largeMotor, byte smallMotor);

    /// <summary>
    /// Approximates per-trigger vibration on gamepads without independent trigger
    /// actuators; null leaves that trigger's current value unchanged.
    /// </summary>
    void SetTriggerRumble(byte? leftTrigger, byte? rightTrigger);

    void SetLightbar(byte red, byte green, byte blue);

    void ResetLightbar();

    /// <summary>True when a window of this process has keyboard focus.</summary>
    bool IsHostWindowFocused();

    /// <summary>Windows virtual-key code semantics; other backends translate.</summary>
    bool IsKeyDown(int virtualKey);
}
