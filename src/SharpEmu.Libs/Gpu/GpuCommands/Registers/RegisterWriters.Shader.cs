// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using static SharpEmu.Libs.Gpu.GpuCommands.Registers.ShaderRegisterOffset;

namespace SharpEmu.Libs.Gpu.GpuCommands.Registers;

// The shader bank writers: stage programs, user scalars and the compute stage.
internal static partial class RegisterWriters
{
    private const uint GraphicsUserScalarCount = 32;
    private const uint ComputeUserScalarCount = 16;
    private const uint UserAccumulatorCount = 4;
    private const uint UserAccumulatorValueMask = 0x7Fu;

    private static readonly uint[] ComputeRegisterOffsets =
    [
        ComputePgmLo, ComputePgmHi, ComputePgmRsrc1, ComputePgmRsrc2, ComputeStartX, ComputeStartY, ComputeStartZ, ComputeNumThreadX,
        ComputeNumThreadY, ComputeNumThreadZ, ComputeResourceLimits, ComputeTmpringSize, ComputePgmRsrc3, ComputeShaderChecksum,
    ];

    private static readonly uint[] IgnoredShaderOffsets =
    [
        SpiShaderPaceIdPs, SpiGraphicsShaderControlPs, SpiShaderPaceIdGs, SpiShaderPgmRsrc4Gs, SpiGraphicsShaderControlGs,
        SpiShaderUserDataAddrLoGs, SpiShaderUserDataAddrHiGs, SpiShaderPgmChksumHs, SpiShaderPgmRsrc4Hs, SpiGraphicsShaderControlHs,
        SpiShaderUserDataAddrLoHs, SpiShaderUserDataAddrHiHs,
    ];

    public static void FillShader(RegisterPacketWriter?[] direct, RegisterWriter?[] indirect)
    {
        for (var slot = 0u; slot < GraphicsUserScalarCount; slot++)
        {
            direct[SpiShaderUserDataPs0 + slot] = PixelUserScalarsPacket;
            direct[SpiShaderUserDataGs0 + slot] = GeometryUserScalarsPacket;
            direct[SpiShaderUserDataHs0 + slot] = HullUserScalarsPacket;
            indirect[SpiShaderUserDataPs0 + slot] = static (banks, offset, value) => UserScalarEntry(banks, banks.Shader.Pixel.UserScalars, offset - SpiShaderUserDataPs0, value);
            indirect[SpiShaderUserDataGs0 + slot] = static (banks, offset, value) => UserScalarEntry(banks, banks.Shader.Vertex.GeometryUserScalars, offset - SpiShaderUserDataGs0, value);
            indirect[SpiShaderUserDataHs0 + slot] = static (banks, offset, value) => UserScalarEntry(banks, banks.Shader.Vertex.HullUserScalars, offset - SpiShaderUserDataHs0, value);
            // The legacy vertex and export user scalars are stored so a title that writes them does not stop.
            indirect[SpiShaderUserDataVs0 + slot] = static (banks, offset, value) => UserScalarEntry(banks, banks.Shader.Vertex.LegacyVertexUserScalars, offset - SpiShaderUserDataVs0, value);
            indirect[SpiShaderUserDataEs0 + slot] = static (banks, offset, value) => UserScalarEntry(banks, banks.Shader.Vertex.ExportUserScalars, offset - SpiShaderUserDataEs0, value);
        }

        for (var slot = 0u; slot < ComputeUserScalarCount; slot++)
        {
            direct[ComputeUserData0 + slot] = ComputeUserScalarsPacket;
            indirect[ComputeUserData0 + slot] = static (banks, offset, value) => UserScalarEntry(banks, banks.Shader.Compute.UserScalars, offset - ComputeUserData0, value);
        }

        for (var slot = 0u; slot < UserAccumulatorCount; slot++)
        {
            foreach (var first in new[] { SpiShaderUserAccumPs0, SpiShaderUserAccumEsGs0, SpiShaderUserAccumLsHs0, ComputeUserAccum0 })
            {
                direct[first + slot] = UserAccumulatorPacket;
                indirect[first + slot] = UserAccumulatorEntry;
            }
        }

        foreach (var offset in ComputeRegisterOffsets)
        {
            direct[offset] = ComputeRegistersPacket;
            indirect[offset] = ComputeRegisterEntry;
        }

        foreach (var offset in IgnoredShaderOffsets)
        {
            indirect[offset] = IgnoreEntry;
        }

        direct[SpiGraphicsShaderControlPs] = ForwardShaderPacket;
        direct[SpiGraphicsShaderControlGs] = ForwardShaderPacket;
        direct[SpiShaderUserDataAddrLoGs] = ForwardShaderPacket;
        direct[SpiShaderUserDataAddrHiGs] = ForwardShaderPacket;
        direct[SpiGraphicsShaderControlHs] = ForwardShaderPacket;
        direct[SpiShaderUserDataAddrLoHs] = ForwardShaderPacket;
        direct[SpiShaderUserDataAddrHiHs] = ForwardShaderPacket;

        indirect[SpiShaderPgmLoHs] = static (banks, _, value) => banks.Shader.Vertex.HullAddress = RegisterField.WithLowAddress(banks.Shader.Vertex.HullAddress, value);
        indirect[SpiShaderPgmHiHs] = static (banks, _, value) => banks.Shader.Vertex.HullAddress = RegisterField.WithHighAddress(banks.Shader.Vertex.HullAddress, value);
        indirect[SpiShaderPgmRsrc1Hs] = static (banks, _, value) => banks.Shader.Vertex.HullResource1 = HullResource1.Decode(value);
        indirect[SpiShaderPgmRsrc2Hs] = static (banks, _, value) => banks.Shader.Vertex.HullResource2 = HullResource2.Decode(value);
        indirect[SpiShaderPgmLoLs] = static (banks, _, value) => banks.Shader.Vertex.LocalAddress = RegisterField.WithLowAddress(banks.Shader.Vertex.LocalAddress, value);
        indirect[SpiShaderPgmHiLs] = static (banks, _, value) => banks.Shader.Vertex.LocalAddress = RegisterField.WithHighAddress(banks.Shader.Vertex.LocalAddress, value);
        indirect[SpiShaderPgmLoEs] = static (banks, _, value) => banks.Shader.Vertex.ExportAddress = RegisterField.WithLowAddress(banks.Shader.Vertex.ExportAddress, value);
        indirect[SpiShaderPgmHiEs] = static (banks, _, value) => banks.Shader.Vertex.ExportAddress = RegisterField.WithHighAddress(banks.Shader.Vertex.ExportAddress, value);
        indirect[SpiShaderPgmRsrc1Es] = static (banks, _, value) => banks.Shader.Vertex.ExportResource1 = value;
        indirect[SpiShaderPgmRsrc2Es] = static (banks, _, value) => banks.Shader.Vertex.ExportResource2 = value;
        indirect[SpiShaderPgmLoGs] = static (banks, _, value) => banks.Shader.Vertex.GeometryAddress = RegisterField.WithLowAddress(banks.Shader.Vertex.GeometryAddress, value);
        indirect[SpiShaderPgmHiGs] = static (banks, _, value) => banks.Shader.Vertex.GeometryAddress = RegisterField.WithHighAddress(banks.Shader.Vertex.GeometryAddress, value);
        indirect[SpiShaderPgmRsrc1Gs] = static (banks, _, value) => banks.Shader.Vertex.GeometryResource1 = GeometryResource1.Decode(value);
        indirect[SpiShaderPgmRsrc2Gs] = static (banks, _, value) => banks.Shader.Vertex.GeometryResource2 = GeometryResource2.Decode(value);
        indirect[SpiShaderPgmLoPs] = static (banks, _, value) => banks.Shader.Pixel.Address = RegisterField.WithLowAddress(banks.Shader.Pixel.Address, value);
        indirect[SpiShaderPgmHiPs] = static (banks, _, value) => banks.Shader.Pixel.Address = RegisterField.WithHighAddress(banks.Shader.Pixel.Address, value);
        indirect[SpiShaderPgmRsrc1Ps] = static (banks, _, value) => banks.Shader.Pixel.Resource1 = PixelResource1.Decode(value);
        indirect[SpiShaderPgmRsrc2Ps] = static (banks, _, value) => banks.Shader.Pixel.Resource2 = PixelResource2.Decode(value);
        indirect[SpiShaderPgmLoVs] = static (banks, _, value) => banks.Shader.Vertex.LegacyVertexAddress = RegisterField.WithLowAddress(banks.Shader.Vertex.LegacyVertexAddress, value);
        indirect[SpiShaderPgmHiVs] = static (banks, _, value) => banks.Shader.Vertex.LegacyVertexAddress = RegisterField.WithHighAddress(banks.Shader.Vertex.LegacyVertexAddress, value);
        indirect[SpiShaderPgmRsrc1Vs] = static (banks, _, value) => banks.Shader.Vertex.LegacyVertexResource1 = value;
        indirect[SpiShaderPgmRsrc2Vs] = static (banks, _, value) => banks.Shader.Vertex.LegacyVertexResource2 = value;
    }

    private static uint PixelUserScalarsPacket(RegisterBanks banks, in PacketContext packet, uint offset, ReadOnlySpan<uint> values) =>
        UserScalarsPacket(banks, in packet, offset, values, banks.Shader.Pixel.UserScalars, SpiShaderUserDataPs0, GraphicsUserScalarCount);

    private static uint GeometryUserScalarsPacket(RegisterBanks banks, in PacketContext packet, uint offset, ReadOnlySpan<uint> values) =>
        UserScalarsPacket(banks, in packet, offset, values, banks.Shader.Vertex.GeometryUserScalars, SpiShaderUserDataGs0, GraphicsUserScalarCount);

    private static uint HullUserScalarsPacket(RegisterBanks banks, in PacketContext packet, uint offset, ReadOnlySpan<uint> values) =>
        UserScalarsPacket(banks, in packet, offset, values, banks.Shader.Vertex.HullUserScalars, SpiShaderUserDataHs0, GraphicsUserScalarCount);

    private static uint ComputeUserScalarsPacket(RegisterBanks banks, in PacketContext packet, uint offset, ReadOnlySpan<uint> values) =>
        UserScalarsPacket(banks, in packet, offset, values, banks.Shader.Compute.UserScalars, ComputeUserData0, ComputeUserScalarCount);

    // The marker of the preceding packet applies to every scalar of this packet, then clears.
    private static uint UserScalarsPacket(
        RegisterBanks banks, in PacketContext packet, uint offset, ReadOnlySpan<uint> values, UserScalarRegisters target, uint first, uint capacity)
    {
        var slot = offset - first;
        if (capacity > UserScalarRegisters.Capacity || slot >= capacity || (uint)values.Length > capacity - slot)
        {
            throw banks.Fatal($"The user scalar packet is outside the stage window: offset=0x{offset:X4} count={values.Length} capacity={capacity} header=0x{packet.Header:X8} address=0x{packet.PacketAddress:X16}.");
        }

        for (var index = 0u; index < values.Length; index++)
        {
            target.Set(slot + index, values[(int)index], banks.UserDataMarker);
        }

        banks.UserDataMarker = UserScalarKind.Unknown;
        return (uint)values.Length;
    }

    private static void UserScalarEntry(RegisterBanks banks, UserScalarRegisters target, uint index, uint value)
    {
        target.Set(index, value, banks.UserDataMarker);
        banks.UserDataMarker = UserScalarKind.Unknown;
    }

    private static uint UserAccumulatorPacket(RegisterBanks banks, in PacketContext packet, uint offset, ReadOnlySpan<uint> values)
    {
        var count = (uint)values.Length;
        var inRange = IsUserAccumulatorRange(offset, count, SpiShaderUserAccumPs0) || IsUserAccumulatorRange(offset, count, SpiShaderUserAccumEsGs0) ||
                      IsUserAccumulatorRange(offset, count, SpiShaderUserAccumLsHs0) || IsUserAccumulatorRange(offset, count, ComputeUserAccum0);
        if (count == 0 || !inRange)
        {
            throw PacketError(banks, "The user accumulator packet is outside its range", in packet, offset, values.Length);
        }

        return EachValue(banks, in packet, offset, values, UserAccumulatorEntry);
    }

    private static bool IsUserAccumulatorRange(uint firstOffset, uint count, uint rangeStart) =>
        firstOffset >= rangeStart && firstOffset - rangeStart < UserAccumulatorCount && count <= UserAccumulatorCount - (firstOffset - rangeStart);

    private static void UserAccumulatorEntry(RegisterBanks banks, uint offset, uint value)
    {
        if ((value & ~UserAccumulatorValueMask) != 0)
        {
            throw banks.Fatal($"The user accumulator value is not supported: offset=0x{offset:X4} value=0x{value:X8}.");
        }
    }

    private static uint ComputeRegistersPacket(RegisterBanks banks, in PacketContext packet, uint offset, ReadOnlySpan<uint> values)
    {
        if (values.Length == 0)
        {
            throw PacketError(banks, "The compute register packet has no values", in packet, offset, values.Length);
        }

        return EachValue(banks, in packet, offset, values, ComputeRegisterEntry);
    }

    private static void ComputeRegisterEntry(RegisterBanks banks, uint offset, uint value)
    {
        var compute = banks.Shader.Compute;
        switch (offset)
        {
            case ComputePgmLo:
                compute.Address = RegisterField.WithLowAddress(compute.Address, value);
                break;
            case ComputePgmHi:
                compute.Address = RegisterField.WithHighAddress(compute.Address, value);
                break;
            case ComputePgmRsrc1:
                compute.VectorRegisterCount = (byte)RegisterField.Get(value, 0, 0x3F);
                compute.Priority = (byte)RegisterField.Get(value, 10, 0x3);
                compute.FloatMode = (byte)RegisterField.Get(value, 12, 0xFF);
                compute.Dx10Clamp = RegisterField.Bit(value, 21);
                compute.DebugMode = RegisterField.Bit(value, 22);
                compute.IeeeMode = RegisterField.Bit(value, 23);
                compute.HalfPrecisionOverflow = RegisterField.Bit(value, 26);
                compute.ThreadgroupConfiguration = RegisterField.Bit(value, 29);
                compute.RequireForwardProgress = RegisterField.Bit(value, 31);
                break;
            case ComputePgmRsrc2:
                compute.ScratchEnable = RegisterField.Bit(value, 0);
                compute.UserScalarCount = (byte)RegisterField.Get(value, 1, 0x1F);
                compute.ThreadGroupIdXEnable = RegisterField.Bit(value, 7);
                compute.ThreadGroupIdYEnable = RegisterField.Bit(value, 8);
                compute.ThreadGroupIdZEnable = RegisterField.Bit(value, 9);
                compute.ThreadGroupSizeEnable = RegisterField.Bit(value, 10);
                compute.ThreadIdComponentCount = (byte)RegisterField.Get(value, 11, 0x3);
                compute.LocalDataShareSize = (ushort)RegisterField.Get(value, 15, 0x1FF);
                break;
            case ComputePgmRsrc3:
                compute.SharedVectorRegisters = (byte)RegisterField.Get(value, 0, 0xF);
                break;
            case ComputeNumThreadX:
                compute.ThreadsX = value;
                break;
            case ComputeNumThreadY:
                compute.ThreadsY = value;
                break;
            case ComputeNumThreadZ:
                compute.ThreadsZ = value;
                break;
            case ComputeShaderChecksum:
                compute.ProgramChecksum = value;
                break;
            case ComputeStartX:
            case ComputeStartY:
            case ComputeStartZ:
            case ComputeResourceLimits:
            case ComputeTmpringSize:
                break;
            default:
                throw banks.Fatal($"The compute shader register is not supported: offset=0x{offset:X4} value=0x{value:X8}.");
        }
    }

    // A direct writer that stores every value through the shader indirect writers.
    private static uint ForwardShaderPacket(RegisterBanks banks, in PacketContext packet, uint offset, ReadOnlySpan<uint> values)
    {
        if (values.Length == 0)
        {
            throw PacketError(banks, "The shader register packet has no values", in packet, offset, values.Length);
        }

        for (var index = 0u; index < values.Length; index++)
        {
            var register = offset + index;
            var writer = register < RegisterBankLayout.ShaderRegisterCount ? RegisterWriteTable.ShaderIndirect[register] : null;
            if (writer is null)
            {
                throw banks.Fatal($"The shader register is not supported: offset=0x{offset:X4} count={values.Length} unsupported=0x{register:X4} header=0x{packet.Header:X8} address=0x{packet.PacketAddress:X16}.");
            }

            writer(banks, register, values[(int)index]);
        }

        return (uint)values.Length;
    }
}
