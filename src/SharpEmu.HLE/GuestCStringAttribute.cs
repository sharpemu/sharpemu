// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE;

/// <summary>
/// Marks a string parameter of a [SysAbiExport] handler as a guest null-terminated
/// UTF-8 pointer. The generated thunk reads the string from the argument register's
/// address (bounded by <see cref="MaxLength"/>) before invoking the handler, and
/// returns ORBIS_GEN2_ERROR_MEMORY_FAULT to the guest when the read fails — including
/// a null pointer, matching TryReadNullTerminatedUtf8's contract.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter, Inherited = false, AllowMultiple = false)]
public sealed class GuestCStringAttribute : Attribute
{
    public GuestCStringAttribute(int maxLength) => MaxLength = maxLength;

    /// <summary>Upper bound in bytes for the guest read, terminator included.</summary>
    public int MaxLength { get; }
}
