// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcShaderCreationTests
{
    private const ulong BaseAddress = 0x3_0000_0000;
    private const int MemorySize = 0x4000;
    private const ulong DestinationAddress = BaseAddress;
    private const ulong HeaderAddress = BaseAddress + 0x100;
    private const ulong RegistersAddress = BaseAddress + 0x500;
    private const ulong CodeAddress = 0xAB_3456_789A00;
    private const ulong DestinationSentinel = 0x1122_3344_5566_7788;
    private const int IncompleteRegistersResult = unchecked((int)0x8A6C0005);

    [Theory]
    [InlineData(0, 0x20C)]
    [InlineData(1, 0x8)]
    [InlineData(2, 0xC8)]
    [InlineData(3, 0x48)]
    [InlineData(4, 0x88)]
    [InlineData(5, 0x108)]
    [InlineData(6, 0xC8)]
    [InlineData(7, 0x148)]
    public void CreateShader_PreservesExistingStageAddressPatching(byte shaderType, uint lowRegister)
    {
        var context = CreateContext(shaderType, 2, out var memory);
        WriteRegister(memory, 0, lowRegister, 0);
        WriteRegister(memory, 1, lowRegister + 1, 0);

        AssertSuccess(context, memory);

        Assert.Equal(unchecked((uint)(CodeAddress >> 8)), ReadUInt32(memory, RegistersAddress + 4));
        Assert.Equal((uint)(CodeAddress >> 40), ReadUInt32(memory, RegistersAddress + 12));
    }

    [Theory]
    [InlineData(4, 0)]
    [InlineData(4, 1)]
    [InlineData(4, 2)]
    [InlineData(5, 0)]
    [InlineData(5, 1)]
    [InlineData(5, 2)]
    [InlineData(8, 0)]
    [InlineData(8, 1)]
    [InlineData(8, 2)]
    public void CreateShader_AcceptsStagesWithoutProgramAddressEntries(byte shaderType, byte registerCount)
    {
        var context = CreateContext(shaderType, registerCount, out var memory);
        for (var registerIndex = 0; registerIndex < registerCount; registerIndex++)
        {
            WriteRegister(memory, registerIndex, (uint)(0x10A + registerIndex), 0xAABB_CCDD);
        }

        AssertSuccess(context, memory);

        for (var registerIndex = 0; registerIndex < registerCount; registerIndex++)
        {
            Assert.Equal(0xAABB_CCDDu, ReadUInt32(memory, RegistersAddress + (ulong)(registerIndex * 8) + 4));
        }
    }

    [Fact]
    public void CreateShader_FunctionShaderDoesNotPatchAddressEntries()
    {
        var context = CreateContext(8, 2, out var memory);
        WriteRegister(memory, 0, 0x8, 0x1122);
        WriteRegister(memory, 1, 0x9, 0x3344);

        AssertSuccess(context, memory);

        Assert.Equal(0x1122u, ReadUInt32(memory, RegistersAddress + 4));
        Assert.Equal(0x3344u, ReadUInt32(memory, RegistersAddress + 12));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(7)]
    public void CreateShader_RejectsMissingRequiredTable(byte shaderType)
    {
        var context = CreateContext(shaderType, 0, out var memory);
        AssertFailure(context, memory, IncompleteRegistersResult);
    }

    [Theory]
    [InlineData(0, 0x20C)]
    [InlineData(0, 0x20D)]
    [InlineData(1, 0x8)]
    [InlineData(1, 0x9)]
    [InlineData(4, 0x88)]
    [InlineData(4, 0x89)]
    [InlineData(5, 0x108)]
    [InlineData(5, 0x109)]
    public void CreateShader_RejectsIncompleteAddressPair(byte shaderType, uint remainingRegister)
    {
        var context = CreateContext(shaderType, 2, out var memory);
        WriteRegister(memory, 0, remainingRegister, 0x1122);
        WriteRegister(memory, 1, 0x10A, 0x3344);

        AssertFailure(context, memory, IncompleteRegistersResult);

        Assert.Equal(0x1122u, ReadUInt32(memory, RegistersAddress + 4));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void CreateShader_RejectsResourceOnlyHardwareStage(byte shaderType)
    {
        var context = CreateContext(shaderType, 2, out var memory);
        WriteRegister(memory, 0, 0x10A, 0);
        WriteRegister(memory, 1, 0x10B, 0);
        AssertFailure(context, memory, IncompleteRegistersResult);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(8)]
    public void CreateShader_RejectsNullPointerWithDeclaredEntries(byte shaderType)
    {
        var context = CreateContext(shaderType, 2, out var memory);
        WriteUInt64(memory, HeaderAddress + 0x20, 0);
        AssertFailure(context, memory, IncompleteRegistersResult);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(4, 0)]
    [InlineData(5, 0)]
    [InlineData(8, 0)]
    [InlineData(1, 4)]
    [InlineData(4, 4)]
    [InlineData(5, 4)]
    [InlineData(8, 4)]
    public void CreateShader_RejectsUnreadableEntryOrValue(byte shaderType, uint readableBytes)
    {
        var context = CreateContext(shaderType, 1, out var memory);
        var tableAddress = BaseAddress + MemorySize - readableBytes;
        WriteUInt64(memory, HeaderAddress + 0x20, tableAddress - (HeaderAddress + 0x20));
        if (readableBytes != 0)
        {
            WriteUInt32(memory, tableAddress, 0x10A);
        }

        AssertFailure(context, memory, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    public void CreateShader_RejectsTableAddressOverflow(byte shaderType)
    {
        var context = CreateContext(shaderType, 2, out var memory);
        WriteUInt64(memory, HeaderAddress + 0x20, (ulong.MaxValue - 7) - (HeaderAddress + 0x20));
        AssertFailure(context, memory, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [Theory]
    [InlineData(9)]
    [InlineData(255)]
    public void CreateShader_RejectsUnknownTypeEvenWithAddressPair(byte shaderType)
    {
        var context = CreateContext(shaderType, 2, out var memory);
        WriteRegister(memory, 0, 0x8, 0);
        WriteRegister(memory, 1, 0x9, 0);
        AssertFailure(context, memory, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
    }

    [Theory]
    [InlineData(4u)]
    [InlineData(12u)]
    public void CreateShader_ReportsAddressWriteFailure(uint valueOffset)
    {
        _ = CreateContext(1, 2, out var memory);
        WriteRegister(memory, 0, 0x8, 0);
        WriteRegister(memory, 1, 0x9, 0);
        var context = new CpuContext(new RejectedWriteMemory(memory, RegistersAddress + valueOffset), Generation.Gen5)
        {
            [CpuRegister.Rdi] = DestinationAddress,
            [CpuRegister.Rsi] = HeaderAddress,
            [CpuRegister.Rdx] = CodeAddress,
        };

        AssertFailure(context, memory, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    private static CpuContext CreateContext(byte shaderType, byte registerCount, out FakeCpuMemory memory)
    {
        memory = new FakeCpuMemory(BaseAddress, MemorySize);
        WriteUInt64(memory, DestinationAddress, DestinationSentinel);
        WriteUInt32(memory, HeaderAddress, 0x34333231);
        WriteUInt32(memory, HeaderAddress + 4, 0x18);
        WriteUInt64(memory, HeaderAddress + 0x20, registerCount == 0 ? 0 : RegistersAddress - (HeaderAddress + 0x20));
        Assert.True(memory.TryWrite(HeaderAddress + 0x5A, [shaderType]));
        Assert.True(memory.TryWrite(HeaderAddress + 0x5C, [registerCount]));
        return new CpuContext(memory, Generation.Gen5)
        {
            [CpuRegister.Rdi] = DestinationAddress,
            [CpuRegister.Rsi] = HeaderAddress,
            [CpuRegister.Rdx] = CodeAddress,
        };
    }

    private static void AssertSuccess(CpuContext context, FakeCpuMemory memory)
    {
        Assert.Equal(0, AgcExports.CreateShader(context));
        Assert.Equal(0UL, context[CpuRegister.Rax]);
        Assert.Equal(HeaderAddress, ReadUInt64(memory, DestinationAddress));
        Assert.Equal(CodeAddress, ReadUInt64(memory, HeaderAddress + 0x10));
    }

    private static void AssertFailure(CpuContext context, FakeCpuMemory memory, int expectedResult)
    {
        Assert.Equal(expectedResult, AgcExports.CreateShader(context));
        Assert.Equal(unchecked((ulong)expectedResult), context[CpuRegister.Rax]);
        Assert.Equal(DestinationSentinel, ReadUInt64(memory, DestinationAddress));
    }

    private static void WriteRegister(FakeCpuMemory memory, int registerIndex, uint registerOffset, uint value)
    {
        var entryAddress = RegistersAddress + (ulong)(registerIndex * 8);
        WriteUInt32(memory, entryAddress, registerOffset);
        WriteUInt32(memory, entryAddress + 4, value);
    }

    private static uint ReadUInt32(FakeCpuMemory memory, ulong address)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        Assert.True(memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    private static ulong ReadUInt64(FakeCpuMemory memory, ulong address)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        Assert.True(memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }

    private static void WriteUInt32(FakeCpuMemory memory, ulong address, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        Assert.True(memory.TryWrite(address, bytes));
    }

    private static void WriteUInt64(FakeCpuMemory memory, ulong address, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        Assert.True(memory.TryWrite(address, bytes));
    }

    private sealed class RejectedWriteMemory(FakeCpuMemory memory, ulong rejectedAddress) : ICpuMemory
    {
        public bool TryRead(ulong virtualAddress, Span<byte> destination) => memory.TryRead(virtualAddress, destination);

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source) =>
            virtualAddress != rejectedAddress && memory.TryWrite(virtualAddress, source);
    }
}
