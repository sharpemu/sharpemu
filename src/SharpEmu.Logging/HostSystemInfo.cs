// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace SharpEmu.Logging;

/// <summary>Provides best-effort information about the host system.</summary>
public static class HostSystemInfo
{
    private static readonly Lazy<string> CpuNameValue = new(GetCpuName);
    private static readonly Lazy<string> GpuNameValue = new(GetPreferredGpuName);
    private static readonly Lazy<string> MemoryDescriptionValue = new(GetMemoryDescription);

    /// <summary>Host CPU name, or a safe fallback when it cannot be determined.</summary>
    public static string CpuName => CpuNameValue.Value;

    /// <summary>Preferred physical GPU name, or a safe fallback when it cannot be determined.</summary>
    public static string GpuName => GpuNameValue.Value;

    /// <summary>Returns a concise description of the host hardware for diagnostic logs.</summary>
    public static string Summary =>
        $"Host hardware: CPU: {CpuName}; GPU: {GpuName}; RAM: {MemoryDescriptionValue.Value}.";

    private static string GetCpuName()
    {
        if (OperatingSystem.IsMacOS())
        {
            var brand = GetSysctlString("machdep.cpu.brand_string");
            if (!string.IsNullOrWhiteSpace(brand))
            {
                return $"{brand} ({Environment.ProcessorCount} logical processors)";
            }
        }

        if (!OperatingSystem.IsWindows())
        {
            return $"{Environment.ProcessorCount} logical processors";
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            if (key?.GetValue("ProcessorNameString") is string name && !string.IsNullOrWhiteSpace(name))
            {
                name = Regex.Replace(
                    name.Trim(),
                    @"\s+\d+-Core Processor$",
                    string.Empty,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                return Regex.Replace(
                    name,
                    @"\s+with Radeon Graphics$",
                    string.Empty,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
        }
        catch (Exception)
        {
            // Hardware information is diagnostic only.
        }

        return $"{Environment.ProcessorCount} logical processors";
    }

    private static string GetPreferredGpuName()
    {
        if (OperatingSystem.IsMacOS())
        {
            return GetMetalDeviceName() ?? "unknown";
        }

        if (!OperatingSystem.IsWindows())
        {
            return "unknown";
        }

        try
        {
            var preferredName = "unknown";
            var preferredScore = int.MinValue;
            for (uint index = 0; ; index++)
            {
                var device = new DisplayDevice
                {
                    cb = Marshal.SizeOf<DisplayDevice>(),
                };

                if (!EnumDisplayDevices(null, index, ref device, 0))
                {
                    break;
                }

                var name = device.DeviceString?.Trim();
                var score = ScoreGpu(name);
                if (score > preferredScore)
                {
                    preferredName = name!;
                    preferredScore = score;
                }
            }

            return preferredScore > 0 ? preferredName : "unknown";
        }
        catch (Exception)
        {
            // Hardware information is diagnostic only.
            return "unknown";
        }
    }

    private static int ScoreGpu(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            name.Contains("virtual", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("remote", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("basic display", StringComparison.OrdinalIgnoreCase))
        {
            return -1;
        }

        if (name.Contains("nvidia", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("geforce", StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }

        if (name.Contains("amd", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("radeon", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        return 1;
    }

    private static string GetMemoryDescription()
    {
        // Reports the cgroup/job-object limit when the process is constrained,
        // which is the figure that actually bounds the emulator.
        var totalBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (totalBytes <= 0)
        {
            return "unknown";
        }

        var megabytes = totalBytes / (1024 * 1024);
        var gigabytes = totalBytes / (1024d * 1024 * 1024);
        return $"{megabytes:N0} MB ({gigabytes:N1} GB)";
    }

    /// <summary>
    /// Names the system default Metal device, or null when unavailable.
    /// </summary>
    /// <remarks>
    /// Metal is the only version-stable way to name an Apple GPU: no sysctl
    /// exposes it, and the Vulkan presenter that also knows the name lives
    /// downstream of this assembly and initializes long after the startup
    /// banner is emitted. Like the Windows path, this reports the host's
    /// preferred GPU, which on a multi-GPU Mac need not be the device the
    /// presenter later selects; that one is logged separately as
    /// "Vulkan device: ...".
    /// </remarks>
    private static string? GetMetalDeviceName()
    {
        try
        {
            var device = MTLCreateSystemDefaultDevice();
            if (device == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var name = objc_msgSend(device, sel_registerName("name"));
                if (name == IntPtr.Zero)
                {
                    return null;
                }

                var utf8 = objc_msgSend(name, sel_registerName("UTF8String"));
                return utf8 == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(utf8);
            }
            finally
            {
                // MTLCreateSystemDefaultDevice returns a +1 retained device.
                objc_msgSend(device, sel_registerName("release"));
            }
        }
        catch (Exception)
        {
            // Hardware information is diagnostic only.
            return null;
        }
    }

    /// <summary>Reads a string sysctl by name, or null when unavailable.</summary>
    private static string? GetSysctlString(string name)
    {
        try
        {
            nuint length = 0;
            if (sysctlbyname(name, IntPtr.Zero, ref length, IntPtr.Zero, 0) != 0 || length == 0)
            {
                return null;
            }

            var buffer = Marshal.AllocHGlobal((int)length);
            try
            {
                if (sysctlbyname(name, buffer, ref length, IntPtr.Zero, 0) != 0)
                {
                    return null;
                }

                // length counts the trailing NUL, which PtrToStringUTF8 must not include.
                return Marshal.PtrToStringUTF8(buffer, (int)length - 1);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception)
        {
            // Hardware information is diagnostic only.
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string? DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string? DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string? DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string? DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(string? deviceName, uint deviceNum, ref DisplayDevice displayDevice, uint flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int sysctlbyname(string name, IntPtr oldp, ref nuint oldlenp, IntPtr newp, nuint newlen);

    [DllImport("/System/Library/Frameworks/Metal.framework/Metal")]
    private static extern IntPtr MTLCreateSystemDefaultDevice();

    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern IntPtr sel_registerName(string name);

    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);
}
