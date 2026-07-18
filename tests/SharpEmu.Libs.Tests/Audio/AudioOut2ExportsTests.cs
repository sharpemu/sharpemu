// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Audio;
using Xunit;

namespace SharpEmu.Libs.Tests.Audio;

[CollectionDefinition("AudioOut2State", DisableParallelization = true)]
public sealed class AudioOut2StateCollection
{
    public const string Name = "AudioOut2State";
}

[Collection(AudioOut2StateCollection.Name)]
public sealed class AudioOut2ExportsTests : IDisposable
{
    private const ulong MemoryBase = 0x1_0000_0000;

    public AudioOut2ExportsTests()
    {
        AudioOut2Exports.ResetForTests();
    }

    [Fact]
    public void MasteringInit_RegistersForGen5AndDoesNotWriteGuestMemory()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x100);
        Span<byte> sentinel = stackalloc byte[0x20];
        sentinel.Fill(0xCC);
        Assert.True(memory.TryWrite(MemoryBase, sentinel));

        var context = new CpuContext(memory, Generation.Gen5)
        {
            [CpuRegister.Rax] = ulong.MaxValue,
            [CpuRegister.Rdi] = 0,
            [CpuRegister.Rsi] = MemoryBase,
            [CpuRegister.Rdx] = MemoryBase + 0x40,
        };

        Assert.Equal(0, AudioOut2Exports.AudioOut2MasteringInit(context));
        Assert.Equal(0UL, context[CpuRegister.Rax]);

        Span<byte> actual = stackalloc byte[sentinel.Length];
        Assert.True(memory.TryRead(MemoryBase, actual));
        Assert.True(actual.SequenceEqual(sentinel));

        var gen5Manager = new ModuleManager();
        gen5Manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));
        Assert.True(gen5Manager.TryGetExport("XHl38ZNknbs", out var export));
        Assert.Equal("sceAudioOut2MasteringInit", export.Name);
        Assert.Equal("libSceAudioOut2", export.LibraryName);

        var gen4Manager = new ModuleManager();
        gen4Manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen4));
        Assert.False(gen4Manager.TryGetExport("XHl38ZNknbs", out _));
    }

    [Fact]
    public void Set3DLatency_RegistersForGen5AndReturnsSuccess()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x100);
        var context = new CpuContext(memory, Generation.Gen5)
        {
            [CpuRegister.Rax] = ulong.MaxValue,
            [CpuRegister.Rdi] = 0xFF,
            [CpuRegister.Rsi] = 2,
            [CpuRegister.Rdx] = 0,
            [CpuRegister.Rcx] = 1,
            [CpuRegister.R8] = 8,
            [CpuRegister.R9] = 8,
        };

        Assert.Equal(0, AudioOut2Exports.AudioOut2Set3DLatency(context));
        Assert.Equal(0UL, context[CpuRegister.Rax]);

        var gen5Manager = new ModuleManager();
        gen5Manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));
        Assert.True(gen5Manager.TryGetExport("TViD1EZXkNI", out var export));
        Assert.Equal("sceAudioOut2Set3DLatency", export.Name);
        Assert.Equal("libSceAudioOut2", export.LibraryName);

        var gen4Manager = new ModuleManager();
        gen4Manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen4));
        Assert.False(gen4Manager.TryGetExport("TViD1EZXkNI", out _));
    }

    [Fact]
    public void ContextSetAttributes_RegistersForGen5AndReturnsSuccess()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x100);
        var context = new CpuContext(memory, Generation.Gen5)
        {
            [CpuRegister.Rax] = ulong.MaxValue,
            [CpuRegister.Rdi] = 3,
            [CpuRegister.Rsi] = MemoryBase,
            [CpuRegister.Rdx] = 1,
        };

        Assert.Equal(0, AudioOut2Exports.AudioOut2ContextSetAttributes(context));
        Assert.Equal(0UL, context[CpuRegister.Rax]);

        var gen5Manager = new ModuleManager();
        gen5Manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));
        Assert.True(gen5Manager.TryGetExport("4dq2rblWlg0", out var export));
        Assert.Equal("sceAudioOut2ContextSetAttributes", export.Name);
        Assert.Equal("libSceAudioOut2", export.LibraryName);

        var gen4Manager = new ModuleManager();
        gen4Manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen4));
        Assert.False(gen4Manager.TryGetExport("4dq2rblWlg0", out _));
    }

    [Fact]
    public void PortCreate_UsesContextHandleAndAllowsDefaultUser()
    {
        const ulong contextParamAddress = MemoryBase + 0x100;
        const ulong contextMemoryAddress = MemoryBase + 0x1000;
        const ulong contextAddress = MemoryBase + 0x200;
        const ulong portParamAddress = MemoryBase + 0x300;
        const ulong portAddress = MemoryBase + 0x400;
        var memory = new FakeCpuMemory(MemoryBase, 0x20_000);
        var context = new CpuContext(memory, Generation.Gen5)
        {
            [CpuRegister.Rdi] = contextParamAddress,
            [CpuRegister.Rsi] = contextMemoryAddress,
            [CpuRegister.Rdx] = 0x1_000,
            [CpuRegister.Rcx] = contextAddress,
        };

        Assert.Equal(0, AudioOut2Exports.AudioOut2ContextCreate(context));
        var contextHandle = ReadUInt64(memory, contextAddress);

        Span<byte> portParam = stackalloc byte[0x20];
        BinaryPrimitives.WriteUInt32LittleEndian(portParam, 2);
        Assert.True(memory.TryWrite(portParamAddress, portParam));
        context[CpuRegister.Rdi] = contextHandle;
        context[CpuRegister.Rsi] = portParamAddress;
        context[CpuRegister.Rdx] = portAddress;
        context[CpuRegister.Rcx] = 0;

        Assert.Equal(0, AudioOut2Exports.AudioOut2PortCreate(context));
        Assert.Equal(0x2002_0001UL, ReadUInt64(memory, portAddress));
    }

    [Fact]
    public void ContextGetQueueLevel_WritesTwo32BitOutputsWithoutOverrun()
    {
        const ulong levelAddress = MemoryBase + 0x20;
        const ulong availableAddress = MemoryBase + 0x30;
        var memory = new FakeCpuMemory(MemoryBase, 0x100);
        Span<byte> sentinel = stackalloc byte[8];
        sentinel.Fill(0xCC);
        Assert.True(memory.TryWrite(levelAddress, sentinel));
        Assert.True(memory.TryWrite(availableAddress, sentinel));
        var context = new CpuContext(memory, Generation.Gen5)
        {
            [CpuRegister.Rdi] = 3,
            [CpuRegister.Rsi] = levelAddress,
            [CpuRegister.Rdx] = availableAddress,
        };

        Assert.Equal(0, AudioOut2Exports.AudioOut2ContextGetQueueLevel(context));

        Span<byte> level = stackalloc byte[8];
        Span<byte> available = stackalloc byte[8];
        Assert.True(memory.TryRead(levelAddress, level));
        Assert.True(memory.TryRead(availableAddress, available));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(level));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(available));
        Assert.True(level[sizeof(uint)..].SequenceEqual(sentinel[sizeof(uint)..]));
        Assert.True(available[sizeof(uint)..].SequenceEqual(sentinel[sizeof(uint)..]));
    }

    public void Dispose()
    {
        AudioOut2Exports.ResetForTests();
    }

    private static ulong ReadUInt64(FakeCpuMemory memory, ulong address)
    {
        Span<byte> value = stackalloc byte[sizeof(ulong)];
        Assert.True(memory.TryRead(address, value));
        return BinaryPrimitives.ReadUInt64LittleEndian(value);
    }
}
