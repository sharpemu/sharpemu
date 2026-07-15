// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Microsoft.Win32.SafeHandles;

namespace SharpEmu.HLE.Host.Windows;

/// <summary>
/// Reads a DualSense controller over raw HID on a background thread.
/// Supports USB (input report 0x01) and Bluetooth (extended report 0x31,
/// activated by requesting feature report 0x05), with hot-plug retry.
/// </summary>
internal static class WindowsDualSenseReader
{
    private const ushort SonyVendorId = 0x054C;
    private const ushort DualSenseProductId = 0x0CE6;
    private const ushort DualSenseEdgeProductId = 0x0DF2;

    private static readonly object Gate = new();
    private static HostGamepadState _state;
    private static bool _started;

    // Output (rumble/lightbar) state, all guarded by Gate.
    private static string? _devicePath;
    private static bool _bluetooth;
    private static bool _outputReady;
    private static bool _lightbarSetupPending;
    private static byte _outputSequence;
    private static FileStream? _outputStream;
    private static byte _motorLeft;
    private static byte _motorRight;
    private static byte _lightbarRed;
    private static byte _lightbarGreen;
    private static byte _lightbarBlue = 64; // PS-style blue default
    private static byte _playerLeds = 0x04; // center LED = player 1

    /// <summary>Starts the background reader once; safe to call repeatedly.</summary>
    internal static void EnsureStarted()
    {
        // The GUI source-links this reader and calls it directly, without the
        // host-platform resolution that otherwise guarantees Windows.
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
                Name = "DualSenseReader",
            };
            thread.Start();
        }
    }

    internal static bool TryGetState(out HostGamepadState state)
    {
        lock (Gate)
        {
            state = _state;
        }

        return state.Connected;
    }

    private static void SetState(in HostGamepadState state)
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
            SendOutputLocked();
        }
    }

    internal static void SetLightbar(byte red, byte green, byte blue)
    {
        lock (Gate)
        {
            if (_lightbarRed == red && _lightbarGreen == green && _lightbarBlue == blue)
            {
                return;
            }

            _lightbarRed = red;
            _lightbarGreen = green;
            _lightbarBlue = blue;
            SendOutputLocked();
        }
    }

    internal static void ResetLightbar() => SetLightbar(0, 0, 64);

    private static void OnDeviceIdentified(string path, bool bluetooth)
    {
        lock (Gate)
        {
            _devicePath = path;
            _bluetooth = bluetooth;
            _outputReady = true;
            _lightbarSetupPending = true;
            // Announce ourselves on the hardware: default lightbar + player 1 LED.
            SendOutputLocked();
        }
    }

    private static void OnDeviceLost()
    {
        lock (Gate)
        {
            _devicePath = null;
            _outputReady = false;
            _motorLeft = 0;
            _motorRight = 0;
            _outputStream?.Dispose();
            _outputStream = null;
        }
    }

    private static void SendOutputLocked()
    {
        if (!_outputReady || _devicePath is null)
        {
            return; // flushed by OnDeviceIdentified once connected
        }

        try
        {
            if (_outputStream is null)
            {
                var handle = WindowsHidNative.CreateFile(
                    _devicePath,
                    WindowsHidNative.GenericRead | WindowsHidNative.GenericWrite,
                    WindowsHidNative.FileShareRead | WindowsHidNative.FileShareWrite,
                    0, WindowsHidNative.OpenExisting, 0, 0);
                if (handle.IsInvalid)
                {
                    handle.Dispose();
                    return; // read-only device access: outputs unavailable
                }

                _outputStream = new FileStream(handle, FileAccess.Write, bufferSize: 1);
            }

            var report = BuildOutputReportLocked();
            _outputStream.Write(report, 0, report.Length);
            _outputStream.Flush();
        }
        catch (Exception)
        {
            _outputStream?.Dispose();
            _outputStream = null;
        }
    }

    private static byte[] BuildOutputReportLocked()
    {
        // Common 47-byte output payload (offsets per the DualSense output
        // report layout, same as Linux hid-playstation).
        Span<byte> common = stackalloc byte[47];
        common[0] = 0x03;                    // valid_flag0: compatible vibration + haptics select
        common[1] = 0x04 | 0x10;             // valid_flag1: lightbar + player indicator
        common[2] = _motorRight;             // right (weak) motor
        common[3] = _motorLeft;              // left (strong) motor
        if (_lightbarSetupPending)
        {
            common[38] |= 0x02;              // valid_flag2: lightbar setup control enable
            common[41] = 0x01;               // lightbar_setup: light on
            _lightbarSetupPending = false;
        }

        common[43] = _playerLeds;
        common[44] = _lightbarRed;
        common[45] = _lightbarGreen;
        common[46] = _lightbarBlue;

        if (!_bluetooth)
        {
            var usbReport = new byte[48];
            usbReport[0] = 0x02;
            common.CopyTo(usbReport.AsSpan(1));
            return usbReport;
        }

        // Bluetooth: 0x31 wrapper with sequence tag and CRC32 over a 0xA2
        // seed byte plus the first 74 report bytes.
        var btReport = new byte[78];
        btReport[0] = 0x31;
        btReport[1] = (byte)((_outputSequence & 0x0F) << 4);
        _outputSequence = (byte)((_outputSequence + 1) & 0x0F);
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

    private static void ReadLoop()
    {
        var announcedConnect = false;
        while (true)
        {
            SafeFileHandle? handle = null;
            try
            {
                handle = OpenDualSense(out var devicePath);
                if (handle is null || devicePath is null)
                {
                    SetState(default);
                    announcedConnect = false;
                    Thread.Sleep(1000);
                    continue;
                }

                // Bluetooth quirk: the DualSense sends a simplified report
                // until feature report 0x05 is requested, which switches it
                // to the full 0x31 input report. Harmless over USB.
                var feature = new byte[41];
                feature[0] = 0x05;
                _ = WindowsHidNative.HidD_GetFeature(handle, feature, feature.Length);

                if (!announcedConnect)
                {
                    Console.Error.WriteLine("[LOADER][INFO] DualSense controller connected.");
                    announcedConnect = true;
                }

                using var stream = new FileStream(handle, FileAccess.Read, bufferSize: 1);
                handle = null; // stream owns it now
                var buffer = new byte[256];
                var transportKnown = false;
                while (true)
                {
                    var read = stream.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                    {
                        break;
                    }

                    if (TryParseReport(buffer.AsSpan(0, read), out var state))
                    {
                        if (!transportKnown)
                        {
                            // The first parsed report tells us the transport,
                            // which the output (rumble/lightbar) path needs.
                            transportKnown = true;
                            OnDeviceIdentified(devicePath, bluetooth: buffer[0] == 0x31);
                        }

                        SetState(state);
                    }
                }
            }
            catch (Exception)
            {
                // Unplugged or read error: fall through and retry.
            }
            finally
            {
                handle?.Dispose();
            }

            if (announcedConnect)
            {
                Console.Error.WriteLine("[LOADER][INFO] DualSense controller disconnected.");
                announcedConnect = false;
            }

            OnDeviceLost();
            SetState(default);
            Thread.Sleep(1000);
        }
    }

    private static SafeFileHandle? OpenDualSense(out string? devicePath)
    {
        devicePath = null;
        foreach (var path in WindowsHidNative.EnumerateHidDevicePaths())
        {
            // Open without access rights just to query VID/PID.
            using var probe = WindowsHidNative.CreateFile(
                path, 0, WindowsHidNative.FileShareRead | WindowsHidNative.FileShareWrite, 0, WindowsHidNative.OpenExisting, 0, 0);
            if (probe.IsInvalid)
            {
                continue;
            }

            var attributes = new WindowsHidNative.HiddAttributes { Size = 12 };
            if (!WindowsHidNative.HidD_GetAttributes(probe, ref attributes) ||
                attributes.VendorId != SonyVendorId ||
                (attributes.ProductId != DualSenseProductId && attributes.ProductId != DualSenseEdgeProductId))
            {
                continue;
            }

            // Read+write so feature reports work; fall back to read-only.
            var handle = WindowsHidNative.CreateFile(
                path,
                WindowsHidNative.GenericRead | WindowsHidNative.GenericWrite,
                WindowsHidNative.FileShareRead | WindowsHidNative.FileShareWrite,
                0, WindowsHidNative.OpenExisting, 0, 0);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                handle = WindowsHidNative.CreateFile(
                    path,
                    WindowsHidNative.GenericRead,
                    WindowsHidNative.FileShareRead | WindowsHidNative.FileShareWrite,
                    0, WindowsHidNative.OpenExisting, 0, 0);
            }

            if (!handle.IsInvalid)
            {
                devicePath = path;
                return handle;
            }

            handle.Dispose();
        }

        return null;
    }

    private static bool TryParseReport(ReadOnlySpan<byte> report, out HostGamepadState state)
    {
        // USB: report id 0x01, payload starts at [1].
        // Bluetooth extended: report id 0x31, sequence byte at [1], payload at [2].
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
