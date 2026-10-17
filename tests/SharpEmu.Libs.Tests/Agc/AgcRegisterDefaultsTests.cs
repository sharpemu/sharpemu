// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcRegisterDefaultsTests
{
    [Fact]
    public void GetRegisterDefaults2_Version7_ReturnsExactTessellationDefault()
    {
        var memory = new SparseGuestAddressSpace();
        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = 7;

        var result = AgcExports.GetRegisterDefaults2(ctx);
        var address = ctx[CpuRegister.Rax];

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(489u, ReadUInt32(memory, address + 0x20));
        Assert.Equal(127u, ReadUInt32(memory, address + 0x38));

        var contextTable = ReadUInt64(memory, address);
        AssertRegister(memory, ReadUInt64(memory, contextTable + (52 * 8)), 0x02D4, 0x88101010);
    }

    [Fact]
    public void GetRegisterDefaults2_Version8_ReturnsExactPublicLayout()
    {
        var memory = new SparseGuestAddressSpace();
        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = 8;

        var result = AgcExports.GetRegisterDefaults2(ctx);
        var address = ctx[CpuRegister.Rax];

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.NotEqual(0UL, address);
        Assert.Equal(489u, ReadUInt32(memory, address + 0x20));
        Assert.Equal(159u, ReadUInt32(memory, address + 0x24));
        Assert.Equal(55u, ReadUInt32(memory, address + 0x28));
        Assert.Equal(0u, ReadUInt32(memory, address + 0x2C));
        Assert.Equal(0UL, ReadUInt64(memory, address + 0x18));
        Assert.Equal(127u, ReadUInt32(memory, address + 0x38));

        var contextTable = ReadUInt64(memory, address);
        AssertRegister(memory, ReadUInt64(memory, contextTable + (1 * 8)), 0x0109, 0x00000008);
        AssertRegister(memory, ReadUInt64(memory, contextTable + (4 * 8)), 0x008E, 0x00000000);
        AssertRegister(memory, ReadUInt64(memory, contextTable + (6 * 8)), 0x0001, 0x11000100);

        var types = ReadUInt64(memory, address + 0x30);
        Assert.Equal(0xE24F806Du, ReadUInt32(memory, types));
        Assert.Equal(0x00040400u, ReadUInt32(memory, types + 4));
    }

    [Fact]
    public void GetRegisterDefaults2Internal_Version8_ReturnsFourthTable()
    {
        var memory = new SparseGuestAddressSpace();
        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = 8;

        var result = AgcExports.GetRegisterDefaults2Internal(ctx);
        var address = ctx[CpuRegister.Rax];

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(4u, ReadUInt32(memory, address + 0x20));
        Assert.Equal(15u, ReadUInt32(memory, address + 0x24));
        Assert.Equal(0u, ReadUInt32(memory, address + 0x28));
        Assert.Equal(6u, ReadUInt32(memory, address + 0x2C));
        Assert.NotEqual(0UL, ReadUInt64(memory, address + 0x18));
        Assert.Equal(22u, ReadUInt32(memory, address + 0x38));
    }

    [Fact]
    public void GetRegisterDefaults2_Version9_ReturnsExactPublicLayout()
    {
        var memory = new SparseGuestAddressSpace();
        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = 9;

        var result = AgcExports.GetRegisterDefaults2(ctx);
        var address = ctx[CpuRegister.Rax];

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(489u, ReadUInt32(memory, address + 0x20));
        Assert.Equal(160u, ReadUInt32(memory, address + 0x24));
        Assert.Equal(55u, ReadUInt32(memory, address + 0x28));
        Assert.Equal(0u, ReadUInt32(memory, address + 0x2C));
        Assert.Equal(127u, ReadUInt32(memory, address + 0x38));

        var contextTable = ReadUInt64(memory, address);
        AssertRegister(memory, ReadUInt64(memory, contextTable + (1 * 8)), 0x0109, 0x00000010);

        var shaderTable = ReadUInt64(memory, address + 8);
        var shaderGroup = ReadUInt64(memory, shaderTable + (12 * 8));
        AssertRegister(memory, shaderGroup + (2 * 8), 0x0081, 0x00000000);

        var types = ReadUInt64(memory, address + 0x30);
        Assert.Equal(0x00041031u, ReadUInt32(memory, types + (90 * 12) + 4));
    }

    [Fact]
    public void GetRegisterDefaults2Internal_Version9_ReturnsExactLayout()
    {
        var memory = new SparseGuestAddressSpace();
        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = 9;

        var result = AgcExports.GetRegisterDefaults2Internal(ctx);
        var address = ctx[CpuRegister.Rax];

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(4u, ReadUInt32(memory, address + 0x20));
        Assert.Equal(14u, ReadUInt32(memory, address + 0x24));
        Assert.Equal(1u, ReadUInt32(memory, address + 0x28));
        Assert.Equal(6u, ReadUInt32(memory, address + 0x2C));
        Assert.Equal(22u, ReadUInt32(memory, address + 0x38));

        var userConfigTable = ReadUInt64(memory, address + 0x10);
        AssertRegister(memory, ReadUInt64(memory, userConfigTable), 0x0260, 0x00000000);

        var types = ReadUInt64(memory, address + 0x30);
        Assert.Equal(0x60289246u, ReadUInt32(memory, types + (18 * 12)));
        Assert.Equal(0x00040402u, ReadUInt32(memory, types + (18 * 12) + 4));
    }

    [Theory]
    [InlineData(10u)]
    [InlineData(12u)]
    public void GetRegisterDefaults2_Version10Family_ReturnsExactPublicLayout(uint version)
    {
        var memory = new SparseGuestAddressSpace();
        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = version;

        var result = AgcExports.GetRegisterDefaults2(ctx);
        var address = ctx[CpuRegister.Rax];

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.NotEqual(0UL, address);
        Assert.Equal(482u, ReadUInt32(memory, address + 0x20));
        Assert.Equal(159u, ReadUInt32(memory, address + 0x24));
        Assert.Equal(55u, ReadUInt32(memory, address + 0x28));
        Assert.Equal(0u, ReadUInt32(memory, address + 0x2C));
        Assert.Equal(128u, ReadUInt32(memory, address + 0x38));

        var contextTable = ReadUInt64(memory, address);
        AssertRegister(memory, ReadUInt64(memory, contextTable + (1 * 8)), 0x0109, 0x00000010);
        AssertRegister(memory, ReadUInt64(memory, contextTable + (7 * 8)), 0x0001, 0x11000100);
    }

    [Theory]
    [InlineData(10u)]
    [InlineData(12u)]
    public void GetRegisterDefaults2Internal_Version10Family_ReturnsAllTables(uint version)
    {
        var memory = new SparseGuestAddressSpace();
        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = version;

        var result = AgcExports.GetRegisterDefaults2Internal(ctx);
        var address = ctx[CpuRegister.Rax];

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(9u, ReadUInt32(memory, address + 0x20));
        Assert.Equal(15u, ReadUInt32(memory, address + 0x24));
        Assert.Equal(1u, ReadUInt32(memory, address + 0x28));
        Assert.Equal(6u, ReadUInt32(memory, address + 0x2C));
        Assert.NotEqual(0UL, ReadUInt64(memory, address + 0x18));
        Assert.Equal(28u, ReadUInt32(memory, address + 0x38));
    }

    [Fact]
    public void GetRegisterDefaults2_Version11_ReturnsExactLayout()
    {
        var memory = new SparseGuestAddressSpace();
        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = 11;

        var result = AgcExports.GetRegisterDefaults2(ctx);
        var address = ctx[CpuRegister.Rax];

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(488u, ReadUInt32(memory, address + 0x20));
        Assert.Equal(162u, ReadUInt32(memory, address + 0x24));
        Assert.Equal(56u, ReadUInt32(memory, address + 0x28));
        Assert.Equal(0u, ReadUInt32(memory, address + 0x2C));
        Assert.Equal(137u, ReadUInt32(memory, address + 0x38));
    }

    [Fact]
    public void GetRegisterDefaults2_Version13_UsesVersion11ByDefault()
    {
        const string variable = "SHARPEMU_AGC_VERSION13_DEFAULTS";
        var original = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, null);
            var memory = new SparseGuestAddressSpace();
            var ctx = new CpuContext(memory, Generation.Gen5);
            ctx[CpuRegister.Rdi] = 13;

            var result = AgcExports.GetRegisterDefaults2(ctx);
            var address = ctx[CpuRegister.Rax];

            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
            Assert.Equal(488u, ReadUInt32(memory, address + 0x20));
            Assert.Equal(137u, ReadUInt32(memory, address + 0x38));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, original);
        }
    }

    [Fact]
    public void GetRegisterDefaults2_Version13_UsesLegacyLayoutWhenRequested()
    {
        const string variable = "SHARPEMU_AGC_VERSION13_DEFAULTS";
        var original = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, "legacy");
            var memory = new SparseGuestAddressSpace();
            var ctx = new CpuContext(memory, Generation.Gen5);
            ctx[CpuRegister.Rdi] = 13;

            var result = AgcExports.GetRegisterDefaults2(ctx);
            var address = ctx[CpuRegister.Rax];

            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
            Assert.Equal(0u, ReadUInt32(memory, address + 0x20));
            Assert.Equal(0u, ReadUInt32(memory, address + 0x24));
            Assert.Equal(0u, ReadUInt32(memory, address + 0x28));
            Assert.Equal(127u, ReadUInt32(memory, address + 0x38));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, original);
        }
    }

    [Fact]
    public void GetRegisterDefaults2_CachesEachRequestedVersionSeparately()
    {
        var memory = new SparseGuestAddressSpace();
        var ctx = new CpuContext(memory, Generation.Gen5);

        ctx[CpuRegister.Rdi] = 8;
        AgcExports.GetRegisterDefaults2(ctx);
        var version8Address = ctx[CpuRegister.Rax];

        ctx[CpuRegister.Rdi] = 12;
        AgcExports.GetRegisterDefaults2(ctx);
        var version12Address = ctx[CpuRegister.Rax];

        ctx[CpuRegister.Rdi] = 8;
        AgcExports.GetRegisterDefaults2(ctx);

        Assert.NotEqual(version8Address, version12Address);
        Assert.Equal(version8Address, ctx[CpuRegister.Rax]);
    }

    private static void AssertRegister(
        ICpuMemory memory,
        ulong address,
        uint expectedOffset,
        uint expectedValue)
    {
        Assert.Equal(expectedOffset, ReadUInt32(memory, address));
        Assert.Equal(expectedValue, ReadUInt32(memory, address + 4));
    }

    private static uint ReadUInt32(ICpuMemory memory, ulong address)
    {
        Span<byte> bytes = stackalloc byte[4];
        Assert.True(memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    private static ulong ReadUInt64(ICpuMemory memory, ulong address)
    {
        Span<byte> bytes = stackalloc byte[8];
        Assert.True(memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }

    private sealed class SparseGuestAddressSpace : ICpuMemory, IGuestAddressSpace
    {
        private readonly List<Allocation> _allocations = [];

        public bool TryRead(ulong virtualAddress, Span<byte> destination) =>
            TryResolve(virtualAddress, destination.Length, out var allocation, out var offset) &&
            CopyTo(allocation!, offset, destination);

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
        {
            if (!TryResolve(virtualAddress, source.Length, out var allocation, out var offset))
            {
                return false;
            }

            source.CopyTo(allocation!.Storage.AsSpan(offset, source.Length));
            return true;
        }

        public bool TryAllocateGuestMemory(ulong size, ulong alignment, out ulong address) =>
            TryAllocateAtOrAbove(0x1_0000_0000, size, false, alignment, out address);

        public bool TryFreeGuestMemory(ulong address) => false;

        public ulong AllocateAt(
            ulong desiredAddress,
            ulong size,
            bool executable = true,
            bool allowAlternative = true)
        {
            if (TryAllocateExact(desiredAddress, size))
            {
                return desiredAddress;
            }

            return allowAlternative &&
                   TryAllocateAtOrAbove(desiredAddress, size, executable, 0x1000, out var address)
                ? address
                : 0;
        }

        public bool TryBackFixedRange(ulong address, ulong size, bool executable) =>
            TryAllocateExact(address, size);

        public bool TryAllocateAtOrAbove(
            ulong desiredAddress,
            ulong size,
            bool executable,
            ulong alignment,
            out ulong actualAddress)
        {
            alignment = Math.Max(alignment, 1);
            var candidate = AlignUp(desiredAddress, alignment);
            foreach (var allocation in _allocations.OrderBy(item => item.Address))
            {
                if (candidate + size <= allocation.Address)
                {
                    break;
                }

                if (candidate < allocation.Address + (ulong)allocation.Storage.Length)
                {
                    candidate = AlignUp(
                        allocation.Address + (ulong)allocation.Storage.Length,
                        alignment);
                }
            }

            actualAddress = TryAllocateExact(candidate, size) ? candidate : 0;
            return actualAddress != 0;
        }

        public bool TryEnsureRangeCommitted(ulong address, ulong size) =>
            size <= int.MaxValue && TryResolve(address, (int)size, out _, out _);

        public bool TryProtect(ulong address, ulong size, GuestPageProtection protection) => true;

        private bool TryAllocateExact(ulong address, ulong size)
        {
            if (address == 0 || size == 0 || size > int.MaxValue ||
                _allocations.Any(item =>
                    address < item.Address + (ulong)item.Storage.Length &&
                    item.Address < address + size))
            {
                return false;
            }

            _allocations.Add(new Allocation(address, new byte[(int)size]));
            return true;
        }

        private bool TryResolve(
            ulong address,
            int length,
            out Allocation? allocation,
            out int offset)
        {
            allocation = _allocations.FirstOrDefault(item =>
                address >= item.Address &&
                address + (ulong)length <= item.Address + (ulong)item.Storage.Length);
            if (allocation is null)
            {
                offset = 0;
                return false;
            }

            offset = (int)(address - allocation.Address);
            return true;
        }

        private static bool CopyTo(Allocation allocation, int offset, Span<byte> destination)
        {
            allocation.Storage.AsSpan(offset, destination.Length).CopyTo(destination);
            return true;
        }

        private static ulong AlignUp(ulong value, ulong alignment) =>
            (value + alignment - 1) & ~(alignment - 1);

        private sealed record Allocation(ulong Address, byte[] Storage);
    }
}
