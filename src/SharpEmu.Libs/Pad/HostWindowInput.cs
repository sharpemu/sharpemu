// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Silk.NET.Input;

namespace SharpEmu.Libs.Pad;

/// <summary>
/// Keyboard and gamepad state sampled from the presenter's window, used by
/// the pad exports on hosts without user32/XInput/hid (macOS/Linux). The
/// presenter attaches the window's input context once the window exists;
/// input events arrive on the window thread and pad reads happen on guest
/// threads, so all state is guarded.
/// </summary>
public static class HostWindowInput
{
    private static readonly object Gate = new();
    private static readonly HashSet<Key> Pressed = new();
    private static volatile bool _connected;

    // Latest window-gamepad snapshot, ORBIS conventions (see PadState).
    private static bool _gamepadConnected;
    private static uint _gamepadButtons;
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
                _gamepadButtons = 0;
                _gamepadLeftX = 128;
                _gamepadLeftY = 128;
                _gamepadRightX = 128;
                _gamepadRightY = 128;
                _gamepadL2 = 0;
                _gamepadR2 = 0;
            }
        };
    }

    public static bool IsKeyDown(Key key)
    {
        lock (Gate)
        {
            return Pressed.Contains(key);
        }
    }

    internal static bool TryGetGamepadState(out PadState state)
    {
        lock (Gate)
        {
            if (!_gamepadConnected)
            {
                state = default;
                return false;
            }

            state = new PadState(
                Connected: true,
                Buttons: _gamepadButtons,
                LeftX: _gamepadLeftX,
                LeftY: _gamepadLeftY,
                RightX: _gamepadRightX,
                RightY: _gamepadRightY,
                L2: _gamepadL2,
                R2: _gamepadR2);
            return true;
        }
    }

    private static void AttachGamepad(IGamepad gamepad)
    {
        lock (Gate)
        {
            _gamepadConnected = true;
        }

        gamepad.ButtonDown += (_, button) =>
        {
            var bit = MapButton(button.Name);
            if (bit == 0)
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
            if (bit == 0)
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
            // matching the ORBIS 0..255 down-growing convention after biasing.
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
                        _gamepadButtons |= OrbisPadButton.L2;
                    }
                    else
                    {
                        _gamepadButtons &= ~OrbisPadButton.L2;
                    }
                }
                else
                {
                    _gamepadR2 = value;
                    if (value > 64)
                    {
                        _gamepadButtons |= OrbisPadButton.R2;
                    }
                    else
                    {
                        _gamepadButtons &= ~OrbisPadButton.R2;
                    }
                }
            }
        };
    }

    private static byte ToStickByte(float value)
    {
        return (byte)Math.Clamp((int)(128.0f + value * 127.0f), 0, 255);
    }

    private static uint MapButton(ButtonName name) => name switch
    {
        // GLFW reports the Xbox layout: A=Cross, B=Circle, X=Square, Y=Triangle.
        ButtonName.A => OrbisPadButton.Cross,
        ButtonName.B => OrbisPadButton.Circle,
        ButtonName.X => OrbisPadButton.Square,
        ButtonName.Y => OrbisPadButton.Triangle,
        ButtonName.LeftBumper => OrbisPadButton.L1,
        ButtonName.RightBumper => OrbisPadButton.R1,
        ButtonName.Back => OrbisPadButton.TouchPad,
        ButtonName.Start => OrbisPadButton.Options,
        ButtonName.LeftStick => OrbisPadButton.L3,
        ButtonName.RightStick => OrbisPadButton.R3,
        ButtonName.DPadUp => OrbisPadButton.Up,
        ButtonName.DPadRight => OrbisPadButton.Right,
        ButtonName.DPadDown => OrbisPadButton.Down,
        ButtonName.DPadLeft => OrbisPadButton.Left,
        _ => 0,
    };
}
