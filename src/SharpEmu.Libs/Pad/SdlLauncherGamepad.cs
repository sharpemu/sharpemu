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
    private static readonly object Gate = new();
    private static SDL_Gamepad* _gamepad;
    private static bool _initialized;
    private static int _suspended;

    public static void EnsureStarted()
    {
        lock (Gate)
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
    }

    /// <summary>
    /// Stops the launcher's own gamepad use and closes its handle, until the matching <see cref="Resume"/>: the gamepad
    /// bridge of a sandboxed game then has every SDL gamepad call of this process to itself. A caller already inside
    /// <see cref="TryGetState"/> finishes first (both take the same lock).
    /// </summary>
    public static void Suspend()
    {
        lock (Gate)
        {
            _suspended++;
            CloseGamepad();
        }
    }

    public static void Resume()
    {
        lock (Gate)
        {
            if (_suspended > 0)
            {
                _suspended--;
            }
        }
    }

    public static bool TryGetState(out HostGamepadState state)
    {
        state = default;
        lock (Gate)
        {
            if (!_initialized || _suspended > 0)
            {
                return false;
            }

            SDL_UpdateGamepads();
            if (_gamepad is not null && !SDL_GamepadConnected(_gamepad))
            {
                CloseGamepad();
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
    }

    public static void Shutdown()
    {
        lock (Gate)
        {
            if (!_initialized)
            {
                return;
            }

            CloseGamepad();
            SDL_QuitSubSystem(InitFlags);
            _initialized = false;
        }
    }

    private static void CloseGamepad()
    {
        if (_gamepad is not null)
        {
            SDL_CloseGamepad(_gamepad);
            _gamepad = null;
        }
    }

    private static void OpenFirstGamepad()
    {
        _gamepad = SdlGamepadStateReader.OpenPreferredGamepad();
    }
}
