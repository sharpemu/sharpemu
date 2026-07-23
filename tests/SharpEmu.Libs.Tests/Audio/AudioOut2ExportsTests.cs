// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Audio;
using Xunit;

namespace SharpEmu.Libs.Tests.Audio;

public sealed class AudioOut2ExportsTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong ContextParamAddress = MemoryBase + 0x100;
    private const ulong ContextMemoryAddress = MemoryBase + 0x800;
    private const ulong ContextOutputAddress = MemoryBase + 0x200;
    private const ulong PortParamAddress = MemoryBase + 0x300;
    private const ulong PortOutputAddress = MemoryBase + 0x400;

    [Fact]
    public void ContextGetQueueLevel_WritesTwo32BitOutputsWithoutClobberingAdjacentField()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5)
        {
            [CpuRegister.Rsi] = MemoryBase + 0x100,
            [CpuRegister.Rdx] = MemoryBase + 0x108,
        };

        WriteUInt32(memory, MemoryBase + 0x100, 0x11111111);
        WriteUInt32(memory, MemoryBase + 0x104, 0xA5A5A5A5);
        WriteUInt32(memory, MemoryBase + 0x108, 0x22222222);

        Assert.Equal(0, AudioOut2Exports.AudioOut2ContextGetQueueLevel(context));
        Assert.Equal(0u, ReadUInt32(memory, MemoryBase + 0x100));
        Assert.Equal(0xA5A5A5A5u, ReadUInt32(memory, MemoryBase + 0x104));
        Assert.Equal(0u, ReadUInt32(memory, MemoryBase + 0x108));
    }

    [Fact]
    public void ContextGetQueueLevel_RejectsMissingSecondOutput()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5)
        {
            [CpuRegister.Rsi] = MemoryBase + 0x100,
            [CpuRegister.Rdx] = 0,
        };

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            AudioOut2Exports.AudioOut2ContextGetQueueLevel(context));
    }

    [Fact]
    public void ContextGetQueueLevel_ReportsConfiguredDepthForKnownContext()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5)
        {
            [CpuRegister.Rdi] = ContextParamAddress,
            [CpuRegister.Rsi] = ContextMemoryAddress,
            [CpuRegister.Rdx] = 0x10000,
            [CpuRegister.Rcx] = ContextOutputAddress,
        };

        AudioOut2Exports.SetQueueDepthForTests(2);
        try
        {
            Assert.Equal(0, AudioOut2Exports.AudioOut2ContextCreate(context));
            var contextHandle = ReadUInt64(memory, ContextOutputAddress);

            context[CpuRegister.Rdi] = contextHandle;
            context[CpuRegister.Rsi] = MemoryBase + 0x100;
            context[CpuRegister.Rdx] = MemoryBase + 0x108;
            Assert.Equal(0, AudioOut2Exports.AudioOut2ContextGetQueueLevel(context));
            Assert.Equal(0u, ReadUInt32(memory, MemoryBase + 0x100));
            Assert.Equal(2u, ReadUInt32(memory, MemoryBase + 0x108));

            context[CpuRegister.Rdi] = contextHandle;
            Assert.Equal(0, AudioOut2Exports.AudioOut2ContextDestroy(context));
        }
        finally
        {
            AudioOut2Exports.SetQueueDepthForTests(0);
        }
    }

    [Fact]
    public void ContextGetQueueLevel_KeepsLegacyZeroOutputsForUnknownContextInDepthMode()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5)
        {
            [CpuRegister.Rdi] = 0xDEAD,
            [CpuRegister.Rsi] = MemoryBase + 0x100,
            [CpuRegister.Rdx] = MemoryBase + 0x108,
        };

        WriteUInt32(memory, MemoryBase + 0x100, 0x11111111);
        WriteUInt32(memory, MemoryBase + 0x108, 0x22222222);

        AudioOut2Exports.SetQueueDepthForTests(2);
        try
        {
            Assert.Equal(0, AudioOut2Exports.AudioOut2ContextGetQueueLevel(context));
            Assert.Equal(0u, ReadUInt32(memory, MemoryBase + 0x100));
            Assert.Equal(0u, ReadUInt32(memory, MemoryBase + 0x108));
        }
        finally
        {
            AudioOut2Exports.SetQueueDepthForTests(0);
        }
    }

    [Fact]
    public void ContextPush_LeavesQueueDrainedInDepthMode()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5)
        {
            [CpuRegister.Rdi] = ContextParamAddress,
            [CpuRegister.Rsi] = ContextMemoryAddress,
            [CpuRegister.Rdx] = 0x10000,
            [CpuRegister.Rcx] = ContextOutputAddress,
        };

        AudioOut2Exports.SetQueueDepthForTests(1);
        try
        {
            Assert.Equal(0, AudioOut2Exports.AudioOut2ContextCreate(context));
            var contextHandle = ReadUInt64(memory, ContextOutputAddress);

            context[CpuRegister.Rdi] = contextHandle;
            Assert.Equal(0, AudioOut2Exports.AudioOut2ContextPush(context));

            context[CpuRegister.Rdi] = contextHandle;
            context[CpuRegister.Rsi] = MemoryBase + 0x100;
            context[CpuRegister.Rdx] = MemoryBase + 0x108;
            Assert.Equal(0, AudioOut2Exports.AudioOut2ContextGetQueueLevel(context));
            Assert.Equal(0u, ReadUInt32(memory, MemoryBase + 0x100));
            Assert.Equal(1u, ReadUInt32(memory, MemoryBase + 0x108));

            context[CpuRegister.Rdi] = contextHandle;
            Assert.Equal(0, AudioOut2Exports.AudioOut2ContextDestroy(context));
        }
        finally
        {
            AudioOut2Exports.SetQueueDepthForTests(0);
        }
    }

    [Fact]
    public void PortCreate_UsesContextHandleAndReadsPortTypeFromParameterBlock()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);

        context[CpuRegister.Rdi] = ContextParamAddress;
        context[CpuRegister.Rsi] = ContextMemoryAddress;
        context[CpuRegister.Rdx] = 0x10000;
        context[CpuRegister.Rcx] = ContextOutputAddress;
        Assert.Equal(0, AudioOut2Exports.AudioOut2ContextCreate(context));
        var contextHandle = ReadUInt64(memory, ContextOutputAddress);
        Assert.NotEqual(0UL, contextHandle);

        Span<byte> portParam = stackalloc byte[0x20];
        BinaryPrimitives.WriteUInt32LittleEndian(portParam, 2);
        Assert.True(memory.TryWrite(PortParamAddress, portParam));

        context[CpuRegister.Rdi] = contextHandle;
        context[CpuRegister.Rsi] = PortParamAddress;
        context[CpuRegister.Rdx] = PortOutputAddress;
        // RCX is intentionally unrelated to the ABI now; the context handle is
        // in RDI and the output port pointer is in RDX.
        context[CpuRegister.Rcx] = MemoryBase + 0x500;

        Assert.Equal(0, AudioOut2Exports.AudioOut2PortCreate(context));
        var portHandle = ReadUInt64(memory, PortOutputAddress);
        Assert.Equal(0x2002u, (uint)(portHandle >> 16));
        Assert.NotEqual(0u, (uint)portHandle & 0xFFu);

        context[CpuRegister.Rdi] = contextHandle;
        Assert.Equal(0, AudioOut2Exports.AudioOut2ContextDestroy(context));
    }

    [Fact]
    public void PortCreate_RejectsUnknownContextHandle()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5)
        {
            [CpuRegister.Rdi] = 0xDEAD,
            [CpuRegister.Rsi] = PortParamAddress,
            [CpuRegister.Rdx] = PortOutputAddress,
        };

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            AudioOut2Exports.AudioOut2PortCreate(context));
    }

    [Fact]
    public void GhostAudioOut2Exports_RegisterForGen5()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        AssertExport(manager, "XHl38ZNknbs", "sceAudioOut2MasteringInit");
        AssertExport(manager, "TViD1EZXkNI", "sceAudioOut2Set3DLatency");
        AssertExport(manager, "4dq2rblWlg0", "sceAudioOut2ContextSetAttributes");
        AssertExport(manager, "R7d0F1g2qsU", "sceAudioOut2ContextGetQueueLevel");
        AssertExport(manager, "JK2wamZPzwM", "sceAudioOut2PortCreate");
    }

    private static void AssertExport(ModuleManager manager, string nid, string name)
    {
        Assert.True(manager.TryGetExport(nid, out var export), $"NID {nid} did not register.");
        Assert.Equal(name, export.Name);
        Assert.Equal("libSceAudioOut2", export.LibraryName);
        Assert.Equal(Generation.Gen5, export.Target);
    }

    private static ulong ReadUInt64(FakeCpuMemory memory, ulong address)
    {
        Span<byte> value = stackalloc byte[sizeof(ulong)];
        Assert.True(memory.TryRead(address, value));
        return BinaryPrimitives.ReadUInt64LittleEndian(value);
    }

    private static uint ReadUInt32(FakeCpuMemory memory, ulong address)
    {
        Span<byte> value = stackalloc byte[sizeof(uint)];
        Assert.True(memory.TryRead(address, value));
        return BinaryPrimitives.ReadUInt32LittleEndian(value);
    }

    private static void WriteUInt32(FakeCpuMemory memory, ulong address, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        Assert.True(memory.TryWrite(address, bytes));
    }
}
