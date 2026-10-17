// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Acm;
using Xunit;

namespace SharpEmu.Libs.Tests.Audio;

[CollectionDefinition("AcmState", DisableParallelization = true)]
public sealed class AcmStateCollection
{
    public const string Name = "AcmState";
}

[Collection(AcmStateCollection.Name)]
public sealed class AcmExportsTests : IDisposable
{
    private const ulong MemoryBase = 0x1_1000_0000;
    private const ulong ContextAddress = MemoryBase + 0x100;
    private const ulong InfoArrayAddress = MemoryBase + 0x200;
    private const ulong ErrorAddress = MemoryBase + 0x300;
    private const ulong BatchAddress = MemoryBase + 0x400;

    private readonly FakeCpuMemory _memory = new(MemoryBase, 0x1000);
    private readonly CpuContext _ctx;

    public AcmExportsTests()
    {
        AcmExports.ResetForTests();
        _ctx = new CpuContext(_memory, Generation.Gen5);
    }

    [Fact]
    public void ContextAndBatchLifecycle_UsesFourByteHandlesAndClearsErrors()
    {
        Span<byte> contextSentinel = stackalloc byte[8];
        contextSentinel.Fill(0xCC);
        Assert.True(_memory.TryWrite(ContextAddress, contextSentinel));

        _ctx[CpuRegister.Rdi] = ContextAddress;
        Assert.Equal(0, AcmExports.AcmContextCreate(_ctx));
        var context = ReadUInt32(ContextAddress);
        Assert.Equal(1u, context);
        Assert.Equal(0xCCCCCCCCu, ReadUInt32(ContextAddress + 4));

        Span<byte> errorSentinel = stackalloc byte[32];
        errorSentinel.Fill(0xCC);
        Assert.True(_memory.TryWrite(ErrorAddress, errorSentinel));
        WriteUInt64(InfoArrayAddress, MemoryBase + 0x500);

        _ctx[CpuRegister.Rdi] = context;
        _ctx[CpuRegister.Rsi] = 1;
        _ctx[CpuRegister.Rdx] = InfoArrayAddress;
        _ctx[CpuRegister.Rcx] = ErrorAddress;
        _ctx[CpuRegister.R8] = BatchAddress;
        Assert.Equal(0, AcmExports.AcmBatchStartBuffers(_ctx));
        Assert.Equal(1u, ReadUInt32(BatchAddress));
        Assert.All(ReadBytes(ErrorAddress, 32), value => Assert.Equal(0, value));

        _ctx[CpuRegister.Rdi] = context;
        _ctx[CpuRegister.Rsi] = 1;
        _ctx[CpuRegister.Rdx] = 0;
        Assert.Equal(0, AcmExports.AcmBatchWait(_ctx));
    }

    [Fact]
    public void BatchStartBuffers_RejectsUnknownContextAndMissingOutputs()
    {
        _ctx[CpuRegister.Rdi] = 99;
        _ctx[CpuRegister.Rsi] = 1;
        _ctx[CpuRegister.Rdx] = InfoArrayAddress;
        _ctx[CpuRegister.R8] = BatchAddress;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            AcmExports.AcmBatchStartBuffers(_ctx));

        _ctx[CpuRegister.Rdi] = ContextAddress;
        Assert.Equal(0, AcmExports.AcmContextCreate(_ctx));
        _ctx[CpuRegister.Rdi] = ReadUInt32(ContextAddress);
        _ctx[CpuRegister.Rsi] = 1;
        _ctx[CpuRegister.Rdx] = 0;
        _ctx[CpuRegister.R8] = BatchAddress;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            AcmExports.AcmBatchStartBuffers(_ctx));
    }

    [Fact]
    public void AcmBatchExports_RegisterForBothGenerations()
    {
        foreach (var generation in new[] { Generation.Gen4, Generation.Gen5 })
        {
            var manager = new ModuleManager();
            manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(generation));

            Assert.True(manager.TryGetExport("8fe55ktlNVo", out var start));
            Assert.Equal("sceAcmBatchStartBuffers", start.Name);
            Assert.True(manager.TryGetExport("RLN3gRlXJLE", out var wait));
            Assert.Equal("sceAcmBatchWait", wait.Name);
        }
    }

    [Fact]
    public void ConvReverbSharedInput_ClearsOutputBuffers()
    {
        const ulong stackAddress = MemoryBase + 0x600;
        const ulong descriptorListAddress = MemoryBase + 0x680;
        const ulong descriptorAddress = MemoryBase + 0x700;
        const ulong bufferListAddress = MemoryBase + 0x740;
        const ulong outputAddress = MemoryBase + 0x780;

        Span<byte> output = stackalloc byte[16];
        output.Fill(0x7F);
        Assert.True(_memory.TryWrite(outputAddress, output));
        WriteUInt64(bufferListAddress, outputAddress);
        WriteUInt64(descriptorListAddress, descriptorAddress);

        Span<byte> descriptor = stackalloc byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor, 4);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor[4..], 1);
        BinaryPrimitives.WriteUInt64LittleEndian(descriptor[16..], bufferListAddress);
        Assert.True(_memory.TryWrite(descriptorAddress, descriptor));

        _ctx[CpuRegister.Rsp] = stackAddress;
        WriteUInt64(stackAddress + 8, descriptorListAddress);
        _ctx[CpuRegister.Rcx] = 1;
        Assert.Equal(0, AcmExports.AcmConvReverbSharedInput(_ctx));
        Assert.All(ReadBytes(outputAddress, 16), value => Assert.Equal(0, value));
    }

    public void Dispose()
    {
        AcmExports.ResetForTests();
    }

    private uint ReadUInt32(ulong address)
    {
        Span<byte> value = stackalloc byte[sizeof(uint)];
        Assert.True(_memory.TryRead(address, value));
        return BinaryPrimitives.ReadUInt32LittleEndian(value);
    }

    private byte[] ReadBytes(ulong address, int length)
    {
        var value = new byte[length];
        Assert.True(_memory.TryRead(address, value));
        return value;
    }

    private void WriteUInt64(ulong address, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        Assert.True(_memory.TryWrite(address, bytes));
    }
}
