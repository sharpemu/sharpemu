// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.VideoOut;

namespace SharpEmu.Libs.Gpu.GpuCommands;

public sealed partial class GpuCommandInterpreter
{
    private const uint RegisterTableSentinel = 0xFFFF_FFFFu;

    private static int _contextTableSkipWarnings;

    private enum RegisterBank
    {
        Context,
        Shader,
        UserConfig,
    }

    private void WriteContextRegister(uint offset, uint value)
    {
        Registers.Context[offset] = value;
        if (offset is RegisterBankLayout.DepthZInfo or RegisterBankLayout.DepthSizeXy)
        {
            // A direct attachment or extent write supersedes a composite binding's extent.
            Registers.CompositeDepthSizeXy = null;
        }
    }

    private void WriteUserConfigRegister(uint offset, uint value)
    {
        Registers.UserConfig[offset] = value;
        if (offset == RegisterBankLayout.IndexTypeRegister)
        {
            SetIndexType(value);
        }
    }

    internal uint SetContextRegisterPacket(in PacketContext packet, ReadOnlySpan<uint> payload)
    {
        var offset = RegisterBankLayout.Normalize(payload[0]);
        if (offset == RegisterBankLayout.ContextNop)
        {
            return 2;
        }

        if (offset >= RegisterBankLayout.ContextRegisterCount)
        {
            throw _host.Fatal($"The context register is outside the bank: offset=0x{offset:X8} value=0x{payload[1]:X8} header=0x{packet.Header:X8} address=0x{packet.PacketAddress:X16}.");
        }

        var count = packet.Length - 2;
        for (var index = 0u; index < count; index++)
        {
            if (offset + index >= RegisterBankLayout.ContextRegisterCount)
            {
                throw _host.Fatal($"The context register range leaves the bank: offset=0x{offset:X8} count={count} address=0x{packet.PacketAddress:X16}.");
            }

            WriteContextRegister(offset + index, payload[1 + (int)index]);
        }

        if (offset == RegisterBankLayout.DepthZInfo && packet.Length == CompositeDepthBindingLeadLength)
        {
            DetectCompositeDepthExtent(packet);
        }

        return count + 1;
    }

    private const uint CompositeDepthBindingLeadLength = 10;
    private const uint CompositeDepthBindingDwords = 24;

    // A composite depth binding ends in a NOP that carries the x/y maxima, not padding.
    private void DetectCompositeDepthExtent(in PacketContext packet)
    {
        if (packet.Remaining < CompositeDepthBindingDwords)
        {
            return;
        }

        var address = packet.PacketAddress;
        var registerHeader = PacketHeader.Make(3, PacketOpcode.SetContextRegister);
        if (ReadDword(address + 40) != registerHeader || ReadDword(address + 44) != RegisterBankLayout.DepthInfo ||
            ReadDword(address + 52) != registerHeader || ReadDword(address + 56) != RegisterBankLayout.DepthView ||
            ReadDword(address + 64) != registerHeader || ReadDword(address + 68) != RegisterBankLayout.HtileDataBase ||
            ReadDword(address + 76) != registerHeader || ReadDword(address + 80) != RegisterBankLayout.HtileSurface ||
            ReadDword(address + 88) != PacketHeader.Make(2, PacketOpcode.Nop))
        {
            return;
        }

        var sizeXy = ReadDword(address + 92);
        if (sizeXy != 0)
        {
            Registers.CompositeDepthSizeXy = sizeXy;
        }
    }

    internal uint SetShaderRegisterPacket(in PacketContext packet, ReadOnlySpan<uint> payload)
    {
        var offset = payload[0];
        if (offset == RegisterBankLayout.ShaderRegisterNop)
        {
            return 2;
        }

        if (offset >= RegisterBankLayout.ShaderRegisterCount)
        {
            throw _host.Fatal($"The shader register is outside the bank: offset=0x{offset:X8} header=0x{packet.Header:X8} address=0x{packet.PacketAddress:X16}.");
        }

        var count = packet.Length - 2;
        for (var index = 0u; index < count; index++)
        {
            if (offset + index >= RegisterBankLayout.ShaderRegisterCount)
            {
                throw _host.Fatal($"The shader register range leaves the bank: offset=0x{offset:X8} count={count} address=0x{packet.PacketAddress:X16}.");
            }

            Registers.Shader[offset + index] = payload[1 + (int)index];
        }

        return count + 1;
    }

    internal uint SetUserConfigRegisterPacket(in PacketContext packet, ReadOnlySpan<uint> payload)
    {
        if (InternalDataPacket.Matches(packet.Header, payload))
        {
            return packet.Length - 1;
        }

        var rawOffset = payload[0];
        var offset = packet.Opcode == PacketOpcode.SetUserConfigRegisterIndex
            ? rawOffset & 0x0FFF_FFFFu
            : RegisterBankLayout.Normalize(rawOffset);
        if (offset == RegisterBankLayout.UserConfigNop)
        {
            return packet.Length - 1;
        }

        if (offset >= RegisterBankLayout.UserConfigRegisterCount)
        {
            throw _host.Fatal($"The user-config register is outside the bank: offset=0x{offset:X8} raw=0x{rawOffset:X8} header=0x{packet.Header:X8} address=0x{packet.PacketAddress:X16}.");
        }

        var count = packet.Length - 2;
        for (var index = 0u; index < count; index++)
        {
            if (offset + index >= RegisterBankLayout.UserConfigRegisterCount)
            {
                throw _host.Fatal($"The user-config register range leaves the bank: offset=0x{offset:X8} count={count} address=0x{packet.PacketAddress:X16}.");
            }

            WriteUserConfigRegister(offset + index, payload[1 + (int)index]);
        }

        return count + 1;
    }

    // Native table form: 64-bit table address, then a count of (offset, value) pairs.
    internal uint SetContextRegisterTablePacket(in PacketContext packet, ReadOnlySpan<uint> payload) =>
        RegisterTablePacket(packet, payload, RegisterBank.Context);

    internal uint SetShaderRegisterTablePacket(in PacketContext packet, ReadOnlySpan<uint> payload) =>
        RegisterTablePacket(packet, payload, RegisterBank.Shader);

    internal uint SetUserConfigRegisterTablePacket(in PacketContext packet, ReadOnlySpan<uint> payload) =>
        RegisterTablePacket(packet, payload, RegisterBank.UserConfig);

    private uint RegisterTablePacket(in PacketContext packet, ReadOnlySpan<uint> payload, RegisterBank bank)
    {
        if (packet.Length != 5)
        {
            throw _host.Fatal($"The register table header is not supported: header=0x{packet.Header:X8} address=0x{packet.PacketAddress:X16}.");
        }

        var tableAddress = (payload[0] & 0xFFFF_FFFCu) | ((ulong)payload[1] << 32);
        var count = payload[3] & 0x3FFFu;
        ApplyRegisterTable(bank, tableAddress, count, packet);
        return 4;
    }

    // Read bounded blocks when the packet executes. Apply entries in table order.
    private void ApplyRegisterTable(RegisterBank bank, ulong tableAddress, uint count, in PacketContext packet)
    {
        if (count == 0)
        {
            return;
        }

        if (tableAddress == 0)
        {
            throw _host.Fatal($"The register table address is zero: bank={bank} count={count} address=0x{packet.PacketAddress:X16}.");
        }

        const int entriesPerBlock = 128;
        const int entryBytes = 2 * sizeof(uint);
        if (tableAddress > ulong.MaxValue - (ulong)count * entryBytes)
        {
            throw _host.Fatal($"The register table range exceeds the address space: address=0x{tableAddress:X16} count={count}.");
        }

        Span<byte> tableBlock = stackalloc byte[entriesPerBlock * entryBytes];
        for (var index = 0u; index < count; index++)
        {
            var blockEntry = (int)(index % entriesPerBlock);
            if (blockEntry == 0)
            {
                var blockBytes = (int)Math.Min(count - index, entriesPerBlock) * entryBytes;
                var blockAddress = tableAddress + (ulong)index * entryBytes;
                RenderPhaseProfile.RecordCommandRead(RenderPhaseProfile.CommandReadKind.RegisterTable, blockBytes);
                if (!_host.TryReadGuest(blockAddress, tableBlock[..blockBytes]))
                {
                    throw _host.Fatal($"The register table cannot be read: address=0x{blockAddress:X16} size={blockBytes}.");
                }
            }

            var entryOffset = blockEntry * entryBytes;
            var rawOffset = BinaryPrimitives.ReadUInt32LittleEndian(tableBlock[entryOffset..]);
            var value = BinaryPrimitives.ReadUInt32LittleEndian(tableBlock[(entryOffset + sizeof(uint))..]);
            var offset = RegisterBankLayout.Normalize(rawOffset);
            switch (bank)
            {
                case RegisterBank.Context:
                    if (offset == RegisterBankLayout.ContextNop || rawOffset == RegisterTableSentinel)
                    {
                        continue;
                    }

                    if (offset >= RegisterBankLayout.ContextRegisterCount)
                    {
                        if (Interlocked.Increment(ref _contextTableSkipWarnings) <= 16)
                        {
                            Console.Error.WriteLine($"[LOADER][WARN] command_stream.context_table_skip offset=0x{offset:X8} value=0x{value:X8}");
                        }

                        continue;
                    }

                    WriteContextRegister(offset, value);
                    break;
                case RegisterBank.Shader:
                    if (offset == RegisterBankLayout.ShaderRegisterNop || rawOffset == RegisterTableSentinel)
                    {
                        continue;
                    }

                    if (offset >= RegisterBankLayout.ShaderRegisterCount)
                    {
                        throw _host.Fatal($"The shader table register is outside the bank: offset=0x{offset:X8} raw=0x{rawOffset:X8} value=0x{value:X8} table=0x{tableAddress:X16}.");
                    }

                    Registers.Shader[offset] = value;
                    break;
                case RegisterBank.UserConfig:
                    if (offset == RegisterBankLayout.UserConfigNop)
                    {
                        continue;
                    }

                    if (offset >= RegisterBankLayout.UserConfigRegisterCount)
                    {
                        throw _host.Fatal($"The user-config table register is outside the bank: offset=0x{offset:X8} raw=0x{rawOffset:X8} value=0x{value:X8} table=0x{tableAddress:X16}.");
                    }

                    // A depth extent in a user-config table belongs to the context bank.
                    if (offset == RegisterBankLayout.DepthSizeXy)
                    {
                        WriteContextRegister(offset, value);
                        continue;
                    }

                    WriteUserConfigRegister(offset, value);
                    break;
            }
        }
    }

    // Wrapped table form: a count, then the 64-bit table address.
    internal uint RegisterTableNopPacket(in PacketContext packet, ReadOnlySpan<uint> payload)
    {
        if (packet.Length < 4)
        {
            throw _host.Fatal($"The wrapped register table is too short: header=0x{packet.Header:X8} address=0x{packet.PacketAddress:X16}.");
        }

        var bank = packet.CustomCode switch
        {
            PacketCustomCode.ContextRegisterTable => RegisterBank.Context,
            PacketCustomCode.ShaderRegisterTable => RegisterBank.Shader,
            _ => RegisterBank.UserConfig,
        };
        ApplyRegisterTable(bank, Address(payload[1], payload[2]), payload[0], packet);
        return packet.Length - 1;
    }
}
