// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.GpuCommands.Registers;

// A direct writer takes the packet's values from its first offset and returns how many it consumed.
public delegate uint RegisterPacketWriter(RegisterBanks banks, in PacketContext packet, uint offset, ReadOnlySpan<uint> values);

// An indirect writer stores one register value.
public delegate void RegisterWriter(RegisterBanks banks, uint offset, uint value);

// Two tiers per bank: a packet tries its direct writer, then needs an indirect writer for every value.
public static class RegisterWriteTable
{
    public static readonly RegisterPacketWriter?[] Context = new RegisterPacketWriter?[RegisterBankLayout.ContextRegisterCount];
    public static readonly RegisterPacketWriter?[] Shader = new RegisterPacketWriter?[RegisterBankLayout.ShaderRegisterCount];
    public static readonly RegisterPacketWriter?[] UserConfig = new RegisterPacketWriter?[RegisterBankLayout.UserConfigRegisterCount];
    public static readonly RegisterWriter?[] ContextIndirect = new RegisterWriter?[RegisterBankLayout.ContextRegisterCount];
    public static readonly RegisterWriter?[] ShaderIndirect = new RegisterWriter?[RegisterBankLayout.ShaderRegisterCount];
    public static readonly RegisterWriter?[] UserConfigIndirect = new RegisterWriter?[RegisterBankLayout.UserConfigRegisterCount];

    static RegisterWriteTable()
    {
        RegisterWriters.FillContext(Context, ContextIndirect);
        RegisterWriters.FillShader(Shader, ShaderIndirect);
        RegisterWriters.FillUserConfig(UserConfig, UserConfigIndirect);
    }

    public static uint WriteContextPacket(RegisterBanks banks, in PacketContext packet, uint offset, ReadOnlySpan<uint> values)
    {
        banks.PrepareContextWrite();
        var consumed = WritePacket(banks, in packet, offset, values, Context, ContextIndirect, "context", emptyIsHandled: true);
        banks.MarkWritten(RegisterStamps.ContextBitsFor(offset, consumed));
        if (Rendering.RenderTrace.Enabled)
        {
            for (var index = 0; index < values.Length && index < consumed; index++)
            {
                TraceDepthStateWrite(offset + (uint)index, values[index], packet.PacketAddress, "packet");
            }
        }

        return consumed;
    }

    // A shader packet without values has no writer; the other banks accept it.
    public static uint WriteShaderPacket(RegisterBanks banks, in PacketContext packet, uint offset, ReadOnlySpan<uint> values)
    {
        banks.PrepareShaderWrite(offset, (uint)values.Length);
        var consumed = WritePacket(banks, in packet, offset, values, Shader, ShaderIndirect, "shader", emptyIsHandled: false);
        banks.MarkWritten(RegisterStamps.ShaderBitsFor(offset, consumed));
        if (Rendering.RenderTrace.Enabled)
        {
            for (var index = 0; index < values.Length && index < consumed; index++)
                TracePixelRegisterWrite(banks, offset + (uint)index, values[index], packet.PacketAddress, "packet");
        }
        return consumed;
    }

    public static uint WriteUserConfigPacket(RegisterBanks banks, in PacketContext packet, uint offset, ReadOnlySpan<uint> values)
    {
        banks.PrepareUserConfigWrite();
        var consumed = WritePacket(banks, in packet, offset, values, UserConfig, UserConfigIndirect, "user-config", emptyIsHandled: true);
        banks.MarkWritten(RegisterStamps.UserConfigBits);
        return consumed;
    }

    public static void WriteContextEntry(RegisterBanks banks, uint offset, uint value, ulong tableAddress)
    {
        banks.PrepareContextWrite();
        if (offset < ContextIndirect.Length && ContextIndirect[offset] is null)
        {
            // Keep unknown in-bank table values so a later renderer implementation can decode them.
            banks.UnmodeledTableRegistersForWrite[offset] = value;
        }
        else
        {
            WriteEntry(banks, offset, value, ContextIndirect, "context", tableAddress);
        }

        banks.MarkWritten(RegisterStamps.ContextBitsFor(offset, 1));
        if (Rendering.RenderTrace.Enabled)
        {
            TraceDepthStateWrite(offset, value, tableAddress, "table");
        }
    }

    private static void TraceDepthStateWrite(uint offset, uint value, ulong address, string source)
    {
        if (offset != ContextRegisterOffset.PaClClipCntl &&
            offset != ContextRegisterOffset.PaClVteCntl &&
            offset != ContextRegisterOffset.PaClViewportXScale + 4 &&
            offset != ContextRegisterOffset.PaClViewportXScale + 5)
        {
            return;
        }

        Rendering.RenderTrace.Write($"DepthStateWrite source={source} address=0x{address:X16} register=0x{offset:X4} value=0x{value:X8}");
    }

    public static void WriteShaderEntry(RegisterBanks banks, uint offset, uint value, ulong tableAddress)
    {
        banks.PrepareShaderWrite(offset, 1);
        WriteEntry(banks, offset, value, ShaderIndirect, "shader", tableAddress);
        banks.MarkWritten(RegisterStamps.ShaderBitsFor(offset, 1));
        if (Rendering.RenderTrace.Enabled)
            TracePixelRegisterWrite(banks, offset, value, tableAddress, "table");
    }

    private static void TracePixelRegisterWrite(RegisterBanks banks, uint offset, uint value, ulong address, string source)
    {
        if (offset != ShaderRegisterOffset.SpiShaderPgmLoPs && offset != ShaderRegisterOffset.SpiShaderPgmHiPs &&
            offset != ShaderRegisterOffset.SpiShaderPgmRsrc2Ps &&
            (offset < ShaderRegisterOffset.SpiShaderUserDataPs0 || offset >= ShaderRegisterOffset.SpiShaderUserDataPs0 + 32))
            return;

        Rendering.RenderTrace.Write($"PixelRegisterWrite source={source} address=0x{address:X16} register=0x{offset:X4} " +
            $"value=0x{value:X8} shader=0x{banks.Shader.Pixel.Address:X16}");
    }

    public static void WriteUserConfigEntry(RegisterBanks banks, uint offset, uint value, ulong tableAddress,
        PacketContext packet = default, uint entryIndex = 0, uint entryCount = 0, uint rawOffset = 0,
        ReadOnlySpan<uint> payload = default)
    {
        banks.PrepareUserConfigWrite();
        if (offset >= UserConfigIndirect.Length || UserConfigIndirect[offset] is null)
        {
            throw banks.Fatal($"The user-config table register is not supported: offset=0x{offset:X4} value=0x{value:X8} " +
                $"table=0x{tableAddress:X16} raw=0x{rawOffset:X8} entry={entryIndex} count={entryCount} " +
                $"header=0x{packet.Header:X8} packet=0x{packet.PacketAddress:X16} " +
                $"payload={string.Join(',', payload.ToArray().Select(word => word.ToString("X8")))}.");
        }

        UserConfigIndirect[offset]!(banks, offset, value);
        banks.MarkWritten(RegisterStamps.UserConfigBits);
    }

    private static uint WritePacket(
        RegisterBanks banks,
        in PacketContext packet,
        uint offset,
        ReadOnlySpan<uint> values,
        RegisterPacketWriter?[] direct,
        RegisterWriter?[] indirect,
        string bank,
        bool emptyIsHandled)
    {
        var writer = offset < direct.Length ? direct[offset] : null;
        if (writer is not null)
        {
            var consumed = writer(banks, in packet, offset, values);
            if (consumed == 0)
            {
                throw banks.Fatal($"The {bank} register writer consumed no values: offset=0x{offset:X4} header=0x{packet.Header:X8} address=0x{packet.PacketAddress:X16}.");
            }

            return consumed;
        }

        if (values.Length == 0 && !emptyIsHandled)
        {
            throw NotSupported(banks, in packet, bank, offset, values.Length, offset);
        }

        for (var index = 0u; index < values.Length; index++)
        {
            if (offset + index >= indirect.Length || indirect[offset + index] is null)
            {
                throw NotSupported(banks, in packet, bank, offset, values.Length, offset + index);
            }
        }

        for (var index = 0u; index < values.Length; index++)
        {
            indirect[offset + index]!(banks, offset + index, values[(int)index]);
        }

        return (uint)values.Length;
    }

    private static void WriteEntry(RegisterBanks banks, uint offset, uint value, RegisterWriter?[] indirect, string bank, ulong tableAddress)
    {
        var writer = offset < indirect.Length ? indirect[offset] : null;
        if (writer is null)
        {
            throw banks.Fatal($"The {bank} table register is not supported: offset=0x{offset:X4} value=0x{value:X8} table=0x{tableAddress:X16}.");
        }

        writer(banks, offset, value);
    }

    private static Exception NotSupported(RegisterBanks banks, in PacketContext packet, string bank, uint offset, int count, uint unsupported) =>
        banks.Fatal($"The {bank} register is not supported: offset=0x{offset:X4} count={count} unsupported=0x{unsupported:X4} header=0x{packet.Header:X8} address=0x{packet.PacketAddress:X16}.");
}
