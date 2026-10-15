// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace SharpEmu.HLE.Host;

/// <summary>
/// The scheduling class a thread asks the host OS for. Named after Darwin's
/// <c>qos_class_t</c>, but the concept — "how much does latency matter on this
/// thread" — is portable: each host maps it onto its own scheduler control (see
/// <see cref="HostThreadQualityOfService"/>).
/// </summary>
public enum HostThreadQos
{
    /// <summary>Frame-deadline work: presenter/render, GPU submission, the guest's
    /// own main thread, frame pacing. Gets the shortest scheduling latency and, on
    /// macOS, is the class that exempts a thread from timer coalescing.</summary>
    UserInteractive,

    /// <summary>Work the user is waiting on but that has no frame deadline: image
    /// loading, shader and SRT compilation.</summary>
    UserInitiated,

    /// <summary>Whatever the runtime picked. Used to say "deliberately unset".</summary>
    Default,

    /// <summary>Long-running background work that nobody is waiting on.</summary>
    Utility,

    /// <summary>Work that may be deferred indefinitely in favour of anything else.</summary>
    Background,
}

/// <summary>
/// Applies a quality-of-service class to the calling thread.
///
/// Per host:
/// <list type="bullet">
/// <item><b>macOS</b>: <c>pthread_set_qos_class_self_np</c>. Threads created by the
/// CoreCLR thread pool and by <c>new Thread(...)</c> otherwise run at
/// <c>QOS_CLASS_DEFAULT</c> at best, and anything below
/// <c>QOS_CLASS_USER_INTERACTIVE</c> is subject to timer coalescing and to being
/// descheduled behind unrelated user-interactive work. On Apple silicon the class
/// also steers a thread towards the P or E cores. <see cref="Thread.Priority"/> is
/// not an alternative there: CoreCLR's Unix PAL does not implement it. Darwin
/// refuses the call (EPERM) on threads CoreCLR created, which is why
/// <see cref="HostQosThread"/> exists.</item>
/// <item><b>Windows</b>: <c>SetThreadPriority</c> on the calling thread —
/// UserInteractive → HIGHEST, UserInitiated → ABOVE_NORMAL, Default → NORMAL,
/// Utility → BELOW_NORMAL, Background → LOWEST. TIME_CRITICAL is never used.</item>
/// <item><b>Linux</b>: the calling thread's own nice value
/// (<c>setpriority(PRIO_PROCESS, gettid(), n)</c>), offset from the process's base
/// nice: UserInteractive −10, UserInitiated −5, Default 0, Utility +5,
/// Background +10. Lowering always works; raising needs CAP_SYS_NICE or an
/// RLIMIT_NICE allowance, and without one the request is logged once and
/// otherwise ignored.</item>
/// </list>
/// </summary>
/// <remarks>
/// Every host here can only (portably) set the calling thread's class, so every
/// call site is inside the thread body it is describing, not next to the
/// <c>new Thread(...)</c> that created it. Scheduling hints are an optimisation:
/// nothing here throws, and no caller needs to handle a failure.
/// </remarks>
public static partial class HostThreadQualityOfService
{
    // qos_class_t, from <sys/qos.h>.
    private const uint QosClassUserInteractive = 0x21;
    private const uint QosClassUserInitiated = 0x19;
    private const uint QosClassDefault = 0x15;
    private const uint QosClassUtility = 0x11;
    private const uint QosClassBackground = 0x09;

    // SetThreadPriority levels, from <processthreadsapi.h>.
    private const int ThreadPriorityLowest = -2;
    private const int ThreadPriorityBelowNormal = -1;
    private const int ThreadPriorityNormal = 0;
    private const int ThreadPriorityAboveNormal = 1;
    private const int ThreadPriorityHighest = 2;

    private const int PrioProcess = 0;
    private const int Eperm = 1;
    private const int Eacces = 13;

    private static int _linuxRaiseDeniedLogged;

    /// <summary>
    /// Asks the host to schedule the calling thread at <paramref name="qos"/>.
    /// Returns true when the class was applied. False means the host rejected the
    /// request or has no such control; either way the thread keeps running at
    /// whatever class it had, so no caller needs to handle the failure.
    /// </summary>
    public static bool TryApplyToCurrentThread(HostThreadQos qos) => TryApplyToCurrentThread(qos, out _);

    /// <summary>As above; <paramref name="errno"/> is the host error code on failure (-1: no host support).</summary>
    public static bool TryApplyToCurrentThread(HostThreadQos qos, out int errno)
    {
        errno = -1;
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                // relative_priority is 0: the only legal values are 0 and negative
                // offsets within the class, and nothing here wants to sit below its
                // own class's floor.
                errno = pthread_set_qos_class_self_np(ToDarwinClass(qos), 0);
                return errno == 0;
            }

            if (OperatingSystem.IsWindows())
            {
                if (SetThreadPriority(GetCurrentThread(), ToWindowsPriority(qos)))
                {
                    errno = 0;
                    return true;
                }

                errno = Marshal.GetLastPInvokeError();
                return false;
            }

            if (OperatingSystem.IsLinux())
            {
                return TryApplyLinuxNice(qos, out errno);
            }
        }
        catch (Exception)
        {
            // An older or unusual C library without one of these entry points.
            // Never let a scheduling hint abort a thread's startup.
        }

        return false;
    }

    /// <summary>
    /// Linux threads inherit their creator's nice value, so a thread spawned from a
    /// raised frame-deadline thread would otherwise run at that boost. Puts the
    /// calling thread back at the process's base nice value when it inherited a
    /// higher priority (lowering never needs a privilege). No-op elsewhere: a
    /// Windows thread never inherits its creator's priority, and on macOS a guest
    /// thread deliberately keeps the class it inherited.
    /// </summary>
    public static void ReleaseInheritedPriority()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        try
        {
            var baseNice = LinuxBaseNice.Value;
            var tid = LinuxGetTid();
            Marshal.SetLastPInvokeError(0);
            var current = getpriority(PrioProcess, tid);
            if (Marshal.GetLastPInvokeError() == 0 && current < baseNice)
            {
                _ = setpriority(PrioProcess, tid, baseNice);
            }
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// The calling thread's host scheduling class as the host reports it
    /// (diagnostics only): <c>qos=0x..</c> on macOS, <c>priority=N</c> on Windows,
    /// <c>nice=N</c> on Linux.
    /// </summary>
    public static string DescribeCurrentThread()
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                return $"qos=0x{qos_class_self():X2}";
            }

            if (OperatingSystem.IsWindows())
            {
                return $"priority={GetThreadPriority(GetCurrentThread())}";
            }

            if (OperatingSystem.IsLinux())
            {
                Marshal.SetLastPInvokeError(0);
                var nice = getpriority(PrioProcess, LinuxGetTid());
                return Marshal.GetLastPInvokeError() == 0 ? $"nice={nice}" : "nice=?";
            }
        }
        catch (Exception)
        {
        }

        return "n/a";
    }

    /// <summary>True when the calling thread is scheduled at exactly <paramref name="qos"/>.</summary>
    public static bool IsCurrentThreadAt(HostThreadQos qos)
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                return qos_class_self() == ToDarwinClass(qos);
            }

            if (OperatingSystem.IsWindows())
            {
                return GetThreadPriority(GetCurrentThread()) == ToWindowsPriority(qos);
            }

            if (OperatingSystem.IsLinux())
            {
                Marshal.SetLastPInvokeError(0);
                var nice = getpriority(PrioProcess, LinuxGetTid());
                return Marshal.GetLastPInvokeError() == 0 && nice == ToLinuxNice(qos);
            }
        }
        catch (Exception)
        {
        }

        return false;
    }

    internal static uint ToDarwinClass(HostThreadQos qos) => qos switch
    {
        HostThreadQos.UserInteractive => QosClassUserInteractive,
        HostThreadQos.UserInitiated => QosClassUserInitiated,
        HostThreadQos.Utility => QosClassUtility,
        HostThreadQos.Background => QosClassBackground,
        _ => QosClassDefault,
    };

    private static int ToWindowsPriority(HostThreadQos qos) => qos switch
    {
        HostThreadQos.UserInteractive => ThreadPriorityHighest,
        HostThreadQos.UserInitiated => ThreadPriorityAboveNormal,
        HostThreadQos.Utility => ThreadPriorityBelowNormal,
        HostThreadQos.Background => ThreadPriorityLowest,
        _ => ThreadPriorityNormal,
    };

    // Offsets from the process's base nice, so a user who launched the emulator
    // under `nice` keeps that bias on every thread.
    private static int ToLinuxNice(HostThreadQos qos)
    {
        var offset = qos switch
        {
            HostThreadQos.UserInteractive => -10,
            HostThreadQos.UserInitiated => -5,
            HostThreadQos.Utility => 5,
            HostThreadQos.Background => 10,
            _ => 0,
        };
        return Math.Clamp(LinuxBaseNice.Value + offset, -20, 19);
    }

    private static bool TryApplyLinuxNice(HostThreadQos qos, out int errno)
    {
        // With PRIO_PROCESS and a thread id, Linux sets that one thread's nice value
        // rather than the whole process's.
        if (setpriority(PrioProcess, LinuxGetTid(), ToLinuxNice(qos)) == 0)
        {
            errno = 0;
            return true;
        }

        errno = Marshal.GetLastPInvokeError();
        if ((errno == Eperm || errno == Eacces) &&
            Interlocked.Exchange(ref _linuxRaiseDeniedLogged, 1) == 0)
        {
            Console.Error.WriteLine(
                $"[HOST][QOS] Raising a thread's priority (class {qos}) needs CAP_SYS_NICE or an RLIMIT_NICE " +
                "allowance; frame-deadline threads keep the default nice value.");
        }

        return false;
    }

    private static int LinuxGetTid()
    {
        try
        {
            return gettid();
        }
        catch (EntryPointNotFoundException)
        {
            // glibc before 2.30 has no gettid wrapper.
            return (int)syscall(RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 178 : 186);
        }
    }

    // The process's nice value as the main thread (whose tid is the pid) carries it.
    // Every path here reads this before its first setpriority, so it is captured
    // before any thread is reprioritised: the bias the user launched with.
    private static class LinuxBaseNice
    {
        public static readonly int Value = Read();

        private static int Read()
        {
            try
            {
                Marshal.SetLastPInvokeError(0);
                var nice = getpriority(PrioProcess, Environment.ProcessId);
                return Marshal.GetLastPInvokeError() == 0 ? nice : 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }

    [LibraryImport("libSystem.B.dylib")]
    private static partial int pthread_set_qos_class_self_np(uint qosClass, int relativePriority);

    [LibraryImport("libSystem.B.dylib")]
    private static partial uint qos_class_self();

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentThread();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetThreadPriority(nint thread, int priority);

    [LibraryImport("kernel32.dll")]
    private static partial int GetThreadPriority(nint thread);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int setpriority(int which, int who, int prio);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int getpriority(int which, int who);

    [LibraryImport("libc")]
    private static partial int gettid();

    [LibraryImport("libc")]
    private static partial long syscall(long number);
}
