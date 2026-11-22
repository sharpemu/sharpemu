// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Numerics;
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
    private static readonly MotionOrientation Orientation = new();
    private const float StandardGravity = 9.80665f;

    public static void Connect(IHostGamepadOutput? gamepadOutput = null)
    {
        lock (Gate)
        {
            _focused = true;
            _gamepadOutput = gamepadOutput;
            PressedKeys.Clear();
        }

        HostWindowInputSource.Set(Source);
        Changed?.Invoke();
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
            Orientation.Reset();
        }

        HostWindowInputSource.Clear(Source);
        Changed?.Invoke();
    }

    // Called after each change of the window, a key, the focus, or a gamepad's buttons or touch contacts, as it
    // happens: the pad keeps the states between two reads of the game (PadExports.RecordInputChange), so a press and
    // its release in between both reach it.
    internal static Action? Changed;

    public static void SetFocused(bool focused)
    {
        bool changed;
        lock (Gate)
        {
            changed = _focused != focused || (!focused && PressedKeys.Count != 0);
            _focused = focused;
            if (!focused)
            {
                PressedKeys.Clear();
            }
        }

        if (changed) Changed?.Invoke();
    }

    public static void SetKey(int virtualKey, bool down)
    {
        bool changed;
        lock (Gate)
        {
            changed = down ? PressedKeys.Add(virtualKey) : PressedKeys.Remove(virtualKey);
        }

        if (changed) Changed?.Invoke();
    }

    public static void SetGamepad(string? name, HostGamepadState state)
    {
        bool changed;
        lock (Gate)
        {
            changed = _gamepadConnected != state.Connected || _gamepadState.Buttons != state.Buttons ||
                      !SameContacts(_gamepadState.Touch, state.Touch);
            if (state.Motion.Available)
            {
                var orientation = Orientation.Orientation;
                state = state with
                {
                    Motion = state.Motion with
                    {
                        OrientationX = orientation.X,
                        OrientationY = orientation.Y,
                        OrientationZ = orientation.Z,
                        OrientationW = orientation.W,
                    },
                };
            }

            _gamepadConnected = state.Connected;
            _gamepadName = name;
            _gamepadState = state;
        }

        if (changed) Changed?.Invoke();
    }

    // One button event of the connected gamepad, applied as it comes: a poll sees only where the buttons ended up.
    public static void SetGamepadButton(HostGamepadButtons button, bool down)
    {
        bool changed;
        lock (Gate)
        {
            var buttons = down ? _gamepadState.Buttons | button : _gamepadState.Buttons & ~button;
            changed = _gamepadConnected && buttons != _gamepadState.Buttons;
            if (changed)
            {
                _gamepadState = _gamepadState with { Buttons = buttons };
            }
        }

        if (changed) Changed?.Invoke();
    }

    public static void ClearGamepad()
    {
        bool changed;
        lock (Gate)
        {
            changed = _gamepadConnected;
            _gamepadConnected = false;
            _gamepadName = null;
            _gamepadState = default;
            Orientation.Reset();
        }

        if (changed) Changed?.Invoke();
    }

    // One sensor reading of the connected gamepad as SDL reports it (acceleration in m/s^2, angular velocity in
    // radians per second, the sensor's timestamp in nanoseconds). Each gyro reading turns the pad's orientation by its
    // own interval, which a poll of the latest reading cannot do.
    public static void ApplyGamepadMotion(bool angularVelocity, float x, float y, float z, ulong timestampNanoseconds)
    {
        lock (Gate)
        {
            if (angularVelocity)
            {
                Orientation.ApplyAngularVelocity(new Vector3(x, y, z), timestampNanoseconds);
            }
            else
            {
                Orientation.ApplyAcceleration(new Vector3(x, y, z) / StandardGravity);
            }
        }
    }

    internal static void ResetMotionOrientation()
    {
        lock (Gate)
        {
            Orientation.Reset();
        }
    }

    internal static void SetMotionTiltCorrection(bool enabled)
    {
        lock (Gate)
        {
            Orientation.TiltCorrection = enabled;
        }
    }

    private static bool SameContacts(HostTouchState left, HostTouchState right) =>
        left.First.Active == right.First.Active && left.First.Id == right.First.Id &&
        left.Second.Active == right.Second.Active && left.Second.Id == right.Second.Id;

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
