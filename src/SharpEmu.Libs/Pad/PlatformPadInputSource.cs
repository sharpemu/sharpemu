// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace SharpEmu.Libs.Pad;

internal interface IPlatformPadInputSource
{
    bool IsInputActive { get; }

    bool IsBindingDown(PadInputBinding binding);

    MouseDelta ReadMouseDelta();
}

internal static class PlatformPadInputSource
{
    public static IPlatformPadInputSource Create() =>
        OperatingSystem.IsWindows()
            ? new WindowsPlatformPadInputSource()
            : new NullPlatformPadInputSource();
}

internal sealed class NullPlatformPadInputSource : IPlatformPadInputSource
{
    public bool IsInputActive => false;

    public bool IsBindingDown(PadInputBinding binding) => false;

    public MouseDelta ReadMouseDelta() => new(0, 0);
}

internal sealed class WindowsPlatformPadInputSource : IPlatformPadInputSource
{
    private bool _hasLastCursorPosition;
    private POINT _lastCursorPosition;

    public bool IsInputActive => IsEmulatorWindowFocused();

    public bool IsBindingDown(PadInputBinding binding)
    {
        if (!IsInputActive)
        {
            return false;
        }

        var virtualKey = binding.Kind switch
        {
            PadInputBindingKind.KeyboardKey => VirtualKeyFromName(binding.Code),
            PadInputBindingKind.MouseButton => MouseButtonVirtualKey(binding.Code),
            _ => 0,
        };

        return virtualKey != 0 && (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
    }

    public MouseDelta ReadMouseDelta()
    {
        if (!IsInputActive || !GetCursorPos(out var position))
        {
            _hasLastCursorPosition = false;
            return new MouseDelta(0, 0);
        }

        if (!_hasLastCursorPosition)
        {
            _lastCursorPosition = position;
            _hasLastCursorPosition = true;
            return new MouseDelta(0, 0);
        }

        var delta = new MouseDelta(
            position.X - _lastCursorPosition.X,
            position.Y - _lastCursorPosition.Y);
        _lastCursorPosition = position;
        return delta;
    }

    private static bool IsEmulatorWindowFocused()
    {
        var foregroundWindow = GetForegroundWindow();
        if (foregroundWindow == 0)
        {
            return false;
        }

        GetWindowThreadProcessId(foregroundWindow, out var processId);
        return processId == (uint)Environment.ProcessId;
    }

    private static int MouseButtonVirtualKey(string code) => code.ToLowerInvariant() switch
    {
        "mouseleft" or "leftbutton" => 0x01,
        "mouseright" or "rightbutton" => 0x02,
        "mousemiddle" or "middlebutton" => 0x04,
        "mousex1" => 0x05,
        "mousex2" => 0x06,
        _ => 0,
    };

    private static int VirtualKeyFromName(string keyName)
    {
        if (string.IsNullOrWhiteSpace(keyName))
        {
            return 0;
        }

        var normalized = keyName.Trim();
        if (normalized.Length == 1)
        {
            var c = char.ToUpperInvariant(normalized[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                return c;
            }
        }

        if (normalized.StartsWith("D", StringComparison.OrdinalIgnoreCase) &&
            normalized.Length == 2 &&
            char.IsDigit(normalized[1]))
        {
            return normalized[1];
        }

        if (normalized.StartsWith("F", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(normalized[1..], out var functionKey) &&
            functionKey is >= 1 and <= 24)
        {
            return 0x6F + functionKey;
        }

        return normalized.ToLowerInvariant() switch
        {
            "back" or "backspace" => 0x08,
            "tab" => 0x09,
            "enter" or "return" => 0x0D,
            "escape" or "esc" => 0x1B,
            "space" => 0x20,
            "pageup" => 0x21,
            "pagedown" => 0x22,
            "end" => 0x23,
            "home" => 0x24,
            "left" => 0x25,
            "up" => 0x26,
            "right" => 0x27,
            "down" => 0x28,
            "insert" => 0x2D,
            "delete" or "del" => 0x2E,
            "leftshift" or "rightshift" or "shift" => 0x10,
            "leftctrl" or "rightctrl" or "ctrl" or "control" => 0x11,
            "leftalt" or "rightalt" or "alt" or "menu" => 0x12,
            "capslock" => 0x14,
            "numlock" => 0x90,
            "scroll" or "scrolllock" => 0x91,
            _ => 0,
        };
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;

        public int Y;
    }
}
