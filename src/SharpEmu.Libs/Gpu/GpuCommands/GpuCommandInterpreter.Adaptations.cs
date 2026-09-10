// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands.Packets;

namespace SharpEmu.Libs.Gpu.GpuCommands;

// Additional packet forms emitted by guest libraries.
public sealed partial class GpuCommandInterpreter
{
    private static int _wrappedWaitSkipWarnings;

    internal uint AtomicMemoryPacket(in PacketContext packet, ReadOnlySpan<uint> payload)
    {
        if (packet.Length < 9)
        {
            throw _host.Fatal($"The atomic packet is too short: length={packet.Length} address=0x{packet.PacketAddress:X16}.");
        }

        var atomic = Packets.AtomicMemoryPacket.Decode(payload[0], payload[1], payload[2], payload[3], payload[4], payload[5], payload[6], payload[7]);
        if (!atomic.IsSupported || !atomic.HasValidEngine(IsComputeQueue))
        {
            throw _host.Fatal(
                $"The atomic packet is not supported: operation=0x{atomic.RawOperation:X2} command={atomic.Command} " +
                $"engine={atomic.EngineSelection} address=0x{atomic.Address:X16} queue={QueueId}.");
        }

        // A compare-swap loop applies once per try and suspends while the compare fails.
        if (!atomic.TryApply(_host.TryReadGuest, _host.Memory, out var priorValue, out _, out var comparePassed))
        {
            throw _host.Fatal($"The atomic address cannot be accessed: address=0x{atomic.Address:X16} size={atomic.ByteCount}.");
        }

        if (atomic.ReturnsData)
        {
            if (!IsComputeQueue && atomic.EngineSelection == 1)
            {
                AtomicReturnPfpData = priorValue;
                AtomicReturnPfpValid = true;
            }
            else
            {
                AtomicReturnMeData = priorValue;
                AtomicReturnMeValid = true;
            }
        }

        if (atomic.Command == 1 && !comparePassed)
        {
            Suspend();
        }

        return 8;
    }

    internal uint MemorySemaphorePacket(in PacketContext packet, ReadOnlySpan<uint> payload)
    {
        if (packet.Length < 4)
        {
            throw _host.Fatal($"The semaphore packet is too short: length={packet.Length} address=0x{packet.PacketAddress:X16}.");
        }

        var semaphore = Packets.MemorySemaphorePacket.Decode(payload[0], payload[1], payload[2]);
        if (!semaphore.IsSupported || !semaphore.IsAligned)
        {
            throw _host.Fatal($"The semaphore packet is not supported: selection={semaphore.Selection} address=0x{semaphore.Address:X16}.");
        }

        if (semaphore.IsSignal)
        {
            if (!Packets.MemorySemaphorePacket.TrySignal(_host.TryReadGuest, _host.Memory, semaphore.Address, semaphore.WriteSignal, out _))
            {
                throw _host.Fatal($"The semaphore cannot be signaled: address=0x{semaphore.Address:X16}.");
            }

            return 3;
        }

        // A wait takes one token, or suspends until a signal adds one.
        if (!Packets.MemorySemaphorePacket.TryConsume(_host.TryReadGuest, _host.Memory, semaphore.Address, out var priorValue))
        {
            throw _host.Fatal($"The semaphore cannot be read: address=0x{semaphore.Address:X16}.");
        }

        if (priorValue == 0)
        {
            Suspend();
        }

        return 3;
    }

    // A passed compare writes the value, or enables the conditional wait when the scratch is the target.
    internal uint ConditionalWritePacket(in PacketContext packet, ReadOnlySpan<uint> payload)
    {
        if (packet.Length < 9)
        {
            throw _host.Fatal($"The conditional-write packet is too short: length={packet.Length} address=0x{packet.PacketAddress:X16}.");
        }

        var control = payload[0];
        var compareFunction = control & 0x7u;
        var pollsMemory = (control & (1u << 4)) != 0;
        var writesMemory = (control & (1u << 8)) != 0;
        var readAddress = ((ulong)(payload[2] & 0xFFFFu) << 32) | (payload[1] & 0xFFFF_FFFCu);
        var reference = payload[3];
        var mask = payload[4];
        var writeAddress = ((ulong)(payload[6] & 0xFFFFu) << 32) | (payload[5] & 0xFFFF_FFFCu);
        var writeValue = payload[7];
        if (!pollsMemory || compareFunction == 7 || readAddress == 0 || (writesMemory && writeAddress == 0))
        {
            throw _host.Fatal(
                $"The conditional-write packet is not supported: compare={compareFunction} pollMemory={pollsMemory} " +
                $"read=0x{readAddress:X16} write=0x{writeAddress:X16} address=0x{packet.PacketAddress:X16}.");
        }

        var observed = ReadDword(readAddress);
        _ = WaitOperation.TryCompare(observed, reference, mask, compareFunction, out var passed);
        if (!passed)
        {
            return 8;
        }

        if (writesMemory)
        {
            WriteDword(writeAddress, writeValue);
        }
        else
        {
            ConditionalWaitEnabled = writeValue != 0;
        }

        return 8;
    }

    // Wrapped waits carry the address first; the 32-bit form has a six- and a seven-dword layout.
    internal uint WrappedWaitPacket(in PacketContext packet, ReadOnlySpan<uint> payload)
    {
        var is64Bit = packet.CustomCode == PacketCustomCode.WaitMemory64;
        var address = Address(payload[0], payload[1]);
        ulong mask;
        ulong reference;
        uint control;
        if (is64Bit)
        {
            if (packet.Length < 9)
            {
                throw _host.Fatal($"The wrapped 64-bit wait is too short: length={packet.Length} address=0x{packet.PacketAddress:X16}.");
            }

            mask = Address(payload[2], payload[3]);
            reference = Address(payload[4], payload[5]);
            control = payload[6];
        }
        else if (packet.Length == 6)
        {
            mask = payload[2];
            control = payload[3];
            reference = payload[4];
        }
        else if (packet.Length >= 7)
        {
            mask = payload[2];
            reference = payload[3];
            control = payload[4];
        }
        else
        {
            throw _host.Fatal($"The wrapped wait is too short: length={packet.Length} address=0x{packet.PacketAddress:X16}.");
        }

        if (address == 0 || mask == 0)
        {
            if (Interlocked.Increment(ref _wrappedWaitSkipWarnings) <= 16)
            {
                Console.Error.WriteLine($"[LOADER][WARN] command_stream.wait_skipped address=0x{address:X16} mask=0x{mask:X16} packet=0x{packet.PacketAddress:X16}");
            }

            return packet.Length - 1;
        }

        WaitOnMemory(control & 0x7u, address, reference, mask, WaitOperation.Decode(control, is64Bit), is64Bit);
        return packet.Length - 1;
    }

    internal uint WrappedWriteDataPacket(in PacketContext packet, ReadOnlySpan<uint> payload)
    {
        if (packet.Length < 4)
        {
            throw _host.Fatal($"The wrapped write-data packet is too short: length={packet.Length} address=0x{packet.PacketAddress:X16}.");
        }

        var control = WriteDataControl.DecodeAgc(payload[0]);
        var destination = Address(payload[1], payload[2]);
        if (control.Destination is 1 or 2 or 4 or 5)
        {
            WriteDataValues(destination, payload[3..], !control.IncrementAddress);
        }

        return packet.Length - 1;
    }

    // Two builder layouts: eight dwords with cache policies, or seven with the address first.
    internal uint WrappedDmaDataPacket(in PacketContext packet, ReadOnlySpan<uint> payload)
    {
        if (packet.Length == 8)
        {
            var selectors = payload[0];
            var controls = payload[1];
            TransferData(
                engine: controls & 0xFFu,
                destinationSelect: selectors & 0xFFu,
                destinationCachePolicy: (selectors >> 8) & 0x3u,
                destination: Address(payload[3], payload[4]),
                sourceSelect: (selectors >> 16) & 0xFFu,
                sourceCachePolicy: (selectors >> 24) & 0x3u,
                sourceOrImmediate: Address(payload[5], payload[6]),
                byteCount: payload[2],
                waitForPrevious: (controls >> 8) & 0xFFu,
                writeConfirm: (controls >> 16) & 0xFFu,
                blockEngine: (controls >> 24) & 0xFFu);
            return 7;
        }

        if (packet.Length == 7)
        {
            var selectors = payload[5];
            TransferData(
                engine: 0,
                destinationSelect: (selectors >> 8) & 0xFFu,
                destinationCachePolicy: 0,
                destination: Address(payload[0], payload[1]),
                sourceSelect: selectors & 0xFFu,
                sourceCachePolicy: 0,
                sourceOrImmediate: Address(payload[2], payload[3]),
                byteCount: payload[4],
                waitForPrevious: 0,
                writeConfirm: 0,
                blockEngine: 0);
            return 6;
        }

        throw _host.Fatal($"The wrapped DMA packet length is not supported: length={packet.Length} address=0x{packet.PacketAddress:X16}.");
    }

    // Bit 20 selects a call (the parent continues) or a chain (the parent ends).
    internal uint IndirectBufferPacket(in PacketContext packet, ReadOnlySpan<uint> payload)
    {
        if (packet.Length == 14)
        {
            return BranchPacket(packet, payload);
        }

        if (packet.Length != 4)
        {
            throw _host.Fatal($"The indirect-buffer packet length is not supported: length={packet.Length} address=0x{packet.PacketAddress:X16}.");
        }

        var control = payload[2];
        var address = payload[0] | ((ulong)(payload[1] & 0xFFFFu) << 32);
        var dwordCount = control & 0xFFFFFu;
        var jumpMode = (control >> 20) & 0x1u;
        if (address != 0 && dwordCount != 0)
        {
            if (jumpMode == 0 && packet.Remaining > packet.Length)
            {
                RunIndirectBuffer(address, dwordCount);
                return 3;
            }

            ChainToBuffer(address, dwordCount);
            return 3;
        }

        // Target one with no size continues at the next contiguous ring chunk.
        var ringChunkBase = RequireExecution().Top.RingChunkBase;
        if (address == 1 && ringChunkBase != 0)
        {
            var nextChunk = ringChunkBase + RingChunkBytes;
            ChainToBuffer(nextChunk, RingChunkBytes / sizeof(uint), followedChunkAdvance: true);
        }

        return 3;
    }

    internal uint BranchPacket(in PacketContext packet, ReadOnlySpan<uint> payload)
    {
        var compareAddress = (payload[1] & 0xFFFF_FFF8u) | ((ulong)payload[2] << 32);
        var mask = Address(payload[3], payload[4]);
        var reference = Address(payload[5], payload[6]);
        var mode = payload[0] & 0x3u;
        var function = (payload[0] >> 8) & 0x7u;
        var thenBuffer = (payload[7] & 0xFFFF_FFFCu) | ((ulong)payload[8] << 32);
        var thenDwords = payload[9] & 0xFFFFFu;
        var elseBuffer = (payload[10] & 0xFFFF_FFFCu) | ((ulong)payload[11] << 32);
        var elseDwords = payload[12] & 0xFFFFFu;
        if (compareAddress == 0 || mode is not (1 or 2) || function > 6 || thenBuffer == 0 || thenDwords == 0)
        {
            throw _host.Fatal(
                $"The branch packet is not supported: compare=0x{compareAddress:X16} mode={mode} function={function} " +
                $"then=0x{thenBuffer:X16}/{thenDwords} else=0x{elseBuffer:X16}/{elseDwords} address=0x{packet.PacketAddress:X16}.");
        }

        _ = WaitOperation.TryCompare(ReadQword(compareAddress), reference, mask, function, out var takeThen);
        if (takeThen)
        {
            RunIndirectBuffer(thenBuffer, thenDwords);
        }
        else if (mode == 2 && elseDwords != 0)
        {
            if (elseBuffer == 0)
            {
                throw _host.Fatal($"The branch else buffer is zero: dwords={elseDwords} address=0x{packet.PacketAddress:X16}.");
            }

            RunIndirectBuffer(elseBuffer, elseDwords);
        }

        return 13;
    }

    internal uint IndexCountPacket(in PacketContext packet, ReadOnlySpan<uint> payload)
    {
        SetIndexBufferSize(payload[0]);
        return packet.Length - 1;
    }

    internal uint WrappedDrawIndexAutoPacket(in PacketContext packet, ReadOnlySpan<uint> payload)
    {
        if (payload[0] != 0)
        {
            DrawAuto(packet.PacketAddress, PacketOpcode.DrawIndexAuto, payload[0]);
        }

        return packet.Length - 1;
    }
}
