// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;

using SharpEmu.HLE;
using SharpEmu.Libs.Fiber;

using Xunit;

namespace SharpEmu.Libs.Tests.Fiber;

/// <summary>
/// Contract tests for the libSceFiber HLE exports. These pin the current
/// validation and layout behaviour of <see cref="FiberExports"/>; they do not
/// exercise a live guest thread scheduler.
/// </summary>
public sealed class FiberExportsTests
{
    private const ulong Base = 0x3_0000_0000UL;
    private const int RegionSize = 0x2000;

    private const int ErrorNull = unchecked((int)0x80590001);
    private const int ErrorAlignment = unchecked((int)0x80590002);
    private const int ErrorRange = unchecked((int)0x80590003);
    private const int ErrorInvalid = unchecked((int)0x80590004);
    private const int ErrorPermission = unchecked((int)0x80590005);

    private const uint SignatureStart = 0xDEF1649Cu;
    private const uint SignatureEnd = 0xB37592A0u;
    private const ulong StackSignature = 0x7149F2CA7149F2CAUL;
    private const uint StateIdle = 2;

    private const ulong FiberAddress = Base;
    private const ulong NameAddress = Base + 0x200;
    private const ulong ContextAddress = Base + 0x400;
    private const ulong EntryAddress = 0x4_0000_1000UL;
    private const ulong InfoAddress = Base + 0x800;

    public FiberExportsTests()
    {
        FiberExports.ResetRuntimeState();
    }

    [Fact]
    public void OptParamInitialize_NullParam_ReturnsNullError()
    {
        var memory = new FakeCpuMemory(Base, RegionSize);
        var context = new CpuContext(memory, Generation.Gen5);

        context[CpuRegister.Rdi] = 0;

        var result = FiberExports.FiberOptParamInitialize(context);

        Assert.Equal(ErrorNull, result);
    }

    [Fact]
    public void GetSelf_NullOutAddress_ReturnsNullError()
    {
        var memory = new FakeCpuMemory(Base, RegionSize);
        var context = new CpuContext(memory, Generation.Gen5);

        context[CpuRegister.Rdi] = 0;

        var result = FiberExports.FiberGetSelf(context);

        Assert.Equal(ErrorNull, result);
    }

    [Fact]
    public void GetSelf_OutsideFiberContext_ReturnsPermissionError()
    {
        var memory = new FakeCpuMemory(Base, RegionSize);
        var context = new CpuContext(memory, Generation.Gen5);

        context[CpuRegister.Rdi] = Base + 0x100;

        var result = FiberExports.FiberGetSelf(context);

        Assert.Equal(ErrorPermission, result);
    }

    [Fact]
    public void GetInfo_NullInfo_ReturnsNullError()
    {
        var memory = new FakeCpuMemory(Base, RegionSize);
        var context = new CpuContext(memory, Generation.Gen5);

        context[CpuRegister.Rdi] = FiberAddress;
        context[CpuRegister.Rsi] = 0;

        var result = FiberExports.FiberGetInfo(context);

        Assert.Equal(ErrorNull, result);
    }

    [Fact]
    public void Initialize_NullFiber_ReturnsNullError()
    {
        var memory = new FakeCpuMemory(Base, RegionSize);
        var context = new CpuContext(memory, Generation.Gen5);
        WriteCString(memory, NameAddress, "F");

        context[CpuRegister.Rdi] = 0;
        context[CpuRegister.Rsi] = NameAddress;
        context[CpuRegister.Rdx] = EntryAddress;

        var result = FiberExports.FiberInitialize(context);

        Assert.Equal(ErrorNull, result);
    }

    [Fact]
    public void Initialize_NullName_ReturnsNullError()
    {
        var memory = new FakeCpuMemory(Base, RegionSize);
        var context = new CpuContext(memory, Generation.Gen5);

        context[CpuRegister.Rdi] = FiberAddress;
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = EntryAddress;

        var result = FiberExports.FiberInitialize(context);

        Assert.Equal(ErrorNull, result);
    }

    [Fact]
    public void Initialize_NullEntry_ReturnsNullError()
    {
        var memory = new FakeCpuMemory(Base, RegionSize);
        var context = new CpuContext(memory, Generation.Gen5);
        WriteCString(memory, NameAddress, "F");

        context[CpuRegister.Rdi] = FiberAddress;
        context[CpuRegister.Rsi] = NameAddress;
        context[CpuRegister.Rdx] = 0;

        var result = FiberExports.FiberInitialize(context);

        Assert.Equal(ErrorNull, result);
    }

    [Fact]
    public void Initialize_MisalignedFiber_ReturnsAlignmentError()
    {
        var memory = new FakeCpuMemory(Base, RegionSize);
        var context = new CpuContext(memory, Generation.Gen5);
        WriteCString(memory, NameAddress, "F");

        context[CpuRegister.Rdi] = FiberAddress + 4;
        context[CpuRegister.Rsi] = NameAddress;
        context[CpuRegister.Rdx] = EntryAddress;

        var result = FiberExports.FiberInitialize(context);

        Assert.Equal(ErrorAlignment, result);
    }

    [Fact]
    public void Initialize_TooSmallContextSize_ReturnsRangeError()
    {
        var memory = new FakeCpuMemory(Base, RegionSize);
        var context = new CpuContext(memory, Generation.Gen5);
        WriteCString(memory, NameAddress, "F");

        context[CpuRegister.Rdi] = FiberAddress;
        context[CpuRegister.Rsi] = NameAddress;
        context[CpuRegister.Rdx] = EntryAddress;
        context[CpuRegister.R8] = ContextAddress;
        context[CpuRegister.R9] = 256;

        var result = FiberExports.FiberInitialize(context);

        Assert.Equal(ErrorRange, result);
    }

    [Fact]
    public void Initialize_ContextAddressWithoutSize_ReturnsInvalidError()
    {
        var memory = new FakeCpuMemory(Base, RegionSize);
        var context = new CpuContext(memory, Generation.Gen5);
        WriteCString(memory, NameAddress, "F");

        context[CpuRegister.Rdi] = FiberAddress;
        context[CpuRegister.Rsi] = NameAddress;
        context[CpuRegister.Rdx] = EntryAddress;
        context[CpuRegister.R8] = ContextAddress;
        context[CpuRegister.R9] = 0;

        var result = FiberExports.FiberInitialize(context);

        Assert.Equal(ErrorInvalid, result);
    }

    [Fact]
    public void Initialize_Valid_WritesExpectedLayout()
    {
        var memory = new FakeCpuMemory(Base, RegionSize);
        var context = new CpuContext(memory, Generation.Gen5);
        const string name = "TestFiber";
        WriteCString(memory, NameAddress, name);
        const ulong argOnInitialize = 0xDEADUL;
        const ulong contextSize = 512UL;

        context[CpuRegister.Rdi] = FiberAddress;
        context[CpuRegister.Rsi] = NameAddress;
        context[CpuRegister.Rdx] = EntryAddress;
        context[CpuRegister.Rcx] = argOnInitialize;
        context[CpuRegister.R8] = ContextAddress;
        context[CpuRegister.R9] = contextSize;
        // RSP defaults to 0 (unmapped); ReadStackArg64 falls back to 0 ->
        // optParam = 0, buildVersion = 0. ApplyInitializationFlags(0, 0, false) == 0.

        var result = FiberExports.FiberInitialize(context);

        Assert.Equal(0, result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);

        Assert.Equal(SignatureStart, ReadUInt32(memory, FiberAddress + 0));
        Assert.Equal(StateIdle, ReadUInt32(memory, FiberAddress + 4));
        Assert.Equal(EntryAddress, ReadUInt64(memory, FiberAddress + 8));
        Assert.Equal(argOnInitialize, ReadUInt64(memory, FiberAddress + 16));
        Assert.Equal(ContextAddress, ReadUInt64(memory, FiberAddress + 24));
        Assert.Equal(contextSize, ReadUInt64(memory, FiberAddress + 32));
        AssertInlineName(memory, FiberAddress + 40, name);
        Assert.Equal(0UL, ReadUInt64(memory, FiberAddress + 72));
        Assert.Equal(0u, ReadUInt32(memory, FiberAddress + 80));
        Assert.Equal(ContextAddress, ReadUInt64(memory, FiberAddress + 88));
        Assert.Equal(ContextAddress + contextSize, ReadUInt64(memory, FiberAddress + 96));
        Assert.Equal(SignatureEnd, ReadUInt32(memory, FiberAddress + 104));
        Assert.Equal(StackSignature, ReadUInt64(memory, ContextAddress));
    }

    [Fact]
    public void GetInfo_AfterInitialize_RoundTripsFields()
    {
        var memory = new FakeCpuMemory(Base, RegionSize);
        var context = new CpuContext(memory, Generation.Gen5);
        const string name = "RoundTrip";
        WriteCString(memory, NameAddress, name);
        const ulong argOnInitialize = 0xCAFEUL;
        const ulong contextSize = 512UL;

        context[CpuRegister.Rdi] = FiberAddress;
        context[CpuRegister.Rsi] = NameAddress;
        context[CpuRegister.Rdx] = EntryAddress;
        context[CpuRegister.Rcx] = argOnInitialize;
        context[CpuRegister.R8] = ContextAddress;
        context[CpuRegister.R9] = contextSize;

        Assert.Equal(0, FiberExports.FiberInitialize(context));

        WriteUInt64(memory, InfoAddress, 128);
        context[CpuRegister.Rdi] = FiberAddress;
        context[CpuRegister.Rsi] = InfoAddress;

        var result = FiberExports.FiberGetInfo(context);

        Assert.Equal(0, result);
        Assert.Equal(EntryAddress, ReadUInt64(memory, InfoAddress + 8));
        Assert.Equal(argOnInitialize, ReadUInt64(memory, InfoAddress + 16));
        Assert.Equal(ContextAddress, ReadUInt64(memory, InfoAddress + 24));
        Assert.Equal(contextSize, ReadUInt64(memory, InfoAddress + 32));
        AssertInlineName(memory, InfoAddress + 40, name);
        Assert.Equal(ulong.MaxValue, ReadUInt64(memory, InfoAddress + 72));
    }

    [Fact]
    public void GetInfo_WrongSize_ReturnsInvalidError()
    {
        var memory = new FakeCpuMemory(Base, RegionSize);
        var context = new CpuContext(memory, Generation.Gen5);
        WriteCString(memory, NameAddress, "F");

        context[CpuRegister.Rdi] = FiberAddress;
        context[CpuRegister.Rsi] = NameAddress;
        context[CpuRegister.Rdx] = EntryAddress;
        context[CpuRegister.R8] = ContextAddress;
        context[CpuRegister.R9] = 512;

        Assert.Equal(0, FiberExports.FiberInitialize(context));

        WriteUInt64(memory, InfoAddress, 64);
        context[CpuRegister.Rdi] = FiberAddress;
        context[CpuRegister.Rsi] = InfoAddress;

        var result = FiberExports.FiberGetInfo(context);

        Assert.Equal(ErrorInvalid, result);
    }

    private static void WriteCString(FakeCpuMemory memory, ulong address, string text)
    {
        memory.WriteCString(address, text);
    }

    private static void WriteUInt64(FakeCpuMemory memory, ulong address, ulong value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        Assert.True(memory.TryWrite(address, buffer));
    }

    private static uint ReadUInt32(FakeCpuMemory memory, ulong address)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        Assert.True(memory.TryRead(address, buffer));
        return BinaryPrimitives.ReadUInt32LittleEndian(buffer);
    }

    private static ulong ReadUInt64(FakeCpuMemory memory, ulong address)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        Assert.True(memory.TryRead(address, buffer));
        return BinaryPrimitives.ReadUInt64LittleEndian(buffer);
    }

    private static void AssertInlineName(FakeCpuMemory memory, ulong address, string expected)
    {
        Span<byte> buffer = stackalloc byte[32];
        Assert.True(memory.TryRead(address, buffer));
        var length = buffer.IndexOf((byte)0);
        if (length < 0)
        {
            length = buffer.Length;
        }

        Assert.Equal(expected, System.Text.Encoding.UTF8.GetString(buffer[..length]));
    }
}
