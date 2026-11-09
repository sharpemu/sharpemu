// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.GpuMemory;

public enum ProtectionCause
{
    CpuWriteRelease,
    EmulatorWrite,
    UploadReprotect,
    SweepReprotect,
    GpuWriteBlock,
    GpuReadRelease,
    MappingReapply,
    Untrack,
    Count,
}

public static class ProtectionCauseCounts
{
    private static readonly string[] Names =
    [
        "cpu_write_release",
        "emulator_write",
        "upload_reprotect",
        "sweep_reprotect",
        "gpu_write_block",
        "gpu_read_release",
        "mapping_reapply",
        "untrack",
    ];

    private static readonly long[] Calls = new long[(int)ProtectionCause.Count];
    private static readonly long[] Pages = new long[(int)ProtectionCause.Count];

    internal static void Initialize() => _ = Calls.Length + Pages.Length;

    internal static void Record(ProtectionCause cause, ulong bytes)
    {
        Interlocked.Increment(ref Calls[(int)cause]);
        Interlocked.Add(ref Pages[(int)cause], (long)(bytes / TrackerLayout.PageBytes));
    }

    public static long CallsOf(ProtectionCause cause) => Volatile.Read(ref Calls[(int)cause]);

    public static long PagesOf(ProtectionCause cause) => Volatile.Read(ref Pages[(int)cause]);

    public static string TakeReport()
    {
        var report = string.Empty;
        for (var cause = 0; cause < (int)ProtectionCause.Count; cause++)
        {
            var calls = Interlocked.Exchange(ref Calls[cause], 0);
            var pages = Interlocked.Exchange(ref Pages[cause], 0);
            report += FormattableString.Invariant($"{(cause == 0 ? "" : " ")}protect_{Names[cause]}={calls}/{pages}");
        }

        return report;
    }
}
