// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcResourceOwnerTests
{
    private const ulong BaseAddress = 0x1_0000_0000;
    private const ulong OwnerAddress = BaseAddress + 0x100;
    private const ulong NameAddress = BaseAddress + 0x200;
    private const ulong RegistrationMemoryAddress = BaseAddress + 0x400;

    [Fact]
    public void RegisterOwner_DoesNotRequireOptionalResourceRegistryMemory()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x2000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        memory.WriteCString(NameAddress, "GIRender");
        ctx[CpuRegister.Rdi] = OwnerAddress;
        ctx[CpuRegister.Rsi] = NameAddress;

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, AgcExports.DriverRegisterOwner(ctx));
        Assert.NotEqual(0u, ReadUInt32(memory, OwnerAddress));
    }

    [Fact]
    public void RegisterOwner_RespectsExplicitRegistryCapacity()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x2000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = RegistrationMemoryAddress;
        ctx[CpuRegister.Rsi] = 0x1000;
        ctx[CpuRegister.Rdx] = 1;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            AgcExports.DriverInitResourceRegistration(ctx));

        memory.WriteCString(NameAddress, "First");
        ctx[CpuRegister.Rdi] = OwnerAddress;
        ctx[CpuRegister.Rsi] = NameAddress;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, AgcExports.DriverRegisterOwner(ctx));

        memory.WriteCString(NameAddress, "Second");
        ctx[CpuRegister.Rdi] = OwnerAddress + 4;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            AgcExports.DriverRegisterOwner(ctx));
    }

    private static uint ReadUInt32(FakeCpuMemory memory, ulong address)
    {
        Span<byte> buffer = stackalloc byte[4];
        Assert.True(memory.TryRead(address, buffer));
        return BinaryPrimitives.ReadUInt32LittleEndian(buffer);
    }
}
