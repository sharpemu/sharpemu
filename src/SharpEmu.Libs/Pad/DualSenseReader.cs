// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Microsoft.Win32.SafeHandles;

namespace SharpEmu.Libs.Pad;

/// <summary>
/// Snapshot of the DualSense state, already translated to ORBIS pad
/// conventions (SCE_PAD_BUTTON bits; sticks 0..255 with 128 centered;
/// triggers 0..255).
/// </summary>
internal readonly record struct DualSenseState(
    bool Connected,
    uint Buttons,
    byte LeftX,
    byte LeftY,
    byte RightX,
    byte RightY,
    byte L2,
    byte R2);

/// <summary>
/// Reads a DualSense controller over raw HID on a background thread.
/// Supports USB (input report 0x01) and Bluetooth (extended report 0x31,
/// activated by requesting feature report 0x05), with hot-plug retry.
/// </summary>
internal static class DualSenseReader
{
    private const ushort SonyVendorId = 0x054C;
    private const ushort DualSenseProductId = 0x0CE6;
    private const ushort DualSenseEdgeProductId = 0x0DF2;

    // SCE_PAD_BUTTON bit values.
    private const uint ButtonL3 = 0x0002;
    private const uint ButtonR3 = 0x0004;
    private const uint ButtonOptions = 0x0008;
    private const uint ButtonUp = 0x0010;
    private const uint ButtonRight = 0x0020;
    private const uint ButtonDown = 0x0040;
    private const uint ButtonLeft = 0x0080;
    private const uint ButtonL2 = 0x0100;
    private const uint ButtonR2 = 0x0200;
    private const uint ButtonL1 = 0x0400;
    private const uint ButtonR1 = 0x0800;
    private const uint ButtonTriangle = 0x1000;
    private const uint ButtonCircle = 0x2000;
    private const uint ButtonCross = 0x4000;
    private const uint ButtonSquare = 0x8000;
    private const uint ButtonTouchPad = 0x100000;

    private static readonly object Gate = new();
    private static DualSenseState _state;
    private static bool _started;

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
                Name = "DualSenseReader",
            };
            thread.Start();
        }
    }

    internal static bool TryGetState(out DualSenseState state)
    {
        lock (Gate)
        {
            state = _state;
        }

        return state.Connected;
    }

    private static void SetState(in DualSenseState state)
    {
        lock (Gate)
        {
            _state = state;
        }
    }

    private static void ReadLoop()
    {
        var announcedConnect = false;
        while (true)
        {
            SafeFileHandle? handle = null;
            try
            {
                handle = OpenDualSense();
                if (handle is null)
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
                _ = HidNative.HidD_GetFeature(handle, feature, feature.Length);

                if (!announcedConnect)
                {
                    Console.Error.WriteLine("[LOADER][INFO] DualSense controller connected.");
                    announcedConnect = true;
                }

                using var stream = new FileStream(handle, FileAccess.Read, bufferSize: 1);
                handle = null; // stream owns it now
                var buffer = new byte[256];
                while (true)
                {
                    var read = stream.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                    {
                        break;
                    }

                    if (TryParseReport(buffer.AsSpan(0, read), out var state))
                    {
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

            SetState(default);
            Thread.Sleep(1000);
        }
    }

    private static SafeFileHandle? OpenDualSense()
    {
        foreach (var path in HidNative.EnumerateHidDevicePaths())
        {
            // Open without access rights just to query VID/PID.
            using var probe = HidNative.CreateFile(
                path, 0, HidNative.FileShareRead | HidNative.FileShareWrite, 0, HidNative.OpenExisting, 0, 0);
            if (probe.IsInvalid)
            {
                continue;
            }

            var attributes = new HidNative.HiddAttributes { Size = 12 };
            if (!HidNative.HidD_GetAttributes(probe, ref attributes) ||
                attributes.VendorId != SonyVendorId ||
                (attributes.ProductId != DualSenseProductId && attributes.ProductId != DualSenseEdgeProductId))
            {
                continue;
            }

            // Read+write so feature reports work; fall back to read-only.
            var handle = HidNative.CreateFile(
                path,
                HidNative.GenericRead | HidNative.GenericWrite,
                HidNative.FileShareRead | HidNative.FileShareWrite,
                0, HidNative.OpenExisting, 0, 0);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                handle = HidNative.CreateFile(
                    path,
                    HidNative.GenericRead,
                    HidNative.FileShareRead | HidNative.FileShareWrite,
                    0, HidNative.OpenExisting, 0, 0);
            }

            if (!handle.IsInvalid)
            {
                return handle;
            }

            handle.Dispose();
        }

        return null;
    }

    private static bool TryParseReport(ReadOnlySpan<byte> report, out DualSenseState state)
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

        uint buttons = 0;
        buttons |= (buttons0 & 0x10) != 0 ? ButtonSquare : 0;
        buttons |= (buttons0 & 0x20) != 0 ? ButtonCross : 0;
        buttons |= (buttons0 & 0x40) != 0 ? ButtonCircle : 0;
        buttons |= (buttons0 & 0x80) != 0 ? ButtonTriangle : 0;
        buttons |= HatToButtons(buttons0 & 0x0F);
        buttons |= (buttons1 & 0x01) != 0 ? ButtonL1 : 0;
        buttons |= (buttons1 & 0x02) != 0 ? ButtonR1 : 0;
        buttons |= (buttons1 & 0x04) != 0 ? ButtonL2 : 0;
        buttons |= (buttons1 & 0x08) != 0 ? ButtonR2 : 0;
        buttons |= (buttons1 & 0x20) != 0 ? ButtonOptions : 0;
        buttons |= (buttons1 & 0x40) != 0 ? ButtonL3 : 0;
        buttons |= (buttons1 & 0x80) != 0 ? ButtonR3 : 0;
        buttons |= (buttons2 & 0x02) != 0 ? ButtonTouchPad : 0;

        state = new DualSenseState(
            Connected: true,
            Buttons: buttons,
            LeftX: leftX,
            LeftY: leftY,
            RightX: rightX,
            RightY: rightY,
            L2: l2,
            R2: r2);
        return true;
    }

    private static uint HatToButtons(int hat) => hat switch
    {
        0 => ButtonUp,
        1 => ButtonUp | ButtonRight,
        2 => ButtonRight,
        3 => ButtonRight | ButtonDown,
        4 => ButtonDown,
        5 => ButtonDown | ButtonLeft,
        6 => ButtonLeft,
        7 => ButtonLeft | ButtonUp,
        _ => 0,
    };
}
