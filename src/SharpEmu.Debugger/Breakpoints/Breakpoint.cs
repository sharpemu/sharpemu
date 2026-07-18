// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Debugger.Breakpoints;

/// <summary>
/// A single breakpoint or watchpoint. Instances are immutable; the owning
/// <see cref="BreakpointStore"/> replaces an entry to change its enabled state.
/// </summary>
public sealed class Breakpoint
{
    public Breakpoint(int id, BreakpointKind kind, ulong address, ulong length = 1, bool enabled = true)
    {
        if (length == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "Breakpoint length must be at least one byte.");
        }

        Id = id;
        Kind = kind;
        Address = address;
        Length = length;
        Enabled = enabled;
    }

    /// <summary>The store-assigned identifier used by clients to reference it.</summary>
    public int Id { get; }

    public BreakpointKind Kind { get; }

    /// <summary>The first guest address the breakpoint covers.</summary>
    public ulong Address { get; }

    /// <summary>
    /// The number of bytes the breakpoint covers. Always one for
    /// <see cref="BreakpointKind.Execute"/>; the watch kinds may span a range.
    /// </summary>
    public ulong Length { get; }

    public bool Enabled { get; }

    /// <summary>True when <paramref name="address"/> falls within this breakpoint.</summary>
    public bool Covers(ulong address) => address >= Address && address < Address + Length;

    /// <summary>Returns a copy with a different enabled state.</summary>
    public Breakpoint WithEnabled(bool enabled)
        => enabled == Enabled ? this : new Breakpoint(Id, Kind, Address, Length, enabled);
}
