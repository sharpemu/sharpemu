// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Network;

/// <summary>
/// Minimal reliable-UDP poll object support. SharpEmu does not expose a host
/// RUDP transport yet, but callers still require a distinct non-zero poll ID
/// for their offline/event-processing path.
/// </summary>
public static class RudpExports
{
    private const int MaxLivePolls = 4096;
    private const ulong PollEventSize = 8;
    private const int RudpErrorInvalidArgument = unchecked((int)0x80770004);
    private const int RudpErrorInvalidPollId = unchecked((int)0x8077001F);
    private const int RudpErrorOutOfMemory = unchecked((int)0x80770003);
    private static readonly object PollGate = new();
    private static readonly Dictionary<int, uint> PollCapacities = [];
    private static int _nextPollId;

    [SysAbiExport(
        Nid = "MVbmLASjn5M",
        ExportName = "sceRudpPollCreate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRudp")]
    public static int RudpPollCreate(CpuContext ctx)
    {
        var requestedEvents = ctx[CpuRegister.Rdi];
        if (requestedEvents is 0 or > uint.MaxValue)
        {
            return ctx.SetReturn(RudpErrorInvalidArgument);
        }

        lock (PollGate)
        {
            if (PollCapacities.Count >= MaxLivePolls)
            {
                return ctx.SetReturn(RudpErrorOutOfMemory);
            }

            int pollId;
            do
            {
                _nextPollId = _nextPollId == int.MaxValue ? 1 : _nextPollId + 1;
                pollId = _nextPollId;
            }
            while (PollCapacities.ContainsKey(pollId));

            PollCapacities.Add(pollId, unchecked((uint)requestedEvents));
            return ctx.SetReturn(pollId);
        }
    }

    [SysAbiExport(
        Nid = "M6ggviwXpLs",
        ExportName = "sceRudpPollWait",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRudp")]
    public static int RudpPollWait(CpuContext ctx)
    {
        var pollId = unchecked((int)ctx[CpuRegister.Rdi]);
        var events = ctx[CpuRegister.Rsi];
        var maxEvents = ctx[CpuRegister.Rdx];
        var timeoutMicroseconds = ctx[CpuRegister.Rcx];

        if (!TryGetPollCapacity(pollId, out _))
        {
            return ctx.SetReturn(RudpErrorInvalidPollId);
        }

        if (events == 0 ||
            maxEvents == 0 ||
            maxEvents > uint.MaxValue ||
            maxEvents > ulong.MaxValue / PollEventSize ||
            !ctx.Memory.CanRead(events, maxEvents * PollEventSize))
        {
            return ctx.SetReturn(RudpErrorInvalidArgument);
        }

        ctx[CpuRegister.Rax] = 0;
        if (timeoutMicroseconds == 0 ||
            !GuestThreadExecution.IsGuestThread ||
            !GuestThreadExecution.TryGetCurrentImportCallFrame(out _))
        {
            return 0;
        }

        var timeout = timeoutMicroseconds > (ulong)(TimeSpan.MaxValue.Ticks / 10)
            ? TimeSpan.MaxValue
            : TimeSpan.FromTicks(checked((long)timeoutMicroseconds * 10));
        _ = GuestThreadExecution.RequestCurrentThreadBlock(
            ctx,
            "sceRudpPollWait",
            $"rudp-poll:{pollId}",
            waiter: null,
            blockDeadlineTimestamp: GuestThreadExecution.ComputeDeadlineTimestamp(timeout));
        return 0;
    }

    public static void ResetRuntimeState()
    {
        lock (PollGate)
        {
            PollCapacities.Clear();
            _nextPollId = 0;
        }
    }

    internal static bool TryGetPollCapacity(int pollId, out uint capacity)
    {
        lock (PollGate)
        {
            return PollCapacities.TryGetValue(pollId, out capacity);
        }
    }

    internal static void ResetForTests() => ResetRuntimeState();
}
