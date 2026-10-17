// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.GpuCommands;

/// <summary>What a suspended submission is waiting for.</summary>
public enum CommandWaitKind : byte
{
    /// <summary>Not known; any wake event has to retry it.</summary>
    Unknown,

    /// <summary>A compare against guest memory: WAIT_REG_MEM, an atomic, a semaphore.</summary>
    GuestMemory,

    /// <summary>A free slot in the presenter's flip queue.</summary>
    FlipSlot,

    /// <summary>A particular display buffer's flip having been presented.</summary>
    FlipDone,

    /// <summary>One of this submission's own engine counters; its own queue advances it.</summary>
    EngineCounter,

    /// <summary>More ring bytes from the guest, which only the guest's CPU writes.</summary>
    RingAppend,
}

/// <summary>
/// Why a submission suspended, so that waking it again can be aimed instead of
/// broadcast. Clearing every queue head's blocked flag on every wake event made each
/// of them re-run a slice, re-read its label and suspend again: Demon's Souls
/// measured 952,889 slices for 371,022 submissions.
/// </summary>
public readonly record struct CommandWaitCondition(
    CommandWaitKind Kind,
    ulong Address = 0,
    ulong Size = 0,
    int Handle = 0,
    int Index = 0)
{
    public static readonly CommandWaitCondition Unknown = new(CommandWaitKind.Unknown);

    public static CommandWaitCondition Memory(ulong address, ulong size) =>
        new(CommandWaitKind.GuestMemory, address, Math.Max(size, 1));

    public static CommandWaitCondition Flip(int handle, int index) =>
        new(CommandWaitKind.FlipDone, Handle: handle, Index: index);

    /// <summary>True when a write to this range could change what the wait reads.</summary>
    public bool TouchedBy(ulong address, ulong size) =>
        Kind == CommandWaitKind.GuestMemory && size != 0 &&
        address < Address + Size && Address < address + size;
}
