// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Gpu.GpuCommands.Packets;

public readonly record struct MemorySemaphorePacket(
    ulong Address,
    bool WaitForMailbox,
    bool WriteSignal,
    uint Selection)
{
    public bool IsSignal => Selection == 6;

    public bool IsWait => Selection == 7;

    public bool IsSupported => IsSignal || IsWait;

    public bool IsAligned => Address != 0 && (Address & (sizeof(ulong) - 1)) == 0;

    public static MemorySemaphorePacket Decode(uint addressLow, uint addressHigh, uint control) =>
        new(
            (addressLow & ~7u) | ((ulong)addressHigh << 32),
            (control & (1u << 16)) != 0,
            (control & (1u << 20)) != 0,
            (control >> 29) & 0x7u);

    // Takes one token when the count is not zero. Returns false when memory is unreadable.
    public static bool TryConsume(GuestReader read, ICpuMemory memory, ulong address, out ulong priorValue)
    {
        priorValue = 0;
        if (address == 0 || (address & (sizeof(ulong) - 1)) != 0)
        {
            return false;
        }

        lock (GuestAtomicGate.Lock)
        {
            Span<byte> bytes = stackalloc byte[sizeof(ulong)];
            if (!read(address, bytes))
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

    public static bool TrySignal(GuestReader read, ICpuMemory memory, ulong address, bool writeSignal, out ulong newValue)
    {
        newValue = 0;
        if (address == 0 || (address & (sizeof(ulong) - 1)) != 0)
        {
            return false;
        }

        lock (GuestAtomicGate.Lock)
        {
            Span<byte> bytes = stackalloc byte[sizeof(ulong)];
            if (!read(address, bytes))
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
