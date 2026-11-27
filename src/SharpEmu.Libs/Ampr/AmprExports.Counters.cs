// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers.Binary;

namespace SharpEmu.Libs.Ampr;

// The APR counters: 256 32-bit slots a command buffer writes and waits on. A write stores into or updates a
// field of a slot (8, 4, 2 or 1 bytes), and a wait compares a field with a reference under a mask.
public static partial class AmprExports
{
    private const ulong CounterRecordHeaderSize = 4;
    private const uint CounterAccessSize8 = 0;
    private const uint WriteCounterRecordType = 3;
    private const uint WaitCounterRecordType = 5;
    private const int CounterSlots = 256;
    private static readonly uint[] _counters = new uint[CounterSlots];
    private static readonly object _counterGate = new();

    private sealed class CounterCommand
    {
        public ulong RecordOffset;
        public bool IsWait;
        public uint Counter;
        public uint Access;
        // Write: the counter operation. Wait: the compare function.
        public uint Operation;
        public ulong Value;
        public ulong Mask;
        public bool Masked;
    }

    private static ulong CounterValueBytes(uint access) => access == CounterAccessSize8 ? sizeof(ulong) : (ulong)sizeof(uint);

    private static ulong WriteCounterRecordSize(uint access) => CounterRecordHeaderSize + CounterValueBytes(access);

    private static ulong WaitCounterRecordSize(uint access, bool masked) =>
        CounterRecordHeaderSize + (masked ? 2UL : 1UL) * CounterValueBytes(access);

    [SysAbiExport(
        Nid = "jK+yuYCI7MA",
        ExportName = "sceAmprCommandBufferWriteCounter_04_00",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int CommandBufferWriteCounter0400(CpuContext ctx)
    {
        var commandBuffer = ctx[CpuRegister.Rdi];
        var counter = (uint)(byte)ctx[CpuRegister.Rsi];
        var access = (uint)(byte)ctx[CpuRegister.Rdx];
        var value = ctx[CpuRegister.Rcx];
        var operation = (uint)(byte)ctx[CpuRegister.R8];
        if (commandBuffer == 0 || !IsValidWriteCounter(counter, access, operation))
        {
            return SetAmprResult(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var command = new CounterCommand
        {
            IsWait = false,
            Counter = counter,
            Access = access,
            Operation = operation,
            Value = value,
        };
        if (!AppendCounterRecord(ctx, commandBuffer, command))
        {
            return SetAmprResult(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceAmpr(ctx, "write_counter", commandBuffer, counter, value);
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "cQb8Zr8Q0Y0",
        ExportName = "sceAmprCommandBufferWaitOnCounter_04_00",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int CommandBufferWaitOnCounter0400(CpuContext ctx)
    {
        var commandBuffer = ctx[CpuRegister.Rdi];
        var counter = (uint)(byte)ctx[CpuRegister.Rsi];
        var access = (uint)(byte)ctx[CpuRegister.Rdx];
        var reference = ctx[CpuRegister.Rcx];
        var compare = (uint)(byte)ctx[CpuRegister.R8];
        var maskOperation = (uint)(byte)ctx[CpuRegister.R9];
        // The mask and the flush are the seventh and eighth arguments, on the stack.
        var stack = ctx[CpuRegister.Rsp];
        if (!ctx.TryReadUInt64(stack + sizeof(ulong), out var mask) ||
            !ctx.TryReadUInt64(stack + (2 * sizeof(ulong)), out var flushRaw))
        {
            return SetAmprResult(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        var flush = (uint)(byte)flushRaw;
        if (commandBuffer == 0 || !IsValidWaitOnCounter(access, compare, maskOperation, flush))
        {
            return SetAmprResult(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var command = new CounterCommand
        {
            IsWait = true,
            Counter = counter,
            Access = access,
            Operation = compare,
            Value = reference,
            Mask = maskOperation != 0 ? mask : ulong.MaxValue,
            Masked = maskOperation != 0,
        };
        if (!AppendCounterRecord(ctx, commandBuffer, command))
        {
            return SetAmprResult(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceAmpr(ctx, "wait_counter", commandBuffer, counter, reference);
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "I-Qm+MEso5c",
        ExportName = "sceAmprMeasureCommandSizeWriteCounter_04_00",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int MeasureCommandSizeWriteCounter0400(CpuContext ctx)
    {
        var counter = (uint)(byte)ctx[CpuRegister.Rdi];
        var access = (uint)(byte)ctx[CpuRegister.Rsi];
        var operation = (uint)(byte)ctx[CpuRegister.Rcx];
        if (!IsValidWriteCounter(counter, access, operation))
        {
            return SetAmprResult(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        ctx[CpuRegister.Rax] = WriteCounterRecordSize(access);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "ClnsFLLLcss",
        ExportName = "sceAmprMeasureCommandSizeWaitOnCounter_04_00",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int MeasureCommandSizeWaitOnCounter0400(CpuContext ctx)
    {
        // The arguments are the counter, access, reference, compare, mask operation, mask and flush; the
        // flush is the seventh, on the stack after the mask (the mask takes the sixth register slot).
        var access = (uint)(byte)ctx[CpuRegister.Rsi];
        var compare = (uint)(byte)ctx[CpuRegister.Rcx];
        var maskOperation = (uint)(byte)ctx[CpuRegister.R8];
        if (!ctx.TryReadUInt64(ctx[CpuRegister.Rsp] + sizeof(ulong), out var flushRaw) ||
            !IsValidWaitOnCounter(access, compare, maskOperation, (uint)(byte)flushRaw))
        {
            return SetAmprResult(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        ctx[CpuRegister.Rax] = WaitCounterRecordSize(access, maskOperation != 0);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static bool IsValidWriteCounter(uint counter, uint access, uint operation) =>
        (counter & 0x80u) == 0 && access < 8 && operation < 5;

    private static bool IsValidWaitOnCounter(uint access, uint compare, uint maskOperation, uint flush) =>
        access < 8 && compare <= 6 && maskOperation < 2 && flush < 2;

    // The bit width and shift of a counter field, by access mode.
    private static (int Bits, int Shift) CounterField(uint access) => access switch
    {
        0 => (64, 0),
        1 => (32, 0),
        < 4 => (16, (int)(access - 2) * 16),
        _ => (8, (int)(access - 4) * 8),
    };

    private static ulong FieldMask(int bits) => bits == 64 ? ulong.MaxValue : (1UL << bits) - 1;

    private static ulong ReadCounterLocked(uint index, uint access)
    {
        var (bits, shift) = CounterField(access);
        if (bits == 64)
        {
            return _counters[index % CounterSlots] | ((ulong)_counters[(index + 1) % CounterSlots] << 32);
        }

        return (_counters[index % CounterSlots] >> shift) & (uint)FieldMask(bits);
    }

    internal static ulong ReadCounter(uint index, uint access)
    {
        lock (_counterGate)
        {
            return ReadCounterLocked(index, access);
        }
    }

    internal static void ResetCounters()
    {
        lock (_counterGate)
        {
            Array.Clear(_counters);
        }
    }

    private static void ExecuteWriteCounter(CounterCommand command)
    {
        var (bits, shift) = CounterField(command.Access);
        lock (_counterGate)
        {
            var current = ReadCounterLocked(command.Counter, command.Access);
            var next = (command.Operation switch
            {
                0 => command.Value,
                1 => current | command.Value,
                2 => current & ~command.Value,
                3 => current ^ command.Value,
                _ => unchecked(current + command.Value),
            }) & FieldMask(bits);
            var low = command.Counter % CounterSlots;
            if (bits == 64)
            {
                _counters[(command.Counter + 1) % CounterSlots] = (uint)(next >> 32);
                _counters[low] = (uint)next;
                return;
            }

            var mask = (uint)(FieldMask(bits) << shift);
            _counters[low] = (_counters[low] & ~mask) | ((uint)next << shift);
        }
    }

    private static bool IsCounterWaitMet(CounterCommand command)
    {
        var unused = 64 - CounterField(command.Access).Bits;
        var reference = (command.Value & command.Mask) << unused;
        var current = (ReadCounter(command.Counter, command.Access) & command.Mask) << unused;
        return IsWaitConditionSatisfied(current, reference, command.Operation);
    }

    private static void WaitForCounter(CounterCommand command)
    {
        var spinWait = new SpinWait();
        while (!IsCounterWaitMet(command))
        {
            spinWait.SpinOnce();
        }
    }

    private static bool AppendCounterRecord(CpuContext ctx, ulong commandBuffer, CounterCommand command)
    {
        var recordSize = command.IsWait ? WaitCounterRecordSize(command.Access, command.Masked) : WriteCounterRecordSize(command.Access);
        var valueBytes = (int)CounterValueBytes(command.Access);
        Span<byte> record = stackalloc byte[(int)recordSize];
        record.Clear();
        record[0] = (byte)(command.IsWait ? WaitCounterRecordType : WriteCounterRecordType);
        record[1] = (byte)command.Counter;
        record[2] = (byte)command.Access;
        record[3] = (byte)command.Operation;
        WriteCounterField(record.Slice((int)CounterRecordHeaderSize, valueBytes), command.Value);
        if (command.IsWait && command.Masked)
        {
            WriteCounterField(record.Slice((int)CounterRecordHeaderSize + valueBytes, valueBytes), command.Mask);
        }

        if (!TryGetCommandBufferState(ctx, commandBuffer, out _, out _, out var state) || state is null)
        {
            return false;
        }

        lock (state)
        {
            command.RecordOffset = state.WriteOffset;
        }

        if (!AppendCommandBufferRecord(ctx, commandBuffer, record))
        {
            return false;
        }

        lock (state)
        {
            state.CounterCommands.Add(command);
        }

        return true;
    }

    private static void WriteCounterField(Span<byte> field, ulong value)
    {
        if (field.Length == sizeof(ulong))
        {
            BinaryPrimitives.WriteUInt64LittleEndian(field, value);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(field, unchecked((uint)value));
        }
    }
}
