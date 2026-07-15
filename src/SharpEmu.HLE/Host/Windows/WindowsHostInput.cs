// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace SharpEmu.HLE.Host.Windows;

/// <summary>
/// Windows input backend: DualSense over raw HID plus XInput controllers for gamepads,
/// user32 for the keyboard-fallback queries. Rumble fans out to every reader; lightbar
/// only exists on the DualSense.
/// </summary>
internal sealed partial class WindowsHostInput : IHostInput
{
    public void EnsureStarted()
    {
        WindowsDualSenseReader.EnsureStarted();
        WindowsXInputReader.EnsureStarted();
    }

    public int GetGamepadStates(Span<HostGamepadState> destination)
    {
        var count = 0;
        if (count < destination.Length && WindowsDualSenseReader.TryGetState(out var dualSense))
        {
            destination[count++] = dualSense;
        }

        if (count < destination.Length && WindowsXInputReader.TryGetState(out var xinput))
        {
            destination[count++] = xinput;
        }

        return count;
    }

    public string? DescribeConnectedGamepad()
    {
        if (WindowsDualSenseReader.TryGetState(out _))
        {
            return "DualSense";
        }

        return WindowsXInputReader.TryGetState(out _) ? "Xbox controller" : null;
    }

    public void SetRumble(byte largeMotor, byte smallMotor)
    {
        WindowsDualSenseReader.SetRumble(largeMotor, smallMotor);
        WindowsXInputReader.SetRumble(largeMotor, smallMotor);
    }

    public void SetTriggerRumble(byte? leftTrigger, byte? rightTrigger) =>
        WindowsXInputReader.SetTriggerRumble(leftTrigger, rightTrigger);

    public void SetLightbar(byte red, byte green, byte blue) =>
        WindowsDualSenseReader.SetLightbar(red, green, blue);

    public void ResetLightbar() => WindowsDualSenseReader.ResetLightbar();

    public bool IsHostWindowFocused()
    {
        var foregroundWindow = GetForegroundWindow();
        if (foregroundWindow == 0)
        {
            return false;
        }

        GetWindowThreadProcessId(foregroundWindow, out var processId);
        return processId == (uint)Environment.ProcessId;
    }

    public bool IsKeyDown(int virtualKey) =>
        (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int vKey);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint hWnd, out uint processId);
}
