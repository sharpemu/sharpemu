// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;

using SharpEmu.HLE;
using SharpEmu.Libs.LibcInternal;

using Xunit;

namespace SharpEmu.Libs.Tests.LibcInternal;

public sealed class LibcInternalExportsTests
{
    private const ulong Base = 0x3_0000_0000;
    private const ulong InfoAddress = Base + 0x100;
    private const ulong ExpectedInfoSize = 32;

    [Fact]
    public void HeapGetTraceInfo_NullPointer_ReturnsInvalidArgument()
    {
        var memory = new FakeCpuMemory(Base, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);

        context[CpuRegister.Rdi] = 0;

        var result = LibcInternalExports.LibcHeapGetTraceInfo(context);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
    }

    [Fact]
    public void HeapGetTraceInfo_WrongSize_ReturnsInvalidArgument()
    {
        var memory = new FakeCpuMemory(Base, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);

        Span<byte> sizeBytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(
            sizeBytes,
            ExpectedInfoSize - 1);

        Assert.True(memory.TryWrite(InfoAddress, sizeBytes));

        context[CpuRegister.Rdi] = InfoAddress;

        var result = LibcInternalExports.LibcHeapGetTraceInfo(context);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
    }

    [Fact]
    public void HeapGetTraceInfo_ValidBuffer_WritesStablePointers()
    {
        var memory = new FakeCpuMemory(Base, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);

        Span<byte> sizeBytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(
            sizeBytes,
            ExpectedInfoSize);

        Assert.True(memory.TryWrite(InfoAddress, sizeBytes));

        context[CpuRegister.Rdi] = InfoAddress;

        var firstResult =
            LibcInternalExports.LibcHeapGetTraceInfo(context);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            firstResult);
        Assert.Equal(0UL, context[CpuRegister.Rax]);

        Assert.True(
            context.TryReadUInt64(
                InfoAddress + 16,
                out var firstMaskAddress));

        Assert.True(
            context.TryReadUInt64(
                InfoAddress + 24,
                out var firstTableAddress));

        Assert.NotEqual(0UL, firstMaskAddress);
        Assert.Equal(firstMaskAddress + 8UL, firstTableAddress);

        var secondResult =
            LibcInternalExports.LibcHeapGetTraceInfo(context);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            secondResult);

        Assert.True(
            context.TryReadUInt64(
                InfoAddress + 16,
                out var secondMaskAddress));

        Assert.True(
            context.TryReadUInt64(
                InfoAddress + 24,
                out var secondTableAddress));

        Assert.Equal(firstMaskAddress, secondMaskAddress);
        Assert.Equal(firstTableAddress, secondTableAddress);
    }

    [Fact]
    public void HeapGetTraceInfo_TruncatedOutput_ReturnsMemoryFault()
    {
        var memory = new FakeCpuMemory(Base, 31);
        var context = new CpuContext(memory, Generation.Gen5);

        Span<byte> sizeBytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(
            sizeBytes,
            ExpectedInfoSize);

        Assert.True(memory.TryWrite(Base, sizeBytes));

        context[CpuRegister.Rdi] = Base;

        var result = LibcInternalExports.LibcHeapGetTraceInfo(context);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
    }
}