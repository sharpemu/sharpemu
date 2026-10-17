// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.Host;
using SDL;
using static SDL.SDL3;

namespace SharpEmu.Libs.Pad;

/// <summary>Cross-platform SDL gamepad polling for launcher navigation.</summary>
public static unsafe class SdlLauncherGamepad
{
    private const SDL_InitFlags InitFlags = SDL_InitFlags.SDL_INIT_GAMEPAD;
    private static SDL_Gamepad* _gamepad;
    private static bool _initialized;

    public static void EnsureStarted()
    {
        if (_initialized)
        {
            return;
        }

        SdlGamepadStateReader.EnableSonyHidApi();
        if (!SDL_InitSubSystem(InitFlags))
        {
            Console.Error.WriteLine("[GUI][WARN] SDL gamepad initialization failed.");
            return;
        }

        _initialized = true;
        OpenFirstGamepad();
    }

    public static bool TryGetState(out HostGamepadState state)
    {
        state = default;
        if (!_initialized)
        {
            return false;
        }

        SDL_UpdateGamepads();
        if (_gamepad is not null && !SDL_GamepadConnected(_gamepad))
        {
            SDL_CloseGamepad(_gamepad);
            _gamepad = null;
        }

        if (_gamepad is null)
        {
            OpenFirstGamepad();
        }

        if (_gamepad is null)
        {
            return false;
        }

        state = SdlGamepadStateReader.Read(_gamepad);
        return true;
    }

    public static void Shutdown()
    {
        if (!_initialized)
        {
            return;
        }

        if (_gamepad is not null)
        {
            SDL_CloseGamepad(_gamepad);
            _gamepad = null;
        }

        SDL_QuitSubSystem(InitFlags);
        _initialized = false;
    }

    private static void OpenFirstGamepad()
    {
        _gamepad = SdlGamepadStateReader.OpenPreferredGamepad();
    }
}
