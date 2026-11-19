// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands.Registers;

namespace SharpEmu.Libs.Gpu.GpuCommands;

// The interpreter's side of deferred dispatch runs: a run of direct dispatches whose rebinds workers resolve while the
// command thread reads on. Inside a run the interpreter applies only compute-range shader register writes, shader register
// tables, cache and flush events, inert NOPs and markers, and direct dispatches; anything else first waits at the barrier,
// which gives every later consumer complete write marks. A rollback restores the compute registers and the raw register
// mirror as they stood at a member's issue and moves the packet cursor back to that member.
public interface IDeferredDispatchRuns
{
    // Members were issued since the last barrier.
    bool IsOpen { get; }

    // A run is open, or deferral is held off after a rollback until the next barrier.
    bool Active { get; }

    DeferredIssue TryIssue(in DeferredIssueSite site, uint groupsX, uint groupsY, uint groupsZ, uint initiator);

    // Waits until every member issued is finalized or cancelled, closes the run, and returns the rollback a poisoned member
    // asks for, or null. Without an open run it only ends the hold-off of a rollback.
    DeferredRewind? Barrier();

    // A shader register table applied while a run is open, before its contents are checked; false when the log is full (the
    // caller closes the run first).
    bool NoteTable(ulong address, ulong size);

    // A packet other than a run packet ended the chain of same-program dispatches.
    void BreakChain();

    // The compute ticket scan stops at the dispatch that would start a run: its workers resolve that dispatch and the ones
    // after it.
    bool StopsTicketScan { get; }

    // Whether a dispatch with these registers could be issued to a run (the compute ticket scan's test).
    bool WouldDefer(ComputeStageRegisters compute, uint initiator);
}

public enum DeferredIssue
{
    NotEligible,
    Issued,
    // A member was poisoned: the caller waits at the barrier and rolls back.
    Rewind,
}

// Where a member was issued: the cursor (depth and buffer), the dispatch packet's offset, the raw-mirror undo log length and
// the user-data marker at issue.
public readonly record struct DeferredIssueSite(ulong SubmitId, int Depth, ulong CursorAddress, uint PacketOffset, int RawUndoMark, uint UserDataMarker);

public sealed class DeferredRewind
{
    public int Depth;
    public ulong CursorAddress;
    public uint Offset;
    public ComputeStageRegisters Compute = null!;
    public int RawUndoMark;
    public uint UserDataMarker;
}
