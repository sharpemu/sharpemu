// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Kernel;

public static class KernelEventFlagCompatExports
{
    private const int MaxEventFlagNameLength = 31;
    private const uint AttrThreadFifo = 0x01;
    private const uint AttrThreadPriority = 0x02;
    private const uint AttrSingle = 0x10;
    private const uint AttrMulti = 0x20;
    private const uint WaitAnd = 0x01;
    private const uint WaitOr = 0x02;
    private const uint ClearAll = 0x10;
    private const uint ClearPattern = 0x20;

    private static readonly ConcurrentDictionary<ulong, EventFlagState> _eventFlags = new();
    private static long _nextEventFlagHandle = 1;

    // Cached once: gating every call site avoids building the interpolated trace string when disabled.
    private static readonly bool _traceEventFlag = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_LOG_EVENT_FLAG"), "1", StringComparison.Ordinal);

    private sealed class EventFlagState
    {
        public required string Name { get; init; }
        public required uint Attributes { get; init; }
        public ulong Bits { get; set; }
        public int WaitingThreads { get; set; }
        public object Gate { get; } = new();
    }

    [SysAbiExport(
        Nid = "BpFoboUJoZU",
        ExportName = "sceKernelCreateEventFlag",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelCreateEventFlag(CpuContext ctx)
    {
        var outAddress = ctx[CpuRegister.Rdi];
        var nameAddress = ctx[CpuRegister.Rsi];
        var attributes = unchecked((uint)ctx[CpuRegister.Rdx]);
        var initialPattern = ctx[CpuRegister.Rcx];
        var optionAddress = ctx[CpuRegister.R8];

        if (outAddress == 0 ||
            nameAddress == 0 ||
            optionAddress != 0 ||
            !IsValidAttributes(attributes))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!TryReadNullTerminatedUtf8(ctx, nameAddress, MaxEventFlagNameLength + 1, out var name))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (Encoding.UTF8.GetByteCount(name) > MaxEventFlagNameLength)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var handle = unchecked((ulong)Interlocked.Increment(ref _nextEventFlagHandle));
        _eventFlags[handle] = new EventFlagState
        {
            Name = name,
            Attributes = attributes,
            Bits = initialPattern,
        };

        if (!ctx.TryWriteUInt64(outAddress, handle))
        {
            _eventFlags.TryRemove(handle, out _);
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (_traceEventFlag) TraceEventFlag($"create handle=0x{handle:X16} name='{name}' attr=0x{attributes:X2} bits=0x{initialPattern:X16}");
        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "8mql9OcQnd4",
        ExportName = "sceKernelDeleteEventFlag",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelDeleteEventFlag(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        if (!_eventFlags.TryRemove(handle, out var state))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND);
        }

        lock (state.Gate)
        {
            Monitor.PulseAll(state.Gate);
        }

        if (_traceEventFlag) TraceEventFlag($"delete handle=0x{handle:X16} name='{state.Name}'");
        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "IOnSvHzqu6A",
        ExportName = "sceKernelSetEventFlag",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelSetEventFlag(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var pattern = ctx[CpuRegister.Rsi];
        var returnRip = GetCurrentReturnRip();
        if (!_eventFlags.TryGetValue(handle, out var state))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND);
        }

        lock (state.Gate)
        {
            state.Bits |= pattern;
            // Wake threads parked in-place on the gate; each re-checks its pattern.
            Monitor.PulseAll(state.Gate);
            if (_traceEventFlag) TraceEventFlag($"set handle=0x{handle:X16} pattern=0x{pattern:X16} bits=0x{state.Bits:X16} ret=0x{returnRip:X16}");
        }

        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "7uhBFWRAS60",
        ExportName = "sceKernelClearEventFlag",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelClearEventFlag(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var pattern = ctx[CpuRegister.Rsi];
        if (!_eventFlags.TryGetValue(handle, out var state))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND);
        }

        lock (state.Gate)
        {
            state.Bits &= pattern;
            if (_traceEventFlag) TraceEventFlag($"clear handle=0x{handle:X16} mask=0x{pattern:X16} bits=0x{state.Bits:X16}");
        }

        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "9lvj5DjHZiA",
        ExportName = "sceKernelPollEventFlag",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelPollEventFlag(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var pattern = ctx[CpuRegister.Rsi];
        var waitMode = unchecked((uint)ctx[CpuRegister.Rdx]);
        var resultAddress = ctx[CpuRegister.Rcx];

        if (!_eventFlags.TryGetValue(handle, out var state))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND);
        }

        if (pattern == 0 || !IsValidWaitMode(waitMode))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        lock (state.Gate)
        {
            if (!TryWriteResultPattern(ctx, resultAddress, state.Bits))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            if (!IsSatisfied(state.Bits, pattern, waitMode))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY);
            }

            ApplyClearMode(state, pattern, waitMode);
            if (_traceEventFlag) TraceEventFlag($"poll handle=0x{handle:X16} pattern=0x{pattern:X16} mode=0x{waitMode:X2} bits=0x{state.Bits:X16}");
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
        }
    }

    [SysAbiExport(
        Nid = "JTvBflhYazQ",
        ExportName = "sceKernelWaitEventFlag",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelWaitEventFlag(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var pattern = ctx[CpuRegister.Rsi];
        var waitMode = unchecked((uint)ctx[CpuRegister.Rdx]);
        var resultAddress = ctx[CpuRegister.Rcx];
        var timeoutAddress = ctx[CpuRegister.R8];
        var returnRip = GetCurrentReturnRip();

        if (!_eventFlags.TryGetValue(handle, out var state))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND);
        }

        if (pattern == 0 || !IsValidWaitMode(waitMode))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        uint timeoutUsec = 0;
        if (timeoutAddress != 0 && !TryReadUInt32(ctx, timeoutAddress, out timeoutUsec))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        // A zero-microsecond timeout degrades to an instant poll because the
        // deadline is already in the past.
        var hostDeadlineMs = timeoutAddress != 0
            ? Environment.TickCount64 + (timeoutUsec == 0
                ? 0L
                : Math.Max(1L, (timeoutUsec + 999L) / 1000L))
            : long.MaxValue;

        lock (state.Gate)
        {
            if (TryCompleteSatisfiedWait(ctx, state, pattern, waitMode, resultAddress, out var immediateWaitResult))
            {
                if (_traceEventFlag) TraceEventFlag($"poll handle=0x{handle:X16} pattern=0x{pattern:X16} mode=0x{waitMode:X2} bits=0x{state.Bits:X16} ret=0x{returnRip:X16}");
                return SetReturn(ctx, immediateWaitResult);
            }

            // In-place block on the flag gate. Monitor.Wait releases the gate
            // and parks atomically, so a concurrent SetEventFlag's PulseAll
            // cannot be lost between the satisfy check and the park. On wake
            // the predicate is re-evaluated (Set semantics on FreeBSD-style
            // event flags: OR/AND over the bit pattern, optional clear).
            state.WaitingThreads++;
            var guestThreadHandle = GuestThreadExecution.CurrentGuestThreadHandle;
            if (_traceEventFlag) TraceEventFlag($"wait-block handle=0x{handle:X16} pattern=0x{pattern:X16} waiters={state.WaitingThreads} guest_thread=0x{guestThreadHandle:X16} ret=0x{returnRip:X16}");
            GuestThreadBlocking.NoteBlocked(guestThreadHandle, "sceKernelWaitEventFlag");
            try
            {
                while (true)
                {
                    if (GuestThreadBlocking.ShutdownRequested)
                    {
                        if (timeoutAddress != 0) _ = TryWriteUInt32(ctx, timeoutAddress, 0);
                        _ = TryWriteResultPattern(ctx, resultAddress, state.Bits);
                        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT);
                    }

                    if (TryCompleteSatisfiedWait(ctx, state, pattern, waitMode, resultAddress, out var wokeResult))
                    {
                        if (timeoutAddress != 0) _ = TryWriteUInt32(ctx, timeoutAddress, 0);
                        if (_traceEventFlag) TraceEventFlag($"wait-wake handle=0x{handle:X16} pattern=0x{pattern:X16} bits=0x{state.Bits:X16} ret=0x{returnRip:X16}");
                        return SetReturn(ctx, wokeResult);
                    }

                    var remaining = hostDeadlineMs - Environment.TickCount64;
                    if (timeoutAddress != 0 && remaining <= 0)
                    {
                        _ = TryWriteUInt32(ctx, timeoutAddress, 0);
                        _ = TryWriteResultPattern(ctx, resultAddress, state.Bits);
                        if (_traceEventFlag) TraceEventFlag($"wait-timeout handle=0x{handle:X16} pattern=0x{pattern:X16} bits=0x{state.Bits:X16} ret=0x{returnRip:X16}");
                        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT);
                    }

                    GuestThreadBlocking.Checkpoint(guestThreadHandle, state.Gate);
                    _ = Monitor.Wait(state.Gate, (int)Math.Min(remaining, GuestThreadBlocking.WaitSliceMilliseconds));
                }
            }
            finally
            {
                state.WaitingThreads = Math.Max(0, state.WaitingThreads - 1);
                GuestThreadBlocking.NoteUnblocked(guestThreadHandle);
            }
        }
    }

    [SysAbiExport(
        Nid = "PZku4ZrXJqg",
        ExportName = "sceKernelCancelEventFlag",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelCancelEventFlag(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var setPattern = ctx[CpuRegister.Rsi];
        var waiterCountAddress = ctx[CpuRegister.Rdx];
        if (!_eventFlags.TryGetValue(handle, out var state))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND);
        }

        lock (state.Gate)
        {
            if (waiterCountAddress != 0 &&
                !TryWriteUInt32(ctx, waiterCountAddress, unchecked((uint)state.WaitingThreads)))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            state.Bits = setPattern;
            state.WaitingThreads = 0;
            Monitor.PulseAll(state.Gate);
            if (_traceEventFlag) TraceEventFlag(
                $"cancel handle=0x{handle:X16} bits=0x{setPattern:X16} " +
                $"guest_thread=0x{GuestThreadExecution.CurrentGuestThreadHandle:X16} ret=0x{GetCurrentReturnRip():X16}");
        }

        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
    }

    private static bool IsValidAttributes(uint attributes)
    {
        var queueMode = attributes & 0x0F;
        var threadMode = attributes & 0xF0;
        return (queueMode is 0 or AttrThreadFifo or AttrThreadPriority) &&
            (threadMode is 0 or AttrSingle or AttrMulti) &&
            (attributes & ~0x33u) == 0;
    }

    private static bool IsValidWaitMode(uint waitMode)
    {
        var condition = waitMode & 0x0F;
        var clearMode = waitMode & 0xF0;
        return condition is WaitAnd or WaitOr &&
            clearMode is 0 or ClearAll or ClearPattern &&
            (waitMode & ~0x33u) == 0;
    }

    private static bool IsSatisfied(ulong bits, ulong pattern, uint waitMode) =>
        (waitMode & 0x0F) == WaitAnd
            ? (bits & pattern) == pattern
            : (bits & pattern) != 0;

    private static void ApplyClearMode(EventFlagState state, ulong pattern, uint waitMode)
    {
        switch (waitMode & 0xF0)
        {
            case ClearAll:
                state.Bits = 0;
                break;
            case ClearPattern:
                state.Bits &= ~pattern;
                break;
        }
    }

    private static bool TryCompleteSatisfiedWait(
    CpuContext ctx,
    EventFlagState state,
    ulong pattern,
    uint waitMode,
    ulong resultAddress,
    out OrbisGen2Result result)
    {
        result = OrbisGen2Result.ORBIS_GEN2_OK;

        if (!IsSatisfied(state.Bits, pattern, waitMode))
        {
            return false;
        }

        if (!TryWriteResultPattern(ctx, resultAddress, state.Bits))
        {
            result = OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            return true;
        }

        ApplyClearMode(state, pattern, waitMode);
        return true;
    }

    private static bool TryWriteResultPattern(CpuContext ctx, ulong address, ulong bits) =>
        address == 0 || ctx.TryWriteUInt64(address, bits);

    private static bool TryReadUInt32(CpuContext ctx, ulong address, out uint value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        if (!ctx.Memory.TryRead(address, buffer))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        return true;
    }

    private static bool TryReadUInt64(CpuContext ctx, ulong address, out ulong value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        if (!ctx.Memory.TryRead(address, buffer))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt64LittleEndian(buffer);
        return true;
    }

    private static bool TryWriteUInt32(CpuContext ctx, ulong address, uint value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        return ctx.Memory.TryWrite(address, buffer);
    }

    private static bool TryReadNullTerminatedUtf8(CpuContext ctx, ulong address, int capacity, out string value)
    {
        var bytes = new byte[capacity];
        Span<byte> current = stackalloc byte[1];
        for (var index = 0; index < bytes.Length; index++)
        {
            if (!ctx.Memory.TryRead(address + (ulong)index, current))
            {
                value = string.Empty;
                return false;
            }

            if (current[0] == 0)
            {
                value = Encoding.UTF8.GetString(bytes, 0, index);
                return true;
            }

            bytes[index] = current[0];
        }

        value = Encoding.UTF8.GetString(bytes);
        return true;
    }

    private static int SetReturn(CpuContext ctx, OrbisGen2Result result)
    {
        var value = (int)result;
        ctx[CpuRegister.Rax] = unchecked((ulong)value);
        return value;
    }

    private static void TraceEventFlag(string message)
    {
        if (_traceEventFlag)
        {
            Console.Error.WriteLine($"[LOADER][TRACE] event_flag.{message}");
        }
    }

    private static ulong GetCurrentReturnRip() =>
        GuestThreadExecution.TryGetCurrentImportCallFrame(out var frame)
            ? frame.ReturnRip
            : 0UL;

}
