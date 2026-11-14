// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.Diagnostics;

/// <summary>
/// The guest import being served on this host thread and the guest address it returns to. Only kept while
/// SHARPEMU_LOG_CPU_WRITE_READBACKS=1, so a diagnostic that fires deep inside memory code can name the
/// guest caller without any cost for a normal run.
/// </summary>
internal static class GuestImportCallSite
{
    internal static readonly bool Enabled =
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_CPU_WRITE_READBACKS"), "1", StringComparison.Ordinal);

    [ThreadStatic] private static ulong _returnAddress;
    [ThreadStatic] private static string? _name;

    internal static void Enter(ulong returnAddress, string? name)
    {
        _returnAddress = returnAddress;
        _name = name;
    }

    internal static ulong ReturnAddress => _returnAddress;

    internal static string? Name => _name;
}
