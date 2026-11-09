// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Diagnostics;

internal static class RegisterCopyTally
{
    public static readonly bool Enabled = VideoOut.RenderPhaseProfile.Enabled;

    private static long _snapshots;
    private static long _contexts;
    private static long _contextParts;
    private static long _unmodeledTables;
    private static long _stages;
    private static long _scalars;
    private static long _userConfigs;

    public static void NoteSnapshot()
    {
        if (Enabled)
        {
            Interlocked.Increment(ref _snapshots);
        }
    }

    public static void NoteContext()
    {
        if (Enabled)
        {
            Interlocked.Increment(ref _contexts);
        }
    }

    public static void NoteContextPart()
    {
        if (Enabled)
        {
            Interlocked.Increment(ref _contextParts);
        }
    }

    public static void NoteUnmodeledTable()
    {
        if (Enabled)
        {
            Interlocked.Increment(ref _unmodeledTables);
        }
    }

    public static void NoteStage()
    {
        if (Enabled)
        {
            Interlocked.Increment(ref _stages);
        }
    }

    public static void NoteScalars()
    {
        if (Enabled)
        {
            Interlocked.Increment(ref _scalars);
        }
    }

    public static void NoteUserConfig()
    {
        if (Enabled)
        {
            Interlocked.Increment(ref _userConfigs);
        }
    }

    public static string TakeReport() =>
        FormattableString.Invariant($"snapshots={Interlocked.Exchange(ref _snapshots, 0)} context_copies={Interlocked.Exchange(ref _contexts, 0)} context_parts={Interlocked.Exchange(ref _contextParts, 0)} ") +
        FormattableString.Invariant($"unmodeled_copies={Interlocked.Exchange(ref _unmodeledTables, 0)} stage_copies={Interlocked.Exchange(ref _stages, 0)} scalar_copies={Interlocked.Exchange(ref _scalars, 0)} uconfig_copies={Interlocked.Exchange(ref _userConfigs, 0)}");
}
