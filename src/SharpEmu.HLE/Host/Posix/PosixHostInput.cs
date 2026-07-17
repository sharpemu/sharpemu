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
        return _source?.HasKeyboardFocus ?? IsEmbeddedX11WindowFocused();
    }

    public bool IsKeyDown(int virtualKey)
    {
        var source = _source;
        if (source is not null)
        {
            return source.IsKeyDown(virtualKey);
        }

        return IsEmbeddedX11WindowFocused() && IsEmbeddedX11KeyDown(virtualKey);
    }

    private static bool IsEmbeddedX11WindowFocused()
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        var display = HostSessionControl.EmbeddedHostDisplay;
        var window = HostSessionControl.EmbeddedHostWindow;
        if (display == 0 || window == 0 || XGetInputFocus(display, out var focusedWindow, out _) == 0 || focusedWindow == 0)
        {
            return false;
        }

        return GetTopLevelWindow(display, focusedWindow) == GetTopLevelWindow(display, window);
    }

    private static bool IsEmbeddedX11KeyDown(int virtualKey)
    {
        var display = HostSessionControl.EmbeddedHostDisplay;
        var keysym = ToX11Keysym(virtualKey);
        if (display == 0 || keysym == 0)
        {
            return false;
        }

        var keycode = XKeysymToKeycode(display, keysym);
        if (keycode == 0)
        {
            return false;
        }

        var keymap = new byte[32];
        XQueryKeymap(display, keymap);
        return (keymap[keycode >> 3] & (1 << (keycode & 7))) != 0;
    }

    private static nint GetTopLevelWindow(nint display, nint window)
    {
        var current = window;
        for (var depth = 0; depth < 16; depth++)
        {
            if (XQueryTree(display, current, out var root, out var parent, out var children, out _) == 0)
            {
                return 0;
            }

            if (children != 0)
            {
                XFree(children);
            }

            if (parent == 0 || parent == root)
            {
                return current;
            }

            current = parent;
        }

        return 0;
    }

    private static nuint ToX11Keysym(int virtualKey)
    {
        return virtualKey switch
        {
            0x08 => 0xFF08, // Backspace
            0x09 => 0xFF09, // Tab
            0x0D => 0xFF0D, // Return
            0x1B => 0xFF1B, // Escape
            0x25 => 0xFF51, // Left
            0x26 => 0xFF52, // Up
            0x27 => 0xFF53, // Right
            0x28 => 0xFF54, // Down
            >= 0x41 and <= 0x5A => (nuint)virtualKey,
            _ => 0,
        };
    }

    [System.Runtime.InteropServices.DllImport("libX11.so.6")]
    private static extern int XGetInputFocus(nint display, out nint focus, out int revertTo);

    [System.Runtime.InteropServices.DllImport("libX11.so.6")]
    private static extern int XQueryKeymap(nint display, [System.Runtime.InteropServices.Out] byte[] keysReturn);

    [System.Runtime.InteropServices.DllImport("libX11.so.6")]
    private static extern byte XKeysymToKeycode(nint display, nuint keysym);

    [System.Runtime.InteropServices.DllImport("libX11.so.6")]
    private static extern int XQueryTree(
        nint display,
        nint window,
        out nint root,
        out nint parent,
        out nint children,
        out uint childCount);

    [System.Runtime.InteropServices.DllImport("libX11.so.6")]
    private static extern int XFree(nint data);
}
