// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Codec;
using Xunit;

namespace SharpEmu.Libs.Tests.Codec;

public sealed class CodecExportsTests
{
    private const ulong MemoryBase = 0x5_0000_0000UL;
    private const ulong ControlAddress = MemoryBase + 0x100;
    private const ulong ParamAddress = MemoryBase + 0x200;
    private const ulong BsiAddress = MemoryBase + 0x300;
    private const uint AacCodecType = 3;
    private const int InvalidAuInfoPointer = unchecked((int)0x807F000B);

    public CodecExportsTests()
    {
        CodecExports.ResetAudioDecodersForTests();
    }

    [Fact]
    public void AudiodecCreateDecoder_AcceptsDeferredAuAndPcmStructures()
    {
        var (memory, context) = CreateAacContext();
        InitializeAacLibrary(context);

        context[CpuRegister.Rdi] = ControlAddress;
        context[CpuRegister.Rsi] = AacCodecType;
        var handle = CodecExports.AudiodecCreateDecoder(context);

        Assert.True(handle > 0);
        Span<byte> bsi = stackalloc byte[20];
        Assert.True(memory.TryRead(BsiAddress, bsi));
        Assert.Equal(20u, BinaryPrimitives.ReadUInt32LittleEndian(bsi));
        Assert.Equal(48_000u, BinaryPrimitives.ReadUInt32LittleEndian(bsi[4..]));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(bsi[8..]));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(bsi[16..]));
    }

    [Fact]
    public void AudiodecDecode_StillRejectsMissingAuStructure()
    {
        var (_, context) = CreateAacContext();
        InitializeAacLibrary(context);
        context[CpuRegister.Rdi] = ControlAddress;
        context[CpuRegister.Rsi] = AacCodecType;
        var handle = CodecExports.AudiodecCreateDecoder(context);
        Assert.True(handle > 0);

        context[CpuRegister.Rdi] = unchecked((ulong)handle);
        context[CpuRegister.Rsi] = ControlAddress;

        Assert.Equal(InvalidAuInfoPointer, CodecExports.AudiodecDecode(context));
    }

    private static (FakeCpuMemory Memory, CpuContext Context) CreateAacContext()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);

        Span<byte> control = stackalloc byte[32];
        control.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(control, ParamAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(control[8..], BsiAddress);
        Assert.True(memory.TryWrite(ControlAddress, control));

        Span<byte> parameter = stackalloc byte[24];
        parameter.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(parameter, 24);
        BinaryPrimitives.WriteInt32LittleEndian(parameter[4..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(parameter[12..], 3);
        BinaryPrimitives.WriteUInt32LittleEndian(parameter[16..], 2);
        Assert.True(memory.TryWrite(ParamAddress, parameter));

        Span<byte> bsi = stackalloc byte[20];
        bsi.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(bsi, 20);
        Assert.True(memory.TryWrite(BsiAddress, bsi));

        return (memory, context);
    }

    private static void InitializeAacLibrary(CpuContext context)
    {
        context[CpuRegister.Rdi] = AacCodecType;
        Assert.Equal(0, CodecExports.AudiodecInitLibrary(context));
    }
}
