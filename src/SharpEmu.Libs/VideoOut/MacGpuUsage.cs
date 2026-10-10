// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace SharpEmu.Libs.VideoOut;

// Reads "Device Utilization %" from the IOAccelerator PerformanceStatistics dictionary. This is
// the whole GPU's utilization (macOS has no per-process engine counters), which is what Activity
// Monitor and ioreg report.
internal sealed class MacGpuUsage : IGpuUsage
{
    private const int CFNumberSInt64Type = 4;
    private const uint CFStringEncodingUtf8 = 0x08000100;
    private int _samplePending;
    private double _percent = double.NaN;
    private bool _disposed;

    public double Percent => Volatile.Read(ref _percent);

    public void RequestSample()
    {
        if (!OperatingSystem.IsMacOS() || _disposed || Interlocked.CompareExchange(ref _samplePending, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                Volatile.Write(ref _percent, CollectSample());
            }
            catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
            {
                Volatile.Write(ref _percent, double.NaN);
            }
            finally
            {
                Volatile.Write(ref _samplePending, 0);
            }
        });
    }

    private static double CollectSample()
    {
        var matching = IOServiceMatching("IOAccelerator");
        if (matching == 0 || IOServiceGetMatchingServices(0, matching, out var iterator) != 0)
        {
            return double.NaN;
        }

        var best = double.NaN;
        var statisticsKey = CFStringCreateWithCString(0, "PerformanceStatistics", CFStringEncodingUtf8);
        var utilizationKey = CFStringCreateWithCString(0, "Device Utilization %", CFStringEncodingUtf8);
        try
        {
            uint service;
            while ((service = IOIteratorNext(iterator)) != 0)
            {
                var statistics = IORegistryEntryCreateCFProperty(service, statisticsKey, 0, 0);
                if (statistics != 0)
                {
                    var number = CFDictionaryGetValue(statistics, utilizationKey);
                    if (number != 0 && CFNumberGetValue(number, CFNumberSInt64Type, out long value))
                    {
                        best = double.IsNaN(best) ? value : Math.Max(best, value);
                    }

                    CFRelease(statistics);
                }

                IOObjectRelease(service);
            }
        }
        finally
        {
            CFRelease(statisticsKey);
            CFRelease(utilizationKey);
            IOObjectRelease(iterator);
        }

        return best;
    }

    public void Dispose() => _disposed = true;

    private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [DllImport(IOKit)] private static extern nint IOServiceMatching(string name);
    [DllImport(IOKit)] private static extern int IOServiceGetMatchingServices(uint mainPort, nint matching, out uint iterator);
    [DllImport(IOKit)] private static extern uint IOIteratorNext(uint iterator);
    [DllImport(IOKit)] private static extern int IOObjectRelease(uint obj);
    [DllImport(IOKit)] private static extern nint IORegistryEntryCreateCFProperty(uint entry, nint key, nint allocator, uint options);
    [DllImport(CoreFoundation)] private static extern nint CFStringCreateWithCString(nint allocator, string text, uint encoding);
    [DllImport(CoreFoundation)] private static extern nint CFDictionaryGetValue(nint dictionary, nint key);
    [DllImport(CoreFoundation)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CFNumberGetValue(nint number, int type, out long value);
    [DllImport(CoreFoundation)] private static extern void CFRelease(nint obj);
}

internal interface IGpuUsage : IDisposable
{
    double Percent { get; }

    void RequestSample();
}
