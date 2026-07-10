// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Silk.NET.Input;

namespace SharpEmu.Libs.Pad;

/// <summary>
/// Keyboard state sampled from the presenter's window, used by the pad
/// exports on hosts without user32 (macOS/Linux). The presenter attaches
/// the window's input context once the window exists; key events arrive on
/// the window thread and pad reads happen on guest threads, so the pressed
/// set is guarded.
/// </summary>
public static class HostWindowInput
{
    private static readonly object Gate = new();
    private static readonly HashSet<Key> Pressed = new();
    private static volatile bool _connected;

    /// <summary>True once a window keyboard is delivering events.</summary>
    public static bool IsConnected => _connected;

    public static void Attach(IInputContext input)
    {
        foreach (var keyboard in input.Keyboards)
        {
            keyboard.KeyDown += (_, key, _) =>
            {
                lock (Gate)
                {
                    Pressed.Add(key);
                }
            };
            keyboard.KeyUp += (_, key, _) =>
            {
                lock (Gate)
                {
                    Pressed.Remove(key);
                }
            };
        }

        if (input.Keyboards.Count > 0)
        {
            _connected = true;
        }
    }

    public static bool IsKeyDown(Key key)
    {
        lock (Gate)
        {
            return Pressed.Contains(key);
        }
    }
}
