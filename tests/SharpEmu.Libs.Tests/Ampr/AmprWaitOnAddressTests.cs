// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Ampr;
using System.Buffers.Binary;
using Xunit;

namespace SharpEmu.Libs.Tests.Ampr;

[Collection("AmprFileRegistry")]
public sealed class AmprWaitOnAddressTests
{
    [Fact]
    public void MeasureCommandSizeWaitOnAddress_UsesReferenceWidth()
    {
        const string nid = "jIlc4p5dSD0";
        const ulong memoryBase = 0x2_0000_0000;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        var manager = CreateManagerWithExport(nid, "sceAmprMeasureCommandSizeWaitOnAddress");

        Assert.Equal(8UL, Measure(manager, context, nid, memoryBase + 0x100, 0, compare: 0, flush: 0));
        Assert.Equal(12UL, Measure(manager, context, nid, memoryBase + 0x100, 0xCAFE, compare: 0, flush: 0));
        Assert.Equal(16UL, Measure(manager, context, nid, memoryBase + 0x100, 0x1_0000_0000, compare: 0, flush: 0));
    }

    [Fact]
    public void MeasureCommandSizeWaitOnAddress_ValidatesModernAndLegacyEnums()
    {
        const ulong memoryBase = 0x2_0000_0000;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        var modern = CreateManagerWithExport("jIlc4p5dSD0", "sceAmprMeasureCommandSizeWaitOnAddress");
        var legacy = CreateManagerWithExport("0BMj1hgG+kE", "sceAmprMeasureCommandSizeWaitOnAddress_04_00");

        context[CpuRegister.Rdi] = memoryBase + 0x100;
        context[CpuRegister.Rsi] = 0xCAFE;
        context[CpuRegister.Rdx] = 4;
        context[CpuRegister.Rcx] = 0;
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            modern.Dispatch("jIlc4p5dSD0", context));

        context[CpuRegister.Rdx] = 6;
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, legacy.Dispatch("0BMj1hgG+kE", context));
        Assert.Equal(12UL, context[CpuRegister.Rax]);

        context[CpuRegister.Rdi] = 0;
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            legacy.Dispatch("0BMj1hgG+kE", context));
    }

    [Fact]
    public void CommandBufferWaitOnAddress_EncodesWaitAndCompletesWhenPredicateIsSatisfied()
    {
        const string nid = "V7GQTEeUfhw";
        const ulong memoryBase = 0x2_0000_0000;
        const ulong commandBufferAddress = memoryBase + 0x100;
        const ulong recordBufferAddress = memoryBase + 0x200;
        const ulong waitAddress = memoryBase + 0x800;
        const ulong reference = 0xCAFE;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        var manager = CreateManagerWithExport(nid, "sceAmprCommandBufferWaitOnAddress");

        context[CpuRegister.Rdi] = commandBufferAddress;
        context[CpuRegister.Rsi] = recordBufferAddress;
        context[CpuRegister.Rdx] = 0x100;
        Assert.Equal(0, AmprExports.CommandBufferConstructor(context));
        Assert.Equal(0, AmprExports.CommandBufferSetBuffer(context));

        Assert.True(context.TryWriteUInt64(waitAddress, reference));
        context[CpuRegister.Rdi] = commandBufferAddress;
        context[CpuRegister.Rsi] = waitAddress;
        context[CpuRegister.Rdx] = reference;
        context[CpuRegister.Rcx] = 0;
        context[CpuRegister.R8] = 0;
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, manager.Dispatch(nid, context));

        Assert.True(context.TryReadUInt32(commandBufferAddress + 4, out var offset));
        Assert.True(context.TryReadUInt32(commandBufferAddress + 8, out var commandCount));
        Assert.Equal(12U, offset);
        Assert.Equal(1U, commandCount);

        Span<byte> record = stackalloc byte[12];
        Assert.True(memory.TryRead(recordBufferAddress, record));
        var expectedHeader = (uint)((waitAddress >> 16) & 0xFFFF0000UL) | 0x0201U;
        Assert.Equal(expectedHeader, BinaryPrimitives.ReadUInt32LittleEndian(record));
        Assert.Equal(0x00000800U, BinaryPrimitives.ReadUInt32LittleEndian(record[4..]));
        Assert.Equal(0x0000CAFEU, BinaryPrimitives.ReadUInt32LittleEndian(record[8..]));

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK,
            AmprExports.CompleteCommandBuffer(context, commandBufferAddress));
    }

    [Fact]
    public void CommandBufferWaitOnAddress_SupportsAllModernAndLegacyComparisons()
    {
        const ulong memoryBase = 0x2_0000_0000;
        const ulong commandBufferAddress = memoryBase + 0x100;
        const ulong recordBufferAddress = memoryBase + 0x200;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        var modern = CreateManagerWithExport("V7GQTEeUfhw", "sceAmprCommandBufferWaitOnAddress");
        var legacy = CreateManagerWithExport("DLfoNxTFNVk", "sceAmprCommandBufferWaitOnAddress_04_00");
        var comparisons = new (uint Mode, ulong Value, ulong Reference, bool Legacy)[]
        {
            (0, 10, 10, false),
            (1, 11, 10, false),
            (2, 9, 10, false),
            (3, 11, 10, false),
            (4, 0, ulong.MaxValue, true),
            (5, 1, 0, true),
            (6, ulong.MaxValue, 0, true),
        };

        context[CpuRegister.Rdi] = commandBufferAddress;
        context[CpuRegister.Rsi] = recordBufferAddress;
        context[CpuRegister.Rdx] = 0x200;
        Assert.Equal(0, AmprExports.CommandBufferConstructor(context));
        Assert.Equal(0, AmprExports.CommandBufferSetBuffer(context));

        for (var index = 0; index < comparisons.Length; index++)
        {
            var comparison = comparisons[index];
            var address = memoryBase + 0x800 + (ulong)index * sizeof(ulong);
            Assert.True(context.TryWriteUInt64(address, comparison.Value));
            context[CpuRegister.Rdi] = commandBufferAddress;
            context[CpuRegister.Rsi] = address;
            context[CpuRegister.Rdx] = comparison.Reference;
            context[CpuRegister.Rcx] = comparison.Mode;
            context[CpuRegister.R8] = 0;
            var manager = comparison.Legacy ? legacy : modern;
            var nid = comparison.Legacy ? "DLfoNxTFNVk" : "V7GQTEeUfhw";
            Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, manager.Dispatch(nid, context));
        }

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK,
            AmprExports.CompleteCommandBuffer(context, commandBufferAddress));
    }

    [Fact]
    public void CommandBufferWaitOnAddress0400_RegistersLegacyVariant()
    {
        const string nid = "DLfoNxTFNVk";
        var manager = CreateManagerWithExport(nid, "sceAmprCommandBufferWaitOnAddress_04_00");

        Assert.True(manager.TryGetExport(nid, out var export));
        Assert.Equal("libSceAmpr", export.LibraryName);
        Assert.Equal(Generation.Gen5, export.Target);
    }

    private static ulong Measure(
        ModuleManager manager,
        CpuContext context,
        string nid,
        ulong address,
        ulong reference,
        ulong compare,
        ulong flush)
    {
        context[CpuRegister.Rdi] = address;
        context[CpuRegister.Rsi] = reference;
        context[CpuRegister.Rdx] = compare;
        context[CpuRegister.Rcx] = flush;
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, manager.Dispatch(nid, context));
        return context[CpuRegister.Rax];
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
