// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.GnmMode.Libraries.Ajm;
using SharpEmu.HLE;
using SharpEmu.Libs.Audio;
using SharpEmu.Libs.Tests.Audio;
using Xunit;

namespace SharpEmu.Libs.Tests.Loader;

[Collection(AjmStateCollection.Name)]
public sealed class GnmAjmRiffTests
{
    private const ulong Base = 0x1_0000_0000;

    [Theory]
    [InlineData(false, false, 0)]
    [InlineData(true, false, 0)]
    [InlineData(true, true, 0)]
    [InlineData(false, false, 8)]
    [InlineData(false, false, 4)]
    public void RawRiffJobsInitializeAtrac9AndReportDecodedSamplesWithoutAnExplicitControlJob(bool split, bool fact, int status)
    {
        AjmExports.ResetForTests();
        var context = new CpuContext(new FakeCpuMemory(Base, 0x10000), Generation.Gen4);
        context[CpuRegister.Rsi] = Base;
        Assert.Equal(0, AjmExports.AjmInitialize(context));
        var contextId = Read32(context, Base);
        context[CpuRegister.Rdi] = contextId;
        context[CpuRegister.Rsi] = 1;
        Assert.Equal(0, AjmExports.AjmModuleRegister(context));
        context[CpuRegister.Rdi] = contextId;
        context[CpuRegister.Rsi] = 1;
        context[CpuRegister.Rdx] = (1UL << 32) | 1;
        context[CpuRegister.Rcx] = Base + 4;
        Assert.Equal(0, GnmAjmExports.InstanceCreate(context));
        var instance = Read32(context, Base + 4);
        try
        {
            var input = Riff(fact);
            if (status == 4)
                input[44] ^= 1;
            Assert.True(context.Memory.TryWrite(Base + 0x1000, input));
            Assert.True(context.TryWriteUInt64(Base + 0x300, Base + 0x1000));
            Assert.True(context.TryWriteUInt64(Base + 0x308, 43));
            Assert.True(context.TryWriteUInt64(Base + 0x310, Base + 0x1000 + 43));
            Assert.True(context.TryWriteUInt64(Base + 0x318, (ulong)input.Length - 43));
            Assert.True(context.TryWriteUInt64(Base + 0x340, Base + 0x2000));
            Assert.True(context.TryWriteUInt64(Base + 0x348, 1024));
            context[CpuRegister.Rsp] = Base + 0x800;
            Assert.True(context.TryWriteUInt64(Base + 0x808, split ? 1UL : 1024UL));
            Assert.True(context.TryWriteUInt64(Base + 0x810, Base + 0x400));
            Assert.True(context.TryWriteUInt64(Base + 0x818, 80));
            context[CpuRegister.Rdi] = Base + 0x600;
            context[CpuRegister.Rsi] = instance;
            context[CpuRegister.Rdx] = AjmExports.AjmJobSidebandFlagStream | AjmExports.AjmJobSidebandFlagFormat;
            context[CpuRegister.Rcx] = split ? Base + 0x300 : Base + 0x1000;
            context[CpuRegister.R8] = split ? 2UL : status == 8 ? 16UL : (ulong)input.Length;
            context[CpuRegister.R9] = split ? Base + 0x340 : Base + 0x2000;
            Assert.Equal(0, split ? GnmAjmExports.BatchJobRunSplitBufferRa(context) : GnmAjmExports.BatchJobRunBufferRa(context));
            var length = context[CpuRegister.Rax] - (Base + 0x600);
            context[CpuRegister.Rdi] = contextId;
            context[CpuRegister.Rsi] = Base + 0x600;
            context[CpuRegister.Rdx] = length;
            context[CpuRegister.Rcx] = 40;
            context[CpuRegister.R8] = 0;
            context[CpuRegister.R9] = Base + 8;
            Assert.Equal(0, GnmAjmExports.BatchStartBuffer(context));
            Assert.Equal((uint)status, Read32(context, Base + 0x400));
            if (status != 0)
            {
                Assert.Equal(0u, Read32(context, Base + 0x408));
                Assert.Equal(0u, Read32(context, Base + 0x40C));
                return;
            }
            Assert.Equal((uint)input.Length, Read32(context, Base + 0x408));
            Assert.Equal(fact ? 504u : 512u, Read32(context, Base + 0x40C));
            Assert.Equal(1u, Read32(context, Base + 0x418));
            Assert.Equal(48000u, Read32(context, Base + 0x420));
        }
        finally
        {
            context[CpuRegister.Rdi] = contextId;
            context[CpuRegister.Rsi] = instance;
            GnmAjmExports.InstanceDestroy(context);
            AjmExports.ResetForTests();
        }
    }

    private static byte[] Riff(bool fact)
    {
        var dataOffset = fact ? 100 : 80;
        var bytes = new byte[dataOffset + 192];
        "RIFF"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)bytes.Length - 8);
        "WAVEfmt "u8.CopyTo(bytes.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 52);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20), 0xFFFE);
        byte[] guid = [0xD2, 0x42, 0xE1, 0x47, 0xBA, 0x36, 0x8D, 0x4D, 0x88, 0xFC, 0x61, 0x65, 0x4F, 0x8C, 0x83, 0x6C];
        guid.CopyTo(bytes, 44);
        new byte[] { 0xFE, 0x70, 0x17, 0xE0 }.CopyTo(bytes, 64);
        if (fact)
        {
            "fact"u8.CopyTo(bytes.AsSpan(72));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(76), 12);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(80), 252);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(84), 4);
        }
        "data"u8.CopyTo(bytes.AsSpan(dataOffset - 8));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(dataOffset - 4), 192);
        new byte[] { 0x00, 0x84, 0x00, 0x40 }.CopyTo(bytes, dataOffset);
        return bytes;
    }

    private static uint Read32(CpuContext context, ulong address)
    {
        Assert.True(context.TryReadUInt32(address, out var value));
        return value;
    }
}
