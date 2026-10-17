// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.Host;

/// <summary>
/// Host-neutral gamepad button flags. Named after the PlayStation layout the guest API
/// exposes, but the numeric values are the seam's own — the HLE pad exports translate
/// them to SCE_PAD_BUTTON bits, so guest ABI values never leak into host backends.
/// </summary>
[Flags]
public enum HostGamepadButtons : uint
{
    None = 0,
    Up = 1 << 0,
    Down = 1 << 1,
    Left = 1 << 2,
    Right = 1 << 3,
    Cross = 1 << 4,
    Circle = 1 << 5,
    Square = 1 << 6,
    Triangle = 1 << 7,
    L1 = 1 << 8,
    R1 = 1 << 9,
    L2 = 1 << 10,
    R2 = 1 << 11,
    L3 = 1 << 12,
    R3 = 1 << 13,
    Options = 1 << 14,
    TouchPad = 1 << 15,
    Create = 1 << 16,
    Ps = 1 << 17,
    Mic = 1 << 18,
}

public enum HostGamepadType : byte
{
    Generic,
    DualShock4,
    DualSense,
}

public enum HostGamepadConnection : byte
{
    Unknown,
    Wired,
    Wireless,
}

public readonly record struct HostMotionState(
    bool Available,
    float AccelerationX,
    float AccelerationY,
    float AccelerationZ,
    float AngularVelocityX,
    float AngularVelocityY,
    float AngularVelocityZ);

public readonly record struct HostTouchPoint(
    bool Active,
    byte Id,
    float X,
    float Y);

public readonly record struct HostTouchState(
    HostTouchPoint First,
    HostTouchPoint Second);

/// <summary>
/// Snapshot of one host gamepad: sticks are 0..255 with 128 centered and Y growing
/// downward; triggers 0..255. Unmanaged on purpose so per-frame polls can stackalloc
/// snapshot buffers.
/// </summary>
public readonly record struct HostGamepadState(
    bool Connected,
    HostGamepadButtons Buttons,
    byte LeftX,
    byte LeftY,
    byte RightX,
    byte RightY,
    byte LeftTrigger,
    byte RightTrigger,
    HostGamepadType Type = HostGamepadType.Generic,
    HostGamepadConnection Connection = HostGamepadConnection.Unknown,
    HostMotionState Motion = default,
    HostTouchState Touch = default,
    byte BatteryPercent = 0);

/// <summary>A complete 11-byte DualSense adaptive-trigger command.</summary>
public readonly record struct HostAdaptiveTriggerEffect(
    byte B0,
    byte B1,
    byte B2,
    byte B3,
    byte B4,
    byte B5,
    byte B6,
    byte B7,
    byte B8,
    byte B9,
    byte B10,
    byte FallbackStrength = 0)
{
    public static HostAdaptiveTriggerEffect FromBytes(ReadOnlySpan<byte> source, byte fallbackStrength = 0)
    {
        if (source.Length < 11)
        {
            throw new ArgumentException("Adaptive-trigger source is too small.", nameof(source));
        }

        return new HostAdaptiveTriggerEffect(
            source[0], source[1], source[2], source[3], source[4], source[5],
            source[6], source[7], source[8], source[9], source[10], fallbackStrength);
    }

    public void CopyTo(Span<byte> destination)
    {
        if (destination.Length < 11)
        {
            throw new ArgumentException("Adaptive-trigger destination is too small.", nameof(destination));
        }

        destination[0] = B0;
        destination[1] = B1;
        destination[2] = B2;
        destination[3] = B3;
        destination[4] = B4;
        destination[5] = B5;
        destination[6] = B6;
        destination[7] = B7;
        destination[8] = B8;
        destination[9] = B9;
        destination[10] = B10;
    }
}
