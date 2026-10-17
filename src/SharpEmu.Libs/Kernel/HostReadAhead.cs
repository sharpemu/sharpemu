// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SharpEmu.Libs.Kernel;

/// <summary>
/// Explicit read-ahead for host files the guest streams sequentially.
/// </summary>
/// <remarks>
/// A guest that reads a large archive in small positional chunks issues one
/// syscall per chunk and waits for the device on each. macOS's own read-ahead
/// (F_RDAHEAD, on by default) keys off the file offset, which a positional
/// <c>pread</c> never advances, so a <c>RandomAccess.Read</c> stream looks
/// random to the kernel however sequential it really is. F_RDADVISE states the
/// intent directly: it queues the read asynchronously with no copy to
/// userspace, so the next chunk can come out of the buffer cache.
///
/// .NET exposes no equivalent. FileOptions.RandomAccess and SequentialScan map
/// to posix_fadvise, which macOS does not implement, so they are inert there.
/// Linux does implement it, and a handle opened with FileOptions.RandomAccess
/// (as the AMPR handle cache is) has its kernel read-ahead switched off by
/// POSIX_FADV_RANDOM; POSIX_FADV_WILLNEED on the following range starts that
/// read asynchronously regardless. Windows has no per-range advisory for a
/// handle - the cache manager does its own read-ahead from the handle's read
/// history - so the hint is not issued there.
/// </remarks>
internal static partial class HostReadAhead
{
    // <sys/fcntl.h> (macOS): "Issue an advisory read async with no copy to user".
    private const int FRdAdvise = 44;

    // <fcntl.h> (Linux): the range will be accessed in the near future.
    private const int PosixFadvWillNeed = 3;

    /// <summary>
    /// How far ahead of a continuing read to prefetch. Large enough to cover
    /// several of Unreal's 64 KiB pak reads per hint, small enough that a
    /// stream which turns out not to continue has not displaced much.
    /// </summary>
    internal const int WindowBytes = 1 << 20;

    [StructLayout(LayoutKind.Sequential)]
    private struct RaAdvisory
    {
        public long Offset;
        public int Count;
    }

    [LibraryImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static partial int Fcntl(int descriptor, int command, ref RaAdvisory advisory);

    // Returns the error number directly rather than through errno.
    [LibraryImport("libc", EntryPoint = "posix_fadvise")]
    private static partial int PosixFadvise(int descriptor, long offset, long length, int advice);

    // The hint is advisory, and a refusal can be about one call (an offset
    // the filesystem will not prefetch) rather than about support. Giving up on
    // the first failure would silently drop read-ahead for the whole process,
    // so support is only written off after a run of failures.
    private const int FailuresBeforeGivingUp = 32;
    private static int _consecutiveFailures;
    private static int _unavailable;

    internal static bool Enabled { get; } =
        (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux()) &&
        !string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_DISABLE_READ_AHEAD"),
            "1",
            StringComparison.Ordinal);

    /// <summary>
    /// Hints that <paramref name="count"/> bytes at <paramref name="offset"/>
    /// will be read next. Best-effort: a kernel or filesystem that refuses the
    /// hint just leaves the following read to fetch its own bytes.
    /// </summary>
    internal static void Advise(SafeFileHandle handle, long offset, int count)
    {
        if (!Enabled || count <= 0 || offset < 0 ||
            Volatile.Read(ref _unavailable) != 0 ||
            handle.IsInvalid || handle.IsClosed)
        {
            return;
        }

        try
        {
            // DangerousGetHandle is correct here: the call does not outlive this
            // frame, and the caller owns the handle for the lifetime of its
            // entry in the open-file cache or descriptor table.
            var descriptor = (int)handle.DangerousGetHandle();
            bool accepted;
            if (OperatingSystem.IsMacOS())
            {
                var advisory = new RaAdvisory { Offset = offset, Count = count };
                accepted = Fcntl(descriptor, FRdAdvise, ref advisory) == 0;
            }
            else
            {
                accepted = PosixFadvise(descriptor, offset, count, PosixFadvWillNeed) == 0;
            }

            if (accepted)
            {
                Volatile.Write(ref _consecutiveFailures, 0);
            }
            else if (Interlocked.Increment(ref _consecutiveFailures) >= FailuresBeforeGivingUp)
            {
                Volatile.Write(ref _unavailable, 1);
            }
        }
        catch (Exception exception) when (
            exception is EntryPointNotFoundException or DllNotFoundException)
        {
            Volatile.Write(ref _unavailable, 1);
        }
        catch (ObjectDisposedException)
        {
            // The handle was closed between the check and the call; the read
            // that follows will fail on its own terms.
        }
    }
}
