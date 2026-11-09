// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Gpu.GpuCommands.Packets;

public readonly record struct CopyDataPacket(
    bool UsesAsyncEncoding,
    uint SourceSelection,
    uint DestinationSelection,
    uint SourceCachePolicy,
    uint DestinationCachePolicy,
    bool Is64Bit,
    bool WriteConfirm,
    ulong SourceValue,
    ulong DestinationAddress)
{
    public int ByteCount => Is64Bit ? sizeof(ulong) : sizeof(uint);

    public bool SourceIsImmediate => UsesAsyncEncoding
        ? SourceSelection == 5
        : SourceSelection is 10 or 11;

    public bool SourceIsMemory => UsesAsyncEncoding
        ? SourceSelection == 2
        : SourceSelection is 4 or 5;

    public bool SourceIsAtomicReturn => UsesAsyncEncoding
        ? SourceSelection == 6
        : SourceSelection == 12;

    public bool DestinationIsMemory => UsesAsyncEncoding
        ? DestinationSelection == 2
        : DestinationSelection is 4 or 5;

    public bool EnginesMatch => UsesAsyncEncoding ||
        ((SourceSelection & 1u) == (DestinationSelection & 1u));

    public bool IsSupported =>
        (SourceIsImmediate || SourceIsMemory || SourceIsAtomicReturn) &&
        DestinationIsMemory &&
        EnginesMatch;

    public static CopyDataPacket Decode(
        uint control,
        uint sourceLow,
        uint sourceHigh,
        uint destinationLow,
        uint destinationHigh,
        bool usesAsyncEncoding)
    {
        var is64Bit = (control & (1u << 16)) != 0;
        var addressAlignmentMask = is64Bit ? ~7u : ~3u;
        var rawSourceSelection = control & 0xFu;
        var rawDestinationSelection = (control >> 8) & 0xFu;
        var sourceEngine = (control >> 30) & 0x1u;
        var sourceSelection = usesAsyncEncoding
            ? rawSourceSelection
            : (rawSourceSelection << 1) | sourceEngine;
        var destinationSelection = usesAsyncEncoding
            ? rawDestinationSelection
            : (rawDestinationSelection << 1) | sourceEngine;
        var sourceIsImmediate = usesAsyncEncoding
            ? sourceSelection == 5
            : sourceSelection is 10 or 11;
        var sourceValue = sourceIsImmediate
            ? sourceLow | ((ulong)sourceHigh << 32)
            : (sourceLow & addressAlignmentMask) | ((ulong)sourceHigh << 32);
        var destinationAddress =
            (destinationLow & addressAlignmentMask) | ((ulong)destinationHigh << 32);
        return new CopyDataPacket(
            usesAsyncEncoding,
            sourceSelection,
            destinationSelection,
            (control >> 13) & 0x3u,
            (control >> 25) & 0x3u,
            is64Bit,
            (control & (1u << 20)) != 0,
            sourceValue,
            destinationAddress);
    }

    public bool TryApply(ICpuMemory memory, out ulong writtenValue) =>
        TryApply(memory, atomicReturnData: 0, atomicReturnDataValid: false, out writtenValue);

    public bool TryApply(
        ICpuMemory memory,
        ulong atomicReturnData,
        bool atomicReturnDataValid,
        out ulong writtenValue)
    {
        writtenValue = 0;
        if (!IsSupported || DestinationAddress == 0)
        {
            return false;
        }

        Span<byte> valueBytes = stackalloc byte[sizeof(ulong)];
        var transfer = valueBytes[..ByteCount];
        if (SourceIsImmediate)
        {
            if (Is64Bit)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(transfer, SourceValue);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(transfer, (uint)SourceValue);
            }
        }
        else if (SourceIsAtomicReturn)
        {
            if (!atomicReturnDataValid)
            {
                return false;
            }

            if (Is64Bit)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(transfer, atomicReturnData);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(transfer, (uint)atomicReturnData);
            }
        }
        else if (!memory.TryRead(SourceValue, transfer))
        {
            return false;
        }

        if (!memory.TryWrite(DestinationAddress, transfer))
        {
            return false;
        }

        writtenValue = Is64Bit
            ? BinaryPrimitives.ReadUInt64LittleEndian(transfer)
            : BinaryPrimitives.ReadUInt32LittleEndian(transfer);
        return true;
    }
}
