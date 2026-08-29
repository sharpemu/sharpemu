// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Runtime.InteropServices;
using SharpEmu.HLE.Host;
using SharpEmu.Libs.Pad;
using SDL;
using Silk.NET.Vulkan;
using static SDL.SDL3;

namespace SharpEmu.Libs.VideoOut;

internal enum SdlGraphicsApi
{
    Vulkan,
    Metal,
}

internal readonly record struct SdlHdrState(
    bool Enabled,
    float SdrWhiteLevel,
    float Headroom);

internal sealed unsafe class SdlHostWindow : IDisposable, IHostGamepadOutput
{
    private const SDL_InitFlags InitFlags = SDL_InitFlags.SDL_INIT_VIDEO | SDL_InitFlags.SDL_INIT_GAMEPAD;
    private static readonly long CursorHideDelayTicks = 2 * Stopwatch.Frequency;
    private const uint OutputDurationMs = 5_000;

    private readonly HostVideoOptions _options;
    private readonly SdlGraphicsApi _graphicsApi;
    private readonly Action? _toggleBackendHud;
    private readonly object _gamepadGate = new();
    private SDL_Window* _window;
    private nint _metalView;
    private SDL_Gamepad* _gamepad;
    private HostGamepadType _gamepadType;
    private byte _leftTriggerRumble;
    private byte _rightTriggerRumble;
    private int _closeRequested;
    private bool _closedByUser;
    private bool _fullscreen;
    private bool _focused = true;
    private bool _cursorVisible = true;
    private long _cursorHideDeadline;
    private bool _surfaceRestorePending;
    private bool _hdrStateChangePending;
    private bool _disposed;

    public SdlHostWindow(
        string title,
        HostVideoOptions options,
        SdlGraphicsApi graphicsApi,
        Action? toggleBackendHud = null)
    {
        _options = options.Normalize();
        _graphicsApi = graphicsApi;
        _toggleBackendHud = toggleBackendHud;
        SdlGamepadStateReader.EnableSonyHidApi();
        if (!SDL_InitSubSystem(InitFlags))
        {
            throw new InvalidOperationException($"SDL video initialization failed: {GetError()}");
        }

        var graphicsFlag = graphicsApi == SdlGraphicsApi.Vulkan
            ? SDL_WindowFlags.SDL_WINDOW_VULKAN
            : SDL_WindowFlags.SDL_WINDOW_METAL;
        var flags = graphicsFlag |
                    SDL_WindowFlags.SDL_WINDOW_RESIZABLE |
                    SDL_WindowFlags.SDL_WINDOW_HIGH_PIXEL_DENSITY |
                    SDL_WindowFlags.SDL_WINDOW_HIDDEN;
        _window = CreateWindow(title, _options.Width, _options.Height, flags);
        if (_window is null)
        {
            SDL_QuitSubSystem(InitFlags);
            throw new InvalidOperationException($"SDL window creation failed: {GetError()}");
        }

        MoveToConfiguredDisplay();
        ApplyConfiguredMode(_options.WindowMode);
        SetIcon();
        SDL_ShowWindow(_window);
        SDL_RaiseWindow(_window);
        if (_fullscreen && !SDL_SyncWindow(_window))
        {
            Console.Error.WriteLine($"[LOADER][WARN] SDL initial fullscreen sync failed: {GetError()}");
        }
        HostWindowInput.Connect(this);
        OpenFirstGamepad();

        Console.Error.WriteLine(
            $"[LOADER][INFO] SDL3 window ready: mode={_options.WindowMode} " +
            $"size={_options.Width}x{_options.Height} display={_options.DisplayIndex} " +
            $"refresh={(_options.RefreshRate == 0 ? "auto" : _options.RefreshRate)}");
        var hdr = HdrState;
        Console.Error.WriteLine(
            $"[LOADER][INFO] SDL3 display HDR: enabled={hdr.Enabled} " +
            $"sdr_white={hdr.SdrWhiteLevel:F3} headroom={hdr.Headroom:F3}");
    }

    public (int Width, int Height) PixelSize
    {
        get
        {
            var width = 0;
            var height = 0;
            if (_window is not null)
            {
                SDL_GetWindowSizeInPixels(_window, &width, &height);
            }

            return (Math.Max(width, 1), Math.Max(height, 1));
        }
    }

    public bool IsMinimized =>
        _window is not null &&
        (SDL_GetWindowFlags(_window) & SDL_WindowFlags.SDL_WINDOW_MINIMIZED) != 0;

    public bool ClosedByUser => _closedByUser;

    public SdlHdrState HdrState
    {
        get
        {
            if (_window is null)
            {
                return default;
            }

            var properties = SDL_GetWindowProperties(_window);
            fixed (byte* hdrEnabledName = SDL_PROP_WINDOW_HDR_ENABLED_BOOLEAN)
            fixed (byte* sdrWhiteName = SDL_PROP_WINDOW_SDR_WHITE_LEVEL_FLOAT)
            fixed (byte* headroomName = SDL_PROP_WINDOW_HDR_HEADROOM_FLOAT)
            {
                return new SdlHdrState(
                    (bool)SDL_GetBooleanProperty(properties, hdrEnabledName, false),
                    SDL_GetFloatProperty(properties, sdrWhiteName, 1f),
                    SDL_GetFloatProperty(properties, headroomName, 1f));
            }
        }
    }

    public bool ConsumeSurfaceRestore()
    {
        var pending = _surfaceRestorePending;
        _surfaceRestorePending = false;
        return pending;
    }

    public bool ConsumeHdrStateChange()
    {
        var pending = _hdrStateChangePending;
        _hdrStateChangePending = false;
        return pending;
    }

    public byte** GetRequiredVulkanInstanceExtensions(out uint count)
    {
        EnsureGraphicsApi(SdlGraphicsApi.Vulkan);
        uint extensionCount = 0;
        var extensions = SDL_Vulkan_GetInstanceExtensions(&extensionCount);
        count = extensionCount;
        if (extensions is null || count == 0)
        {
            throw new InvalidOperationException($"SDL did not provide Vulkan instance extensions: {GetError()}");
        }

        return extensions;
    }

    public SurfaceKHR CreateVulkanSurface(Instance instance)
    {
        EnsureGraphicsApi(SdlGraphicsApi.Vulkan);
        VkSurfaceKHR_T* surface = null;
        if (!SDL_Vulkan_CreateSurface(
                _window,
                (VkInstance_T*)instance.Handle,
                null,
                &surface) || surface is null)
        {
            throw new InvalidOperationException($"SDL Vulkan surface creation failed: {GetError()}");
        }

        return new SurfaceKHR(unchecked((ulong)surface));
    }

    public nint CreateMetalLayer()
    {
        EnsureGraphicsApi(SdlGraphicsApi.Metal);
        if (_metalView == 0)
        {
            _metalView = SDL_Metal_CreateView(_window);
            if (_metalView == 0)
            {
                throw new InvalidOperationException($"SDL Metal view creation failed: {GetError()}");
            }
        }

        var layer = SDL_Metal_GetLayer(_metalView);
        if (layer == 0)
        {
            throw new InvalidOperationException($"SDL Metal layer lookup failed: {GetError()}");
        }

        return layer;
    }

    public void SetTitle(string title)
    {
        if (_window is null)
        {
            return;
        }

        var utf8 = Marshal.StringToCoTaskMemUTF8(title);
        try
        {
            SDL_SetWindowTitle(_window, (byte*)utf8);
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8);
        }
    }

    public void Close() => Volatile.Write(ref _closeRequested, 1);

    public void SetRumble(byte largeMotor, byte smallMotor)
    {
        lock (_gamepadGate)
        {
            if (IsGamepadConnected())
            {
                SDL_RumbleGamepad(
                    _gamepad,
                    ExpandByte(largeMotor),
                    ExpandByte(smallMotor),
                    OutputDurationMs);
            }
        }
    }

    public void SetTriggerRumble(byte? leftTrigger, byte? rightTrigger)
    {
        lock (_gamepadGate)
        {
            if (IsGamepadConnected())
            {
                if (leftTrigger is { } left)
                {
                    _leftTriggerRumble = left;
                }
                if (rightTrigger is { } right)
                {
                    _rightTriggerRumble = right;
                }

                SDL_RumbleGamepadTriggers(
                    _gamepad,
                    ExpandByte(_leftTriggerRumble),
                    ExpandByte(_rightTriggerRumble),
                    OutputDurationMs);
            }
        }
    }

    public void SetAdaptiveTriggerEffect(
        HostAdaptiveTriggerEffect? leftTrigger,
        HostAdaptiveTriggerEffect? rightTrigger)
    {
        lock (_gamepadGate)
        {
            if (!IsGamepadConnected())
            {
                return;
            }

            if (_gamepadType != HostGamepadType.DualSense)
            {
                if (leftTrigger is { } fallbackLeft)
                {
                    _leftTriggerRumble = fallbackLeft.FallbackStrength;
                }
                if (rightTrigger is { } fallbackRight)
                {
                    _rightTriggerRumble = fallbackRight.FallbackStrength;
                }

                SDL_RumbleGamepadTriggers(
                    _gamepad,
                    ExpandByte(_leftTriggerRumble),
                    ExpandByte(_rightTriggerRumble),
                    OutputDurationMs);
                return;
            }

            Span<byte> state = stackalloc byte[47];
            state.Clear();
            if (rightTrigger is { } nativeRight)
            {
                state[0] |= 0x04;
                nativeRight.CopyTo(state[10..21]);
            }
            if (leftTrigger is { } nativeLeft)
            {
                state[0] |= 0x08;
                nativeLeft.CopyTo(state[21..32]);
            }

            if (state[0] == 0)
            {
                return;
            }

            fixed (byte* data = state)
            {
                SDL_SendGamepadEffect(_gamepad, (nint)data, state.Length);
            }
        }
    }

    public void SetLightbar(byte red, byte green, byte blue)
    {
        lock (_gamepadGate)
        {
            if (IsGamepadConnected())
            {
                SDL_SetGamepadLED(_gamepad, red, green, blue);
            }
        }
    }

    public void ResetLightbar() => SetLightbar(0, 0, 64);

    public void Run(
        Action initialize,
        Action<double> render,
        Action closing,
        Action? idle = null)
    {
        initialize();
        var timer = Stopwatch.StartNew();
        var last = timer.Elapsed.TotalSeconds;
        try
        {
            while (Volatile.Read(ref _closeRequested) == 0)
            {
                PumpEvents();
                if (Volatile.Read(ref _closeRequested) != 0)
                {
                    break;
                }

                UpdateCursorAutoHide();
                SampleGamepad();
                var now = timer.Elapsed.TotalSeconds;
                render(now - last);
                last = now;
                if (IsMinimized)
                {
                    SDL_Delay(10);
                }
                else
                {
                    idle?.Invoke();
                }
            }
        }
        finally
        {
            closing();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SDL_ShowCursor();
        HostWindowInput.Disconnect();
        CloseGamepad();
        if (_metalView != 0)
        {
            SDL_Metal_DestroyView(_metalView);
            _metalView = 0;
        }

        if (_window is not null)
        {
            SDL_DestroyWindow(_window);
            _window = null;
        }

        SDL_QuitSubSystem(InitFlags);
    }

    private void PumpEvents()
    {
        SDL_Event windowEvent;
        while (SDL_PollEvent(&windowEvent))
        {
            switch (windowEvent.Type)
            {
                case SDL_EventType.SDL_EVENT_QUIT:
                case SDL_EventType.SDL_EVENT_WINDOW_CLOSE_REQUESTED:
                    _closedByUser = true;
                    Volatile.Write(ref _closeRequested, 1);
                    break;
                case SDL_EventType.SDL_EVENT_WINDOW_FOCUS_GAINED:
                    _focused = true;
                    UpdateCursorVisibility();
                    HostWindowInput.SetFocused(true);
                    break;
                case SDL_EventType.SDL_EVENT_WINDOW_FOCUS_LOST:
                    _focused = false;
                    UpdateCursorVisibility();
                    HostWindowInput.SetFocused(false);
                    break;
                case SDL_EventType.SDL_EVENT_WINDOW_MINIMIZED:
                    _focused = false;
                    UpdateCursorVisibility();
                    HostWindowInput.SetFocused(false);
                    break;
                case SDL_EventType.SDL_EVENT_WINDOW_RESTORED:
                case SDL_EventType.SDL_EVENT_WINDOW_RESIZED:
                case SDL_EventType.SDL_EVENT_WINDOW_PIXEL_SIZE_CHANGED:
                case SDL_EventType.SDL_EVENT_WINDOW_MAXIMIZED:
                case SDL_EventType.SDL_EVENT_WINDOW_ENTER_FULLSCREEN:
                case SDL_EventType.SDL_EVENT_WINDOW_LEAVE_FULLSCREEN:
                case SDL_EventType.SDL_EVENT_WINDOW_EXPOSED:
                case SDL_EventType.SDL_EVENT_WINDOW_DISPLAY_CHANGED:
                case SDL_EventType.SDL_EVENT_WINDOW_DISPLAY_SCALE_CHANGED:
                    _surfaceRestorePending = true;
                    break;
                case SDL_EventType.SDL_EVENT_WINDOW_HDR_STATE_CHANGED:
                    _hdrStateChangePending = true;
                    break;
                case SDL_EventType.SDL_EVENT_KEY_DOWN:
                case SDL_EventType.SDL_EVENT_KEY_UP:
                    HandleKey(windowEvent.key);
                    break;
                case SDL_EventType.SDL_EVENT_MOUSE_MOTION:
                    ShowCursorTemporarily();
                    break;
                case SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN:
                    ShowCursorTemporarily();
                    if (windowEvent.button.button == 1 && windowEvent.button.clicks == 2)
                    {
                        ToggleFullscreen();
                    }
                    break;
                case SDL_EventType.SDL_EVENT_GAMEPAD_ADDED:
                    if (_gamepad is null)
                    {
                        OpenFirstGamepad();
                    }
                    break;
                case SDL_EventType.SDL_EVENT_GAMEPAD_REMOVED:
                    if (_gamepad is not null && !SDL_GamepadConnected(_gamepad))
                    {
                        CloseGamepad();
                        OpenFirstGamepad();
                    }
                    break;
            }
        }
    }

    private void HandleKey(SDL_KeyboardEvent keyEvent)
    {
        var down = keyEvent.type == SDL_EventType.SDL_EVENT_KEY_DOWN;
        if (down && !keyEvent.repeat)
        {
            if (keyEvent.key == SDL_Keycode.SDLK_F1 &&
                (keyEvent.mod & SDL_Keymod.SDL_KMOD_GUI) != 0 &&
                _toggleBackendHud is not null)
            {
                _toggleBackendHud();
            }
            else if (keyEvent.key == SDL_Keycode.SDLK_F1)
            {
                PerfOverlay.Toggle();
            }
            else if (keyEvent.key is SDL_Keycode.SDLK_F10 or SDL_Keycode.SDLK_F12)
            {
                RenderDocCapture.RequestCapture();
            }
            else if (keyEvent.key == SDL_Keycode.SDLK_F11)
            {
                ToggleFullscreen();
            }
        }

        if (TryMapVirtualKey(keyEvent.key, out var virtualKey))
        {
            HostWindowInput.SetKey(virtualKey, down);
        }
    }

    private void ToggleFullscreen()
    {
        if (_fullscreen)
        {
            SDL_SetWindowFullscreen(_window, false);
            SDL_SetWindowFullscreenMode(_window, null);
            SDL_SetWindowResizable(_window, true);
            SDL_SetWindowSize(_window, _options.Width, _options.Height);
            MoveToConfiguredDisplay();
            _fullscreen = false;
            UpdateCursorVisibility();
            return;
        }

        ApplyConfiguredMode(
            _options.WindowMode == HostWindowMode.ExclusiveFullscreen
                ? HostWindowMode.ExclusiveFullscreen
                : HostWindowMode.Borderless);
    }

    private void ApplyConfiguredMode(HostWindowMode mode)
    {
        if (mode == HostWindowMode.Windowed)
        {
            _fullscreen = false;
            UpdateCursorVisibility();
            return;
        }

        if (mode == HostWindowMode.ExclusiveFullscreen)
        {
            var display = GetConfiguredDisplay();
            SDL_DisplayMode closest;
            if (SDL_GetClosestFullscreenDisplayMode(
                    display,
                    _options.Width,
                    _options.Height,
                    _options.RefreshRate,
                    true,
                    &closest))
            {
                SDL_SetWindowFullscreenMode(_window, &closest);
            }
            else
            {
                Console.Error.WriteLine(
                    $"[LOADER][WARN] SDL exclusive mode unavailable; using borderless: {GetError()}");
                SDL_SetWindowFullscreenMode(_window, null);
            }
        }
        else
        {
            SDL_SetWindowFullscreenMode(_window, null);
        }

        SDL_SetWindowFullscreen(_window, true);
        _fullscreen = true;
        UpdateCursorVisibility();
    }

    private void UpdateCursorVisibility()
    {
        _cursorHideDeadline = 0;
        SetCursorVisible(!_fullscreen || !_focused);
    }

    private void ShowCursorTemporarily()
    {
        if (!_fullscreen || !_focused)
        {
            return;
        }

        SetCursorVisible(true);
        _cursorHideDeadline = Stopwatch.GetTimestamp() + CursorHideDelayTicks;
    }

    private void UpdateCursorAutoHide()
    {
        if (!_fullscreen || !_focused || !_cursorVisible || _cursorHideDeadline == 0 ||
            Stopwatch.GetTimestamp() < _cursorHideDeadline)
        {
            return;
        }

        _cursorHideDeadline = 0;
        SetCursorVisible(false);
    }

    private void SetCursorVisible(bool visible)
    {
        if (_cursorVisible == visible)
        {
            return;
        }

        if (visible)
        {
            SDL_ShowCursor();
        }
        else
        {
            SDL_HideCursor();
        }

        _cursorVisible = visible;
    }

    private void MoveToConfiguredDisplay()
    {
        var display = GetConfiguredDisplay();
        SDL_Rect bounds;
        if (SDL_GetDisplayBounds(display, &bounds))
        {
            var x = bounds.x + Math.Max(0, (bounds.w - _options.Width) / 2);
            var y = bounds.y + Math.Max(0, (bounds.h - _options.Height) / 2);
            SDL_SetWindowPosition(_window, x, y);
        }
    }

    private SDL_DisplayID GetConfiguredDisplay()
    {
        using var displays = SDL_GetDisplays();
        if (displays is null || displays.Count == 0)
        {
            return SDL_GetPrimaryDisplay();
        }

        return displays[Math.Clamp(_options.DisplayIndex, 0, displays.Count - 1)];
    }

    private void OpenFirstGamepad()
    {
        lock (_gamepadGate)
        {
            _gamepad = SdlGamepadStateReader.OpenPreferredGamepad();
            if (_gamepad is null)
            {
                return;
            }

            _gamepadType = SdlGamepadStateReader.MapGamepadType(SDL_GetRealGamepadType(_gamepad));
            EnableSensor(SDL_SensorType.SDL_SENSOR_ACCEL);
            EnableSensor(SDL_SensorType.SDL_SENSOR_GYRO);
        }

        Console.Error.WriteLine(
            $"[LOADER][INFO] SDL gamepad connected: {DescribeGamepad()} " +
            $"type={_gamepadType} connection={SdlGamepadStateReader.GetConnection(_gamepad)}");
        SampleGamepad();
    }

    private void CloseGamepad()
    {
        lock (_gamepadGate)
        {
            if (_gamepad is not null)
            {
                SDL_CloseGamepad(_gamepad);
                _gamepad = null;
            }

            _gamepadType = HostGamepadType.Generic;
            _leftTriggerRumble = 0;
            _rightTriggerRumble = 0;
        }

        HostWindowInput.ClearGamepad();
    }

    private void SampleGamepad()
    {
        lock (_gamepadGate)
        {
            if (!IsGamepadConnected())
            {
                HostWindowInput.ClearGamepad();
                return;
            }

            SDL_UpdateGamepads();
            var state = SdlGamepadStateReader.Read(_gamepad) with
            {
                Motion = ReadMotion(),
                Touch = ReadTouch(),
            };
            HostWindowInput.SetGamepad(
                DescribeGamepad(),
                state);
        }
    }

    private void EnableSensor(SDL_SensorType sensor)
    {
        if (SDL_GamepadHasSensor(_gamepad, sensor))
        {
            SDL_SetGamepadSensorEnabled(_gamepad, sensor, true);
        }
    }

    private HostMotionState ReadMotion()
    {
        Span<float> acceleration = stackalloc float[3];
        Span<float> angularVelocity = stackalloc float[3];
        acceleration.Clear();
        angularVelocity.Clear();
        var hasAcceleration = false;
        var hasAngularVelocity = false;
        fixed (float* data = acceleration)
        {
            hasAcceleration = SDL_GamepadHasSensor(_gamepad, SDL_SensorType.SDL_SENSOR_ACCEL) &&
                SDL_GetGamepadSensorData(_gamepad, SDL_SensorType.SDL_SENSOR_ACCEL, data, acceleration.Length);
        }
        fixed (float* data = angularVelocity)
        {
            hasAngularVelocity = SDL_GamepadHasSensor(_gamepad, SDL_SensorType.SDL_SENSOR_GYRO) &&
                SDL_GetGamepadSensorData(_gamepad, SDL_SensorType.SDL_SENSOR_GYRO, data, angularVelocity.Length);
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

    private HostTouchState ReadTouch()
    {
        if (SDL_GetNumGamepadTouchpads(_gamepad) <= 0)
        {
            return default;
        }

        return new HostTouchState(ReadTouchPoint(0), ReadTouchPoint(1));
    }

    private HostTouchPoint ReadTouchPoint(int finger)
    {
        if (SDL_GetNumGamepadTouchpadFingers(_gamepad, 0) <= finger)
        {
            return default;
        }

        SDLBool down = false;
        float x = 0;
        float y = 0;
        float pressure = 0;
        if (!SDL_GetGamepadTouchpadFinger(_gamepad, 0, finger, &down, &x, &y, &pressure))
        {
            return default;
        }

        return new HostTouchPoint(down, (byte)finger, Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1));
    }

    private bool IsGamepadConnected() => _gamepad is not null && SDL_GamepadConnected(_gamepad);

    private string DescribeGamepad() =>
        Marshal.PtrToStringUTF8((nint)Unsafe_SDL_GetGamepadName(_gamepad)) ?? "SDL gamepad";

    private static ushort ExpandByte(byte value) => (ushort)(value * 257);

    private static bool TryMapVirtualKey(SDL_Keycode key, out int virtualKey)
    {
        virtualKey = key switch
        {
            SDL_Keycode.SDLK_BACKSPACE => 0x08,
            SDL_Keycode.SDLK_TAB => 0x09,
            SDL_Keycode.SDLK_RETURN => 0x0D,
            SDL_Keycode.SDLK_ESCAPE => 0x1B,
            SDL_Keycode.SDLK_LEFT => 0x25,
            SDL_Keycode.SDLK_UP => 0x26,
            SDL_Keycode.SDLK_RIGHT => 0x27,
            SDL_Keycode.SDLK_DOWN => 0x28,
            >= SDL_Keycode.SDLK_A and <= SDL_Keycode.SDLK_Z => 0x41 + (int)(key - SDL_Keycode.SDLK_A),
            _ => 0,
        };
        return virtualKey != 0;
    }

    private void SetIcon()
    {
        if (!PngSplashLoader.TryLoadIcon(out var pixels, out var width, out var height))
        {
            return;
        }

        fixed (byte* data = pixels)
        {
            var surface = SDL_CreateSurfaceFrom(
                (int)width,
                (int)height,
                SDL_PixelFormat.SDL_PIXELFORMAT_ABGR8888,
                (nint)data,
                checked((int)width * 4));
            if (surface is not null)
            {
                SDL_SetWindowIcon(_window, surface);
                SDL_DestroySurface(surface);
            }
        }
    }

    private static SDL_Window* CreateWindow(string title, int width, int height, SDL_WindowFlags flags)
    {
        var utf8 = Marshal.StringToCoTaskMemUTF8(title);
        try
        {
            return SDL_CreateWindow((byte*)utf8, width, height, flags);
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8);
        }
    }

    private static string GetError() =>
        Marshal.PtrToStringUTF8((nint)Unsafe_SDL_GetError()) ?? "unknown SDL error";

    private void EnsureGraphicsApi(SdlGraphicsApi expected)
    {
        if (_graphicsApi != expected)
        {
            throw new InvalidOperationException(
                $"SDL host window uses {_graphicsApi}, not {expected}.");
        }
    }
}
