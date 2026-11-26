// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SharpEmu.Libs.Pad;

/// <summary>
/// A sleep that ends close to its deadline: a high-resolution waitable timer (Windows 10 1803 and later), which is
/// not rounded to the scheduler tick the way Thread.Sleep and SDL_DelayNS are. Owned by the one thread that calls
/// <see cref="Wait"/>. When the timer cannot be created <see cref="IsAvailable"/> is false and the caller sleeps
/// by other means.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class HighResolutionWait : IDisposable
{
    private const uint CreateWaitableTimerHighResolution = 0x00000002;
    private const uint TimerAllAccess = 0x1F0003;
    private const uint Infinite = 0xFFFFFFFF;

    private nint _timer;

    public HighResolutionWait()
    {
        try
        {
            _timer = CreateWaitableTimerExW(0, null, CreateWaitableTimerHighResolution, TimerAllAccess);
        }
        catch (Exception exception) when (exception is EntryPointNotFoundException or DllNotFoundException)
        {
            _timer = 0;
        }
    }

    public bool IsAvailable => _timer != 0;

    /// <summary>Blocks for about <paramref name="nanoseconds"/>; false when the timer failed (the caller must sleep itself).</summary>
    public bool Wait(long nanoseconds)
    {
        if (_timer == 0)
        {
            return false;
        }

        // Negative due time: relative, in 100 ns units.
        var dueTime = -Math.Max(1, nanoseconds / 100);
        return SetWaitableTimerEx(_timer, ref dueTime, 0, 0, 0, 0, 0) &&
            WaitForSingleObject(_timer, Infinite) == 0;
    }

    public void Dispose()
    {
        if (_timer != 0)
        {
            CloseHandle(_timer);
            _timer = 0;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWaitableTimerExW(nint attributes, string? name, uint flags, uint desiredAccess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetWaitableTimerEx(
        nint timer, ref long dueTime, int period, nint completionRoutine, nint argument, nint wakeContext, uint tolerableDelay);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(nint handle, uint milliseconds);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);
}
