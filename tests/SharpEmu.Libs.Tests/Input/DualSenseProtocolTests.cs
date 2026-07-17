// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.Host;
using SharpEmu.HLE.Host.DualSense;
using Xunit;

namespace SharpEmu.Libs.Tests.Input;

/// <summary>
/// Pins the DualSense wire format. Every host transport (Win32 HID, Linux
/// hidraw, macOS IOKit) feeds these same bytes in, so a regression here
/// breaks the controller on all three platforms at once.
/// </summary>
public class DualSenseProtocolTests
{
    /// <summary>USB input report: id 0x01, payload from [1].</summary>
    private static byte[] CreateUsbReport()
    {
        var report = new byte[64];
        report[0] = 0x01;
        report[1] = 0x20; // left X
        report[2] = 0x30; // left Y
        report[3] = 0x40; // right X
        report[4] = 0x50; // right Y
        report[5] = 0x60; // L2
        report[6] = 0x70; // R2
        report[8] = 0x08; // buttons0: hat = 8 (centered), no face buttons
        return report;
    }

    [Fact]
    public void TryParseReport_ReadsSticksAndTriggersFromUsbReport()
    {
        Assert.True(DualSenseProtocol.TryParseReport(CreateUsbReport(), out var state));

        Assert.True(state.Connected);
        Assert.Equal(0x20, state.LeftX);
        Assert.Equal(0x30, state.LeftY);
        Assert.Equal(0x40, state.RightX);
        Assert.Equal(0x50, state.RightY);
        Assert.Equal(0x60, state.LeftTrigger);
        Assert.Equal(0x70, state.RightTrigger);
        Assert.Equal(HostGamepadButtons.None, state.Buttons);
    }

    [Theory]
    [InlineData(0x10, HostGamepadButtons.Square)]
    [InlineData(0x20, HostGamepadButtons.Cross)]
    [InlineData(0x40, HostGamepadButtons.Circle)]
    [InlineData(0x80, HostGamepadButtons.Triangle)]
    public void TryParseReport_MapsFaceButtons(byte raw, HostGamepadButtons expected)
    {
        var report = CreateUsbReport();
        report[8] = (byte)(0x08 | raw); // keep the hat centered

        Assert.True(DualSenseProtocol.TryParseReport(report, out var state));
        Assert.Equal(expected, state.Buttons);
    }

    [Theory]
    [InlineData(0x01, HostGamepadButtons.L1)]
    [InlineData(0x02, HostGamepadButtons.R1)]
    [InlineData(0x04, HostGamepadButtons.L2)]
    [InlineData(0x08, HostGamepadButtons.R2)]
    [InlineData(0x20, HostGamepadButtons.Options)]
    [InlineData(0x40, HostGamepadButtons.L3)]
    [InlineData(0x80, HostGamepadButtons.R3)]
    public void TryParseReport_MapsShoulderAndStickButtons(byte raw, HostGamepadButtons expected)
    {
        var report = CreateUsbReport();
        report[9] = raw;

        Assert.True(DualSenseProtocol.TryParseReport(report, out var state));
        Assert.Equal(expected, state.Buttons);
    }

    [Fact]
    public void TryParseReport_MapsTouchPadButton()
    {
        var report = CreateUsbReport();
        report[10] = 0x02;

        Assert.True(DualSenseProtocol.TryParseReport(report, out var state));
        Assert.Equal(HostGamepadButtons.TouchPad, state.Buttons);
    }

    [Theory]
    [InlineData(0, HostGamepadButtons.Up)]
    [InlineData(1, HostGamepadButtons.Up | HostGamepadButtons.Right)]
    [InlineData(2, HostGamepadButtons.Right)]
    [InlineData(3, HostGamepadButtons.Right | HostGamepadButtons.Down)]
    [InlineData(4, HostGamepadButtons.Down)]
    [InlineData(5, HostGamepadButtons.Down | HostGamepadButtons.Left)]
    [InlineData(6, HostGamepadButtons.Left)]
    [InlineData(7, HostGamepadButtons.Left | HostGamepadButtons.Up)]
    [InlineData(8, HostGamepadButtons.None)] // released
    public void TryParseReport_MapsHatToDirections(int hat, HostGamepadButtons expected)
    {
        var report = CreateUsbReport();
        report[8] = (byte)hat;

        Assert.True(DualSenseProtocol.TryParseReport(report, out var state));
        Assert.Equal(expected, state.Buttons);
    }

    [Fact]
    public void TryParseReport_ReadsBluetoothReportFromShiftedPayload()
    {
        // Bluetooth 0x31 carries a sequence byte at [1], so the payload the
        // USB report puts at [1] starts at [2] instead.
        var report = new byte[78];
        report[0] = 0x31;
        report[1] = 0x10; // sequence
        report[2] = 0x20; // left X
        report[9] = 0x08; // hat centered
        report[10] = 0x01; // L1

        Assert.True(DualSenseProtocol.TryParseReport(report, out var state));
        Assert.Equal(0x20, state.LeftX);
        Assert.Equal(HostGamepadButtons.L1, state.Buttons);
    }

    [Theory]
    [InlineData(new byte[] { 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 })] // unknown id
    [InlineData(new byte[] { 0x01, 0, 0 })]                            // truncated USB
    [InlineData(new byte[] { 0x31, 0, 0, 0, 0 })]                      // truncated Bluetooth
    [InlineData(new byte[0])]
    public void TryParseReport_RejectsReportsItCannotTrust(byte[] report)
    {
        Assert.False(DualSenseProtocol.TryParseReport(report, out var state));
        Assert.False(state.Connected);
    }

    [Fact]
    public void IsDualSense_MatchesOnlySonyPads()
    {
        Assert.True(DualSenseProtocol.IsDualSense(0x054C, 0x0CE6));  // DualSense
        Assert.True(DualSenseProtocol.IsDualSense(0x054C, 0x0DF2));  // DualSense Edge
        Assert.False(DualSenseProtocol.IsDualSense(0x054C, 0x05C4)); // DualShock 4
        Assert.False(DualSenseProtocol.IsDualSense(0x045E, 0x0CE6)); // Microsoft VID
    }

    [Fact]
    public void BuildOutputReport_UsbCarriesMotorsBehindReportId()
    {
        byte sequence = 0;
        var pending = false;
        var report = DualSenseProtocol.BuildOutputReport(
            bluetooth: false,
            motorLeft: 0xAA,
            motorRight: 0xBB,
            lightbarRed: 1,
            lightbarGreen: 2,
            lightbarBlue: 3,
            playerLeds: 0x04,
            ref sequence,
            ref pending);

        Assert.Equal(48, report.Length);
        Assert.Equal(0x02, report[0]);  // USB output report id
        Assert.Equal(0xBB, report[3]);  // right (weak) motor
        Assert.Equal(0xAA, report[4]);  // left (strong) motor
        Assert.Equal(1, report[45]);
        Assert.Equal(2, report[46]);
        Assert.Equal(3, report[47]);
        Assert.Equal(0, sequence); // USB does not use the sequence tag
    }

    [Fact]
    public void BuildOutputReport_BluetoothTagsSequenceAndAppendsCrc()
    {
        byte sequence = 0;
        var pending = false;
        var first = DualSenseProtocol.BuildOutputReport(
            bluetooth: true,
            motorLeft: 0xAA,
            motorRight: 0xBB,
            lightbarRed: 1,
            lightbarGreen: 2,
            lightbarBlue: 3,
            playerLeds: 0x04,
            ref sequence,
            ref pending);

        Assert.Equal(78, first.Length);
        Assert.Equal(0x31, first[0]);
        Assert.Equal(0x00, first[1]); // sequence 0 in the high nibble
        Assert.Equal(1, sequence);    // advanced for the next send

        // A non-zero CRC32 is appended over the first 74 bytes.
        Assert.NotEqual(0u, BitConverter.ToUInt32(first, 74));

        var second = DualSenseProtocol.BuildOutputReport(
            bluetooth: true,
            motorLeft: 0xAA,
            motorRight: 0xBB,
            lightbarRed: 1,
            lightbarGreen: 2,
            lightbarBlue: 3,
            playerLeds: 0x04,
            ref sequence,
            ref pending);

        Assert.Equal(0x10, second[1]); // sequence 1 in the high nibble
        Assert.Equal(2, sequence);
        // The sequence is part of the CRC input, so identical state still
        // produces a different trailer.
        Assert.NotEqual(BitConverter.ToUInt32(first, 74), BitConverter.ToUInt32(second, 74));
    }

    [Fact]
    public void BuildOutputReport_SendsLightbarSetupOnceThenClearsIt()
    {
        byte sequence = 0;
        var pending = true;
        var first = DualSenseProtocol.BuildOutputReport(
            bluetooth: false, 0, 0, 0, 0, 64, 0x04, ref sequence, ref pending);

        Assert.Equal(0x02, first[39] & 0x02); // valid_flag2: lightbar setup enable
        Assert.Equal(0x01, first[42]);        // lightbar_setup: light on
        Assert.False(pending);                // consumed

        var second = DualSenseProtocol.BuildOutputReport(
            bluetooth: false, 0, 0, 0, 0, 64, 0x04, ref sequence, ref pending);

        Assert.Equal(0, second[39] & 0x02);
        Assert.Equal(0, second[42]);
    }

    [Fact]
    public void IsBluetoothReport_RecognisesTheExtendedReportId()
    {
        Assert.True(DualSenseProtocol.IsBluetoothReport(new byte[] { 0x31, 0 }));
        Assert.False(DualSenseProtocol.IsBluetoothReport(new byte[] { 0x01, 0 }));
        Assert.False(DualSenseProtocol.IsBluetoothReport(Array.Empty<byte>()));
    }
}
