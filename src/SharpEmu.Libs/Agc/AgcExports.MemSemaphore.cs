// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Agc;

public static partial class AgcExports
{
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
}
