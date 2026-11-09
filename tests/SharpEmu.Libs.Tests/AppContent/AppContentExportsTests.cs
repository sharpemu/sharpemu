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
}
