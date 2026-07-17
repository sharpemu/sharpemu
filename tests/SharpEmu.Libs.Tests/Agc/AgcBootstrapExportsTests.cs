// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcBootstrapExportsTests
{
    private const ulong MemoryBase = 0x1_0000_0000;

    [Theory]
    [InlineData("cb-nop", 7, 28)]
    [InlineData("cb-eop", 0, 32)]
    [InlineData("acb-acquire", 0, 32)]
    [InlineData("dcb-acquire", 0, 32)]
    [InlineData("dcb-rewind", 0, 8)]
    [InlineData("dcb-jump", 0, 16)]
    public void PacketSizeQueriesReturnEmittedByteCounts(string export, ulong argument, ulong expected)
    {
        var context = new CpuContext(new FakeCpuMemory(MemoryBase, 0x1000), Generation.Gen5);
        context[CpuRegister.Rdi] = argument;

        var result = export switch
        {
            "cb-nop" => AgcExports.CbNopGetSize(context),
            "cb-eop" => AgcExports.CbQueueEndOfPipeActionGetSize(context),
            "acb-acquire" => AgcExports.AcbAcquireMemGetSize(context),
            "dcb-acquire" => AgcExports.DcbAcquireMemGetSize(context),
            "dcb-rewind" => AgcExports.DcbRewindGetSize(context),
            "dcb-jump" => AgcExports.DcbJumpGetSize(context),
            _ => throw new InvalidOperationException(export),
        };

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(expected, context[CpuRegister.Rax]);
    }

    [Fact]
    public void DriverHardwareRegistrationsAreAccepted()
    {
        var context = new CpuContext(new FakeCpuMemory(MemoryBase, 0x1000), Generation.Gen5);

        context[CpuRegister.Rdi] = 0x3_12B0_0200;
        context[CpuRegister.Rsi] = 0x3FFF8;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, AgcExports.DriverSetTFRing(context));
        Assert.Equal(0UL, context[CpuRegister.Rax]);

        context[CpuRegister.Rdi] = 0x1234;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, AgcExports.DriverSetHsOffchipParam(context));
        Assert.Equal(0UL, context[CpuRegister.Rax]);
    }

    [Fact]
    public void DirectUcRegisterWritesSetUconfigPacketAndAdvancesCursor()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        var commandBufferAddress = MemoryBase + 0x100;
        var commandAddress = MemoryBase + 0x400;

        WriteUInt64(memory, commandBufferAddress + 0x10, commandAddress);
        WriteUInt64(memory, commandBufferAddress + 0x18, commandAddress + 0x100);
        WriteUInt64(memory, commandBufferAddress + 0x20, 0);
        WriteUInt64(memory, commandBufferAddress + 0x28, 0);
        WriteUInt32(memory, commandBufferAddress + 0x30, 0);

        context[CpuRegister.Rdi] = commandBufferAddress;
        context[CpuRegister.Rsi] = (0x1234UL << 32) | 0xDEAD_BEEFUL;

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, AgcExports.DcbSetUcRegisterDirect(context));
        Assert.Equal(commandAddress, context[CpuRegister.Rax]);
        Assert.Equal(commandAddress + 12, ReadUInt64(memory, commandBufferAddress + 0x10));
        Assert.Equal(0xC001_7900u, ReadUInt32(memory, commandAddress));
        Assert.Equal(0x1234u, ReadUInt32(memory, commandAddress + 4));
        Assert.Equal(0xDEAD_BEEFu, ReadUInt32(memory, commandAddress + 8));
    }

    [Fact]
    public void DriverRegisterOwnerSupportsOwnerFirstFlow()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        var nameAddress = memory.WriteCString(MemoryBase + 0x100, "owner-first");
        var ownerAddress = MemoryBase + 0x200;

        context[CpuRegister.Rdi] = ownerAddress;
        context[CpuRegister.Rsi] = nameAddress;

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, AgcExports.DriverRegisterOwner(context));
        Assert.Equal(2u, ReadUInt32(memory, ownerAddress));
    }

    [Fact]
    public void DriverRegisterOwnerMemoryFaultDoesNotConsumeOwnerOrRegistrationMode()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        var nameAddress = memory.WriteCString(MemoryBase + 0x100, "owner-first");

        context[CpuRegister.Rdi] = MemoryBase + 0x2000;
        context[CpuRegister.Rsi] = nameAddress;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            AgcExports.DriverRegisterOwner(context));

        var ownerAddress = MemoryBase + 0x200;
        context[CpuRegister.Rdi] = ownerAddress;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, AgcExports.DriverRegisterOwner(context));
        Assert.Equal(2u, ReadUInt32(memory, ownerAddress));
    }

    [Fact]
    public void ExplicitInitializationReplacesOwnerFirstRegistrationState()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        var ownerAddress = MemoryBase + 0x200;
        var firstNameAddress = memory.WriteCString(MemoryBase + 0x100, "implicit-owner");
        var explicitNameAddress = memory.WriteCString(MemoryBase + 0x180, "explicit-owner");

        context[CpuRegister.Rdi] = ownerAddress;
        context[CpuRegister.Rsi] = firstNameAddress;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, AgcExports.DriverRegisterOwner(context));
        Assert.Equal(2u, ReadUInt32(memory, ownerAddress));

        context[CpuRegister.Rdi] = MemoryBase + 0x300;
        context[CpuRegister.Rsi] = 0x1E0;
        context[CpuRegister.Rdx] = 1;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            AgcExports.DriverInitResourceRegistration(context));

        context[CpuRegister.Rdi] = ownerAddress;
        context[CpuRegister.Rsi] = explicitNameAddress;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, AgcExports.DriverRegisterOwner(context));
        Assert.Equal(2u, ReadUInt32(memory, ownerAddress));
    }

    [Fact]
    public void DriverRegisterOwnerPreservesExplicitOwnerLimit()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        var firstNameAddress = memory.WriteCString(MemoryBase + 0x100, "first-owner");
        var secondNameAddress = memory.WriteCString(MemoryBase + 0x180, "second-owner");
        var ownerAddress = MemoryBase + 0x200;

        context[CpuRegister.Rdi] = MemoryBase + 0x300;
        context[CpuRegister.Rsi] = 0x1E0;
        context[CpuRegister.Rdx] = 1;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            AgcExports.DriverInitResourceRegistration(context));

        context[CpuRegister.Rdi] = ownerAddress;
        context[CpuRegister.Rsi] = firstNameAddress;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, AgcExports.DriverRegisterOwner(context));

        context[CpuRegister.Rdi] = ownerAddress;
        context[CpuRegister.Rsi] = secondNameAddress;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            AgcExports.DriverRegisterOwner(context));
    }

    [Fact]
    public void DriverRegisterResourceUsesSevenArgumentAbiInImplicitModeAndUnregisters()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x2000);
        var context = new CpuContext(memory, Generation.Gen5);
        var ownerNameAddress = memory.WriteCString(MemoryBase + 0x100, "implicit-owner");
        var ownerAddress = MemoryBase + 0x200;
        var resourceNameAddress = memory.WriteCString(MemoryBase + 0x300, "implicit-resource");
        var resourceHandleAddress = MemoryBase + 0x400;
        var stackAddress = MemoryBase + 0x1000;

        context[CpuRegister.Rdi] = ownerAddress;
        context[CpuRegister.Rsi] = ownerNameAddress;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, AgcExports.DriverRegisterOwner(context));
        var owner = ReadUInt32(memory, ownerAddress);
        Assert.Equal(2u, owner);

        WriteUInt32(memory, resourceHandleAddress, 0xCCCCCCCC);
        ConfigureRegisterResource(
            context,
            memory,
            resourceHandleAddress,
            owner,
            resourceAddress: MemoryBase + 0x800,
            resourceSize: 0x3456,
            resourceNameAddress,
            type: 0x55,
            flags: 0xA5A55A5A,
            stackAddress);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, AgcExports.DriverRegisterResource(context));
        var resourceHandle = ReadUInt32(memory, resourceHandleAddress);
        Assert.Equal(1u, resourceHandle);

        context[CpuRegister.Rdi] = resourceHandle;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, AgcExports.DriverUnregisterResource(context));
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            AgcExports.DriverUnregisterResource(context));
    }

    [Fact]
    public void DriverRegisterResourceRequiresARegistrationModeAndRejectsUnknownOwners()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x2000);
        var context = new CpuContext(memory, Generation.Gen5);
        var ownerNameAddress = memory.WriteCString(MemoryBase + 0x100, "explicit-owner");
        var ownerAddress = MemoryBase + 0x200;
        var resourceNameAddress = memory.WriteCString(MemoryBase + 0x300, "explicit-resource");
        var resourceHandleAddress = MemoryBase + 0x400;
        var stackAddress = MemoryBase + 0x1000;

        WriteUInt32(memory, resourceHandleAddress, 0x11111111);
        ConfigureRegisterResource(
            context,
            memory,
            resourceHandleAddress,
            owner: 1,
            resourceAddress: MemoryBase + 0x800,
            resourceSize: 0x100,
            resourceNameAddress,
            type: 3,
            flags: 4,
            stackAddress);
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            AgcExports.DriverRegisterResource(context));
        Assert.Equal(0x11111111u, ReadUInt32(memory, resourceHandleAddress));

        context[CpuRegister.Rdi] = MemoryBase + 0x600;
        context[CpuRegister.Rsi] = 0x400;
        context[CpuRegister.Rdx] = 2;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            AgcExports.DriverInitResourceRegistration(context));

        context[CpuRegister.Rdi] = ownerAddress;
        context[CpuRegister.Rsi] = ownerNameAddress;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, AgcExports.DriverRegisterOwner(context));
        var owner = ReadUInt32(memory, ownerAddress);
        Assert.Equal(2u, owner);

        WriteUInt32(memory, resourceHandleAddress, 0x22222222);
        ConfigureRegisterResource(
            context,
            memory,
            resourceHandleAddress,
            owner: 99,
            resourceAddress: MemoryBase + 0x800,
            resourceSize: 0x100,
            resourceNameAddress,
            type: 3,
            flags: 4,
            stackAddress);
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            AgcExports.DriverRegisterResource(context));
        Assert.Equal(0x22222222u, ReadUInt32(memory, resourceHandleAddress));

        ConfigureRegisterResource(
            context,
            memory,
            resourceHandleAddress,
            owner,
            resourceAddress: MemoryBase + 0x800,
            resourceSize: 0x100,
            resourceNameAddress,
            type: 3,
            flags: 4,
            stackAddress);
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, AgcExports.DriverRegisterResource(context));
        Assert.Equal(1u, ReadUInt32(memory, resourceHandleAddress));

        ConfigureRegisterResource(
            context,
            memory,
            resourceHandleAddress,
            owner: 1,
            resourceAddress: MemoryBase + 0x900,
            resourceSize: 0x200,
            resourceNameAddress,
            type: 5,
            flags: 6,
            stackAddress);
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, AgcExports.DriverRegisterResource(context));
        Assert.Equal(2u, ReadUInt32(memory, resourceHandleAddress));
    }

    [Fact]
    public void DriverRegisterResourceFaultsDoNotConsumeAHandle()
    {
        const ulong faultingAddress = 0xDEAD_0000_0000;
        var memory = new FakeCpuMemory(MemoryBase, 0x2000);
        var context = new CpuContext(memory, Generation.Gen5);
        var ownerNameAddress = memory.WriteCString(MemoryBase + 0x100, "fault-owner");
        var ownerAddress = MemoryBase + 0x200;
        var resourceNameAddress = memory.WriteCString(MemoryBase + 0x300, "fault-resource");
        var resourceHandleAddress = MemoryBase + 0x400;
        var stackAddress = MemoryBase + 0x1000;

        context[CpuRegister.Rdi] = ownerAddress;
        context[CpuRegister.Rsi] = ownerNameAddress;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, AgcExports.DriverRegisterOwner(context));
        var owner = ReadUInt32(memory, ownerAddress);

        WriteUInt32(memory, resourceHandleAddress, 0x33333333);
        ConfigureRegisterResource(
            context,
            memory,
            resourceHandleAddress,
            owner,
            resourceAddress: MemoryBase + 0x800,
            resourceSize: 0x100,
            resourceNameAddress,
            type: 7,
            flags: 8,
            stackAddress: faultingAddress,
            writeStackArgument: false);
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            AgcExports.DriverRegisterResource(context));
        Assert.Equal(0x33333333u, ReadUInt32(memory, resourceHandleAddress));

        ConfigureRegisterResource(
            context,
            memory,
            resourceHandleAddress: faultingAddress,
            owner,
            resourceAddress: MemoryBase + 0x800,
            resourceSize: 0x100,
            resourceNameAddress,
            type: 7,
            flags: 8,
            stackAddress);
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            AgcExports.DriverRegisterResource(context));

        ConfigureRegisterResource(
            context,
            memory,
            resourceHandleAddress,
            owner,
            resourceAddress: MemoryBase + 0x800,
            resourceSize: 0x100,
            resourceNameAddress,
            type: 7,
            flags: 8,
            stackAddress);
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, AgcExports.DriverRegisterResource(context));
        Assert.Equal(1u, ReadUInt32(memory, resourceHandleAddress));
    }

    [Fact]
    public void DriverGetDefaultOwnerReflectsRegisteredDefaultOwner()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        var ownerAddress = MemoryBase + 0x100;

        context[CpuRegister.Rdi] = MemoryBase + 0x300;
        context[CpuRegister.Rsi] = 0x200;
        context[CpuRegister.Rdx] = 1;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            AgcExports.DriverInitResourceRegistration(context));

        context[CpuRegister.Rdi] = 7;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            AgcExports.DriverRegisterDefaultOwner(context));

        context[CpuRegister.Rdi] = ownerAddress;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, AgcExports.DriverGetDefaultOwner(context));
        Assert.Equal(7u, ReadUInt32(memory, ownerAddress));
    }

    private static void ConfigureRegisterResource(
        CpuContext context,
        FakeCpuMemory memory,
        ulong resourceHandleAddress,
        uint owner,
        ulong resourceAddress,
        ulong resourceSize,
        ulong resourceNameAddress,
        uint type,
        uint flags,
        ulong stackAddress,
        bool writeStackArgument = true)
    {
        context[CpuRegister.Rdi] = resourceHandleAddress;
        context[CpuRegister.Rsi] = owner;
        context[CpuRegister.Rdx] = resourceAddress;
        context[CpuRegister.Rcx] = resourceSize;
        context[CpuRegister.R8] = resourceNameAddress;
        context[CpuRegister.R9] = type;
        context[CpuRegister.Rsp] = stackAddress;
        if (writeStackArgument)
        {
            WriteUInt32(memory, stackAddress + sizeof(ulong), flags);
        }
    }

    private static void WriteUInt64(FakeCpuMemory memory, ulong address, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        Assert.True(memory.TryWrite(address, bytes));
    }

    private static void WriteUInt32(FakeCpuMemory memory, ulong address, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        Assert.True(memory.TryWrite(address, bytes));
    }

    private static ulong ReadUInt64(FakeCpuMemory memory, ulong address)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        Assert.True(memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }

    private static uint ReadUInt32(FakeCpuMemory memory, ulong address)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        Assert.True(memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }
}
