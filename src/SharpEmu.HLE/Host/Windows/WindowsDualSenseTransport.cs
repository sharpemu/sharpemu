// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using SharpEmu.HLE.Host.DualSense;

namespace SharpEmu.HLE.Host.Windows;

/// <summary>
/// DualSense transport over Win32 HID device files, enumerated through
/// SetupAPI. Win32 exposes no bus type for a HID device, so the transport is
/// inferred from the first input report id instead.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsDualSenseTransport : DualSenseTransport
{
    private readonly string _devicePath;
    private readonly FileStream _readStream;
    private FileStream? _writeStream;
    private bool _bluetooth;

    private WindowsDualSenseTransport(string devicePath, FileStream readStream)
    {
        _devicePath = devicePath;
        _readStream = readStream;
    }

    internal override bool Bluetooth => _bluetooth;

    /// <summary>Win32 does not report the bus; the first input report decides.</summary>
    internal override bool ReportsTransport => false;

    internal override void ObserveTransport(bool bluetooth) => _bluetooth = bluetooth;

    internal static WindowsDualSenseTransport? TryOpen()
    {
        var handle = OpenDualSense(out var devicePath);
        if (handle is null || devicePath is null)
        {
            handle?.Dispose();
            return null;
        }

        // Bluetooth quirk: the DualSense sends a simplified report until
        // feature report 0x05 is requested, which switches it to the full
        // 0x31 input report. Harmless over USB.
        var feature = new byte[DualSenseProtocol.BluetoothEnableFeatureReportSize];
        feature[0] = DualSenseProtocol.BluetoothEnableFeatureReportId;
        _ = WindowsHidNative.HidD_GetFeature(handle, feature, feature.Length);

        var readStream = new FileStream(handle, FileAccess.Read, bufferSize: 1);
        return new WindowsDualSenseTransport(devicePath, readStream);
    }

    internal override int Read(Span<byte> buffer) => _readStream.Read(buffer);

    internal override bool Write(ReadOnlySpan<byte> report)
    {
        try
        {
            if (_writeStream is null)
            {
                var handle = WindowsHidNative.CreateFile(
                    _devicePath,
                    WindowsHidNative.GenericRead | WindowsHidNative.GenericWrite,
                    WindowsHidNative.FileShareRead | WindowsHidNative.FileShareWrite,
                    0, WindowsHidNative.OpenExisting, 0, 0);
                if (handle.IsInvalid)
                {
                    handle.Dispose();
                    return false; // read-only device access: outputs unavailable
                }

                _writeStream = new FileStream(handle, FileAccess.Write, bufferSize: 1);
            }

            _writeStream.Write(report);
            _writeStream.Flush();
            return true;
        }
        catch (Exception)
        {
            _writeStream?.Dispose();
            _writeStream = null;
            return false;
        }
    }

    public override void Dispose()
    {
        _readStream.Dispose();
        _writeStream?.Dispose();
        _writeStream = null;
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
                !DualSenseProtocol.IsDualSense(attributes.VendorId, attributes.ProductId))
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
}
