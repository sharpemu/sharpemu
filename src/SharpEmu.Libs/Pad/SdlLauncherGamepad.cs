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
    private static bool _suspended;

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
        if (!_suspended)
        {
            OpenFirstGamepad();
        }
    }

    public static bool TryGetState(out HostGamepadState state)
    {
        state = default;
        if (!_initialized || _suspended)
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

    /// <summary>
    /// Releases the launcher's controller while a separate game process owns input.
    /// Some HID backends cannot open the same controller reliably in two processes.
    /// </summary>
    public static void Suspend()
    {
        _suspended = true;
        CloseGamepad();
    }

    /// <summary>Allows launcher navigation to acquire a controller again.</summary>
    public static void Resume()
    {
        _suspended = false;
        if (_initialized && _gamepad is null)
        {
            OpenFirstGamepad();
        }
    }

    public static void Shutdown()
    {
        if (!_initialized)
        {
            return;
        }

        CloseGamepad();

        SDL_QuitSubSystem(InitFlags);
        _initialized = false;
        _suspended = false;
    }

    private static void OpenFirstGamepad()
    {
        _gamepad = SdlGamepadStateReader.OpenPreferredGamepad();
    }

    private static void CloseGamepad()
    {
        if (_gamepad is null)
        {
            return;
        }

        SDL_CloseGamepad(_gamepad);
        _gamepad = null;
    }
}
