// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Ampr;
using Xunit;

namespace SharpEmu.Libs.Tests.Ampr;

public sealed class AmprExportsTests : IDisposable
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong CommandBufferAddress = MemoryBase + 0x100;
    private const ulong RecordBufferAddress = MemoryBase + 0x400;
    private const ulong DestinationAddress = MemoryBase + 0x800;
    private const ulong StackAddress = MemoryBase + 0x1800;
    private const ulong FaultingAddress = 0xDEAD_0000_0000;

    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        $"sharpemu-ampr-exports-{Guid.NewGuid():N}");
    private readonly FakeCpuMemory _memory = new(MemoryBase, 0x2000);
    private readonly CpuContext _context;

    public AmprExportsTests()
    {
        Directory.CreateDirectory(_tempRoot);
        _context = new CpuContext(_memory, Generation.Gen5);
    }

    [Fact]
    public void AprReadFileUsesSevenArgumentAbiAndAppendsCompletionRecord()
    {
        var hostPath = Path.Combine(_tempRoot, "stream.bin");
        File.WriteAllBytes(hostPath, [0x10, 0x20, 0x30, 0x40, 0x50, 0x60]);
        var fileId = AmprFileRegistry.Register("/app0/stream.bin", hostPath);
        InitializeCommandBuffer();

        _context[CpuRegister.Rdi] = CommandBufferAddress;
        _context[CpuRegister.Rsi] = CommandBufferAddress + 0x18;
        _context[CpuRegister.Rdx] = CommandBufferAddress + 0x20;
        _context[CpuRegister.Rcx] = fileId;
        _context[CpuRegister.R8] = DestinationAddress;
        _context[CpuRegister.R9] = 3;
        _context[CpuRegister.Rsp] = StackAddress;
        Assert.True(_context.TryWriteUInt64(StackAddress + sizeof(ulong), 2));

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            AmprExports.AprCommandBufferReadFile(_context));

        Span<byte> payload = stackalloc byte[3];
        Assert.True(_memory.TryRead(DestinationAddress, payload));
        Assert.Equal(new byte[] { 0x30, 0x40, 0x50 }, payload.ToArray());

        Span<byte> record = stackalloc byte[0x30];
        Assert.True(_memory.TryRead(RecordBufferAddress, record));
        Assert.Equal(1U, BinaryPrimitives.ReadUInt32LittleEndian(record));
        Assert.Equal(fileId, BinaryPrimitives.ReadUInt32LittleEndian(record[0x04..]));
        Assert.Equal(DestinationAddress, BinaryPrimitives.ReadUInt64LittleEndian(record[0x08..]));
        Assert.Equal(3UL, BinaryPrimitives.ReadUInt64LittleEndian(record[0x10..]));
        Assert.Equal(2UL, BinaryPrimitives.ReadUInt64LittleEndian(record[0x18..]));
        Assert.Equal(3UL, BinaryPrimitives.ReadUInt64LittleEndian(record[0x20..]));
    }

    [Fact]
    public void AprReadFileRejectsUnknownIdAndFaultingDestination()
    {
        InitializeCommandBuffer();
        _context[CpuRegister.Rdi] = CommandBufferAddress;
        _context[CpuRegister.Rsi] = CommandBufferAddress + 0x18;
        _context[CpuRegister.Rdx] = CommandBufferAddress + 0x20;
        _context[CpuRegister.Rcx] = 0xF0000001;
        _context[CpuRegister.R8] = DestinationAddress;
        _context[CpuRegister.R9] = 1;
        _context[CpuRegister.Rsp] = StackAddress;
        Assert.True(_context.TryWriteUInt64(StackAddress + sizeof(ulong), 0));

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND,
            AmprExports.AprCommandBufferReadFile(_context));

        var hostPath = Path.Combine(_tempRoot, "fault.bin");
        File.WriteAllBytes(hostPath, [0x42]);
        _context[CpuRegister.Rcx] = AmprFileRegistry.Register("/app0/fault.bin", hostPath);
        _context[CpuRegister.R8] = FaultingAddress;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            AmprExports.AprCommandBufferReadFile(_context));
    }

    [Fact]
    public void AprReadFilePreservesStackFaultPrecedenceOverUnknownFileId()
    {
        InitializeCommandBuffer();
        _context[CpuRegister.Rdi] = CommandBufferAddress;
        _context[CpuRegister.Rsi] = CommandBufferAddress + 0x18;
        _context[CpuRegister.Rdx] = CommandBufferAddress + 0x20;
        _context[CpuRegister.Rcx] = 0xF0000001;
        _context[CpuRegister.R8] = DestinationAddress;
        _context[CpuRegister.R9] = 1;
        _context[CpuRegister.Rsp] = FaultingAddress;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            AmprExports.AprCommandBufferReadFile(_context));
    }

    public void Dispose()
    {
        Directory.Delete(_tempRoot, recursive: true);
    }

    private void InitializeCommandBuffer()
    {
        _context[CpuRegister.Rdi] = CommandBufferAddress;
        _context[CpuRegister.Rsi] = RecordBufferAddress;
        _context[CpuRegister.Rdx] = 0x100;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            AmprExports.CommandBufferConstructor(_context));
    }
}
