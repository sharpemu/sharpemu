// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.Host.DualSense;

/// <summary>
/// The DualSense HID wire protocol: input report parsing and output
/// (rumble/lightbar) report building. Pure and platform-neutral — every host
/// transport (Win32 HID, Linux hidraw, macOS IOKit) moves the same bytes, so
/// only the transport differs per platform.
/// </summary>
internal static class DualSenseProtocol
{
    internal const ushort SonyVendorId = 0x054C;
    internal const ushort DualSenseProductId = 0x0CE6;
    internal const ushort DualSenseEdgeProductId = 0x0DF2;

    /// <summary>Longest report we ever send (the Bluetooth 0x31 wrapper).</summary>
    internal const int MaxOutputReportSize = 78;

    /// <summary>Feature report that switches Bluetooth to the full 0x31 input report.</summary>
    internal const byte BluetoothEnableFeatureReportId = 0x05;

    internal const int BluetoothEnableFeatureReportSize = 41;

    internal static bool IsDualSense(ushort vendorId, ushort productId) =>
        vendorId == SonyVendorId &&
        (productId == DualSenseProductId || productId == DualSenseEdgeProductId);

    /// <summary>
    /// Parses a DualSense input report into the host-neutral snapshot.
    /// USB uses report id 0x01 with the payload at [1]; Bluetooth uses the
    /// extended report 0x31 with a sequence byte at [1] and the payload at [2].
    /// </summary>
    internal static bool TryParseReport(ReadOnlySpan<byte> report, out HostGamepadState state)
    {
        int offset;
        if (report.Length >= 11 && report[0] == 0x01)
        {
            offset = 1;
        }
        else if (report.Length >= 12 && report[0] == 0x31)
        {
            offset = 2;
        }
        else
        {
            state = default;
            return false;
        }

        var leftX = report[offset + 0];
        var leftY = report[offset + 1];
        var rightX = report[offset + 2];
        var rightY = report[offset + 3];
        var l2 = report[offset + 4];
        var r2 = report[offset + 5];
        var buttons0 = report[offset + 7];
        var buttons1 = report[offset + 8];
        var buttons2 = report[offset + 9];

        var buttons = HostGamepadButtons.None;
        buttons |= (buttons0 & 0x10) != 0 ? HostGamepadButtons.Square : 0;
        buttons |= (buttons0 & 0x20) != 0 ? HostGamepadButtons.Cross : 0;
        buttons |= (buttons0 & 0x40) != 0 ? HostGamepadButtons.Circle : 0;
        buttons |= (buttons0 & 0x80) != 0 ? HostGamepadButtons.Triangle : 0;
        buttons |= HatToButtons(buttons0 & 0x0F);
        buttons |= (buttons1 & 0x01) != 0 ? HostGamepadButtons.L1 : 0;
        buttons |= (buttons1 & 0x02) != 0 ? HostGamepadButtons.R1 : 0;
        buttons |= (buttons1 & 0x04) != 0 ? HostGamepadButtons.L2 : 0;
        buttons |= (buttons1 & 0x08) != 0 ? HostGamepadButtons.R2 : 0;
        buttons |= (buttons1 & 0x20) != 0 ? HostGamepadButtons.Options : 0;
        buttons |= (buttons1 & 0x40) != 0 ? HostGamepadButtons.L3 : 0;
        buttons |= (buttons1 & 0x80) != 0 ? HostGamepadButtons.R3 : 0;
        buttons |= (buttons2 & 0x02) != 0 ? HostGamepadButtons.TouchPad : 0;

        state = new HostGamepadState(
            Connected: true,
            Buttons: buttons,
            LeftX: leftX,
            LeftY: leftY,
            RightX: rightX,
            RightY: rightY,
            LeftTrigger: l2,
            RightTrigger: r2);
        return true;
    }

    /// <summary>True when the report id identifies a Bluetooth-transport report.</summary>
    internal static bool IsBluetoothReport(ReadOnlySpan<byte> report) =>
        report.Length > 0 && report[0] == 0x31;

    /// <summary>
    /// Builds the rumble/lightbar output report for the given transport.
    /// <paramref name="sequence"/> and <paramref name="lightbarSetupPending"/>
    /// are advanced/consumed as part of building the report.
    /// </summary>
    internal static byte[] BuildOutputReport(
        bool bluetooth,
        byte motorLeft,
        byte motorRight,
        byte lightbarRed,
        byte lightbarGreen,
        byte lightbarBlue,
        byte playerLeds,
        ref byte sequence,
        ref bool lightbarSetupPending)
    {
        // Common 47-byte output payload (offsets per the DualSense output
        // report layout, same as Linux hid-playstation).
        Span<byte> common = stackalloc byte[47];
        common[0] = 0x03;                    // valid_flag0: compatible vibration + haptics select
        common[1] = 0x04 | 0x10;             // valid_flag1: lightbar + player indicator
        common[2] = motorRight;              // right (weak) motor
        common[3] = motorLeft;               // left (strong) motor
        if (lightbarSetupPending)
        {
            common[38] |= 0x02;              // valid_flag2: lightbar setup control enable
            common[41] = 0x01;               // lightbar_setup: light on
            lightbarSetupPending = false;
        }

        common[43] = playerLeds;
        common[44] = lightbarRed;
        common[45] = lightbarGreen;
        common[46] = lightbarBlue;

        if (!bluetooth)
        {
            var usbReport = new byte[48];
            usbReport[0] = 0x02;
            common.CopyTo(usbReport.AsSpan(1));
            return usbReport;
        }

        // Bluetooth: 0x31 wrapper with sequence tag and CRC32 over a 0xA2
        // seed byte plus the first 74 report bytes.
        var btReport = new byte[MaxOutputReportSize];
        btReport[0] = 0x31;
        btReport[1] = (byte)((sequence & 0x0F) << 4);
        sequence = (byte)((sequence + 1) & 0x0F);
        btReport[2] = 0x10;
        common.CopyTo(btReport.AsSpan(3));
        var crc = Crc32(0xA2, btReport.AsSpan(0, 74));
        btReport[74] = (byte)crc;
        btReport[75] = (byte)(crc >> 8);
        btReport[76] = (byte)(crc >> 16);
        btReport[77] = (byte)(crc >> 24);
        return btReport;
    }

    private static uint Crc32(byte seed, ReadOnlySpan<byte> data)
    {
        var crc = Crc32Update(0xFFFFFFFFu, seed);
        foreach (var value in data)
        {
            crc = Crc32Update(crc, value);
        }

        return ~crc;
    }

    private static uint Crc32Update(uint crc, byte value)
    {
        crc ^= value;
        for (var bit = 0; bit < 8; bit++)
        {
            crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
        }

        return crc;
    }

    private static HostGamepadButtons HatToButtons(int hat) => hat switch
    {
        0 => HostGamepadButtons.Up,
        1 => HostGamepadButtons.Up | HostGamepadButtons.Right,
        2 => HostGamepadButtons.Right,
        3 => HostGamepadButtons.Right | HostGamepadButtons.Down,
        4 => HostGamepadButtons.Down,
        5 => HostGamepadButtons.Down | HostGamepadButtons.Left,
        6 => HostGamepadButtons.Left,
        7 => HostGamepadButtons.Left | HostGamepadButtons.Up,
        _ => 0,
    };
}
