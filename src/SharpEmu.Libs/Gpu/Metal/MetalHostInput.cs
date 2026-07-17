// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.Host;
using SharpEmu.HLE.Host.Posix;

namespace SharpEmu.Libs.Gpu.Metal;

/// <summary>
/// Keyboard state sampled from the Metal presenter's window, feeding the POSIX
/// host input seam so pad emulation works like the Vulkan presenter's
/// HostWindowInput. Key events arrive on the AppKit main thread as macOS
/// virtual key codes; pad reads happen on guest threads, so state is guarded.
/// Window gamepads are not surfaced by AppKit — controller support would go
/// through GameController.framework and is out of scope here.
/// </summary>
internal static class MetalHostInput
{
    private static readonly object Gate = new();
    private static readonly HashSet<ushort> Pressed = new();
    private static volatile bool _connected;

    /// <summary>Registers this window's keyboard as the host input source.</summary>
    public static void Attach()
    {
        _connected = true;
        PosixHostInput.SetSource(new MetalWindowInputSource());
        Console.Error.WriteLine("[LOADER][INFO] Window keyboard input attached for pad emulation.");
    }

    // Debug automation: SHARPEMU_METAL_AUTOKEY="12:0x24,15:0x24" presses the
    // macOS key code at each elapsed-seconds mark for a few frames, letting
    // headless test runs navigate menus without a human at the keyboard.
    private static readonly List<(double At, ushort Key, bool[] State)> _autoKeys = ParseAutoKeys();
    private static readonly System.Diagnostics.Stopwatch _autoKeyClock =
        System.Diagnostics.Stopwatch.StartNew();

    private static List<(double, ushort, bool[])> ParseAutoKeys()
    {
        var keys = new List<(double, ushort, bool[])>();
        var spec = Environment.GetEnvironmentVariable("SHARPEMU_METAL_AUTOKEY");
        if (string.IsNullOrWhiteSpace(spec))
        {
            return keys;
        }

        foreach (var entry in spec.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = entry.Split(':');
            if (parts.Length == 2 &&
                double.TryParse(parts[0], out var at) &&
                TryParseKeyCode(parts[1], out var key))
            {
                keys.Add((at, key, new bool[2]));
            }
        }

        return keys;
    }

    private static bool TryParseKeyCode(string text, out ushort key)
    {
        return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ushort.TryParse(text[2..], System.Globalization.NumberStyles.HexNumber, null, out key)
            : ushort.TryParse(text, out key);
    }

    /// <summary>Called once per render frame; fires and releases scripted keys.</summary>
    public static void PumpAutoKeys()
    {
        if (_autoKeys.Count == 0)
        {
            return;
        }

        var elapsed = _autoKeyClock.Elapsed.TotalSeconds;
        foreach (var (at, key, state) in _autoKeys)
        {
            if (!state[0] && elapsed >= at)
            {
                state[0] = true;
                KeyDown(key, isRepeat: false);
                Console.Error.WriteLine($"[LOADER][INFO] Metal autokey press 0x{key:X} at {elapsed:F1}s");
            }
            else if (state[0] && !state[1] && elapsed >= at + 0.2)
            {
                state[1] = true;
                KeyUp(key);
            }
        }
    }

    public static void KeyDown(ushort keyCode, bool isRepeat)
    {
        // kVK_F1: parity with the Vulkan window's perf-overlay toggle.
        if (keyCode == 0x7A && !isRepeat)
        {
            VideoOut.PerfOverlay.Toggle();
        }

        lock (Gate)
        {
            Pressed.Add(keyCode);
        }
    }

    public static void KeyUp(ushort keyCode)
    {
        lock (Gate)
        {
            Pressed.Remove(keyCode);
        }
    }

    private static bool IsKeyCodeDown(ushort keyCode)
    {
        lock (Gate)
        {
            return Pressed.Contains(keyCode);
        }
    }

    private sealed class MetalWindowInputSource : IPosixWindowInputSource
    {
        public bool HasKeyboardFocus => _connected;

        public bool IsKeyDown(int virtualKey) =>
            TryMapVirtualKey(virtualKey, out var keyCode) && IsKeyCodeDown(keyCode);

        public int GetGamepadStates(Span<HostGamepadState> destination) => 0;

        public string? DescribeConnectedGamepad() => null;
    }

    /// <summary>Windows virtual-key semantics (the seam's contract) to macOS
    /// kVK virtual key codes, covering the keys pad emulation polls.</summary>
    private static bool TryMapVirtualKey(int vk, out ushort keyCode)
    {
        keyCode = vk switch
        {
            0x08 => 0x33, // Backspace -> kVK_Delete
            0x09 => 0x30, // Tab
            0x0D => 0x24, // Enter -> kVK_Return
            0x1B => 0x35, // Escape
            0x20 => 0x31, // Space
            0x25 => 0x7B, // Left
            0x26 => 0x7E, // Up
            0x27 => 0x7C, // Right
            0x28 => 0x7D, // Down
            // Letters: macOS ANSI key codes are layout-position based and
            // non-contiguous, so map each polled letter explicitly.
            0x41 => 0x00, // A
            0x42 => 0x0B, // B
            0x43 => 0x08, // C
            0x44 => 0x02, // D
            0x45 => 0x0E, // E
            0x46 => 0x03, // F
            0x47 => 0x05, // G
            0x48 => 0x04, // H
            0x49 => 0x22, // I
            0x4A => 0x26, // J
            0x4B => 0x28, // K
            0x4C => 0x25, // L
            0x4D => 0x2E, // M
            0x4E => 0x2D, // N
            0x4F => 0x1F, // O
            0x50 => 0x23, // P
            0x51 => 0x0C, // Q
            0x52 => 0x0F, // R
            0x53 => 0x01, // S
            0x54 => 0x11, // T
            0x55 => 0x20, // U
            0x56 => 0x09, // V
            0x57 => 0x0D, // W
            0x58 => 0x07, // X
            0x59 => 0x10, // Y
            0x5A => 0x06, // Z
            _ => ushort.MaxValue,
        };
        return keyCode != ushort.MaxValue;
    }
}
