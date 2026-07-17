// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.Host.Linux;
using SharpEmu.HLE.Host.Mac;
using SharpEmu.HLE.Host.Windows;

namespace SharpEmu.HLE.Host.DualSense;

/// <summary>
/// Reads a DualSense controller on a background thread, over whichever HID
/// stack the host provides (Win32 HID, Linux hidraw, macOS IOKit). Supports
/// USB (input report 0x01) and Bluetooth (extended report 0x31), rumble, and
/// the lightbar, with hot-plug retry. Every platform shares this loop; only
/// <see cref="DualSenseTransport"/> differs.
/// </summary>
internal static class DualSenseReader
{
    private static readonly object Gate = new();
    private static HostGamepadState _state;
    private static bool _started;

    /// <summary>
    /// Why no pad is readable, when that is actionable by the user (most
    /// often Linux hidraw permissions). Null when simply nothing is plugged in.
    /// </summary>
    private static string? _unavailableReason;

    // Output (rumble/lightbar) state, all guarded by Gate.
    private static DualSenseTransport? _transport;
    private static byte _outputSequence;
    private static bool _lightbarSetupPending;
    private static byte _motorLeft;
    private static byte _motorRight;
    private static byte _lightbarRed;
    private static byte _lightbarGreen;
    private static byte _lightbarBlue = 64; // PS-style blue default
    private static byte _playerLeds = 0x04; // center LED = player 1

    /// <summary>True on hosts with a DualSense transport; elsewhere every query is inert.</summary>
    internal static bool IsSupported =>
        OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    /// <summary>Starts the background reader once; safe to call repeatedly.</summary>
    internal static void EnsureStarted()
    {
        if (!IsSupported)
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

    /// <summary>
    /// A user-actionable explanation for why no pad is readable (e.g. missing
    /// hidraw permissions on Linux), or null when nothing is simply connected.
    /// </summary>
    internal static string? UnavailableReason
    {
        get
        {
            lock (Gate)
            {
                return _unavailableReason;
            }
        }
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
        if (!IsSupported)
        {
            return;
        }

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
        if (!IsSupported)
        {
            return;
        }

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

    private static void OnDeviceIdentified(DualSenseTransport transport)
    {
        lock (Gate)
        {
            _transport = transport;
            _lightbarSetupPending = true;
            _unavailableReason = null;
            // Announce ourselves on the hardware: default lightbar + player 1 LED.
            SendOutputLocked();
        }
    }

    private static void OnDeviceLost()
    {
        lock (Gate)
        {
            _transport = null;
            _motorLeft = 0;
            _motorRight = 0;
        }
    }

    private static void SendOutputLocked()
    {
        if (_transport is not { } transport)
        {
            return; // flushed by OnDeviceIdentified once connected
        }

        var report = DualSenseProtocol.BuildOutputReport(
            transport.Bluetooth,
            _motorLeft,
            _motorRight,
            _lightbarRed,
            _lightbarGreen,
            _lightbarBlue,
            _playerLeds,
            ref _outputSequence,
            ref _lightbarSetupPending);

        // A failed write is not fatal: read-only device access simply has no
        // outputs, and a transient error heals on the next send because the
        // transport reopens its write side lazily. Dropping the transport
        // here would silently kill rumble until the pad is replugged.
        _ = transport.Write(report);
    }

    private static DualSenseTransport? TryOpenTransport(out string? unavailableReason)
    {
        unavailableReason = null;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return WindowsDualSenseTransport.TryOpen();
            }

            if (OperatingSystem.IsLinux())
            {
                return LinuxDualSenseTransport.TryOpen(out unavailableReason);
            }

            if (OperatingSystem.IsMacOS())
            {
                return MacDualSenseTransport.TryOpen(out unavailableReason);
            }
        }
        catch (Exception)
        {
            // A broken/absent HID stack must not take the launcher down; the
            // reader simply reports no pad and retries.
        }

        return null;
    }

    private static void ReadLoop()
    {
        var announcedConnect = false;
        while (true)
        {
            DualSenseTransport? transport = null;
            try
            {
                transport = TryOpenTransport(out var unavailableReason);
                if (transport is null)
                {
                    SetState(default);
                    lock (Gate)
                    {
                        _unavailableReason = unavailableReason;
                    }

                    announcedConnect = false;
                    Thread.Sleep(1000);
                    continue;
                }

                if (!announcedConnect)
                {
                    Console.Error.WriteLine("[LOADER][INFO] DualSense controller connected.");
                    announcedConnect = true;
                }

                // A transport that knows its bus (hidraw, IOKit) is trusted
                // immediately, so rumble works before the first report lands.
                var transportKnown = transport.ReportsTransport;
                if (transportKnown)
                {
                    OnDeviceIdentified(transport);
                }

                var buffer = new byte[256];
                while (true)
                {
                    var read = transport.Read(buffer);
                    if (read <= 0)
                    {
                        break;
                    }

                    if (DualSenseProtocol.TryParseReport(buffer.AsSpan(0, read), out var state))
                    {
                        if (!transportKnown)
                        {
                            // The first parsed report tells us the transport,
                            // which the output (rumble/lightbar) path needs.
                            transportKnown = true;
                            transport.ObserveTransport(
                                DualSenseProtocol.IsBluetoothReport(buffer.AsSpan(0, read)));
                            OnDeviceIdentified(transport);
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
                OnDeviceLost();
                transport?.Dispose();
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
}
