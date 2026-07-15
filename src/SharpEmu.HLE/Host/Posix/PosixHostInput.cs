// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.Host.Posix;

/// <summary>
/// Bridges a window-provided input source into the host input seam. POSIX
/// hosts have no user32/XInput/raw-HID readers; keyboard and gamepad state
/// come from the presenter's GLFW window instead, which registers itself via
/// <see cref="SetSource"/> once the window exists. Until then (and with no
/// window at all, e.g. headless runs) every query reports neutral input.
/// Rumble and lightbar are unsupported by the GLFW input layer and no-op.
/// </summary>
public interface IPosixWindowInputSource
{
    /// <summary>True while the window's keyboard is delivering events.</summary>
    bool HasKeyboardFocus { get; }

    /// <summary>Windows virtual-key semantics; the source translates.</summary>
    bool IsKeyDown(int virtualKey);

    /// <summary>Same contract as <see cref="IHostInput.GetGamepadStates"/>.</summary>
    int GetGamepadStates(Span<HostGamepadState> destination);

    string? DescribeConnectedGamepad();
}

// Public so the presenter's window layer (SharpEmu.Libs) can register its
// input source; the platform still constructs the singleton itself.
public sealed class PosixHostInput : IHostInput
{
    private static volatile IPosixWindowInputSource? _source;

    /// <summary>Called by the presenter's window layer when input is ready.</summary>
    public static void SetSource(IPosixWindowInputSource source)
    {
        _source = source;
    }

    public void EnsureStarted()
    {
        // Device readers are event-driven off the window thread; nothing to start.
    }

    public int GetGamepadStates(Span<HostGamepadState> destination)
    {
        return _source?.GetGamepadStates(destination) ?? 0;
    }

    public string? DescribeConnectedGamepad() => _source?.DescribeConnectedGamepad();

    public void SetRumble(byte largeMotor, byte smallMotor)
    {
    }

    public void SetTriggerRumble(byte? leftTrigger, byte? rightTrigger)
    {
    }

    public void SetLightbar(byte red, byte green, byte blue)
    {
    }

    public void ResetLightbar()
    {
    }

    public bool IsHostWindowFocused()
    {
        // GLFW only delivers key events to the focused window, so a
        // delivering keyboard implies focus.
        return _source?.HasKeyboardFocus ?? false;
    }

    public bool IsKeyDown(int virtualKey)
    {
        return _source?.IsKeyDown(virtualKey) ?? false;
    }
}
