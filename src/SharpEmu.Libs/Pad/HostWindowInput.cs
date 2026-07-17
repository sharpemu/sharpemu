// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.Host;
using SharpEmu.HLE.Host.Posix;
using Silk.NET.Input;

namespace SharpEmu.Libs.Pad;

/// <summary>
/// Keyboard and gamepad state sampled from the presenter's window, feeding
/// the POSIX host input seam (macOS/Linux have no user32/XInput/raw-HID
/// readers). The presenter attaches the window's input context once the
/// window exists; input events arrive on the window thread and pad reads
/// happen on guest threads, so all state is guarded.
/// </summary>
public static class HostWindowInput
{
    private static readonly object Gate = new();
    private static readonly HashSet<Key> Pressed = new();
    private static volatile bool _connected;

    // Latest window-gamepad snapshot in the host seam's conventions.
    private static bool _gamepadConnected;
    private static string? _gamepadName;
    private static HostGamepadButtons _gamepadButtons;
    private static byte _gamepadLeftX = 128;
    private static byte _gamepadLeftY = 128;
    private static byte _gamepadRightX = 128;
    private static byte _gamepadRightY = 128;
    private static byte _gamepadL2;
    private static byte _gamepadR2;

    /// <summary>True once a window keyboard is delivering events.</summary>
    public static bool IsConnected => _connected;

    public static void Attach(IInputContext input)
    {
        foreach (var keyboard in input.Keyboards)
        {
            keyboard.KeyDown += (_, key, _) =>
            {
                if (key == Key.F1)
                {
                    VideoOut.PerfOverlay.Toggle();
                }

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

        foreach (var gamepad in input.Gamepads)
        {
            AttachGamepad(gamepad);
        }

        input.ConnectionChanged += (device, connected) =>
        {
            if (device is not IGamepad gamepad)
            {
                return;
            }

            if (connected)
            {
                AttachGamepad(gamepad);
                return;
            }

            lock (Gate)
            {
                _gamepadConnected = false;
                _gamepadName = null;
                _gamepadButtons = HostGamepadButtons.None;
                _gamepadLeftX = 128;
                _gamepadLeftY = 128;
                _gamepadRightX = 128;
                _gamepadRightY = 128;
                _gamepadL2 = 0;
                _gamepadR2 = 0;
            }
        };

        PosixHostInput.SetSource(new WindowInputSource());
    }

    public static bool IsKeyDown(Key key)
    {
        lock (Gate)
        {
            return Pressed.Contains(key);
        }
    }

    private sealed class WindowInputSource : IPosixWindowInputSource
    {
        public bool HasKeyboardFocus => _connected;

        public bool IsKeyDown(int virtualKey)
        {
            return TryMapVirtualKey(virtualKey, out var key) && HostWindowInput.IsKeyDown(key);
        }

        public int GetGamepadStates(Span<HostGamepadState> destination)
        {
            lock (Gate)
            {
                if (!_gamepadConnected || destination.Length == 0)
                {
                    return 0;
                }

                destination[0] = new HostGamepadState(
                    Connected: true,
                    Buttons: _gamepadButtons,
                    LeftX: _gamepadLeftX,
                    LeftY: _gamepadLeftY,
                    RightX: _gamepadRightX,
                    RightY: _gamepadRightY,
                    LeftTrigger: _gamepadL2,
                    RightTrigger: _gamepadR2);
                return 1;
            }
        }

        public string? DescribeConnectedGamepad()
        {
            lock (Gate)
            {
                return _gamepadConnected ? _gamepadName ?? "GLFW gamepad" : null;
            }
        }
    }

    private static bool TryMapVirtualKey(int vk, out Key key)
    {
        key = vk switch
        {
            0x08 => Key.Backspace,
            0x09 => Key.Tab,
            0x0D => Key.Enter,
            0x1B => Key.Escape,
            0x25 => Key.Left,
            0x26 => Key.Up,
            0x27 => Key.Right,
            0x28 => Key.Down,
            >= 0x41 and <= 0x5A => Key.A + (vk - 0x41),
            _ => Key.Unknown,
        };
        return key != Key.Unknown;
    }

    private static void AttachGamepad(IGamepad gamepad)
    {
        lock (Gate)
        {
            _gamepadConnected = true;
            _gamepadName = gamepad.Name;
        }

        gamepad.ButtonDown += (_, button) =>
        {
            var bit = MapButton(button.Name);
            if (bit == HostGamepadButtons.None)
            {
                return;
            }

            lock (Gate)
            {
                _gamepadButtons |= bit;
            }
        };
        gamepad.ButtonUp += (_, button) =>
        {
            var bit = MapButton(button.Name);
            if (bit == HostGamepadButtons.None)
            {
                return;
            }

            lock (Gate)
            {
                _gamepadButtons &= ~bit;
            }
        };
        gamepad.ThumbstickMoved += (_, thumbstick) =>
        {
            // Silk's GLFW backend reports sticks -1..1 with +Y pointing down,
            // matching the seam's 0..255 down-growing convention after biasing.
            var x = ToStickByte(thumbstick.X);
            var y = ToStickByte(thumbstick.Y);
            lock (Gate)
            {
                if (thumbstick.Index == 0)
                {
                    _gamepadLeftX = x;
                    _gamepadLeftY = y;
                }
                else
                {
                    _gamepadRightX = x;
                    _gamepadRightY = y;
                }
            }
        };
        gamepad.TriggerMoved += (_, trigger) =>
        {
            // GLFW gamepad triggers rest at -1 and saturate at +1.
            var value = (byte)Math.Clamp((int)((trigger.Position + 1.0f) * 0.5f * 255.0f), 0, 255);
            lock (Gate)
            {
                if (trigger.Index == 0)
                {
                    _gamepadL2 = value;
                    if (value > 64)
                    {
                        _gamepadButtons |= HostGamepadButtons.L2;
                    }
                    else
                    {
                        _gamepadButtons &= ~HostGamepadButtons.L2;
                    }
                }
                else
                {
                    _gamepadR2 = value;
                    if (value > 64)
                    {
                        _gamepadButtons |= HostGamepadButtons.R2;
                    }
                    else
                    {
                        _gamepadButtons &= ~HostGamepadButtons.R2;
                    }
                }
            }
        };
    }

    internal static byte ToStickByte(float value)
    {
        return (byte)Math.Clamp((int)MathF.Round((value + 1.0f) * 127.5f), 0, 255);
    }

    private static HostGamepadButtons MapButton(ButtonName name) => name switch
    {
        // GLFW reports the Xbox layout: A=Cross, B=Circle, X=Square, Y=Triangle.
        ButtonName.A => HostGamepadButtons.Cross,
        ButtonName.B => HostGamepadButtons.Circle,
        ButtonName.X => HostGamepadButtons.Square,
        ButtonName.Y => HostGamepadButtons.Triangle,
        ButtonName.LeftBumper => HostGamepadButtons.L1,
        ButtonName.RightBumper => HostGamepadButtons.R1,
        ButtonName.Back => HostGamepadButtons.TouchPad,
        ButtonName.Start => HostGamepadButtons.Options,
        ButtonName.LeftStick => HostGamepadButtons.L3,
        ButtonName.RightStick => HostGamepadButtons.R3,
        ButtonName.DPadUp => HostGamepadButtons.Up,
        ButtonName.DPadRight => HostGamepadButtons.Right,
        ButtonName.DPadDown => HostGamepadButtons.Down,
        ButtonName.DPadLeft => HostGamepadButtons.Left,
        _ => HostGamepadButtons.None,
    };
}
