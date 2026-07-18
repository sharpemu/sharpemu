// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Audio;
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
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong FirstContextAddress = MemoryBase + 0x100;
    private const ulong SecondContextAddress = MemoryBase + 0x104;

    private readonly FakeCpuMemory _memory = new(MemoryBase, 0x1000);
    private readonly CpuContext _context;

    public AcmExportsTests()
    {
        AcmExports.ResetForTests();
        _context = new CpuContext(_memory, Generation.Gen5);
    }

    [Fact]
    public void ContextLifecycle_WritesUnique32BitContextIds()
    {
        Assert.Equal(0, CreateContext(FirstContextAddress));
        Assert.Equal(1u, ReadUInt32(FirstContextAddress));

        Assert.Equal(0, CreateContext(SecondContextAddress));
        Assert.Equal(2u, ReadUInt32(SecondContextAddress));

        _context[CpuRegister.Rdi] = 1;
        Assert.Equal(0, AcmExports.AcmContextDestroy(_context));
        Assert.Equal(0UL, _context[CpuRegister.Rax]);
    }

    [Fact]
    public void ContextCreate_ValidatesAndWritesOnlyTheContextId()
    {
        Span<byte> sentinel = stackalloc byte[8];
        sentinel.Fill(0xCC);
        Assert.True(_memory.TryWrite(FirstContextAddress, sentinel));

        Assert.Equal(0, CreateContext(FirstContextAddress));

        Span<byte> actual = stackalloc byte[sentinel.Length];
        Assert.True(_memory.TryRead(FirstContextAddress, actual));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(actual));
        Assert.True(actual[sizeof(uint)..].SequenceEqual(sentinel[sizeof(uint)..]));

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            CreateContext(0));
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            CreateContext(MemoryBase + 0x1000));
    }

    [Fact]
    public void ContextExports_RegisterForGen5Only()
    {
        var gen5Manager = new ModuleManager();
        gen5Manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));
        Assert.True(gen5Manager.TryGetExport("ZIXln2K3XMk", out var create));
        Assert.Equal("sceAcmContextCreate", create.Name);
        Assert.Equal("libSceAcm", create.LibraryName);
        Assert.True(gen5Manager.TryGetExport("jBgBjAj02R8", out var destroy));
        Assert.Equal("sceAcmContextDestroy", destroy.Name);

        var gen4Manager = new ModuleManager();
        gen4Manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen4));
        Assert.False(gen4Manager.TryGetExport("ZIXln2K3XMk", out _));
        Assert.False(gen4Manager.TryGetExport("jBgBjAj02R8", out _));
    }

    public void Dispose()
    {
        AcmExports.ResetForTests();
    }

    private int CreateContext(ulong outputAddress)
    {
        _context[CpuRegister.Rdi] = outputAddress;
        _context[CpuRegister.Rsi] = MemoryBase + 0x200;
        _context[CpuRegister.Rdx] = 0;
        _context[CpuRegister.Rcx] = MemoryBase + 0x300;
        _context[CpuRegister.R8] = 0x100;
        _context[CpuRegister.R9] = MemoryBase + 0x400;
        return AcmExports.AcmContextCreate(_context);
    }

    private uint ReadUInt32(ulong address)
    {
        Span<byte> value = stackalloc byte[sizeof(uint)];
        Assert.True(_memory.TryRead(address, value));
        return BinaryPrimitives.ReadUInt32LittleEndian(value);
    }
}
