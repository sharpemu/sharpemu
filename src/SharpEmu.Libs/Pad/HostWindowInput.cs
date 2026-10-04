// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.Host;

namespace SharpEmu.Libs.Pad;

/// <summary>Cross-platform input state supplied by the SDL game window.</summary>
public static class HostWindowInput
{
    private static readonly object Gate = new();
    private static readonly HashSet<int> PressedKeys = new();
    private static bool _focused;
    private static bool _gamepadConnected;
    private static string? _gamepadName;
    private static HostGamepadState _gamepadState;
    private static IHostGamepadOutput? _gamepadOutput;
    private static readonly WindowInputSource Source = new();

    internal static event Action? StateChanged;

    public static void Connect(IHostGamepadOutput? gamepadOutput = null)
    {
        lock (Gate)
        {
            _focused = true;
            _gamepadOutput = gamepadOutput;
            PressedKeys.Clear();
        }

        HostWindowInputSource.Set(Source);
        StateChanged?.Invoke();
    }

    public static void Disconnect()
    {
        lock (Gate)
        {
            _focused = false;
            _gamepadConnected = false;
            _gamepadName = null;
            _gamepadState = default;
            _gamepadOutput = null;
            PressedKeys.Clear();
        }

        StateChanged?.Invoke();
        HostWindowInputSource.Clear(Source);
    }

    public static void SetFocused(bool focused)
    {
        bool changed;
        lock (Gate)
        {
            changed = _focused != focused;
            _focused = focused;
            if (!focused)
            {
                changed |= PressedKeys.Count != 0;
                PressedKeys.Clear();
            }
        }

        if (changed)
        {
            StateChanged?.Invoke();
        }
    }

    public static void SetKey(int virtualKey, bool down)
    {
        bool changed;
        lock (Gate)
        {
            // SDL can deliver keyboard events that were queued around an
            // alt-tab after the focus-lost event has already cleared our
            // state.  Never let a late key-down repopulate PressedKeys while
            // the game window is unfocused; otherwise it becomes a phantom
            // held button as soon as focus returns.
            if (down && !_focused)
            {
                changed = false;
            }
            else if (down)
            {
                changed = PressedKeys.Add(virtualKey);
            }
            else
            {
                changed = PressedKeys.Remove(virtualKey);
            }
        }

        if (changed)
        {
            StateChanged?.Invoke();
        }
    }

    public static void SetGamepad(string? name, HostGamepadState state)
    {
        bool changed;
        lock (Gate)
        {
            changed = _gamepadConnected != state.Connected ||
                !string.Equals(_gamepadName, name, StringComparison.Ordinal) ||
                !HasSameInput(_gamepadState, state);
            _gamepadConnected = state.Connected;
            _gamepadName = name;
            _gamepadState = state;
        }

        if (changed)
        {
            StateChanged?.Invoke();
        }
    }

    public static void ClearGamepad()
    {
        bool changed;
        lock (Gate)
        {
            changed = _gamepadConnected || _gamepadName is not null || _gamepadState != default;
            _gamepadConnected = false;
            _gamepadName = null;
            _gamepadState = default;
        }

        if (changed)
        {
            StateChanged?.Invoke();
        }
    }

    private static bool HasSameInput(HostGamepadState left, HostGamepadState right) =>
        left.Connected == right.Connected &&
        left.Buttons == right.Buttons &&
        left.LeftX == right.LeftX &&
        left.LeftY == right.LeftY &&
        left.RightX == right.RightX &&
        left.RightY == right.RightY &&
        left.LeftTrigger == right.LeftTrigger &&
        left.RightTrigger == right.RightTrigger &&
        left.Type == right.Type &&
        left.Connection == right.Connection &&
        left.Touch == right.Touch;

    internal static byte ToStickByte(short value)
    {
        var normalized = value + 32768;
        return (byte)Math.Clamp((normalized * 255 + 32767) / 65535, 0, 255);
    }

    internal static byte ToTriggerByte(short value) =>
        (byte)Math.Clamp(value * 255 / 32767, 0, 255);

    private sealed class WindowInputSource : IHostWindowInputSource
    {
        public bool HasKeyboardFocus
        {
            get
            {
                lock (Gate)
                {
                    return _focused;
                }
            }
        }

        public bool IsKeyDown(int virtualKey)
        {
            lock (Gate)
            {
                return PressedKeys.Contains(virtualKey);
            }
        }

        public int GetGamepadStates(Span<HostGamepadState> destination)
        {
            lock (Gate)
            {
                if (!_gamepadConnected || destination.IsEmpty)
                {
                    return 0;
                }

                destination[0] = _gamepadState;
                return 1;
            }
        }

        public string? DescribeConnectedGamepad()
        {
            lock (Gate)
            {
                return _gamepadConnected ? _gamepadName ?? "SDL gamepad" : null;
            }
        }

        public void SetRumble(byte largeMotor, byte smallMotor)
        {
            IHostGamepadOutput? output;
            lock (Gate)
            {
                output = _gamepadOutput;
            }

            output?.SetRumble(largeMotor, smallMotor);
        }

        public void SetTriggerRumble(byte? leftTrigger, byte? rightTrigger)
        {
            IHostGamepadOutput? output;
            lock (Gate)
            {
                output = _gamepadOutput;
            }

            output?.SetTriggerRumble(leftTrigger, rightTrigger);
        }

        public void SetAdaptiveTriggerEffect(
            HostAdaptiveTriggerEffect? leftTrigger,
            HostAdaptiveTriggerEffect? rightTrigger)
        {
            IHostGamepadOutput? output;
            lock (Gate)
            {
                output = _gamepadOutput;
            }

            output?.SetAdaptiveTriggerEffect(leftTrigger, rightTrigger);
        }

        public void SetLightbar(byte red, byte green, byte blue)
        {
            IHostGamepadOutput? output;
            lock (Gate)
            {
                output = _gamepadOutput;
            }

            output?.SetLightbar(red, green, blue);
        }

        public void ResetLightbar()
        {
            IHostGamepadOutput? output;
            lock (Gate)
            {
                output = _gamepadOutput;
            }

            output?.ResetLightbar();
        }
    }
}

public interface IHostGamepadOutput
{
    void SetRumble(byte largeMotor, byte smallMotor);

    void SetTriggerRumble(byte? leftTrigger, byte? rightTrigger);

    void SetAdaptiveTriggerEffect(
        HostAdaptiveTriggerEffect? leftTrigger,
        HostAdaptiveTriggerEffect? rightTrigger);

    void SetLightbar(byte red, byte green, byte blue);

    void ResetLightbar();
}
