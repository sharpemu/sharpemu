// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.HLE.Host;
using SDL;
using static SDL.SDL3;

namespace SharpEmu.Libs.Pad;

internal static unsafe class SdlGamepadStateReader
{
    public static void EnableSonyHidApi()
    {
        fixed (byte* enabled = "1"u8)
        fixed (byte* ps4 = SDL_HINT_JOYSTICK_HIDAPI_PS4)
        fixed (byte* ps5 = SDL_HINT_JOYSTICK_HIDAPI_PS5)
        {
            SDL_SetHint(ps4, enabled);
            SDL_SetHint(ps5, enabled);
        }
    }

    public static SDL_Gamepad* OpenPreferredGamepad()
    {
        using var gamepads = SDL_GetGamepads();
        if (gamepads is null || gamepads.Count == 0)
        {
            return null;
        }

        var selected = gamepads[0];
        for (var index = 0; index < gamepads.Count; index++)
        {
            var type = SDL_GetRealGamepadTypeForID(gamepads[index]);
            if (type is SDL_GamepadType.SDL_GAMEPAD_TYPE_PS5 or SDL_GamepadType.SDL_GAMEPAD_TYPE_PS4)
            {
                selected = gamepads[index];
                break;
            }
        }

        return SDL_OpenGamepad(selected);
    }

    // SDL's gamepad buttons and the pad buttons they are, for a state read and a button event alike.
    private static readonly (SDL_GamepadButton Source, HostGamepadButtons Target)[] ButtonMap =
    [
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_SOUTH, HostGamepadButtons.Cross),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_EAST, HostGamepadButtons.Circle),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_WEST, HostGamepadButtons.Square),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_NORTH, HostGamepadButtons.Triangle),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_LEFT_SHOULDER, HostGamepadButtons.L1),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_RIGHT_SHOULDER, HostGamepadButtons.R1),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_LEFT_STICK, HostGamepadButtons.L3),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_RIGHT_STICK, HostGamepadButtons.R3),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_BACK, HostGamepadButtons.Create),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_GUIDE, HostGamepadButtons.Ps),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_START, HostGamepadButtons.Options),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_MISC1, HostGamepadButtons.Mic),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_TOUCHPAD, HostGamepadButtons.TouchPad),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_UP, HostGamepadButtons.Up),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_RIGHT, HostGamepadButtons.Right),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_DOWN, HostGamepadButtons.Down),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_LEFT, HostGamepadButtons.Left),
    ];

    public static HostGamepadButtons MapButton(byte button)
    {
        foreach (var (source, target) in ButtonMap)
        {
            if ((byte)source == button)
            {
                return target;
            }
        }

        return HostGamepadButtons.None;
    }

    // A trigger past a quarter of its travel is also its digital button.
    public static bool IsTriggerPressed(byte value) => value > 64;

    public static HostGamepadState Read(SDL_Gamepad* gamepad)
    {
        var buttons = HostGamepadButtons.None;
        foreach (var (source, target) in ButtonMap)
        {
            AddButton(gamepad, source, target, ref buttons);
        }

        var leftTrigger = HostWindowInput.ToTriggerByte(
            SDL_GetGamepadAxis(gamepad, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFT_TRIGGER));
        var rightTrigger = HostWindowInput.ToTriggerByte(
            SDL_GetGamepadAxis(gamepad, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHT_TRIGGER));
        if (IsTriggerPressed(leftTrigger))
        {
            buttons |= HostGamepadButtons.L2;
        }
        if (IsTriggerPressed(rightTrigger))
        {
            buttons |= HostGamepadButtons.R2;
        }

        var battery = 0;
        SDL_GetGamepadPowerInfo(gamepad, &battery);
        return new HostGamepadState(
            Connected: true,
            Buttons: buttons,
            LeftX: HostWindowInput.ToStickByte(SDL_GetGamepadAxis(gamepad, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFTX)),
            LeftY: HostWindowInput.ToStickByte(SDL_GetGamepadAxis(gamepad, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFTY)),
            RightX: HostWindowInput.ToStickByte(SDL_GetGamepadAxis(gamepad, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHTX)),
            RightY: HostWindowInput.ToStickByte(SDL_GetGamepadAxis(gamepad, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHTY)),
            LeftTrigger: leftTrigger,
            RightTrigger: rightTrigger,
            Type: MapGamepadType(SDL_GetRealGamepadType(gamepad)),
            Connection: GetConnection(gamepad),
            BatteryPercent: (byte)Math.Clamp(battery, 0, 100));
    }

    public static HostGamepadType MapGamepadType(SDL_GamepadType type) => type switch
    {
        SDL_GamepadType.SDL_GAMEPAD_TYPE_PS4 => HostGamepadType.DualShock4,
        SDL_GamepadType.SDL_GAMEPAD_TYPE_PS5 => HostGamepadType.DualSense,
        SDL_GamepadType.SDL_GAMEPAD_TYPE_XBOX360 or SDL_GamepadType.SDL_GAMEPAD_TYPE_XBOXONE => HostGamepadType.Xbox,
        _ => HostGamepadType.Generic,
    };

    public static HostGamepadConnection GetConnection(SDL_Gamepad* gamepad) =>
        SDL_GetGamepadConnectionState(gamepad) switch
        {
            SDL_JoystickConnectionState.SDL_JOYSTICK_CONNECTION_WIRED => HostGamepadConnection.Wired,
            SDL_JoystickConnectionState.SDL_JOYSTICK_CONNECTION_WIRELESS => HostGamepadConnection.Wireless,
            _ => HostGamepadConnection.Unknown,
        };

    private static void AddButton(
        SDL_Gamepad* gamepad,
        SDL_GamepadButton source,
        HostGamepadButtons target,
        ref HostGamepadButtons buttons)
    {
        if (SDL_GetGamepadButton(gamepad, source))
        {
            buttons |= target;
        }
    }

    public static string GetName(SDL_Gamepad* gamepad) =>
        Marshal.PtrToStringUTF8((nint)Unsafe_SDL_GetGamepadName(gamepad)) ?? "SDL gamepad";

    public static void EnableSensors(SDL_Gamepad* gamepad)
    {
        EnableSensor(gamepad, SDL_SensorType.SDL_SENSOR_ACCEL);
        EnableSensor(gamepad, SDL_SensorType.SDL_SENSOR_GYRO);
    }

    private static void EnableSensor(SDL_Gamepad* gamepad, SDL_SensorType sensor)
    {
        if (SDL_GamepadHasSensor(gamepad, sensor))
        {
            SDL_SetGamepadSensorEnabled(gamepad, sensor, true);
        }
    }

    // The state of the gamepad with its motion sensors and touchpad, as the window and the bridge both sample it.
    public static HostGamepadState ReadFull(SDL_Gamepad* gamepad, TouchContactIds touchIds) =>
        Read(gamepad) with
        {
            Motion = ReadMotion(gamepad),
            Touch = ReadTouch(gamepad, touchIds),
        };

    public static HostMotionState ReadMotion(SDL_Gamepad* gamepad)
    {
        Span<float> acceleration = stackalloc float[3];
        Span<float> angularVelocity = stackalloc float[3];
        acceleration.Clear();
        angularVelocity.Clear();
        var hasAcceleration = false;
        var hasAngularVelocity = false;
        fixed (float* data = acceleration)
        {
            hasAcceleration = SDL_GamepadHasSensor(gamepad, SDL_SensorType.SDL_SENSOR_ACCEL) &&
                SDL_GetGamepadSensorData(gamepad, SDL_SensorType.SDL_SENSOR_ACCEL, data, acceleration.Length);
        }
        fixed (float* data = angularVelocity)
        {
            hasAngularVelocity = SDL_GamepadHasSensor(gamepad, SDL_SensorType.SDL_SENSOR_GYRO) &&
                SDL_GetGamepadSensorData(gamepad, SDL_SensorType.SDL_SENSOR_GYRO, data, angularVelocity.Length);
        }

        return new HostMotionState(
            hasAcceleration || hasAngularVelocity,
            acceleration[0],
            acceleration[1],
            acceleration[2],
            angularVelocity[0],
            angularVelocity[1],
            angularVelocity[2]);
    }

    public static HostTouchState ReadTouch(SDL_Gamepad* gamepad, TouchContactIds touchIds)
    {
        if (SDL_GetNumGamepadTouchpads(gamepad) <= 0)
        {
            return default;
        }

        return new HostTouchState(ReadTouchPoint(gamepad, touchIds, 0), ReadTouchPoint(gamepad, touchIds, 1));
    }

    private static HostTouchPoint ReadTouchPoint(SDL_Gamepad* gamepad, TouchContactIds touchIds, int finger)
    {
        var point = ReadRawTouchPoint(gamepad, finger);
        return point with { Id = touchIds.Track(finger, point.Active) };
    }

    private static HostTouchPoint ReadRawTouchPoint(SDL_Gamepad* gamepad, int finger)
    {
        if (SDL_GetNumGamepadTouchpadFingers(gamepad, 0) <= finger)
        {
            return default;
        }

        SDLBool down = false;
        float x = 0;
        float y = 0;
        float pressure = 0;
        if (!SDL_GetGamepadTouchpadFinger(gamepad, 0, finger, &down, &x, &y, &pressure))
        {
            return default;
        }

        return new HostTouchPoint(down, (byte)finger, Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1));
    }
}
