// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.GnmMode;
using SharpEmu.Libs.Np;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Np;

public sealed class NpContentRestrictionTests
{
    [Fact]
    public void ContentRestrictionIsSharedByBothGenerations()
    {
        foreach (var generation in new[] { Generation.Gen4, Generation.Gen5 })
        {
            var export = Assert.Single(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(generation),
                export => export.Nid == "A2CQ3kgSopQ");
            Assert.Equal("sceNpSetContentRestriction", export.Name);
            Assert.Equal("libSceNpManager", export.LibraryName);
            Assert.Equal(NpManagerExports.NpSetContentRestriction, export.Function);
        }
        Assert.Equal(GnmExportRegistry.CreateExports().Count,
            GnmExportRegistry.CreateExports().Select(export => export.Nid).Distinct().Count());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(256)]
    public void ValidContentRestrictionSucceedsWithoutModifyingGuestMemory(int count)
    {
        var context = CreateContext(24, 18, count, count == 0 ? 0UL : 0x1100UL);
        var before = new byte[24];
        Assert.True(context.Memory.TryRead(0x1000, before));
        Assert.Equal(0, NpManagerExports.NpSetContentRestriction(context));
        Assert.Equal(0UL, context[CpuRegister.Rax]);
        var after = new byte[24];
        Assert.True(context.Memory.TryRead(0x1000, after));
        Assert.Equal(before, after);
    }

    [Theory]
    [InlineData(8UL, 18, 0, 0UL, 0x80550011u)]
    [InlineData(24UL, -1, 0, 0UL, 0x80550003u)]
    [InlineData(24UL, 18, -1, 0UL, 0x80550003u)]
    [InlineData(24UL, 18, 257, 0x1100UL, 0x80550003u)]
    [InlineData(24UL, 18, 1, 0UL, 0x80550003u)]
    [InlineData(24UL, 18, 1, 0x5000UL, 0x80550003u)]
    [InlineData(24UL, 18, 1, ulong.MaxValue, 0x80550003u)]
    public void RejectsInvalidRestrictionArguments(ulong size, int age, int count, ulong entries, uint expected)
    {
        var context = CreateContext(size, age, count, entries);
        Assert.Equal(unchecked((int)expected), NpManagerExports.NpSetContentRestriction(context));
        Assert.Equal(unchecked((ulong)(long)(int)expected), context[CpuRegister.Rax]);
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(0xFFFFUL)]
    public void RejectsNullOrUnmappedRestrictionPointer(ulong pointer)
    {
        var context = CreateContext(24, 18, 0, 0);
        context[CpuRegister.Rdi] = pointer;
        Assert.Equal(unchecked((int)0x80550003), NpManagerExports.NpSetContentRestriction(context));
    }

    private static CpuContext CreateContext(ulong size, int age, int count, ulong entries)
    {
        var memory = new FakeCpuMemory(0x1000, 0x2000);
        Span<byte> header = stackalloc byte[24];
        header.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(header, size);
        header[8] = unchecked((byte)age);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], count);
        BinaryPrimitives.WriteUInt64LittleEndian(header[16..], entries);
        Assert.True(memory.TryWrite(0x1000, header));
        var context = new CpuContext(memory, Generation.Gen4);
        context[CpuRegister.Rdi] = 0x1000;
        return context;
    }
}
