// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SharpEmu.Libs;

internal static unsafe partial class HostHighResolutionSleep
{
    private const long SpinLimit100ns = 500;
    private const long SpinLimitNanoseconds = 50_000;
    private const uint CreateWaitableTimerManualReset = 0x1;
    private const uint CreateWaitableTimerHighResolution = 0x2;
    private const uint TimerAllAccess = 0x1F0003;
    private const uint Infinite = 0xFFFFFFFF;
    private const int ClockMonotonic = 1;
    private const int TimerAbsoluteTime = 1;
    private const int ErrorInterrupted = 4;
    private const long NanosecondsPerSecond = 1_000_000_000;

    [ThreadStatic]
    private static nint _timer;

    [ThreadStatic]
    private static bool _timerInitialized;

    private static int _posixClockState;

    // Spins for the time asked, with pause hints: for a short wait the host timer would round up to its period.
    public static void SpinMicroseconds(ulong microseconds)
    {
        var ticks = (long)Math.Min(microseconds, 1_000_000UL) * Stopwatch.Frequency / 1_000_000;
        var deadline = Stopwatch.GetTimestamp() + Math.Max(ticks, 1);
        do
        {
            Thread.SpinWait(8);
        }
        while (Stopwatch.GetTimestamp() < deadline);
    }

    public static void SleepMicroseconds(ulong microseconds) =>
        Sleep100ns(microseconds > long.MaxValue / 10 ? long.MaxValue : (long)microseconds * 10, allowSpinning: true);

    private static void Sleep100ns(long units, bool allowSpinning)
    {
        if (units <= 0)
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            SleepWindows(units, allowSpinning);
            return;
        }

        if (OperatingSystem.IsLinux() && TrySleepPosix(units * 100, allowSpinning))
        {
            return;
        }

        if (OperatingSystem.IsMacOS() && TryNanosleep(units * 100))
        {
            return;
        }

        SleepFallback(units);
    }

    private static bool TryNanosleep(long nanoseconds)
    {
        if (Volatile.Read(ref _posixClockState) < 0)
        {
            return false;
        }

        var request = new Timespec
        {
            Seconds = nanoseconds / NanosecondsPerSecond,
            Nanoseconds = nanoseconds % NanosecondsPerSecond,
        };
        Timespec remaining;
        try
        {
            while (Nanosleep(&request, &remaining) != 0)
            {
                if (Marshal.GetLastPInvokeError() != ErrorInterrupted)
                {
                    break;
                }

                request = remaining;
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            Volatile.Write(ref _posixClockState, -1);
            return false;
        }

        return true;
    }

    private static void SleepWindows(long units, bool allowSpinning)
    {
        if (allowSpinning && units <= SpinLimit100ns)
        {
            var deadline = Stopwatch.GetTimestamp() + Math.Max(units * Stopwatch.Frequency / 10_000_000, 1);
            do
            {
                Thread.SpinWait(1);
            }
            while (Stopwatch.GetTimestamp() < deadline);
            return;
        }

        var timer = ThreadTimer();
        if (timer != 0)
        {
            var dueTime = -units;
            if (SetWaitableTimerEx(timer, &dueTime, 0, 0, 0, 0, 0) != 0)
            {
                _ = WaitForSingleObject(timer, Infinite);
                return;
            }
        }

        SleepFallback(units);
    }

    private static nint ThreadTimer()
    {
        if (_timerInitialized)
        {
            return _timer;
        }

        _timerInitialized = true;
        var timer = CreateWaitableTimerExW(0, 0, CreateWaitableTimerHighResolution, TimerAllAccess);
        if (timer == 0)
        {
            timer = CreateWaitableTimerExW(0, 0, CreateWaitableTimerManualReset, TimerAllAccess);
        }

        _timer = timer;
        return timer;
    }

    private static bool TrySleepPosix(long nanoseconds, bool allowSpinning)
    {
        if (Volatile.Read(ref _posixClockState) < 0)
        {
            return false;
        }

        Timespec deadline;
        try
        {
            if (ClockGetTime(ClockMonotonic, &deadline) != 0)
            {
                return false;
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            Volatile.Write(ref _posixClockState, -1);
            return false;
        }

        var target = deadline.Nanoseconds + nanoseconds % NanosecondsPerSecond;
        deadline.Seconds += nanoseconds / NanosecondsPerSecond + target / NanosecondsPerSecond;
        deadline.Nanoseconds = target % NanosecondsPerSecond;

        if (allowSpinning && nanoseconds <= SpinLimitNanoseconds)
        {
            Timespec now;
            do
            {
                if (ClockGetTime(ClockMonotonic, &now) != 0)
                {
                    return true;
                }
            }
            while (now.Seconds < deadline.Seconds ||
                (now.Seconds == deadline.Seconds && now.Nanoseconds < deadline.Nanoseconds));
            return true;
        }

        while (ClockNanosleep(ClockMonotonic, TimerAbsoluteTime, &deadline, null) == ErrorInterrupted)
        {
        }

        return true;
    }

    private static void SleepFallback(long units)
    {
        var milliseconds = units / 10_000 + (units % 10_000 == 0 ? 0 : 1);
        Thread.Sleep((int)Math.Min(milliseconds, int.MaxValue));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Timespec
    {
        public long Seconds;
        public long Nanoseconds;
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint CreateWaitableTimerExW(nint attributes, nint name, uint flags, uint desiredAccess);

    [LibraryImport("kernel32.dll")]
    private static partial int SetWaitableTimerEx(
        nint timer,
        long* dueTime,
        int period,
        nint completionRoutine,
        nint argument,
        nint wakeContext,
        uint tolerableDelay);

    [LibraryImport("kernel32.dll")]
    private static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("libc", EntryPoint = "clock_gettime")]
    private static partial int ClockGetTime(int clock, Timespec* time);

    [LibraryImport("libc", EntryPoint = "clock_nanosleep")]
    private static partial int ClockNanosleep(int clock, int flags, Timespec* request, Timespec* remaining);

    [LibraryImport("libc", EntryPoint = "nanosleep", SetLastError = true)]
    private static partial int Nanosleep(Timespec* request, Timespec* remaining);
}
