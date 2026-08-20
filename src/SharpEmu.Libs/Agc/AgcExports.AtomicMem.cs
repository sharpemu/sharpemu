// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Agc;

public static partial class AgcExports
{
    private static readonly object _gpuAtomicMemoryGate = new();

    internal readonly record struct AtomicMemPacket(
        uint RawOperation,
        uint Command,
        uint CachePolicy,
        ulong Address,
        ulong SourceData,
        ulong CompareData,
        uint LoopIntervalCycles)
    {
        public uint BaseOperation => RawOperation & 0x1Fu;
        public bool Is64Bit => (RawOperation & 0x20u) != 0;
        public bool ReturnsData => (RawOperation & 0x40u) == 0;
        public int ByteCount => Is64Bit ? sizeof(ulong) : sizeof(uint);
        public bool IsCompareSwap => BaseOperation == 8;
        public bool IsIntegerOperation =>
            BaseOperation is 7 or 8 || BaseOperation is >= 15 and <= 25;
        public bool HasValidCommand => Command switch
        {
            0 => ReturnsData,
            1 => ReturnsData && IsCompareSwap,
            2 or 3 => !ReturnsData,
            _ => false,
        };
        public bool IsAligned =>
            Address != 0 && (Address & (ulong)(ByteCount - 1)) == 0;
        public bool IsSupported =>
            IsIntegerOperation && HasValidCommand && IsAligned;
    }

    internal static AtomicMemPacket DecodeAtomicMemPacket(
        uint control,
        uint addressLow,
        uint addressHigh,
        uint sourceLow,
        uint sourceHigh,
        uint compareLow,
        uint compareHigh,
        uint loopControl) =>
        new(
            control & 0x7Fu,
            (control >> 8) & 0xFu,
            (control >> 25) & 0x3u,
            addressLow | ((ulong)addressHigh << 32),
            sourceLow | ((ulong)sourceHigh << 32),
            compareLow | ((ulong)compareHigh << 32),
            loopControl & 0x1FFFu);

    internal static bool TryApplyAtomicMem(
        ICpuMemory memory,
        in AtomicMemPacket packet,
        out ulong priorValue,
        out ulong newValue,
        out bool comparePassed)
    {
        priorValue = 0;
        newValue = 0;
        comparePassed = false;
        if (!packet.IsSupported)
        {
            return false;
        }

        lock (_gpuAtomicMemoryGate)
        {
            Span<byte> bytes = stackalloc byte[sizeof(ulong)];
            var valueBytes = bytes[..packet.ByteCount];
            if (!memory.TryRead(packet.Address, valueBytes))
            {
                return false;
            }

            if (packet.Is64Bit)
            {
                priorValue = BinaryPrimitives.ReadUInt64LittleEndian(valueBytes);
                newValue = ApplyAtomicOperation64(
                    packet.BaseOperation,
                    priorValue,
                    packet.SourceData,
                    packet.CompareData,
                    out comparePassed);
                BinaryPrimitives.WriteUInt64LittleEndian(valueBytes, newValue);
            }
            else
            {
                var prior32 = BinaryPrimitives.ReadUInt32LittleEndian(valueBytes);
                priorValue = prior32;
                newValue = ApplyAtomicOperation32(
                    packet.BaseOperation,
                    prior32,
                    (uint)packet.SourceData,
                    (uint)packet.CompareData,
                    out comparePassed);
                BinaryPrimitives.WriteUInt32LittleEndian(valueBytes, (uint)newValue);
            }

            return memory.TryWrite(packet.Address, valueBytes);
        }
    }

    private static uint ApplyAtomicOperation32(
        uint operation,
        uint current,
        uint source,
        uint compare,
        out bool comparePassed)
    {
        comparePassed = operation != 8 || current == compare;
        return operation switch
        {
            7 => source,
            8 => comparePassed ? source : current,
            15 => unchecked(current + source),
            16 => unchecked(current - source),
            17 => (int)source < (int)current ? source : current,
            18 => Math.Min(source, current),
            19 => (int)source > (int)current ? source : current,
            20 => Math.Max(source, current),
            21 => current & source,
            22 => current | source,
            23 => current ^ source,
            24 => current >= source ? 0 : unchecked(current + 1),
            25 => current == 0 || current > source ? source : current - 1,
            _ => current,
        };
    }

    private static ulong ApplyAtomicOperation64(
        uint operation,
        ulong current,
        ulong source,
        ulong compare,
        out bool comparePassed)
    {
        comparePassed = operation != 8 || current == compare;
        return operation switch
        {
            7 => source,
            8 => comparePassed ? source : current,
            15 => unchecked(current + source),
            16 => unchecked(current - source),
            17 => (long)source < (long)current ? source : current,
            18 => Math.Min(source, current),
            19 => (long)source > (long)current ? source : current,
            20 => Math.Max(source, current),
            21 => current & source,
            22 => current | source,
            23 => current ^ source,
            24 => current >= source ? 0 : unchecked(current + 1),
            25 => current == 0 || current > source ? source : current - 1,
            _ => current,
        };
    }

    internal static ulong GetAtomicLoopReferenceValue(
        in AtomicMemPacket packet) =>
        packet.Is64Bit ? packet.CompareData : (uint)packet.CompareData;
}
