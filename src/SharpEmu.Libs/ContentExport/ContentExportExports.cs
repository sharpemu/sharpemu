// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.ContentExport;

/// <summary>
/// Minimal libSceContentExport surface: Cult of the Lamb initializes the PSN
/// content export API at boot and terminates it right after. No content is
/// ever exported, so init/term only track state and report success.
/// </summary>
public static class ContentExportExports
{
    private static int _initialized;

    [SysAbiExport(
        Nid = "0GnN4QCgIfs",
        ExportName = "sceContentExportInit2",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceContentExport")]
    public static int ContentExportInit2(CpuContext ctx)
    {
        Interlocked.Exchange(ref _initialized, 1);
        TraceContentExport("init2");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "+KDWny9Y-6k",
        ExportName = "sceContentExportTerm",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceContentExport")]
    public static int ContentExportTerm(CpuContext ctx)
    {
        var wasInitialized = Interlocked.Exchange(ref _initialized, 0) != 0;
        TraceContentExport(wasInitialized ? "term" : "term while not initialized");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    private static void TraceContentExport(string message)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_SHARE"), "1", StringComparison.Ordinal))
        {
            return;
        }

        Console.Error.WriteLine($"[LOADER][TRACE] content_export.{message}");
    }
}
