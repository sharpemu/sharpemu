// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;

namespace SharpEmu.Libs.Gpu.GpuCommands;

internal static class SkippedIndexedDrawScanner
{
    internal static void Scan(IndexedDrawTrace.ReadGuestBytes read, ulong address, uint dwords,
        uint indexType, ulong indexBase, Action<uint, uint, ulong, ulong> candidate, Action<string> incomplete)
    {
        var remainingPackets = 4096;
        ScanRange(address, dwords, indexType, indexBase, true, 0);

        void ScanRange(ulong start, uint count, uint type, ulong baseAddress, bool baseKnown, int depth)
        {
            if (depth >= 8 || start > ulong.MaxValue - (ulong)count * sizeof(uint))
            {
                incomplete("range-or-depth-limit");
                return;
            }
            Span<byte> bytes = stackalloc byte[36];
            for (uint offset = 0; offset < count;)
            {
                if (--remainingPackets < 0) { incomplete("packet-limit"); return; }
                var packet = start + (ulong)offset * sizeof(uint);
                if (!read(packet, bytes[..4])) { incomplete("unreadable-header"); return; }
                var header = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
                if (header == PacketHeader.FillerHeader) { offset++; continue; }
                var length = PacketHeader.Length(header);
                if (PacketHeader.PacketType(header) != 3 || length > count - offset)
                {
                    incomplete("invalid-packet");
                    return;
                }
                var opcode = PacketHeader.Opcode(header);
                if (!read(packet, bytes[..(int)(Math.Min(length, 9u) * sizeof(uint))]))
                {
                    incomplete("unreadable-packet");
                    return;
                }
                var first = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
                var second = length >= 3 ? BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]) : 0;
                var third = length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]) : 0;
                switch (opcode)
                {
                    case PacketOpcode.IndexType when length == 2:
                        type = first & 3;
                        break;
                    case PacketOpcode.IndexBase when length == 3:
                        baseAddress = first | ((ulong)second << 32);
                        baseKnown = true;
                        break;
                    case PacketOpcode.DrawIndex2 when length == 6:
                        candidate(BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]), type,
                            second | ((ulong)third << 32), packet);
                        break;
                    case PacketOpcode.DispatchDrawPreamble when length == 9:
                        candidate(first, type, second | ((ulong)third << 32), packet);
                        break;
                    case PacketOpcode.DrawIndexOffset2 when length == 5:
                        var elementSize = type switch { 0 => 2u, 1 => 4u, 2 => 1u, _ => 0u };
                        var byteOffset = (ulong)second * elementSize;
                        if (!baseKnown || elementSize == 0 || baseAddress > ulong.MaxValue - byteOffset)
                            incomplete("invalid-index-offset");
                        else
                            candidate(third, type, baseAddress + byteOffset, packet);
                        break;
                    case PacketOpcode.IndirectBuffer when length == 4:
                        ScanRange(first | ((ulong)(second & 0xFFFFu) << 32), third & 0xFFFFFu,
                            type, baseAddress, baseKnown, depth + 1);
                        // The child can change state. Do not infer the parent's later index state.
                        type = 3;
                        baseKnown = false;
                        if ((third & (1u << 20)) != 0) return;
                        break;
                    case PacketOpcode.DrawIndexIndirect:
                    case PacketOpcode.DrawIndexIndirectMulti:
                        incomplete("indirect-draw-arguments");
                        break;
                    case PacketOpcode.ConditionalExecute:
                    case PacketOpcode.Rewind:
                    case PacketOpcode.ClearState:
                        incomplete("conditional-or-reset-state");
                        type = 3;
                        baseKnown = false;
                        break;
                }
                offset += length;
            }
        }
    }
}
