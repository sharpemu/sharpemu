// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.PngEnc;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class PngEncExportsTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const int MemorySize = 0x3000;
    private const ulong ParameterAddress = MemoryBase + 0x100;
    private const ulong ContextAddress = MemoryBase + 0x200;
    private const ulong HandleAddress = MemoryBase + 0x300;
    private const ulong EncodeParameterAddress = MemoryBase + 0x400;
    private const ulong ImageAddress = MemoryBase + 0x500;
    private const ulong PngAddress = MemoryBase + 0x800;
    private const ulong OutputInfoAddress = MemoryBase + 0x1800;
    private const int InvalidAddress = unchecked((int)0x80690101);
    private const int InvalidSize = unchecked((int)0x80690102);
    private const int InvalidParameter = unchecked((int)0x80690103);
    private const int InvalidHandle = unchecked((int)0x80690104);
    private const int DataOverflow = unchecked((int)0x80690110);
    private readonly FakeCpuMemory _memory = new(MemoryBase, MemorySize);

    [Fact]
    public void InitializationExportsRegisterWithPngEncoderLibrary()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport("9030RnBDoh4", out var query));
        Assert.Equal("scePngEncQueryMemorySize", query.Name);
        Assert.Equal("libScePngEnc", query.LibraryName);
        Assert.True(manager.TryGetExport("7aGTPfrqT9s", out var create));
        Assert.Equal("scePngEncCreate", create.Name);
        Assert.Equal("libScePngEnc", create.LibraryName);
        Assert.True(manager.TryGetExport("xgDjJKpcyHo", out var encode));
        Assert.Equal("scePngEncEncode", encode.Name);
        Assert.Equal("libScePngEnc", encode.LibraryName);
        Assert.True(manager.TryGetExport("RUrWdwTWZy8", out var delete));
        Assert.Equal("scePngEncDelete", delete.Name);
        Assert.Equal("libScePngEnc", delete.LibraryName);
    }

    [Fact]
    public void QueryAndCreateUseCallerOwnedSixteenByteContext()
    {
        WriteCreateParameters(size: 0x10, attribute: 0, maximumWidth: 4096, maximumFilters: 4);
        Fill(ContextAddress - 8, 32, 0xA5);
        Fill(HandleAddress - 8, 24, 0xA5);
        var context = new CpuContext(_memory, Generation.Gen5);

        context[CpuRegister.Rdi] = ParameterAddress;
        Assert.Equal(0x10, PngEncExports.PngEncQueryMemorySize(context));
        Assert.Equal(0x10UL, context[CpuRegister.Rax]);

        context[CpuRegister.Rdi] = ParameterAddress;
        context[CpuRegister.Rsi] = ContextAddress;
        context[CpuRegister.Rdx] = 0x10;
        context[CpuRegister.Rcx] = HandleAddress;
        Assert.Equal(0, PngEncExports.PngEncCreate(context));
        Assert.Equal(0UL, context[CpuRegister.Rax]);

        var createdContext = Read(ContextAddress, 0x10);
        Assert.NotEqual(0UL, BinaryPrimitives.ReadUInt64LittleEndian(createdContext));
        Assert.Equal(4096U, BinaryPrimitives.ReadUInt32LittleEndian(createdContext.AsSpan(8)));
        Assert.Equal(4U, BinaryPrimitives.ReadUInt32LittleEndian(createdContext.AsSpan(12)));
        Assert.Equal(ContextAddress, BinaryPrimitives.ReadUInt64LittleEndian(Read(HandleAddress, 8)));
        Assert.All(Read(ContextAddress - 8, 8), value => Assert.Equal(0xA5, value));
        Assert.All(Read(ContextAddress + 0x10, 8), value => Assert.Equal(0xA5, value));
        Assert.All(Read(HandleAddress - 8, 8), value => Assert.Equal(0xA5, value));
        Assert.All(Read(HandleAddress + 8, 8), value => Assert.Equal(0xA5, value));
    }

    [Theory]
    [InlineData(0x0F, 0, 4096, 4, InvalidParameter)]
    [InlineData(0x10, 1, 4096, 4, InvalidParameter)]
    [InlineData(0x10, 0, 4096, 5, InvalidParameter)]
    [InlineData(0x10, 0, 0, 4, InvalidSize)]
    [InlineData(0x10, 0, 1_000_001, 4, InvalidSize)]
    public void QueryRejectsInvalidCreateParameters(
        uint size,
        uint attribute,
        uint maximumWidth,
        uint maximumFilters,
        int expectedResult)
    {
        WriteCreateParameters(size, attribute, maximumWidth, maximumFilters);
        var context = new CpuContext(_memory, Generation.Gen5);
        context[CpuRegister.Rdi] = ParameterAddress;

        Assert.Equal(expectedResult, PngEncExports.PngEncQueryMemorySize(context));
        Assert.Equal(unchecked((ulong)(long)expectedResult), context[CpuRegister.Rax]);
    }

    [Fact]
    public void QueryRejectsNullAndTruncatedParameterPointers()
    {
        var context = new CpuContext(_memory, Generation.Gen5);
        context[CpuRegister.Rdi] = 0;
        Assert.Equal(InvalidAddress, PngEncExports.PngEncQueryMemorySize(context));

        context[CpuRegister.Rdi] = MemoryBase + MemorySize - 8;
        Assert.Equal(InvalidAddress, PngEncExports.PngEncQueryMemorySize(context));
    }

    [Fact]
    public void CreateRejectsShortStorageWithoutTouchingGuestOutputs()
    {
        WriteCreateParameters(size: 0x10, attribute: 0, maximumWidth: 4096, maximumFilters: 4);
        Fill(ContextAddress, 0x10, 0xA5);
        Fill(HandleAddress, 8, 0xA5);
        var context = new CpuContext(_memory, Generation.Gen5);
        context[CpuRegister.Rdi] = ParameterAddress;
        context[CpuRegister.Rsi] = ContextAddress;
        context[CpuRegister.Rdx] = 0x0F;
        context[CpuRegister.Rcx] = HandleAddress;

        Assert.Equal(InvalidSize, PngEncExports.PngEncCreate(context));
        Assert.All(Read(ContextAddress, 0x10), value => Assert.Equal(0xA5, value));
        Assert.All(Read(HandleAddress, 8), value => Assert.Equal(0xA5, value));
    }

    [Fact]
    public void EncodeProducesPngAndDeleteInvalidatesTheHandle()
    {
        var context = CreateEncoder();
        var pixels = new byte[]
        {
            255, 0, 13, 0, 4, 210, 6, 255,
            7, 8, 220, 64, 111, 12, 113, 128,
        };
        Assert.True(_memory.TryWrite(ImageAddress, pixels));
        WriteEncodeParameters(pngSize: 0x800);

        context[CpuRegister.Rdi] = ContextAddress;
        context[CpuRegister.Rsi] = EncodeParameterAddress;
        context[CpuRegister.Rdx] = OutputInfoAddress;
        var encodedSize = PngEncExports.PngEncEncode(context);
        Assert.True(encodedSize > 0);
        Assert.Equal((ulong)encodedSize, context[CpuRegister.Rax]);

        var png = Read(PngAddress, encodedSize);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);
        Assert.Equal(2U, BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16)));
        Assert.Equal(2U, BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20)));
        var outputInfo = Read(OutputInfoAddress, 8);
        Assert.Equal((uint)encodedSize, BinaryPrimitives.ReadUInt32LittleEndian(outputInfo));
        Assert.Equal(2U, BinaryPrimitives.ReadUInt32LittleEndian(outputInfo.AsSpan(4)));

        context[CpuRegister.Rdi] = ContextAddress;
        Assert.Equal(0, PngEncExports.PngEncDelete(context));
        Assert.Equal(InvalidHandle, PngEncExports.PngEncDelete(context));
        context[CpuRegister.Rsi] = EncodeParameterAddress;
        context[CpuRegister.Rdx] = OutputInfoAddress;
        Assert.Equal(InvalidHandle, PngEncExports.PngEncEncode(context));
    }

    [Fact]
    public void EncodeReportsOverflowWithoutTouchingDestination()
    {
        var context = CreateEncoder();
        Assert.True(_memory.TryWrite(ImageAddress, new byte[16]));
        WriteEncodeParameters(pngSize: 0x800);
        context[CpuRegister.Rdi] = ContextAddress;
        context[CpuRegister.Rsi] = EncodeParameterAddress;
        context[CpuRegister.Rdx] = OutputInfoAddress;
        var requiredSize = PngEncExports.PngEncEncode(context);
        Assert.True(requiredSize > 1);

        Fill(PngAddress, requiredSize, 0xA5);
        WriteEncodeParameters((uint)(requiredSize - 1));
        Assert.Equal(DataOverflow, PngEncExports.PngEncEncode(context));
        Assert.All(Read(PngAddress, requiredSize), value => Assert.Equal(0xA5, value));
        Assert.All(Read(OutputInfoAddress, 8), value => Assert.Equal(0, value));
    }

    private void WriteCreateParameters(uint size, uint attribute, uint maximumWidth, uint maximumFilters)
    {
        Span<byte> payload = stackalloc byte[0x10];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, size);
        BinaryPrimitives.WriteUInt32LittleEndian(payload[4..], attribute);
        BinaryPrimitives.WriteUInt32LittleEndian(payload[8..], maximumWidth);
        BinaryPrimitives.WriteUInt32LittleEndian(payload[12..], maximumFilters);
        Assert.True(_memory.TryWrite(ParameterAddress, payload));
    }

    private CpuContext CreateEncoder()
    {
        WriteCreateParameters(size: 0x10, attribute: 0, maximumWidth: 4096, maximumFilters: 4);
        var context = new CpuContext(_memory, Generation.Gen5);
        context[CpuRegister.Rdi] = ParameterAddress;
        context[CpuRegister.Rsi] = ContextAddress;
        context[CpuRegister.Rdx] = 0x10;
        context[CpuRegister.Rcx] = HandleAddress;
        Assert.Equal(0, PngEncExports.PngEncCreate(context));
        Assert.True(context.TryReadUInt64(HandleAddress, out var handle));
        Assert.Equal(ContextAddress, handle);
        return context;
    }

    private void WriteEncodeParameters(uint pngSize)
    {
        Span<byte> payload = stackalloc byte[0x30];
        BinaryPrimitives.WriteUInt64LittleEndian(payload, ImageAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(payload[8..], PngAddress);
        BinaryPrimitives.WriteUInt32LittleEndian(payload[16..], 16);
        BinaryPrimitives.WriteUInt32LittleEndian(payload[20..], pngSize);
        BinaryPrimitives.WriteUInt32LittleEndian(payload[24..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(payload[28..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(payload[32..], 8);
        BinaryPrimitives.WriteUInt16LittleEndian(payload[36..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(payload[38..], 19);
        BinaryPrimitives.WriteUInt16LittleEndian(payload[40..], 8);
        BinaryPrimitives.WriteUInt16LittleEndian(payload[42..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(payload[44..], 15);
        BinaryPrimitives.WriteUInt16LittleEndian(payload[46..], 6);
        Assert.True(_memory.TryWrite(EncodeParameterAddress, payload));
    }

    private void Fill(ulong address, int length, byte value)
    {
        var bytes = new byte[length];
        Array.Fill(bytes, value);
        Assert.True(_memory.TryWrite(address, bytes));
    }

    private byte[] Read(ulong address, int length)
    {
        var bytes = new byte[length];
        Assert.True(_memory.TryRead(address, bytes));
        return bytes;
    }
}
