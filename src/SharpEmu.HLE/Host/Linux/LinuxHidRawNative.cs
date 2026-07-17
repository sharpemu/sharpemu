// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SharpEmu.HLE.Host.Linux;

/// <summary>
/// Minimal Linux hidraw interop used to talk to a DualSense controller
/// directly, without any external input library. hidraw hands back the raw
/// HID reports the device sent, byte for byte, so the same report parsing
/// serves every platform.
/// </summary>
[SupportedOSPlatform("linux")]
internal static partial class LinuxHidRawNative
{
    internal const int O_RDONLY = 0x0000;
    internal const int O_RDWR = 0x0002;
    internal const int O_CLOEXEC = 0x80000; // keep the fd out of the emulator child

    internal const int EACCES = 13;
    internal const int ENOENT = 2;

    // linux/hidraw.h bus types (from linux/input.h).
    internal const uint BUS_USB = 0x03;
    internal const uint BUS_BLUETOOTH = 0x05;

    /// <summary>linux/hidraw.h: struct hidraw_devinfo.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct HidrawDevinfo
    {
        public uint BusType;
        public short Vendor;
        public short Product;
    }

    // asm-generic/ioctl.h encoding: dir[31:30] size[29:16] type[15:8] nr[7:0].
    private const uint IocRead = 2u;
    private const uint IocWrite = 1u;
    private const uint HidIocMagic = 'H';

    private static uint Ioc(uint dir, uint type, uint nr, uint size) =>
        (dir << 30) | (size << 16) | (type << 8) | nr;

    /// <summary>HIDIOCGRAWINFO: _IOR('H', 0x03, struct hidraw_devinfo).</summary>
    internal static uint HidIocGRawInfo { get; } =
        Ioc(IocRead, HidIocMagic, 0x03, (uint)Marshal.SizeOf<HidrawDevinfo>());

    /// <summary>HIDIOCGFEATURE(len): _IOC(_IOC_WRITE|_IOC_READ, 'H', 0x07, len).</summary>
    internal static uint HidIocGFeature(int length) =>
        Ioc(IocRead | IocWrite, HidIocMagic, 0x07, (uint)length);

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    internal static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    internal static partial int Close(int fd);

    [LibraryImport("libc", EntryPoint = "read", SetLastError = true)]
    internal static partial nint Read(int fd, ref byte buffer, nuint count);

    [LibraryImport("libc", EntryPoint = "write", SetLastError = true)]
    internal static partial nint Write(int fd, ref byte buffer, nuint count);

    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    internal static partial int Ioctl(int fd, nuint request, ref HidrawDevinfo argument);

    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    internal static partial int Ioctl(int fd, nuint request, ref byte argument);

    /// <summary>Device nodes hidraw exposes, in a stable order.</summary>
    internal static IEnumerable<string> EnumerateHidRawPaths()
    {
        string[] paths;
        try
        {
            paths = Directory.GetFiles("/dev", "hidraw*");
        }
        catch (Exception)
        {
            return Array.Empty<string>(); // no /dev, no devices
        }

        Array.Sort(paths, StringComparer.Ordinal);
        return paths;
    }
}
