// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Acm;
using Xunit;

namespace SharpEmu.Libs.Tests.Acm;

[CollectionDefinition("AcmState", DisableParallelization = true)]
public sealed class AcmStateCollection
{
    public const string Name = "AcmState";
}

[Collection(AcmStateCollection.Name)]
public sealed class AcmExportsTests : IDisposable
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong ContextOutputAddress = MemoryBase + 0x100;

    private readonly FakeCpuMemory _memory = new(MemoryBase, 0x1000);
    private readonly CpuContext _context;

    public AcmExportsTests()
    {
        AcmExports.ResetForTests();
        _context = new CpuContext(_memory, Generation.Gen5);
    }

    [Fact]
    public void ContextCreate_Writes32BitIdWithoutClobberingAdjacentGuestField()
    {
        WriteUInt32(ContextOutputAddress + sizeof(uint), 0xA5A5A5A5);
        _context[CpuRegister.Rdi] = ContextOutputAddress;
        _context[CpuRegister.Rsi] = MemoryBase + 0x200;
        _context[CpuRegister.Rcx] = MemoryBase + 0x400;
        _context[CpuRegister.R8] = 0x2000;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            AcmExports.AcmContextCreate(_context));
        Assert.Equal(1u, ReadUInt32(ContextOutputAddress));
        Assert.Equal(0xA5A5A5A5u, ReadUInt32(ContextOutputAddress + sizeof(uint)));

        _context[CpuRegister.Rdi] = 1;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            AcmExports.AcmContextDestroy(_context));
    }

    [Fact]
    public void ContextCreate_ReturnsMemoryFaultForUnmappedOutput()
    {
        _context[CpuRegister.Rdi] = MemoryBase + 0x1000;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            AcmExports.AcmContextCreate(_context));
    }

    [Fact]
    public void BatchStartBuffers_Writes32BitBatchIdWithoutClobberingAdjacentGuestField()
    {
        var contextId = CreateContext();
        const ulong DescriptorListAddress = MemoryBase + 0x200;
        const ulong DescriptorAddress = MemoryBase + 0x300;
        const ulong CommandBufferAddress = MemoryBase + 0x400;
        const ulong StatusAddress = MemoryBase + 0x500;
        const ulong OutBatchIdAddress = MemoryBase + 0x600;

        // Guest layout: descriptor list -> descriptor {base, used, capacity};
        // the command buffer ends with the terminator {opcode 0xB, &status}.
        WriteUInt64(DescriptorListAddress, DescriptorAddress);
        WriteUInt64(DescriptorAddress, CommandBufferAddress);
        WriteUInt64(DescriptorAddress + 8, 0x10);
        WriteUInt64(DescriptorAddress + 16, 0x2000);
        WriteUInt64(CommandBufferAddress, 0xB);
        WriteUInt64(CommandBufferAddress + 8, StatusAddress);
        WriteUInt32(OutBatchIdAddress + sizeof(uint), 0xA5A5A5A5);

        _context[CpuRegister.Rdi] = contextId;
        _context[CpuRegister.Rsi] = 0;
        _context[CpuRegister.Rdx] = DescriptorListAddress;
        _context[CpuRegister.Rcx] = 0;
        _context[CpuRegister.R8] = OutBatchIdAddress;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            AcmExports.AcmBatchStartBuffers(_context));

        var batchId = ReadUInt32(OutBatchIdAddress);
        Assert.NotEqual(0u, batchId);
        Assert.NotEqual(uint.MaxValue, batchId);
        Assert.Equal(0xA5A5A5A5u, ReadUInt32(OutBatchIdAddress + sizeof(uint)));
    }

    [Fact]
    public void BatchStartBuffers_RejectsUnknownContext()
    {
        _context[CpuRegister.Rdi] = 0xDEAD;
        _context[CpuRegister.Rdx] = MemoryBase + 0x200;
        _context[CpuRegister.R8] = MemoryBase + 0x600;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            AcmExports.AcmBatchStartBuffers(_context));
    }

    [Fact]
    public void BatchWait_CompletesBatchAndNeutralizesStatusTimestamp()
    {
        var contextId = CreateContext();
        const ulong DescriptorListAddress = MemoryBase + 0x200;
        const ulong DescriptorAddress = MemoryBase + 0x300;
        const ulong CommandBufferAddress = MemoryBase + 0x400;
        const ulong StatusAddress = MemoryBase + 0x500;
        const ulong OutBatchIdAddress = MemoryBase + 0x600;

        WriteUInt64(DescriptorListAddress, DescriptorAddress);
        WriteUInt64(DescriptorAddress, CommandBufferAddress);
        WriteUInt64(DescriptorAddress + 8, 0x10);
        WriteUInt64(DescriptorAddress + 16, 0x2000);
        WriteUInt64(CommandBufferAddress, 0xB);
        WriteUInt64(CommandBufferAddress + 8, StatusAddress);

        // Guest pre-fills the status block timestamp with a sentinel; ACM must
        // replace it on completion or the guest converts the sentinel to a
        // garbage audio-clock value.
        WriteUInt64(StatusAddress + 8, 0xFFFFFFFF00000000);

        _context[CpuRegister.Rdi] = contextId;
        _context[CpuRegister.Rdx] = DescriptorListAddress;
        _context[CpuRegister.R8] = OutBatchIdAddress;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            AcmExports.AcmBatchStartBuffers(_context));
        var batchId = ReadUInt32(OutBatchIdAddress);

        _context[CpuRegister.Rdi] = contextId;
        _context[CpuRegister.Rsi] = batchId;
        _context[CpuRegister.Rdx] = 0;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            AcmExports.AcmBatchWait(_context));
        Assert.Equal(0UL, ReadUInt64(StatusAddress + 8));
    }

    [Fact]
    public void BatchWait_RejectsUnknownContext()
    {
        _context[CpuRegister.Rdi] = 0xDEAD;
        _context[CpuRegister.Rsi] = 1;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            AcmExports.AcmBatchWait(_context));
    }

    [Fact]
    public void ContextExports_RegisterForBothGenerations()
    {
        foreach (var generation in new[] { Generation.Gen4, Generation.Gen5 })
        {
            var manager = new ModuleManager();
            manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(generation));

            Assert.True(manager.TryGetExport("ZIXln2K3XMk", out var create));
            Assert.Equal("sceAcmContextCreate", create.Name);
            Assert.True(manager.TryGetExport("jBgBjAj02R8", out var destroy));
            Assert.Equal("sceAcmContextDestroy", destroy.Name);
            Assert.True(manager.TryGetExport("8fe55ktlNVo", out var start));
            Assert.Equal("sceAcmBatchStartBuffers", start.Name);
            Assert.True(manager.TryGetExport("RLN3gRlXJLE", out var wait));
            Assert.Equal("sceAcmBatchWait", wait.Name);
        }
    }

    public void Dispose() => AcmExports.ResetForTests();

    private ulong CreateContext()
    {
        _context[CpuRegister.Rdi] = ContextOutputAddress;
        _context[CpuRegister.Rsi] = MemoryBase + 0x800;
        _context[CpuRegister.Rcx] = MemoryBase + 0x900;
        _context[CpuRegister.R8] = 0x2000;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            AcmExports.AcmContextCreate(_context));
        return ReadUInt32(ContextOutputAddress);
    }

    private ulong ReadUInt64(ulong address)
    {
        Span<byte> value = stackalloc byte[sizeof(ulong)];
        Assert.True(_memory.TryRead(address, value));
        return BinaryPrimitives.ReadUInt64LittleEndian(value);
    }

    private void WriteUInt64(ulong address, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        Assert.True(_memory.TryWrite(address, bytes));
    }

    private uint ReadUInt32(ulong address)
    {
        Span<byte> value = stackalloc byte[sizeof(uint)];
        Assert.True(_memory.TryRead(address, value));
        return BinaryPrimitives.ReadUInt32LittleEndian(value);
    }

    private void WriteUInt32(ulong address, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        Assert.True(_memory.TryWrite(address, bytes));
    }
}
