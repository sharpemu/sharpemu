// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SDL;
using static SDL.SDL3;

namespace SharpEmu.Libs.VideoOut;

public sealed record HostDisplayMode(int Width, int Height, int RefreshRate);

public sealed record HostDisplayInfo(
    int Index,
    string Name,
    IReadOnlyList<HostDisplayMode> Modes);

public static unsafe class HostDisplayCatalog
{
    private const SDL_InitFlags VideoFlag = SDL_InitFlags.SDL_INIT_VIDEO;
    private static int _queryFailureLogged;

    public static IReadOnlyList<HostDisplayInfo> Query()
    {
        var initializedHere = false;
        var videoReady = false;
        try
        {
            initializedHere = (SDL_WasInit(VideoFlag) & VideoFlag) == 0;
            if (initializedHere && !SDL_InitSubSystem(VideoFlag))
            {
                LogQueryFailure(SDL_GetError() ?? "unknown SDL error");
                return CreateFallback();
            }
            videoReady = true;

            using var displays = SDL_GetDisplays();
            if (displays is null || displays.Count == 0)
            {
                LogQueryFailure("SDL reported no displays");
                return CreateFallback();
            }

            var result = new List<HostDisplayInfo>(displays.Count);
            for (var index = 0; index < displays.Count; index++)
            {
                var display = displays[index];
                var name = SDL_GetDisplayName(display);
                var modes = ReadModes(display);
                result.Add(new HostDisplayInfo(
                    index,
                    string.IsNullOrWhiteSpace(name) ? $"Display {index + 1}" : name,
                    modes));
            }

            return result;
        }
        catch (Exception exception)
        {
            LogQueryFailure(exception.Message);
            return CreateFallback();
        }
        finally
        {
            if (initializedHere && videoReady)
            {
                SDL_QuitSubSystem(VideoFlag);
            }
        }
    }

    private static IReadOnlyList<HostDisplayMode> ReadModes(SDL_DisplayID display)
    {
        var modes = new HashSet<HostDisplayMode>();
        using (var fullscreenModes = SDL_GetFullscreenDisplayModes(display))
        {
            if (fullscreenModes is not null)
            {
                for (var index = 0; index < fullscreenModes.Count; index++)
                {
                    var mode = fullscreenModes[index];
                    AddMode(modes, &mode);
                }
            }
        }

        AddMode(modes, SDL_GetDesktopDisplayMode(display));
        if (modes.Count == 0)
        {
            return CreateFallbackModes();
        }

        return modes
            .OrderByDescending(mode => (long)mode.Width * mode.Height)
            .ThenByDescending(mode => mode.Width)
            .ThenByDescending(mode => mode.RefreshRate)
            .ToArray();
    }

    private static void AddMode(HashSet<HostDisplayMode> modes, SDL_DisplayMode* mode)
    {
        if (mode is null || mode->w <= 0 || mode->h <= 0)
        {
            return;
        }

        var refreshRate = mode->refresh_rate > 0
            ? Math.Max(1, (int)Math.Round(mode->refresh_rate, MidpointRounding.AwayFromZero))
            : 0;
        modes.Add(new HostDisplayMode(mode->w, mode->h, refreshRate));
    }

    private static IReadOnlyList<HostDisplayInfo> CreateFallback() =>
        [new HostDisplayInfo(0, "Display 1", CreateFallbackModes())];

    private static IReadOnlyList<HostDisplayMode> CreateFallbackModes() =>
        [
            new HostDisplayMode(3840, 2160, 60),
            new HostDisplayMode(2560, 1440, 60),
            new HostDisplayMode(1920, 1080, 60),
            new HostDisplayMode(1280, 720, 60),
        ];

    private static void LogQueryFailure(string message)
    {
        if (Interlocked.Exchange(ref _queryFailureLogged, 1) == 0)
        {
            Console.Error.WriteLine($"[GUI][WARN] SDL display query failed: {message}");
        }
    }
}
