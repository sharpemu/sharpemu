// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SDL;
using static SDL.SDL3;

namespace SharpEmu.HLE.Host.Sdl;

/// <summary>
/// The controller link of a process that owns SDL gamepads (everything outside the sandbox, and the sandbox launcher's
/// bridge host, which publishes <see cref="QueryPads"/> to the child). It looks at the gamepads this process has open.
/// </summary>
internal sealed unsafe class SdlControllerLink : IHostControllerLink
{
    public static SdlControllerLink Instance { get; } = new();

    public HostControllerPads QueryPads()
    {
        var pads = HostControllerPads.None;
        if (TryFindDualSense(SDL_JoystickConnectionState.SDL_JOYSTICK_CONNECTION_WIRED, out _))
        {
            pads |= HostControllerPads.WiredDualSense;
        }

        if (TryFindDualSense(SDL_JoystickConnectionState.SDL_JOYSTICK_CONNECTION_WIRELESS, out _))
        {
            pads |= HostControllerPads.BluetoothDualSense;
        }

        return pads;
    }

    public bool SendEffect(HostControllerPads pad, ReadOnlySpan<byte> report) =>
        TryFindDualSense(ConnectionOf(pad), out var id) && SendEffect(id, report);

    public IHostAudioStream? TryOpenBluetoothStream(uint sampleRate, int maxQueuedBytes, bool routeSpeaker) =>
        TryFindDualSense(SDL_JoystickConnectionState.SDL_JOYSTICK_CONNECTION_WIRELESS, out var pad)
            ? SdlDualSenseBluetooth.TryOpen(pad, sampleRate, maxQueuedBytes, routeSpeaker)
            : null;

    private static SDL_JoystickConnectionState ConnectionOf(HostControllerPads pad) =>
        pad == HostControllerPads.BluetoothDualSense
            ? SDL_JoystickConnectionState.SDL_JOYSTICK_CONNECTION_WIRELESS
            : SDL_JoystickConnectionState.SDL_JOYSTICK_CONNECTION_WIRED;

    private static bool TryFindDualSense(SDL_JoystickConnectionState connection, out SDL_JoystickID pad)
    {
        pad = default;
        var found = 0;
        SDL_LockJoysticks();
        try
        {
            using var gamepads = SDL_GetGamepads();
            for (var index = 0; gamepads is not null && index < gamepads.Count; index++)
            {
                var id = gamepads[index];
                var gamepad = SDL_GetGamepadFromID(id);
                if (gamepad is not null &&
                    SDL_GetGamepadTypeForID(id) == SDL_GamepadType.SDL_GAMEPAD_TYPE_PS5 &&
                    SDL_GetGamepadConnectionState(gamepad) == connection)
                {
                    pad = id;
                    found++;
                }
            }
        }
        finally
        {
            SDL_UnlockJoysticks();
        }

        return found == 1;
    }

    private static bool SendEffect(SDL_JoystickID pad, ReadOnlySpan<byte> report)
    {
        SDL_LockJoysticks();
        try
        {
            var gamepad = SDL_GetGamepadFromID(pad);
            if (gamepad is null)
            {
                return false;
            }

            fixed (byte* data = report)
            {
                return SDL_SendGamepadEffect(gamepad, (nint)data, report.Length);
            }
        }
        finally
        {
            SDL_UnlockJoysticks();
        }
    }
}
