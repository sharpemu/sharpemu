// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Pipelines;

namespace SharpEmu.Libs.Gpu.GpuCommands;

public sealed partial class GpuCommandInterpreter
{
    private const int TicketScanChunkDwords = 256;
    private const uint TicketTableEntries = 512;

    private RegisterBanks? _ticketShadow;
    private uint[]? _ticketWords;
    private ulong[]? _ticketFound;
    private byte[]? _ticketTable;

    // SHARPEMU_COMPUTE_TICKETS: requests a ticket for each direct dispatch that follows this one in its buffer, with the
    // compute registers predicted on a shadow copy. Shader register writes and tables, events, write-data packets and inert
    // NOPs pass; any other packet ends the scan, so nothing between two scanned dispatches changes a register the prediction
    // does not follow. A wrong prediction only costs the ticket: the dispatch compares the real registers before it takes
    // one, and the bytes the ticket read before it uses one.
    private void ScanComputeTickets(ComputeTickets tickets, in PacketContext packet, ulong requestedThrough)
    {
        var found = _ticketFound ??= new ulong[ComputeTickets.MaxLead];
        var count = 0;
        var stopped = ScanComputeTicketsCore(tickets, in packet, requestedThrough, found, ref count);
        tickets.EndScan(this, packet.PacketAddress, found.AsSpan(0, count), stopped);
    }

    // True when the scan ended at a packet it cannot pass or at the end of the buffer; false when it stopped for its own
    // limits.
    private bool ScanComputeTicketsCore(ComputeTickets tickets, in PacketContext packet, ulong requestedThrough, ulong[] found, ref int count)
    {
        if (packet.Remaining <= packet.Length)
        {
            return true;
        }

        var start = packet.PacketAddress + (ulong)packet.Length * sizeof(uint);
        var remaining = packet.Remaining - packet.Length;
        var available = (int)Math.Min(remaining, (uint)ComputeTickets.MaxScanDwords);
        var words = _ticketWords ??= new uint[ComputeTickets.MaxScanDwords];
        var read = 0;
        var shadow = _ticketShadow ??= new RegisterBanks(_host.Fatal);
        shadow.Shader.Compute = TypedRegisters.Shader.Compute.Copy();
        var position = 0;
        // The third dispatch of one shader in a row starts a deferred dispatch run, whose workers resolve it and the dispatches
        // after it: tickets past it would go unused.
        var runStops = _runs is { StopsTicketScan: true };
        var runAddress = TypedRegisters.Shader.Compute.Address;
        var runLength = 1;
        while (position < available)
        {
            if (!EnsureTicketWords(start, words, ref read, position + 1, available))
            {
                return true;
            }

            var header = words[position];
            if (header == PacketHeader.FillerHeader)
            {
                position++;
                continue;
            }

            var length = (int)PacketHeader.Length(header);
            if (header == 0 || length < 2)
            {
                return true;
            }

            if (length > available - position)
            {
                break;
            }

            if (!EnsureTicketWords(start, words, ref read, position + length, available) || PacketHeader.IsPredicated(header))
            {
                return true;
            }

            var opcode = PacketHeader.Opcode(header);
            var address = start + (ulong)position * sizeof(uint);
            var payload = words.AsSpan(position + 1, length - 1);
            if (opcode == PacketOpcode.SetShaderRegister)
            {
                var offset = payload[0];
                var values = (uint)length - 2;
                if (offset != RegisterBankLayout.ShaderRegisterNop)
                {
                    if (offset >= RegisterBankLayout.ShaderRegisterCount || values > RegisterBankLayout.ShaderRegisterCount - offset)
                    {
                        return true;
                    }

                    var context = new PacketContext(header & ~1u, address, (uint)position, (uint)(available - position), (uint)available);
                    if (RegisterWriteTable.WriteShaderPacket(shadow, in context, offset, payload.Slice(1, (int)values)) != values)
                    {
                        return true;
                    }
                }
            }
            else if (opcode == PacketOpcode.SetShaderRegisterIndirect)
            {
                if (length != 5 || !ScanShaderRegisterTable(shadow, address, (payload[0] & 0xFFFF_FFFCu) | ((ulong)payload[1] << 32), payload[3] & 0x3FFFu))
                {
                    return true;
                }
            }
            else if (opcode == PacketOpcode.Nop)
            {
                var custom = PacketHeader.CustomCode(header);
                if (custom == PacketCustomCode.ShaderRegisterTable)
                {
                    if (length < 4 || !ScanShaderRegisterTable(shadow, address, Address(payload[1], payload[2]), payload[0]))
                    {
                        return true;
                    }
                }
                else if (custom == PacketCustomCode.Zero && !payload.IsEmpty && (payload[0] & 0xFFFF_0000u) == MarkerMagic)
                {
                    // The marker ids with a side effect (MarkerPacket) are flips. The user-data markers only set an interpreter
                    // property no register write reads.
                    if ((payload[0] & 0xFFFu) is 0x777 or 0x778 or 0x781)
                    {
                        return true;
                    }
                }
                else if (!(custom is PacketCustomCode.Zero or PacketCustomCode.PushMarker or PacketCustomCode.PopMarker or PacketCustomCode.AcquireMemory or
                    PacketCustomCode.WriteData))
                {
                    // An acquire is a GPU ordering point and a write-data writes memory only: neither changes a register, and a
                    // ticket checks its reads when it is taken.
                    return true;
                }
            }
            else if (opcode is PacketOpcode.EventWrite or PacketOpcode.WriteData)
            {
                // Events order GPU work and change no register; a dispatch's ticket checks its reads when it is taken.
            }
            else if (opcode == PacketOpcode.DispatchDirect && length == 5)
            {
                if (runStops)
                {
                    var compute = shadow.Shader.Compute.Address;
                    runLength = compute == runAddress ? runLength + 1 : 1;
                    runAddress = compute;
                    if (runLength >= 3 && _runs!.WouldDefer(shadow.Shader.Compute, payload[3]))
                    {
                        return true;
                    }
                }

                if (address > requestedThrough)
                {
                    tickets.Request(this, address, shadow.Shader.Compute, payload[0], payload[1], payload[2], payload[3]);
                    found[count++] = address;
                    if (count == found.Length || tickets.LeadCount >= ComputeTickets.MaxLead)
                    {
                        return false;
                    }
                }
            }
            else
            {
                return true;
            }

            position += length;
        }

        return (uint)position >= remaining;
    }

    // Reads the command words up to needed, in chunks, so a scan that stops early reads little.
    private bool EnsureTicketWords(ulong start, uint[] words, ref int read, int needed, int available)
    {
        if (needed <= read)
        {
            return true;
        }

        var target = Math.Min(available, Math.Max(needed, read + TicketScanChunkDwords));
        if (!TryReadCommand(start + (ulong)read * sizeof(uint), MemoryMarshal.AsBytes(words.AsSpan(read, target - read))))
        {
            return false;
        }

        read = target;
        return true;
    }

    // A shader register table as the packet would apply it: the copy retained at submission when there is one, otherwise
    // the guest bytes. False for a table the GPU wrote, one too large, or one the scan cannot read: the scan ends there.
    private bool ScanShaderRegisterTable(RegisterBanks shadow, ulong packetAddress, ulong tableAddress, uint count)
    {
        if (count == 0)
        {
            return true;
        }

        const int entryBytes = 2 * sizeof(uint);
        if (tableAddress == 0 || count > TicketTableEntries || tableAddress > ulong.MaxValue - (ulong)count * entryBytes)
        {
            return false;
        }

        var bytes = (int)count * entryBytes;
        if (!_submittedCommands.IsEmpty && _host.HasGpuWrittenCommandData(tableAddress, (ulong)bytes))
        {
            return false;
        }

        ReadOnlySpan<byte> table;
        if (_submittedRegisterTables is { } tables && tables.TryGetValue(packetAddress, out var submitted) && submitted.Length == bytes)
        {
            table = submitted;
        }
        else
        {
            var buffer = _ticketTable ??= new byte[(int)TicketTableEntries * entryBytes];
            if (!TryReadCommand(tableAddress, buffer.AsSpan(0, bytes)))
            {
                return false;
            }

            table = buffer.AsSpan(0, bytes);
        }

        for (var index = 0; index < bytes; index += entryBytes)
        {
            var rawOffset = BinaryPrimitives.ReadUInt32LittleEndian(table[index..]);
            var value = BinaryPrimitives.ReadUInt32LittleEndian(table[(index + sizeof(uint))..]);
            var offset = RegisterBankLayout.Normalize(rawOffset);
            if (offset == RegisterBankLayout.ShaderRegisterNop || rawOffset == RegisterTableSentinel)
            {
                continue;
            }

            if (offset >= RegisterBankLayout.ShaderRegisterCount)
            {
                return false;
            }

            RegisterWriteTable.WriteShaderEntry(shadow, offset, value, tableAddress);
        }

        return true;
    }
}
