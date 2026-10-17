// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs;

/// <summary>
/// Key equivalence for caches and comparisons over <em>host</em> filesystem
/// paths. Windows resolves names case-insensitively, but Linux hosts are
/// case-sensitive and the guest filesystem is too, so a dump can legitimately
/// contain "DATA.BIN" alongside "Data.bin". An ignore-case cache aliases those
/// distinct files into one entry there, which silently serves the wrong bytes
/// or drops one of them entirely.
/// </summary>
internal static class HostFsPath
{
    public static readonly StringComparer Comparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static readonly StringComparison Comparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
