// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Ampr;
using System.Buffers.Binary;
using Xunit;

namespace SharpEmu.Libs.Tests.Ampr;

public sealed class AmprWriteAddressTests
{
    [Fact]
    public void MeasureCommandSizeWriteAddress0400_MatchesOnCompletionVariant()
    {
        const string nid = "4fgtGfXDrFc";
        const ulong memoryBase = 0x1_0000_0000;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        var manager = CreateManagerWithExport(
            nid,
            "sceAmprMeasureCommandSizeWriteAddress_04_00");

        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, manager.Dispatch(nid, context));
        var measured = context[CpuRegister.Rax];

        Assert.Equal(0, AmprExports.MeasureCommandSizeWriteAddressOnCompletion(context));
        Assert.Equal(context[CpuRegister.Rax], measured);
    }

    [Fact]
    public void CommandBufferWriteAddress0400_WritesValueOnCompletion()
    {
        const string nid = "j0+3uJMxYJY";
        const ulong memoryBase = 0x1_0000_0000;
        const ulong commandBufferAddress = memoryBase + 0x100;
        const ulong recordBufferAddress = memoryBase + 0x200;
        const ulong watcherAddress = memoryBase + 0x800;
        const ulong watcherValue = 1;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        var manager = CreateManagerWithExport(
            nid,
            "sceAmprCommandBufferWriteAddress_04_00");

        context[CpuRegister.Rdi] = commandBufferAddress;
        context[CpuRegister.Rsi] = recordBufferAddress;
        context[CpuRegister.Rdx] = 0x100;

        Assert.Equal(0, AmprExports.CommandBufferConstructor(context));

        context[CpuRegister.Rdi] = commandBufferAddress;
        context[CpuRegister.Rsi] = watcherAddress;
        context[CpuRegister.Rdx] = watcherValue;

        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, manager.Dispatch(nid, context));

        Span<byte> watcher = stackalloc byte[sizeof(ulong)];
        Assert.True(memory.TryRead(watcherAddress, watcher));
        Assert.Equal(0UL, BinaryPrimitives.ReadUInt64LittleEndian(watcher));

        Assert.Equal(0, AmprExports.CompleteCommandBuffer(context, commandBufferAddress));

        Assert.True(memory.TryRead(watcherAddress, watcher));
        Assert.Equal(watcherValue, BinaryPrimitives.ReadUInt64LittleEndian(watcher));
    }

    private static ModuleManager CreateManagerWithExport(string nid, string exportName)
    {
        var manager = new ModuleManager();
        manager.RegisterExports(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport(nid, out var export), $"NID {nid} did not register.");
        Assert.Equal(exportName, export.Name);
        Assert.Equal("libSceAmpr", export.LibraryName);
        Assert.Equal(Generation.Gen5, export.Target);
        return manager;
    }
}
