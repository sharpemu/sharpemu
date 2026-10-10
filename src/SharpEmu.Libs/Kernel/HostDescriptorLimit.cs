// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace SharpEmu.Libs.Kernel;

/// <summary>
/// The process's open-file-descriptor budget, for caches that hold host handles.
/// </summary>
/// <remarks>
/// Any cache of open files has to be bounded, because running out of
/// descriptors turns every later open into an IOException that the guest sees
/// as a missing file. Bounding it by a hardcoded guess is the problem this
/// solves: the real limit differs by an order of magnitude between a shell
/// launch and a bundle launch, so a cap chosen for the small case throws away
/// almost all of a title's working set on the large one.
/// </remarks>
internal static partial class HostDescriptorLimit
{
    private const int RlimitNofileMac = 8;
    private const int RlimitNofileLinux = 7;

    [StructLayout(LayoutKind.Sequential)]
    private struct RLimit
    {
        public ulong Current;
        public ulong Maximum;
    }

    [LibraryImport("libc", EntryPoint = "getrlimit", SetLastError = true)]
    private static partial int GetRLimit(int resource, out RLimit limit);

    /// <summary>
    /// The soft descriptor limit, or 0 when it cannot be read (callers then use
    /// their own conservative default).
    /// </summary>
    internal static long SoftLimit { get; } = ReadSoftLimit();

    private static long ReadSoftLimit()
    {
        if (OperatingSystem.IsWindows())
        {
            return 0;
        }

        try
        {
            var resource = OperatingSystem.IsMacOS() ? RlimitNofileMac : RlimitNofileLinux;
            if (GetRLimit(resource, out var limit) != 0)
            {
                return 0;
            }

            // RLIM_INFINITY is ulong.MaxValue; a cache sized from it would be
            // unbounded, so report "unknown" and let the caller's clamp decide.
            return limit.Current == ulong.MaxValue || limit.Current > long.MaxValue
                ? 0
                : (long)limit.Current;
        }
        catch (Exception exception) when (
            exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return 0;
        }
    }
}
