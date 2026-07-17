// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SharpEmu.HLE.Host.DualSense;

namespace SharpEmu.HLE.Host.Linux;

/// <summary>
/// DualSense transport over Linux hidraw. Devices are discovered through
/// sysfs, which publishes each hidraw node's bus/vendor/product without
/// opening it — so an unreadable node can be reported as a permissions
/// problem rather than silently looking like "no controller", and no
/// unrelated HID device is ever opened.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class LinuxDualSenseTransport : DualSenseTransport
{
    private const string HidRawClassDirectory = "/sys/class/hidraw";

    private readonly int _fd;
    private readonly bool _bluetooth;
    private readonly bool _writable;

    private LinuxDualSenseTransport(int fd, bool bluetooth, bool writable)
    {
        _fd = fd;
        _bluetooth = bluetooth;
        _writable = writable;
    }

    internal override bool Bluetooth => _bluetooth;

    /// <summary>hidraw reports the bus type, so the transport is known up front.</summary>
    internal override bool ReportsTransport => true;

    /// <summary>
    /// Opens the first DualSense hidraw node. <paramref name="unavailableReason"/>
    /// is set only when a pad was found but could not be opened, which on
    /// Linux is almost always missing udev permissions on /dev/hidraw*.
    /// </summary>
    internal static LinuxDualSenseTransport? TryOpen(out string? unavailableReason)
    {
        unavailableReason = null;
        string? permissionDeniedPath = null;

        foreach (var (devicePath, bluetooth) in EnumerateDualSenseNodes())
        {
            var writable = true;
            var fd = LinuxHidRawNative.Open(
                devicePath,
                LinuxHidRawNative.O_RDWR | LinuxHidRawNative.O_CLOEXEC);
            if (fd < 0)
            {
                // Rumble and the lightbar need write access, but a read-only
                // node still drives the buttons; take what we can get.
                writable = false;
                fd = LinuxHidRawNative.Open(
                    devicePath,
                    LinuxHidRawNative.O_RDONLY | LinuxHidRawNative.O_CLOEXEC);
            }

            if (fd < 0)
            {
                if (Marshal.GetLastPInvokeError() == LinuxHidRawNative.EACCES)
                {
                    permissionDeniedPath ??= devicePath;
                }

                continue;
            }

            // Bluetooth quirk: the DualSense sends a simplified report until
            // feature report 0x05 is requested, which switches it to the full
            // 0x31 input report. Harmless over USB.
            RequestFullReportMode(fd);

            return new LinuxDualSenseTransport(fd, QueryBluetooth(fd) ?? bluetooth, writable);
        }

        if (permissionDeniedPath is not null)
        {
            unavailableReason =
                $"A DualSense is connected but {permissionDeniedPath} is not readable. " +
                "Install the game-controller udev rules (e.g. the game-devices-udev package) " +
                "and replug the pad.";
        }

        return null;
    }

    internal override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }

        var read = LinuxHidRawNative.Read(_fd, ref MemoryMarshal.GetReference(buffer), (nuint)buffer.Length);
        return (int)read;
    }

    internal override bool Write(ReadOnlySpan<byte> report)
    {
        if (!_writable || report.IsEmpty || report.Length > DualSenseProtocol.MaxOutputReportSize)
        {
            return false;
        }

        // The span is only read, but write(2) takes a non-const pointer in
        // the P/Invoke signature; the copy keeps the public span read-only.
        Span<byte> scratch = stackalloc byte[DualSenseProtocol.MaxOutputReportSize];
        report.CopyTo(scratch);
        var written = LinuxHidRawNative.Write(
            _fd,
            ref MemoryMarshal.GetReference(scratch),
            (nuint)report.Length);
        return written == report.Length;
    }

    public override void Dispose()
    {
        if (_fd >= 0)
        {
            _ = LinuxHidRawNative.Close(_fd);
        }
    }

    private static void RequestFullReportMode(int fd)
    {
        Span<byte> feature = stackalloc byte[DualSenseProtocol.BluetoothEnableFeatureReportSize];
        feature[0] = DualSenseProtocol.BluetoothEnableFeatureReportId;
        _ = LinuxHidRawNative.Ioctl(
            fd,
            LinuxHidRawNative.HidIocGFeature(feature.Length),
            ref MemoryMarshal.GetReference(feature));
    }

    /// <summary>The bus straight from the driver, or null when the ioctl fails.</summary>
    private static bool? QueryBluetooth(int fd)
    {
        var info = default(LinuxHidRawNative.HidrawDevinfo);
        if (LinuxHidRawNative.Ioctl(fd, LinuxHidRawNative.HidIocGRawInfo, ref info) < 0)
        {
            return null;
        }

        return info.BusType == LinuxHidRawNative.BUS_BLUETOOTH;
    }

    /// <summary>
    /// DualSense hidraw nodes and their bus, read from sysfs so nothing is
    /// opened during discovery.
    /// </summary>
    private static IEnumerable<(string DevicePath, bool Bluetooth)> EnumerateDualSenseNodes()
    {
        string[] entries;
        try
        {
            entries = Directory.GetDirectories(HidRawClassDirectory);
        }
        catch (Exception)
        {
            yield break; // no sysfs (container, unusual kernel): no pad
        }

        Array.Sort(entries, StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var name = Path.GetFileName(entry);
            if (!TryReadHidId(entry, out var bus, out var vendor, out var product) ||
                !DualSenseProtocol.IsDualSense(vendor, product))
            {
                continue;
            }

            yield return ($"/dev/{name}", bus == LinuxHidRawNative.BUS_BLUETOOTH);
        }
    }

    /// <summary>
    /// Parses "HID_ID=0003:0000054C:00000CE6" (bus:vendor:product, hex) from
    /// the hidraw device's uevent.
    /// </summary>
    private static bool TryReadHidId(string classEntry, out uint bus, out ushort vendor, out ushort product)
    {
        bus = 0;
        vendor = 0;
        product = 0;

        string[] lines;
        try
        {
            lines = File.ReadAllLines(Path.Combine(classEntry, "device", "uevent"));
        }
        catch (Exception)
        {
            return false;
        }

        foreach (var line in lines)
        {
            if (!line.StartsWith("HID_ID=", StringComparison.Ordinal))
            {
                continue;
            }

            var fields = line["HID_ID=".Length..].Split(':');
            if (fields.Length != 3 ||
                !uint.TryParse(fields[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bus) ||
                !uint.TryParse(fields[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rawVendor) ||
                !uint.TryParse(fields[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rawProduct))
            {
                return false;
            }

            vendor = (ushort)rawVendor;
            product = (ushort)rawProduct;
            return true;
        }

        return false;
    }
}
