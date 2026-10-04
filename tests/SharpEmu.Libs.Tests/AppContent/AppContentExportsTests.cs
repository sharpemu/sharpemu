// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.AppContent;
using Xunit;

namespace SharpEmu.Libs.Tests.AppContent;

public sealed class AppContentExportsTests
{
    private const ulong MemoryBase = 0x2_0000_0000;
    private const ulong AvailableSpaceAddress = MemoryBase + 0x100;

    private readonly FakeCpuMemory _memory = new(MemoryBase, 0x1000);

    [Fact]
    public void TemporaryDataGetAvailableSpaceKb_WritesNonzeroCapacity()
    {
        var ctx = new CpuContext(_memory, Generation.Gen5);
        ctx[CpuRegister.Rsi] = AvailableSpaceAddress;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            AppContentExports.AppContentTemporaryDataGetAvailableSpaceKb(ctx));

        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        Assert.True(_memory.TryRead(AvailableSpaceAddress, bytes));
        Assert.True(BinaryPrimitives.ReadUInt64LittleEndian(bytes) > 0);
    }

    [Fact]
    public void TemporaryDataGetAvailableSpaceKb_RejectsNullOutput()
    {
        var ctx = new CpuContext(_memory, Generation.Gen5);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            AppContentExports.AppContentTemporaryDataGetAvailableSpaceKb(ctx));
    }

    [Fact]
    public void AddcontMount_ClearsMountPointAndReportsMissingEntitlement()
    {
        var ctx = new CpuContext(_memory, Generation.Gen5);
        var mountPointAddress = MemoryBase + 0x200;
        var bytes = Enumerable.Repeat((byte)0xA5, 18).ToArray();
        Assert.True(_memory.TryWrite(mountPointAddress, bytes));

        ctx[CpuRegister.Rdi] = 0;
        ctx[CpuRegister.Rsi] = MemoryBase + 0x100;
        ctx[CpuRegister.Rdx] = mountPointAddress;
        Assert.Equal(unchecked((int)0x80D90007), AppContentExports.AppContentAddcontMount(ctx));

        Assert.True(_memory.TryRead(mountPointAddress, bytes));
        Assert.All(bytes[..16], value => Assert.Equal(0, value));
        Assert.All(bytes[16..], value => Assert.Equal(0xA5, value));
    }

    [Theory]
    [InlineData(0UL, 0x2_0000_0200UL)]
    [InlineData(0x2_0000_0100UL, 0UL)]
    public void AddcontMount_RejectsNullPointers(ulong entitlementLabel, ulong mountPoint)
    {
        var ctx = new CpuContext(_memory, Generation.Gen5);
        ctx[CpuRegister.Rsi] = entitlementLabel;
        ctx[CpuRegister.Rdx] = mountPoint;

        Assert.Equal(unchecked((int)0x80D90002), AppContentExports.AppContentAddcontMount(ctx));
    }

    [Fact]
    public void AddcontMount_RegistersKnownNid()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport("VANhIWcqYak", out var export));
        Assert.Equal("sceAppContentAddcontMount", export.Name);
        Assert.Equal("libSceAppContent", export.LibraryName);
    }
}
