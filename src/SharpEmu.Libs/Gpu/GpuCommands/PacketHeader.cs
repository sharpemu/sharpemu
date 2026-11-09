// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.GpuCommands;

// Type-3 packet header fields: 0xC0000000 | (dwords - 2) << 16 | opcode << 8 | customCode << 2.
public static class PacketHeader
{
    public const uint FillerHeader = 0x8000_0000u;

    public const uint MaxLength = 0x3FFFu + 2u;

    public static uint Length(uint header) => ((header >> 16) & 0x3FFFu) + 2u;

    public static uint Opcode(uint header) => (header >> 8) & 0xFFu;

    public static uint CustomCode(uint header) => (header >> 2) & 0x3Fu;

    public static uint PacketType(uint header) => header >> 30;

    public static bool IsPredicated(uint header) => (header & 1u) != 0;

    public static uint Make(uint dwords, uint opcode, uint customCode = 0) =>
        0xC000_0000u |
        (((dwords - 2u) & 0x3FFFu) << 16) |
        ((opcode & 0xFFu) << 8) |
        ((customCode & 0x3Fu) << 2);
}

public static class PacketOpcode
{
    public const uint Nop = 0x10;
    public const uint SetBase = 0x11;
    public const uint ClearState = 0x12;
    public const uint IndexBufferSize = 0x13;
    public const uint DispatchDirect = 0x15;
    public const uint DispatchIndirect = 0x16;
    public const uint AtomicMemory = 0x1E;
    public const uint SetPredication = 0x20;
    public const uint ConditionalExecute = 0x22;
    public const uint DrawIndirect = 0x24;
    public const uint DrawIndexIndirect = 0x25;
    public const uint IndexBase = 0x26;
    public const uint DrawIndex2 = 0x27;
    public const uint ContextControl = 0x28;
    public const uint IndexType = 0x2A;
    public const uint DrawIndirectMulti = 0x2C;
    public const uint DrawIndexAuto = 0x2D;
    public const uint NumInstances = 0x2F;
    public const uint DrawIndexMultiAuto = 0x30;
    public const uint DrawIndexOffset2 = 0x35;
    public const uint WriteData = 0x37;
    public const uint DrawIndexIndirectMulti = 0x38;
    public const uint MemorySemaphore = 0x39;
    public const uint DispatchDrawPreamble = 0x3A;
    public const uint WaitRegisterMemory = 0x3C;
    public const uint IndirectBuffer = 0x3F;
    public const uint CopyData = 0x40;
    public const uint PfpSyncMe = 0x42;
    public const uint ConditionalWrite = 0x45;
    public const uint EventWrite = 0x46;
    public const uint EventWriteEndOfPipe = 0x47;
    public const uint EventWriteEndOfShader = 0x48;
    public const uint ReleaseMemory = 0x49;
    public const uint DmaData = 0x50;
    public const uint AcquireMemory = 0x58;
    public const uint Rewind = 0x59;
    public const uint SetShaderRegisterIndirect = 0x63;
    public const uint SetUserConfigRegisterIndirect = 0x64;
    public const uint SetContextRegister = 0x69;
    public const uint SetShaderRegister = 0x76;
    public const uint SetUserConfigRegister = 0x79;
    public const uint SetUserConfigRegisterIndex = 0x7A;
    public const uint WriteConstantRam = 0x81;
    public const uint DumpConstantRam = 0x83;
    public const uint IncrementCeCounter = 0x84;
    public const uint IncrementDeCounter = 0x85;
    public const uint WaitOnCeCounter = 0x86;
    public const uint WaitOnDeCounterDiff = 0x88;
    public const uint GetLodStats = 0x8E;
    public const uint WaitRegisterMemory64 = 0x93;
    public const uint SetContextRegisterIndirect = 0x9F;
}

// Codes carried in the custom field of a NOP packet.
public static class PacketCustomCode
{
    public const uint Zero = 0x00;
    public const uint DrawIndexAuto = 0x04;
    public const uint DrawReset = 0x05;
    public const uint WaitFlipDone = 0x06;
    public const uint DispatchReset = 0x09;
    public const uint WaitMemory32 = 0x0A;
    public const uint PushMarker = 0x0B;
    public const uint PopMarker = 0x0C;
    public const uint ShaderRegisterTable = 0x11;
    public const uint ContextRegisterTable = 0x12;
    public const uint UserConfigRegisterTable = 0x13;
    public const uint AcquireMemory = 0x14;
    public const uint WriteData = 0x15;
    public const uint WaitMemory64 = 0x16;
    public const uint Flip = 0x17;
    public const uint ReleaseMemory = 0x18;
    public const uint DmaData = 0x19;
    public const uint ContextState = 0x1A;
    public const uint IndexCount = 0x1C;
    public const uint Count = 0x40;
}

public static class RegisterBankLayout
{
    public const uint ContextRegisterCount = 0x400;
    public const uint ContextNop = 0xDB;
    public const uint DepthInfo = 0x00F;
    public const uint DepthView = 0x002;
    public const uint HtileDataBase = 0x005;
    public const uint HtileSurface = 0x2AF;
    public const uint ShaderRegisterNop = 0x280;
    public const uint ShaderRegisterCount = 0x300;
    public const uint UserConfigNop = 0xA2;
    public const uint UserConfigRegisterCount = 0x4000;
    public const uint SelectorMask = 0x7000_0000u;
    public const uint DepthSizeXy = 0x007;
    public const uint DepthZInfo = 0x010;
    public const uint IndexTypeRegister = 0x243;
    public const uint GeometryIndexOffset = 0x24A;

    public static uint Normalize(uint rawOffset) => rawOffset & ~SelectorMask;
}

// Detects the user-config packets the AGC library uses to carry private data.
public static class InternalDataPacket
{
    private const uint InternalDataRegister = 0x342;
    private const uint DrawIndirectMultiBeginTag = 0xC600_0008u;
    private const uint DrawIndirectMultiEndTag = 0xC600_0000u;
    private const uint WaitUserDataBegin32Tag = 0xC801_0000u;
    private const uint WaitUserDataBegin64Tag = 0xC802_0000u;
    private const uint WaitUserDataEndTag = 0xC800_0000u;
    private const uint WaitUserDataBeginDwords = 4;
    private const uint InternalDataPacketDwords = 3;

    public static bool Matches(uint header, ReadOnlySpan<uint> payload)
    {
        if (payload.Length < 2 ||
            PacketHeader.Opcode(header) != PacketOpcode.SetUserConfigRegister ||
            PacketHeader.CustomCode(header) != 1 ||
            payload[0] != InternalDataRegister)
        {
            return false;
        }

        var length = PacketHeader.Length(header);
        if (length == WaitUserDataBeginDwords)
        {
            var tag = payload[1] & 0xFFFF_0000u;
            return tag is WaitUserDataBegin32Tag or WaitUserDataBegin64Tag;
        }

        return length == InternalDataPacketDwords &&
            payload[1] is WaitUserDataEndTag or DrawIndirectMultiBeginTag or DrawIndirectMultiEndTag;
    }
}
