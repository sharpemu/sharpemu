// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using static SharpEmu.Libs.Gpu.GpuCommands.Registers.UserConfigRegisterOffset;

namespace SharpEmu.Libs.Gpu.GpuCommands.Registers;

// The user-config bank writers: primitive assembly and the ordered-append counters.
internal static partial class RegisterWriters
{
    public static void FillUserConfig(RegisterPacketWriter?[] direct, RegisterWriter?[] indirect)
    {
        direct[VgtPrimitiveType] = PrimitiveTypePacket;
        direct[VgtIndexType] = IndexTypePacket;
        direct[VgtObjectId] = ObjectIdPacket;
        direct[TextureGradientFactors] = TextureGradientPacket;
        direct[TextureGradientControl] = TextureGradientPacket;
        direct[IaMultiVgtParam] = IgnoreValues;
        direct[GeMultiPrimIbResetEn] = PrimitiveResetControlPacket;
        direct[TaCsBcBaseAddr] = BorderColorTablePacket;
        direct[TaCsBcBaseAddrHi] = BorderColorTablePacket;
        direct[GeIndexOffset] = IndexOffsetPacket;
        direct[GdsOaCntl] = OrderedAppendPacket;
        direct[GdsOaCounter] = OrderedAppendPacket;
        direct[GdsOaAddress] = OrderedAppendPacket;

        indirect[GeCntl] = static (banks, _, value) => banks.UserConfig.GeometryEngineControl = new GeometryEngineControlRegisters
        {
            PrimitiveGroupSize = (ushort)RegisterField.Get(value, 0, 0x1FF),
            VertexGroupSize = (ushort)RegisterField.Get(value, 9, 0x1FF),
        };
        indirect[ParameterOversubscription] = IgnoreEntry;
        indirect[GeUserVgprEn] = static (banks, _, value) => banks.UserConfig.GeometryEngineUserVectorEnable = new GeometryEngineUserVectorEnableRegisters
        {
            VectorRegister1 = RegisterField.Bit(value, 0),
            VectorRegister2 = RegisterField.Bit(value, 1),
            VectorRegister3 = RegisterField.Bit(value, 2),
        };
        indirect[VgtPrimitiveType] = PrimitiveTypeEntry;
        indirect[VgtIndexType] = IndexTypeEntry;
        indirect[VgtObjectId] = static (banks, _, value) => banks.UserConfig.ObjectId = value;
        indirect[TextureGradientFactors] = IgnoreEntry;
        indirect[TextureGradientControl] = IgnoreEntry;
        indirect[IaMultiVgtParam] = IgnoreEntry;
        indirect[GeMultiPrimIbResetEn] = static (banks, _, value) => banks.UserConfig.PrimitiveResetControl = value;
        indirect[TaCsBcBaseAddr] = IgnoreEntry;
        indirect[TaCsBcBaseAddrHi] = IgnoreEntry;
        indirect[GeIndexOffset] = static (banks, _, value) => banks.UserConfig.IndexOffset = value;
        indirect[GdsOaCntl] = OrderedAppendEntry;
        indirect[GdsOaCounter] = OrderedAppendEntry;
        indirect[GdsOaAddress] = OrderedAppendEntry;
        indirect[GeStereoCntl] = IgnoreEntry;
    }

    private static uint PrimitiveTypePacket(RegisterBanks banks, in PacketContext packet, uint offset, ReadOnlySpan<uint> values) =>
        SingleValue(banks, in packet, offset, values, PrimitiveTypeEntry);

    private static uint IndexTypePacket(RegisterBanks banks, in PacketContext packet, uint offset, ReadOnlySpan<uint> values) =>
        SingleValue(banks, in packet, offset, values, IndexTypeEntry);

    private static uint ObjectIdPacket(RegisterBanks banks, in PacketContext packet, uint offset, ReadOnlySpan<uint> values) =>
        SingleValue(banks, in packet, offset, values, RegisterWriteTable.UserConfigIndirect[VgtObjectId]!);

    private static uint TextureGradientPacket(RegisterBanks banks, in PacketContext packet, uint offset, ReadOnlySpan<uint> values) =>
        RequireUserConfigRange(banks, in packet, offset, values, TextureGradientFactors, TextureGradientControl);

    private static uint PrimitiveResetControlPacket(RegisterBanks banks, in PacketContext packet, uint offset, ReadOnlySpan<uint> values) =>
        SingleValue(banks, in packet, offset, values, RegisterWriteTable.UserConfigIndirect[GeMultiPrimIbResetEn]!);

    private static uint BorderColorTablePacket(RegisterBanks banks, in PacketContext packet, uint offset, ReadOnlySpan<uint> values) =>
        RequireUserConfigRange(banks, in packet, offset, values, TaCsBcBaseAddr, TaCsBcBaseAddrHi);

    private static uint IndexOffsetPacket(RegisterBanks banks, in PacketContext packet, uint offset, ReadOnlySpan<uint> values) =>
        SingleValue(banks, in packet, offset, values, RegisterWriteTable.UserConfigIndirect[GeIndexOffset]!);

    private static uint OrderedAppendPacket(RegisterBanks banks, in PacketContext packet, uint offset, ReadOnlySpan<uint> values) =>
        EachValue(banks, in packet, offset, values, OrderedAppendEntry);

    // The values in the range are ignored; a packet outside the range is fatal.
    private static uint RequireUserConfigRange(RegisterBanks banks, in PacketContext packet, uint offset, ReadOnlySpan<uint> values, uint first, uint last)
    {
        if (values.Length == 0 || offset < first || offset + (uint)values.Length - 1 > last)
        {
            throw banks.Fatal($"The user-config register packet is outside its range: offset=0x{offset:X4} count={values.Length} range=0x{first:X4}-0x{last:X4} header=0x{packet.Header:X8} address=0x{packet.PacketAddress:X16}.");
        }

        return (uint)values.Length;
    }

    private static void PrimitiveTypeEntry(RegisterBanks banks, uint offset, uint value) =>
        banks.UserConfig.PrimitiveType = RegisterField.Get(value, 0, 0x3F);

    private static void IndexTypeEntry(RegisterBanks banks, uint offset, uint value) =>
        banks.IndexTypeAndSize = value & 0x3u;

    private static void OrderedAppendEntry(RegisterBanks banks, uint offset, uint value)
    {
        var append = banks.UserConfig.OrderedAppend;
        switch (offset)
        {
            case GdsOaCntl:
                append.Control = value;
                break;
            case GdsOaCounter:
                append.Counters[append.Index].Counter = value;
                break;
            case GdsOaAddress:
                append.Counters[append.Index].Address = value;
                break;
            default:
                throw banks.Fatal($"The ordered-append register is unknown: offset=0x{offset:X4} value=0x{value:X8}.");
        }
    }
}
