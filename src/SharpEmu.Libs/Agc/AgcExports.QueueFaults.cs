// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Agc;

public static partial class AgcExports
{
    /// <summary>
    /// Stops one guest GPU queue after a synchronization packet fails.
    /// The caller holds the submitted GPU state lock.
    /// </summary>
    private static void StopSubmittedQueue(
        CpuContext ctx,
        SubmittedDcbState state,
        ulong packetAddress,
        uint opcode,
        string reason)
    {
        if (state.IsFaulted)
        {
            return;
        }

        state.IsFaulted = true;
        state.FaultReason = reason;
        var removedWaiters = GpuWaitRegistry.RemoveAllByState(ctx.Memory, state);
        var droppedSubmissions = state.PendingSubmissions.Count;
        state.PendingSubmissions.Clear();
        state.IsSuspended = false;
        state.HasActiveSubmission = false;
        state.ActiveIndexSnapshots = null;
        state.CurrentIndexSnapshot = null;
        state.ActiveVertexSnapshots = null;
        state.CurrentVertexSnapshot = null;
        state.RingTailParkAddress = 0;

        Console.Error.WriteLine(
            $"[LOADER][ERROR] agc.queue_stopped queue={state.QueueName} " +
            $"submission={state.ActiveSubmissionId} packet=0x{packetAddress:X16} " +
            $"op=0x{opcode:X2} waiters={removedWaiters} " +
            $"pending={droppedSubmissions} reason='{reason}'");
    }
}
