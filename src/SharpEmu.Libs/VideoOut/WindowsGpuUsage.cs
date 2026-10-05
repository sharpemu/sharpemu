// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace SharpEmu.Libs.VideoOut;

internal sealed class WindowsGpuUsage : IDisposable
{
    private const uint FormatDouble = 0x200;
    private const uint MoreData = 0x800007D2;
    private readonly object _gate = new();
    private nint _query;
    private nint _counter;
    private bool _disposed;
    private int _samplePending;
    private double _percent = double.NaN;

    public double Percent => Volatile.Read(ref _percent);

    public void RequestSample()
    {
        if (!OperatingSystem.IsWindows() || Interlocked.CompareExchange(ref _samplePending, 1, 0) != 0)
        {
            return;
        }

        // Counter collection can block. Keep it off the presentation thread.
        _ = Task.Run(() =>
        {
            try
            {
                lock (_gate)
                {
                    if (!_disposed)
                    {
                        Volatile.Write(ref _percent, CollectSample());
                    }
                }
            }
            catch (DllNotFoundException)
            {
                Volatile.Write(ref _percent, double.NaN);
            }
            catch (EntryPointNotFoundException)
            {
                Volatile.Write(ref _percent, double.NaN);
            }
            finally
            {
                Volatile.Write(ref _samplePending, 0);
            }
        });
    }

    private double CollectSample()
    {
        if (_query == 0)
        {
            if (PdhOpenQueryW(null, 0, out _query) != 0)
            {
                return double.NaN;
            }

            var path = $@"\GPU Engine(pid_{Environment.ProcessId}_*)\Utilization Percentage";
            if (PdhAddEnglishCounterW(_query, path, 0, out _counter) != 0)
            {
                PdhCloseQuery(_query);
                _query = 0;
                return double.NaN;
            }

            // Rate counters need two samples before they can report usage.
            PdhCollectQueryData(_query);
            return double.NaN;
        }

        if (PdhCollectQueryData(_query) != 0)
        {
            return double.NaN;
        }

        uint bufferBytes = 0;
        if (PdhGetFormattedCounterArrayW(_counter, FormatDouble, ref bufferBytes, out _, 0) != MoreData ||
            bufferBytes == 0 || bufferBytes > int.MaxValue)
        {
            return double.NaN;
        }

        var buffer = Marshal.AllocHGlobal((int)bufferBytes);
        try
        {
            if (PdhGetFormattedCounterArrayW(_counter, FormatDouble, ref bufferBytes, out var count, buffer) != 0)
            {
                return double.NaN;
            }

            var itemBytes = Marshal.SizeOf<CounterItem>();
            if ((ulong)count * (uint)itemBytes > bufferBytes)
            {
                return double.NaN;
            }

            var percent = double.NaN;
            for (var index = 0; index < count; index++)
            {
                var item = Marshal.PtrToStructure<CounterItem>(buffer + checked(index * itemBytes));
                percent = IncludeEngine(percent, item.Value.Status, item.Value.Percent);
            }

            return percent;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static double IncludeEngine(double current, uint status, double value)
    {
        if (status > 1 || !double.IsFinite(value) || value < 0)
        {
            return current;
        }

        // The busiest engine represents process usage; parallel engines do not add up.
        return Math.Max(double.IsNaN(current) ? 0 : current, Math.Min(100, value));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            if (_query != 0)
            {
                PdhCloseQuery(_query);
                _query = 0;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CounterValue
    {
        public uint Status;
        public double Percent;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CounterItem
    {
        public nint Name;
        public CounterValue Value;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhOpenQueryW(string? dataSource, nuint userData, out nint query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhAddEnglishCounterW(nint query, string path, nuint userData, out nint counter);

    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhCollectQueryData(nint query);

    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhGetFormattedCounterArrayW(
        nint counter, uint format, ref uint bufferBytes, out uint count, nint buffer);

    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhCloseQuery(nint query);
}
