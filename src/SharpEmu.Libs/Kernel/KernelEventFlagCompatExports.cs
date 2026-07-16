// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Fiber;

namespace SharpEmu.Libs.Kernel;

public static class KernelEventFlagCompatExports
{
    private const int MaxEventFlagNameLength = 31;
    private const int HostWaitPumpMilliseconds = 1;
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

    // Cached once: gating every call site avoids building the interpolated
    // trace string (and FormatFrameChain/FormatGuestWaitObject) when disabled.
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
            Monitor.PulseAll(state.Gate);
            if (_traceEventFlag) TraceEventFlag($"set handle=0x{handle:X16} pattern=0x{pattern:X16} bits=0x{state.Bits:X16} ret=0x{returnRip:X16}");
        }

        _ = GuestThreadExecution.Scheduler?.WakeBlockedThreads(GetEventFlagWakeKey(handle));
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

        Monitor.Enter(state.Gate);
        try
        {
            if (TryCompleteSatisfiedWait(ctx, state, pattern, waitMode, resultAddress, out var immediateWaitResult))
            {
                return SetReturn(ctx, immediateWaitResult);
            }

            // Timed waits block on a deadline instead of returning TIMED_OUT
            // immediately; a zero-microsecond timeout still degrades to an
            // instant poll because the deadline is already in the past.
            var deadline = timeoutAddress != 0
                ? GuestThreadExecution.ComputeDeadlineTimestamp(TimeSpan.FromMicroseconds(timeoutUsec))
                : 0;
            var hostDeadlineMs = timeoutAddress != 0
                ? Environment.TickCount64 + (timeoutUsec == 0
                    ? 0L
                    : Math.Max(1L, (timeoutUsec + 999L) / 1000L))
                : long.MaxValue;

            var currentGuestThread = GuestThreadExecution.CurrentGuestThreadHandle;
            var currentFiber = FiberExports.GetCurrentFiberAddressForDiagnostics(ctx);
            var managedThread = Environment.CurrentManagedThreadId;
            var blockedWaitResult = OrbisGen2Result.ORBIS_GEN2_OK;
            var satisfied = false;
            var requestedBlock = GuestThreadExecution.RequestCurrentThreadBlock(
                ctx,
                "sceKernelWaitEventFlag",
                GetEventFlagWakeKey(handle),
                () =>
                {
                    if (satisfied)
                    {
                        return (int)blockedWaitResult;
                    }

                    // Deadline expiry: report timeout with the current bits.
                    if (timeoutAddress != 0)
                    {
                        _ = TryWriteUInt32(ctx, timeoutAddress, 0);
                    }

                    _ = TryWriteResultPattern(ctx, resultAddress, state.Bits);
                    return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT;
                },
                () =>
                {
                    if (!TryPrepareBlockedWait(
                            ctx,
                            state,
                            pattern,
                            waitMode,
                            resultAddress,
                            out var preparedResult))
                    {
                        return false;
                    }

                    blockedWaitResult = preparedResult;
                    satisfied = true;
                    return true;
                },
                deadline);
            if (_traceEventFlag) TraceEventFlag($"wait-unsatisfied handle=0x{handle:X16} pattern=0x{pattern:X16} bits=0x{state.Bits:X16} guest_thread=0x{currentGuestThread:X16} fiber=0x{currentFiber:X16} managed={managedThread} block={requestedBlock} ret=0x{returnRip:X16} frames={FormatFrameChain(ctx)}");
            if (_traceEventFlag) TraceEventFlag($"wait-object handle=0x{handle:X16} name='{state.Name}' {FormatGuestWaitObject(ctx)}");
            if (!requestedBlock)
            {
                var scheduler = GuestThreadExecution.Scheduler;
                if (scheduler is null)
                {
                    return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_TRY_AGAIN);
                }

                state.WaitingThreads++;
                if (_traceEventFlag) TraceEventFlag($"wait-pump handle=0x{handle:X16} pattern=0x{pattern:X16} waiters={state.WaitingThreads} guest_thread=0x{currentGuestThread:X16} fiber=0x{currentFiber:X16} managed={managedThread} ret=0x{returnRip:X16}");
                var releaseWaiter = true;
                try
                {
                    while (true)
                    {
                        Monitor.Exit(state.Gate);
                        try
                        {
                            scheduler.Pump(ctx, "sceKernelWaitEventFlag");
                        }
                        finally
                        {
                            Monitor.Enter(state.Gate);
                        }

                        if (TryCompleteSatisfiedWait(ctx, state, pattern, waitMode, resultAddress, out var pumpedWaitResult))
                        {
                            state.WaitingThreads = Math.Max(0, state.WaitingThreads - 1);
                            releaseWaiter = false;
                            if (_traceEventFlag) TraceEventFlag($"wait-wake handle=0x{handle:X16} pattern=0x{pattern:X16} bits=0x{state.Bits:X16} waiters={state.WaitingThreads} ret=0x{returnRip:X16}");
                            return SetReturn(ctx, pumpedWaitResult);
                        }

                        var remaining = hostDeadlineMs - Environment.TickCount64;
                        if (timeoutAddress != 0 && remaining <= 0)
                        {
                            state.WaitingThreads = Math.Max(0, state.WaitingThreads - 1);
                            releaseWaiter = false;
                            _ = TryWriteUInt32(ctx, timeoutAddress, 0);
                            _ = TryWriteResultPattern(ctx, resultAddress, state.Bits);
                            if (_traceEventFlag) TraceEventFlag($"wait-timeout handle=0x{handle:X16} pattern=0x{pattern:X16} bits=0x{state.Bits:X16} ret=0x{returnRip:X16}");
                            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT);
                        }

                        Monitor.Wait(state.Gate, (int)Math.Min(remaining, HostWaitPumpMilliseconds));
                    }
                }
                finally
                {
                    if (releaseWaiter)
                    {
                        state.WaitingThreads = Math.Max(0, state.WaitingThreads - 1);
                    }
                }
            }

            state.WaitingThreads++;
            if (_traceEventFlag) TraceEventFlag($"wait-block handle=0x{handle:X16} pattern=0x{pattern:X16} waiters={state.WaitingThreads} guest_thread=0x{currentGuestThread:X16} fiber=0x{currentFiber:X16} managed={managedThread} ret=0x{returnRip:X16}");
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
        }
        finally
        {
            Monitor.Exit(state.Gate);
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

    private static bool TryPrepareBlockedWait(
        CpuContext ctx,
        EventFlagState state,
        ulong pattern,
        uint waitMode,
        ulong resultAddress,
        out OrbisGen2Result result)
    {
        lock (state.Gate)
        {
            result = OrbisGen2Result.ORBIS_GEN2_OK;
            if (!IsSatisfied(state.Bits, pattern, waitMode))
            {
                return false;
            }

            if (!TryWriteResultPattern(ctx, resultAddress, state.Bits))
            {
                result = OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            }
            else
            {
                ApplyClearMode(state, pattern, waitMode);
            }

            state.WaitingThreads = Math.Max(0, state.WaitingThreads - 1);
            if (_traceEventFlag) TraceEventFlag(
                $"wait-wake pattern=0x{pattern:X16} mode=0x{waitMode:X2} bits=0x{state.Bits:X16} waiters={state.WaitingThreads}");
            return true;
        }
    }

    private static string GetEventFlagWakeKey(ulong handle) =>
        $"event_flag:0x{handle:X16}";

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

    private static bool TryReadByte(CpuContext ctx, ulong address, out byte value)
    {
        Span<byte> buffer = stackalloc byte[1];
        if (!ctx.Memory.TryRead(address, buffer))
        {
            value = 0;
            return false;
        }

        value = buffer[0];
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

    private static string FormatFrameChain(CpuContext ctx)
    {
        Span<ulong> returns = stackalloc ulong[4];
        var count = 0;
        var frame = ctx[CpuRegister.Rbp];
        for (var index = 0; index < returns.Length && frame != 0; index++)
        {
            if (!ctx.TryReadUInt64(frame, out var nextFrame) ||
                !ctx.TryReadUInt64(frame + sizeof(ulong), out var returnAddress))
            {
                break;
            }

            returns[count++] = returnAddress;
            if (nextFrame <= frame)
            {
                break;
            }

            frame = nextFrame;
        }

        return count switch
        {
            0 => "none",
            1 => $"0x{returns[0]:X16}",
            2 => $"0x{returns[0]:X16},0x{returns[1]:X16}",
            3 => $"0x{returns[0]:X16},0x{returns[1]:X16},0x{returns[2]:X16}",
            _ => $"0x{returns[0]:X16},0x{returns[1]:X16},0x{returns[2]:X16},0x{returns[3]:X16}",
        };
    }

    private static string FormatGuestWaitObject(CpuContext ctx)
    {
        var r12 = ctx[CpuRegister.R12];
        var r13 = ctx[CpuRegister.R13];
        var objectAddress = r12 != 0
            ? r12
            : r13 >= 0xA8
                ? r13 - 0xA8
                : 0;

        var builder = new StringBuilder(256);
        builder.Append($"r12=0x{r12:X16} r13=0x{r13:X16}");
        if (objectAddress == 0)
        {
            return builder.ToString();
        }

        builder.Append($" obj=0x{objectAddress:X16}");
        AppendUInt32(builder, ctx, objectAddress + 0x58, "o58");
        AppendUInt32(builder, ctx, objectAddress + 0x5C, "o5C");
        AppendUInt64(builder, ctx, objectAddress + 0x60, "o60");
        AppendByte(builder, ctx, objectAddress + 0x6C, "state6C");
        AppendByte(builder, ctx, objectAddress + 0x6D, "o6D");
        AppendByte(builder, ctx, objectAddress + 0xA0, "waitA0");
        AppendByte(builder, ctx, objectAddress + 0xA1, "stateA1");
        AppendByte(builder, ctx, objectAddress + 0xA2, "oA2");
        AppendUInt64(builder, ctx, objectAddress + 0xA8, "eventA8");
        if (r13 != 0)
        {
            AppendUInt64(builder, ctx, r13, "r13_0");
            AppendUInt64(builder, ctx, r13 + 8, "r13_8");
        }

        return builder.ToString();
    }

    private static void AppendByte(StringBuilder builder, CpuContext ctx, ulong address, string name)
    {
        if (TryReadByte(ctx, address, out var value))
        {
            builder.Append($" {name}=0x{value:X2}");
        }
    }

    private static void AppendUInt32(StringBuilder builder, CpuContext ctx, ulong address, string name)
    {
        if (TryReadUInt32(ctx, address, out var value))
        {
            builder.Append($" {name}=0x{value:X8}");
        }
    }

    private static void AppendUInt64(StringBuilder builder, CpuContext ctx, ulong address, string name)
    {
        if (TryReadUInt64(ctx, address, out var value))
        {
            builder.Append($" {name}=0x{value:X16}");
        }
    }
}
