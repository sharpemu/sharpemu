// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.Libs.Gpu.GpuCommands.Packets;
using SharpEmu.Libs.Gpu.Scheduling;

namespace SharpEmu.Libs.Gpu.GpuCommands;

public sealed partial class GpuCommandInterpreter
{
    private const uint DmaSkippedDestination = 0x3022C;

    // Destination selectors 1, 2, 4 and 5 are memory; registers and GDS are not supported.
    internal void WriteData(ulong destination, ReadOnlySpan<uint> source, uint writeControl)
    {
        var control = WriteDataControl.DecodeNative(writeControl);
        if (control.Destination is not (1 or 2 or 4 or 5))
        {
            throw _host.Fatal($"The write-data destination selector is not supported: selector=0x{control.Destination:X2} address=0x{destination:X16}.");
        }

        WriteDataValues(destination, source, !control.IncrementAddress);
    }

    private void WriteDataValues(ulong destination, ReadOnlySpan<uint> source, bool writeOneAddress)
    {
        if (source.IsEmpty)
        {
            return;
        }

        if (writeOneAddress)
        {
            WriteDword(destination, source[^1]);
            return;
        }

        WriteBytes(destination, MemoryMarshal.AsBytes(source));
    }

    internal void WriteReferenceClock(ulong destination, uint byteCount)
    {
        if (destination == 0 || byteCount is not (sizeof(uint) or sizeof(ulong)) || (destination & (byteCount - 1u)) != 0)
        {
            throw _host.Fatal($"The reference-clock copy is invalid: destination=0x{destination:X16} size={byteCount}.");
        }

        var clock = EndOfPipe.ReadReferenceClock();
        if (byteCount == sizeof(uint))
        {
            WriteDword(destination, (uint)clock);
        }
        else
        {
            WriteQword(destination, clock);
        }
    }

    // Selectors 0 and 3 are memory, 1 is GDS, 2 is immediate data (source) or nowhere (destination).
    internal void TransferData(
        uint engine,
        uint destinationSelect,
        uint destinationCachePolicy,
        ulong destination,
        uint sourceSelect,
        uint sourceCachePolicy,
        ulong sourceOrImmediate,
        uint byteCount,
        uint waitForPrevious,
        uint writeConfirm,
        uint blockEngine)
    {
        if (engine > 1)
        {
            throw _host.Fatal($"The DMA engine is not supported: engine={engine} destination=0x{destination:X16}.");
        }

        if (byteCount == 0)
        {
            return;
        }

        if (destinationCachePolicy > 3 || sourceCachePolicy > 3 || waitForPrevious > 1 || writeConfirm > 1 || blockEngine > 1)
        {
            throw _host.Fatal(
                $"The DMA control fields are out of range: dstCache={destinationCachePolicy} srcCache={sourceCachePolicy} " +
                $"wait={waitForPrevious} confirm={writeConfirm} block={blockEngine} destination=0x{destination:X16}.");
        }

        if ((uint)destination == DmaSkippedDestination)
        {
            return;
        }

        if (destinationSelect == 2)
        {
            if (sourceSelect != 3)
            {
                throw _host.Fatal($"The DMA nowhere source selector is not supported: selector=0x{sourceSelect:X2}.");
            }

            return;
        }

        if (!TryDecodeGds(destinationSelect, out var destinationIsGds))
        {
            throw _host.Fatal($"The DMA destination selector is not supported: selector=0x{destinationSelect:X2} destination=0x{destination:X16}.");
        }

        if (sourceSelect == 2)
        {
            _host.FillBuffer(destination, byteCount, (uint)sourceOrImmediate, destinationIsGds);
            return;
        }

        if (!TryDecodeGds(sourceSelect, out var sourceIsGds))
        {
            throw _host.Fatal($"The DMA source selector is not supported: selector=0x{sourceSelect:X2} source=0x{sourceOrImmediate:X16}.");
        }

        if (sourceIsGds && destinationIsGds)
        {
            throw _host.Fatal($"A GDS-to-GDS DMA copy is not supported: destination=0x{destination:X16} source=0x{sourceOrImmediate:X16} size={byteCount}.");
        }

        _host.CopyBuffer(destination, sourceOrImmediate, byteCount, destinationIsGds, sourceIsGds);
    }

    private static bool TryDecodeGds(uint selector, out bool isGds)
    {
        switch (selector)
        {
            case 0:
            case 3:
                isGds = false;
                return true;
            case 1:
                isGds = true;
                return true;
            default:
                isGds = false;
                return false;
        }
    }

    internal void WriteConstantRam(uint byteOffset, ReadOnlySpan<uint> source) =>
        source.CopyTo(_constantRam.AsSpan((int)(byteOffset / 4)));

    internal void DumpConstantRam(ulong destination, uint byteOffset, uint dwordCount) =>
        WriteBytes(destination, MemoryMarshal.AsBytes(_constantRam.AsSpan((int)(byteOffset / 4), (int)dwordCount)));

    internal uint WriteDataPacket(in PacketContext packet, ReadOnlySpan<uint> payload)
    {
        var dwordCount = (packet.Header >> 16) & 0x3FFFu;
        var destination = Address(payload[1], payload[2]);
        WriteData(destination, payload.Slice(3, (int)dwordCount - 2), payload[0]);
        return 1 + dwordCount;
    }

    internal uint DmaDataPacket(in PacketContext packet, ReadOnlySpan<uint> payload)
    {
        if (packet.Header != PacketHeader.Make(7, PacketOpcode.DmaData))
        {
            throw _host.Fatal($"The DMA packet header is not supported: header=0x{packet.Header:X8} address=0x{packet.PacketAddress:X16}.");
        }

        var control = payload[0];
        var control2 = payload[5];
        var source = Address(payload[1], payload[2]);
        var destination = Address(payload[3], payload[4]);
        if (control == 0x6000_0000u && destination == DmaSkippedDestination && (control2 >> 21) == 0x141u)
        {
            return 6;
        }

        TransferData(
            engine: control & 0x1u,
            destinationSelect: ((control >> 20) & 0x3u) | ((control2 >> 25) & 0x4u) | ((control2 >> 26) & 0x8u),
            destinationCachePolicy: (control >> 25) & 0x3u,
            destination,
            sourceSelect: ((control >> 29) & 0x3u) | ((control2 >> 24) & 0x4u) | ((control2 >> 25) & 0x8u),
            sourceCachePolicy: (control >> 13) & 0x3u,
            source,
            byteCount: control2 & 0x03FF_FFFFu,
            waitForPrevious: (control2 >> 30) & 0x1u,
            writeConfirm: (control2 >> 31) & 0x1u,
            blockEngine: (control >> 31) & 0x1u);
        return 6;
    }

    private uint CopyDataDestinationToDma(uint destinationSelect) => destinationSelect switch
    {
        2 or 4 or 5 => 3,
        3 or 6 or 7 => 1,
        _ => throw _host.Fatal($"The copy-data destination selector is not supported: selector=0x{destinationSelect:X2}."),
    };

    private uint CopyDataSourceToDma(uint sourceSelect) => sourceSelect switch
    {
        2 or 4 or 5 => 3,
        3 or 6 or 7 => 1,
        10 or 11 => 2,
        _ => throw _host.Fatal($"The copy-data source selector is not supported: selector=0x{sourceSelect:X2}."),
    };

    internal uint CopyDataPacketHandler(in PacketContext packet, ReadOnlySpan<uint> payload)
    {
        if (packet.Header != PacketHeader.Make(6, PacketOpcode.CopyData))
        {
            throw _host.Fatal($"The copy-data packet header is not supported: header=0x{packet.Header:X8} address=0x{packet.PacketAddress:X16}.");
        }

        var control = payload[0];
        var byteCount = ((control >> 16) & 0x1u) != 0 ? 8u : 4u;
        var source = Address(payload[1], payload[2]);
        var destination = Address(payload[3], payload[4]);
        var sourceCachePolicy = (control >> 13) & 0x3u;
        var destinationCachePolicy = (control >> 25) & 0x3u;
        var writeConfirm = (control >> 20) & 0x1u;

        // Compute queues carry the raw selectors; the graphics queue folds the engine bit in.
        if (IsComputeQueue)
        {
            CopyDataOnComputeQueue(control, source, destination, byteCount, sourceCachePolicy, destinationCachePolicy, writeConfirm);
            return 5;
        }

        var sourceSelect = ((control & 0xFu) << 1) | ((control >> 30) & 0x1u);
        var destinationSelect = ((control >> 8) & 0xFu) << 1;
        var referenceClockDestination = sourceSelect switch
        {
            9u => 2u,
            18u => 4u,
            _ => 0u,
        };
        if (referenceClockDestination != 0)
        {
            if (destinationSelect != referenceClockDestination || destination == 0 || (destination & (byteCount - 1u)) != 0)
            {
                throw _host.Fatal($"The reference-clock copy-data is not supported: srcSel=0x{sourceSelect:X2} dstSel=0x{destinationSelect:X2} destination=0x{destination:X16} size={byteCount}.");
            }

            WriteReferenceClock(destination, byteCount);
            return 5;
        }

        if (sourceSelect == 12)
        {
            CopyAtomicReturn(destinationSelect, destination, byteCount, usePfp: false);
            return 5;
        }

        var dmaSource = CopyDataSourceToDma(sourceSelect);
        if (dmaSource == 2 && byteCount == 8)
        {
            // An 8-byte immediate is a plain label store, not a 32-bit fill.
            WriteQword(destination, source);
            return 5;
        }

        TransferData(0, CopyDataDestinationToDma(destinationSelect), destinationCachePolicy, destination, dmaSource, sourceCachePolicy, source, byteCount, 1, writeConfirm, 1);
        return 5;
    }

    private void CopyDataOnComputeQueue(uint control, ulong source, ulong destination, uint byteCount, uint sourceCachePolicy, uint destinationCachePolicy, uint writeConfirm)
    {
        var sourceSelect = control & 0xFu;
        var destinationSelect = (control >> 8) & 0xFu;
        var dmaDestination = destinationSelect switch
        {
            1 or 2 => 3u,
            3 => 1u,
            _ => throw _host.Fatal($"The copy-data destination selector is not supported: selector=0x{destinationSelect:X2} queue={QueueId}."),
        };
        switch (sourceSelect)
        {
            case 1:
            case 2:
                TransferData(0, dmaDestination, destinationCachePolicy, destination, 3, sourceCachePolicy, source, byteCount, 1, writeConfirm, 1);
                return;
            case 3:
                TransferData(0, dmaDestination, destinationCachePolicy, destination, 1, sourceCachePolicy, source, byteCount, 1, writeConfirm, 1);
                return;
            case 5:
                if (byteCount == 8)
                {
                    WriteQword(destination, source);
                    return;
                }

                TransferData(0, dmaDestination, destinationCachePolicy, destination, 2, sourceCachePolicy, source, byteCount, 1, writeConfirm, 1);
                return;
            case 6:
                CopyAtomicReturn(destinationSelect, destination, byteCount, usePfp: false);
                return;
            default:
                throw _host.Fatal($"The copy-data source selector is not supported: selector=0x{sourceSelect:X2} queue={QueueId}.");
        }
    }

    // The atomic return latch is the value the last returning atomic read before it wrote.
    private void CopyAtomicReturn(uint destinationSelect, ulong destination, uint byteCount, bool usePfp)
    {
        var valid = usePfp ? AtomicReturnPfpValid : AtomicReturnMeValid;
        if (!valid)
        {
            throw _host.Fatal($"The atomic return value is not available: destination=0x{destination:X16} queue={QueueId}.");
        }

        if (destination == 0 || (destination & (byteCount - 1u)) != 0)
        {
            throw _host.Fatal($"The copy-data destination is invalid: dstSel=0x{destinationSelect:X2} destination=0x{destination:X16} size={byteCount}.");
        }

        var data = usePfp ? AtomicReturnPfpData : AtomicReturnMeData;
        if (byteCount == 8)
        {
            WriteQword(destination, data);
        }
        else
        {
            WriteDword(destination, (uint)data);
        }
    }

    internal uint WriteConstantRamPacket(in PacketContext packet, ReadOnlySpan<uint> payload)
    {
        var dwordCount = (packet.Header >> 16) & 0x3FFFu;
        var offset = payload[0];
        if (dwordCount >= ConstantRamDwords || offset > 0xBFFC || (offset & 0x3u) != 0)
        {
            throw _host.Fatal($"The constant RAM write is out of range: offset=0x{offset:X} dwords={dwordCount}.");
        }

        WriteConstantRam(offset, payload.Slice(1, (int)dwordCount));
        return 1 + dwordCount;
    }

    internal uint DumpConstantRamPacket(in PacketContext packet, ReadOnlySpan<uint> payload)
    {
        if (packet.Header != PacketHeader.Make(5, PacketOpcode.DumpConstantRam))
        {
            throw _host.Fatal($"The constant RAM dump header is not supported: header=0x{packet.Header:X8}.");
        }

        var offset = payload[0];
        var dwordCount = payload[1];
        var destination = Address(payload[2], payload[3]);
        if (dwordCount >= ConstantRamDwords || offset > 0xBFFC || (offset & 0x3u) != 0)
        {
            throw _host.Fatal($"The constant RAM dump is out of range: offset=0x{offset:X} dwords={dwordCount} destination=0x{destination:X16}.");
        }

        DumpConstantRam(destination, offset, dwordCount);
        return 4;
    }

    internal uint GetLodStatsPacket(in PacketContext packet, ReadOnlySpan<uint> payload)
    {
        if (packet.Header != PacketHeader.Make(5, PacketOpcode.GetLodStats))
        {
            throw _host.Fatal($"The LOD statistics header is not supported: header=0x{packet.Header:X8}.");
        }

        var bufferSize = payload[0];
        var destination = (payload[1] & 0xFFFF_FFC0u) | ((ulong)payload[2] << 32);
        if (destination != 0 && bufferSize != 0)
        {
            // The statistics buffer is zeroed and its first dword marks it as valid.
            var zeros = new byte[bufferSize];
            WriteBytes(destination, zeros);
            if (bufferSize >= sizeof(uint))
            {
                WriteDword(destination, 1);
            }
        }

        return 4;
    }
}
