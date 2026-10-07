// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE;

[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
public sealed class SysAbiExportAttribute : Attribute
{
    public string LibraryName { get; set; } = "libKernel";

    public string Nid { get; set; } = string.Empty;

    public string ExportName { get; set; } = string.Empty;

    public Generation Target { get; set; } = Generation.None;

    /// <summary>
    /// Prefer a matching export from a loaded guest module and use this handler only
    /// as the explicit fallback when that LLE provider is unavailable. Individual
    /// handlers define whether that fallback is fail-closed or compatibility behavior.
    /// </summary>
    public bool PreferLle { get; set; }
}
