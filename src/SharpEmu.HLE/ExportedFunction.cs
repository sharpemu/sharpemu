// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE;

public sealed class ExportedFunction
{
    public ExportedFunction(
        string libraryName,
        string nid,
        string name,
        Generation target,
        SysAbiFunction function,
        bool preferLle = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryName);
        ArgumentException.ThrowIfNullOrWhiteSpace(nid);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(function);

        LibraryName = libraryName;
        Nid = nid;
        Name = name;
        Target = target;
        Function = function;
        PreferLle = preferLle;
    }

    public string LibraryName { get; }

    public string Nid { get; }

    public string Name { get; }

    public Generation Target { get; }

    public SysAbiFunction Function { get; }

    /// <summary>
    /// A loaded guest export is authoritative for this registration. The HLE function
    /// remains available as an explicit fallback when no usable guest target exists.
    /// </summary>
    public bool PreferLle { get; }
}
