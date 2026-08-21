// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Agc;

public static partial class AgcExports
{
    private const uint ItMemSemaphore = 0x39;

    internal readonly record struct MemSemaphorePacket(
        ulong Address,
        bool WaitForMailbox,
        bool WriteSignal,
        uint Selection)
    {
        public bool IsSignal => Selection == 6;
        public bool IsWait => Selection == 7;
        public bool IsSupported => IsSignal || IsWait;
    }

    internal static MemSemaphorePacket DecodeMemSemaphorePacket(
        uint addressLow,
        uint addressHigh,
        uint control) =>
        new(
            (addressLow & ~7u) | ((ulong)addressHigh << 32),
            (control & (1u << 16)) != 0,
            (control & (1u << 20)) != 0,
            (control >> 29) & 0x7u);

    [SysAbiExport(
        Nid = "G0jrLdvEqDw",
        ExportName = "sceAgcDcbMemSemaphore",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbMemSemaphore(CpuContext ctx) =>
        BuildMemSemaphorePacket(ctx);

    [SysAbiExport(
        Nid = "q4VuU-QsLOE",
        ExportName = "sceAgcAcbMemSemaphore",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int AcbMemSemaphore(CpuContext ctx) =>
        BuildMemSemaphorePacket(ctx);

    private static int BuildMemSemaphorePacket(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var semaphoreAddress = ctx[CpuRegister.Rsi];
        var operation = unchecked((uint)ctx[CpuRegister.Rdx]);
        var signalType = unchecked((uint)ctx[CpuRegister.Rcx]);
        var mailbox = unchecked((uint)ctx[CpuRegister.R8]);
        if (commandBufferAddress == 0 ||
            semaphoreAddress == 0 ||
            (semaphoreAddress & (sizeof(ulong) - 1)) != 0 ||
            operation is not (6u or 7u) ||
            signalType > 1 ||
            mailbox > 1)
        {
            return ReturnPointer(ctx, 0);
        }

        var control = (operation << 29) |
            (signalType << 20) |
            (mailbox << 16);
        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 4, out var packetAddress) ||
            !TryWriteUInt32(ctx, packetAddress, Pm4(4, ItMemSemaphore, 0)) ||
            !TryWriteUInt32(ctx, packetAddress + sizeof(uint), unchecked((uint)semaphoreAddress)) ||
            !TryWriteUInt32(
                ctx,
                packetAddress + (2 * sizeof(uint)),
                unchecked((uint)(semaphoreAddress >> 32))) ||
            !TryWriteUInt32(ctx, packetAddress + (3 * sizeof(uint)), control))
        {
            return ReturnPointer(ctx, 0);
        }

        return ReturnPointer(ctx, packetAddress);
    }

    internal static bool TryConsumeMemSemaphore(
        ICpuMemory memory,
        ulong address,
        out ulong priorValue)
    {
        priorValue = 0;
        if (address == 0 || (address & (sizeof(ulong) - 1)) != 0)
        {
            return false;
        }

        lock (_gpuAtomicMemoryGate)
        {
            Span<byte> bytes = stackalloc byte[sizeof(ulong)];
            if (!memory.TryRead(address, bytes))
            {
                return false;
            }

            priorValue = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
            if (priorValue == 0)
            {
                return true;
            }

            BinaryPrimitives.WriteUInt64LittleEndian(bytes, priorValue - 1);
            return memory.TryWrite(address, bytes);
        }
    }

    internal static bool TrySignalMemSemaphore(
        ICpuMemory memory,
        ulong address,
        bool writeSignal,
        out ulong newValue)
    {
        newValue = 0;
        if (address == 0 || (address & (sizeof(ulong) - 1)) != 0)
        {
            return false;
        }

        lock (_gpuAtomicMemoryGate)
        {
            Span<byte> bytes = stackalloc byte[sizeof(ulong)];
            if (!memory.TryRead(address, bytes))
            {
                return false;
            }

            var current = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
            newValue = writeSignal ? 1 : unchecked(current + 1);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes, newValue);
            return memory.TryWrite(address, bytes);
        }
    }

    internal static bool TryArmMemSemaphoreWait(
        ICpuMemory memory,
        ulong address,
        GpuWaitRegistry.WaitingDcb waiter,
        out ulong priorValue)
    {
        priorValue = 0;
        if (address == 0 || (address & (sizeof(ulong) - 1)) != 0)
        {
            return false;
        }

        lock (_gpuAtomicMemoryGate)
        {
            Span<byte> bytes = stackalloc byte[sizeof(ulong)];
            if (!memory.TryRead(address, bytes))
            {
                return false;
            }

            priorValue = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
            if (priorValue != 0)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(bytes, priorValue - 1);
                if (!memory.TryWrite(address, bytes))
                {
                    return false;
                }

                waiter.Latched = true;
            }

            GpuWaitRegistry.Register(address, waiter);
            return true;
        }
    }

    internal static bool TrySignalMemSemaphoreAndAssignWaiter(
        ICpuMemory memory,
        ulong address,
        bool writeSignal,
        out ulong storedValue,
        out bool waiterAssigned)
    {
        storedValue = 0;
        waiterAssigned = false;
        if (address == 0 || (address & (sizeof(ulong) - 1)) != 0)
        {
            return false;
        }

        lock (_gpuAtomicMemoryGate)
        {
            Span<byte> bytes = stackalloc byte[sizeof(ulong)];
            if (!memory.TryRead(address, bytes))
            {
                return false;
            }

            var current = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
            var signaledValue = writeSignal ? 1UL : unchecked(current + 1);
            var committedValue = 0UL;
            var committed = GpuWaitRegistry.CommitMemSemaphoreSignal(
                memory,
                address,
                consumeToken =>
                {
                    committedValue = signaledValue - (consumeToken ? 1UL : 0UL);
                    Span<byte> output = stackalloc byte[sizeof(ulong)];
                    BinaryPrimitives.WriteUInt64LittleEndian(output, committedValue);
                    return memory.TryWrite(address, output);
                },
                out waiterAssigned);
            storedValue = committedValue;
            return committed;
        }
    }
}
