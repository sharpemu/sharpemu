// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

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

    public static HostGamepadState Read(SDL_Gamepad* gamepad)
    {
        var buttons = HostGamepadButtons.None;
        AddButton(gamepad, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_SOUTH, HostGamepadButtons.Cross, ref buttons);
        AddButton(gamepad, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_EAST, HostGamepadButtons.Circle, ref buttons);
        AddButton(gamepad, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_WEST, HostGamepadButtons.Square, ref buttons);
        AddButton(gamepad, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_NORTH, HostGamepadButtons.Triangle, ref buttons);
        AddButton(gamepad, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_LEFT_SHOULDER, HostGamepadButtons.L1, ref buttons);
        AddButton(gamepad, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_RIGHT_SHOULDER, HostGamepadButtons.R1, ref buttons);
        AddButton(gamepad, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_LEFT_STICK, HostGamepadButtons.L3, ref buttons);
        AddButton(gamepad, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_RIGHT_STICK, HostGamepadButtons.R3, ref buttons);
        AddButton(gamepad, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_BACK, HostGamepadButtons.Create, ref buttons);
        AddButton(gamepad, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_GUIDE, HostGamepadButtons.Ps, ref buttons);
        AddButton(gamepad, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_START, HostGamepadButtons.Options, ref buttons);
        AddButton(gamepad, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_MISC1, HostGamepadButtons.Mic, ref buttons);
        AddButton(gamepad, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_TOUCHPAD, HostGamepadButtons.TouchPad, ref buttons);
        AddButton(gamepad, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_UP, HostGamepadButtons.Up, ref buttons);
        AddButton(gamepad, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_RIGHT, HostGamepadButtons.Right, ref buttons);
        AddButton(gamepad, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_DOWN, HostGamepadButtons.Down, ref buttons);
        AddButton(gamepad, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_LEFT, HostGamepadButtons.Left, ref buttons);

        var leftTrigger = HostWindowInput.ToTriggerByte(
            SDL_GetGamepadAxis(gamepad, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFT_TRIGGER));
        var rightTrigger = HostWindowInput.ToTriggerByte(
            SDL_GetGamepadAxis(gamepad, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHT_TRIGGER));
        if (leftTrigger > 64)
        {
            buttons |= HostGamepadButtons.L2;
        }
        if (rightTrigger > 64)
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
}
