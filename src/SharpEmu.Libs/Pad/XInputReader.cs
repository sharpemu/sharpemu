// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace SharpEmu.Libs.Pad;

/// <summary>
/// Reads Xbox 360 / Xbox One (and other XInput-compatible) controllers via
/// the Windows XInput API on a background thread, translated to the same
/// ORBIS pad conventions as <see cref="DualSenseReader"/>. Supports rumble
/// and hot-plug retry; the first connected slot (of four) is used.
/// </summary>
internal static class XInputReader
{
    private const uint ErrorSuccess = 0;
    private const int SlotCount = 4;
    private const byte TriggerThreshold = 30; // XINPUT_GAMEPAD_TRIGGER_THRESHOLD

    // XINPUT_GAMEPAD wButtons bit values.
    private const ushort XinputDpadUp = 0x0001;
    private const ushort XinputDpadDown = 0x0002;
    private const ushort XinputDpadLeft = 0x0004;
    private const ushort XinputDpadRight = 0x0008;
    private const ushort XinputStart = 0x0010;
    private const ushort XinputBack = 0x0020;
    private const ushort XinputLeftThumb = 0x0040;
    private const ushort XinputRightThumb = 0x0080;
    private const ushort XinputLeftShoulder = 0x0100;
    private const ushort XinputRightShoulder = 0x0200;
    private const ushort XinputA = 0x1000;
    private const ushort XinputB = 0x2000;
    private const ushort XinputX = 0x4000;
    private const ushort XinputY = 0x8000;

    private static readonly object Gate = new();
    private static PadState _state;
    private static bool _started;
    private static int _slot = -1; // connected XInput user index, -1 when none
    private static byte _motorLeft;
    private static byte _motorRight;

    /// <summary>Starts the background reader once; safe to call repeatedly.</summary>
    internal static void EnsureStarted()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        lock (Gate)
        {
            if (_started)
            {
                return;
            }

            _started = true;
            var thread = new Thread(ReadLoop)
            {
                IsBackground = true,
                Name = "XInputReader",
            };
            thread.Start();
        }
    }

    internal static bool TryGetState(out PadState state)
    {
        lock (Gate)
        {
            state = _state;
        }

        return state.Connected;
    }

    private static void SetState(in PadState state)
    {
        lock (Gate)
        {
            _state = state;
        }
    }

    /// <summary>Sets rumble; large = left/strong motor, small = right/weak.</summary>
    internal static void SetRumble(byte largeMotor, byte smallMotor)
    {
        lock (Gate)
        {
            if (_motorLeft == largeMotor && _motorRight == smallMotor)
            {
                return;
            }

            _motorLeft = largeMotor;
            _motorRight = smallMotor;
            SendRumbleLocked();
        }
    }

    private static void SendRumbleLocked()
    {
        if (_slot < 0)
        {
            return; // resent on connect
        }

        var vibration = new XInputVibration
        {
            LeftMotorSpeed = (ushort)(_motorLeft * 257),   // 0..255 -> 0..65535
            RightMotorSpeed = (ushort)(_motorRight * 257),
        };
        _ = XInputSetState((uint)_slot, ref vibration);
    }

    private static void ReadLoop()
    {
        try
        {
            while (true)
            {
                var slot = FindConnectedSlot();
                if (slot < 0)
                {
                    SetState(default);
                    Thread.Sleep(1000);
                    continue;
                }

                lock (Gate)
                {
                    _slot = slot;
                    SendRumbleLocked();
                }

                Console.Error.WriteLine("[LOADER][INFO] XInput (Xbox) controller connected.");
                while (XInputGetState((uint)slot, out var state) == ErrorSuccess)
                {
                    SetState(Translate(state.Gamepad));
                    Thread.Sleep(8);
                }

                Console.Error.WriteLine("[LOADER][INFO] XInput (Xbox) controller disconnected.");
                lock (Gate)
                {
                    _slot = -1;
                    _motorLeft = 0;
                    _motorRight = 0;
                    _state = default;
                }

                Thread.Sleep(1000);
            }
        }
        catch (DllNotFoundException)
        {
            // XInput unavailable on this system; leave the reader disconnected.
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    private static int FindConnectedSlot()
    {
        for (var index = 0; index < SlotCount; index++)
        {
            if (XInputGetState((uint)index, out _) == ErrorSuccess)
            {
                return index;
            }
        }

        return -1;
    }

    private static PadState Translate(in XInputGamepad pad)
    {
        uint buttons = 0;
        buttons |= (pad.Buttons & XinputDpadUp) != 0 ? OrbisPadButton.Up : 0;
        buttons |= (pad.Buttons & XinputDpadDown) != 0 ? OrbisPadButton.Down : 0;
        buttons |= (pad.Buttons & XinputDpadLeft) != 0 ? OrbisPadButton.Left : 0;
        buttons |= (pad.Buttons & XinputDpadRight) != 0 ? OrbisPadButton.Right : 0;
        buttons |= (pad.Buttons & XinputStart) != 0 ? OrbisPadButton.Options : 0;
        buttons |= (pad.Buttons & XinputBack) != 0 ? OrbisPadButton.TouchPad : 0;
        buttons |= (pad.Buttons & XinputLeftThumb) != 0 ? OrbisPadButton.L3 : 0;
        buttons |= (pad.Buttons & XinputRightThumb) != 0 ? OrbisPadButton.R3 : 0;
        buttons |= (pad.Buttons & XinputLeftShoulder) != 0 ? OrbisPadButton.L1 : 0;
        buttons |= (pad.Buttons & XinputRightShoulder) != 0 ? OrbisPadButton.R1 : 0;
        buttons |= (pad.Buttons & XinputA) != 0 ? OrbisPadButton.Cross : 0;
        buttons |= (pad.Buttons & XinputB) != 0 ? OrbisPadButton.Circle : 0;
        buttons |= (pad.Buttons & XinputX) != 0 ? OrbisPadButton.Square : 0;
        buttons |= (pad.Buttons & XinputY) != 0 ? OrbisPadButton.Triangle : 0;
        buttons |= pad.LeftTrigger > TriggerThreshold ? OrbisPadButton.L2 : 0;
        buttons |= pad.RightTrigger > TriggerThreshold ? OrbisPadButton.R2 : 0;

        return new PadState(
            Connected: true,
            Buttons: buttons,
            LeftX: AxisToByte(pad.ThumbLX),
            LeftY: AxisToByteInverted(pad.ThumbLY),
            RightX: AxisToByte(pad.ThumbRX),
            RightY: AxisToByteInverted(pad.ThumbRY),
            L2: pad.LeftTrigger,
            R2: pad.RightTrigger);
    }

    private static byte AxisToByte(short value) => (byte)((value + 32768) >> 8);

    // XInput Y grows upward, ORBIS pads report Y growing downward.
    private static byte AxisToByteInverted(short value) => (byte)(255 - ((value + 32768) >> 8));

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad
    {
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short ThumbLX;
        public short ThumbLY;
        public short ThumbRX;
        public short ThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint PacketNumber;
        public XInputGamepad Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputVibration
    {
        public ushort LeftMotorSpeed;
        public ushort RightMotorSpeed;
    }

    // xinput1_4.dll ships with Windows 8 and later.
    [DllImport("xinput1_4.dll")]
    private static extern uint XInputGetState(uint userIndex, out XInputState state);

    [DllImport("xinput1_4.dll")]
    private static extern uint XInputSetState(uint userIndex, ref XInputVibration vibration);
}
