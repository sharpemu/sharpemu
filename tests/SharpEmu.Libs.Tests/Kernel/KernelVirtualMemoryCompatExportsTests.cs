// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SharpEmu.Core.Memory;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

[CollectionDefinition("KernelVirtualMemoryCompat", DisableParallelization = true)]
public sealed class KernelVirtualMemoryCompatCollectionDefinition;

[Collection("KernelVirtualMemoryCompat")]
public sealed class KernelVirtualMemoryCompatExportsTests
{
    private const ulong MemoryBase = 0x0000_7FFF_5000_0000;
    private const ulong InfoAddress = MemoryBase + 0x1000;
    private const ulong ReservedAddress = 0x0000_6F00_0000_0000;
    private const ulong ReservedLength = 0x4000;

    [Fact]
    public void VirtualQuery_ReservedRangeIsNotReportedAsCommitted()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x4000);
        var context = new CpuContext(memory, Generation.Gen5);
        KernelMemoryCompatExports.RegisterReservedVirtualRange(ReservedAddress, ReservedLength);

        context[CpuRegister.Rdi] = ReservedAddress;
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = InfoAddress;
        context[CpuRegister.Rcx] = 72;

        Assert.Equal(0, KernelMemoryCompatExports.KernelVirtualQuery(context));

        Span<byte> info = stackalloc byte[72];
        Assert.True(memory.TryRead(InfoAddress, info));
        Assert.Equal(ReservedAddress, BinaryPrimitives.ReadUInt64LittleEndian(info[0..8]));
        Assert.Equal(ReservedAddress + ReservedLength, BinaryPrimitives.ReadUInt64LittleEndian(info[8..16]));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(info[24..28]));
        Assert.Equal(0, info[32]);
    }

    [Fact]
    public void ReserveVirtualRange_FixedReservationReplacesDirectView()
    {
        if (!NativeGuestExecutionIsSupported)
        {
            return;
        }

        const ulong scratchAddress = 0x0000_6F10_0000_0000;
        const ulong mappingAddress = 0x0000_6F10_0010_0000;
        const ulong mappingLength = 0x4000;
        const ulong physicalSize = 0x20_000;
        using var memory = new PhysicalVirtualMemory();
        Assert.True(memory.TryAllocateAtExact(
            scratchAddress,
            0x4000,
            executable: false,
            out var scratch));
        Assert.Equal(
            mappingAddress,
            memory.ReserveAt(
                mappingAddress,
                mappingLength,
                executable: false,
                allowAlternative: false));
        Assert.True(memory.TryMapDirectMemory(
            mappingAddress,
            mappingLength,
            directMemoryOffset: 0,
            physicalSize,
            GuestPageProtection.Read | GuestPageProtection.Write,
            mappingLength,
            allowSearch: false,
            out var mappedAddress));
        Assert.True(memory.TryWriteUInt64(scratch, mappedAddress));

        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = scratch;
        context[CpuRegister.Rsi] = mappingLength;
        context[CpuRegister.Rdx] = 0x0040_0010;
        context[CpuRegister.Rcx] = mappingLength;

        Assert.Equal(0, KernelRuntimeCompatExports.KernelReserveVirtualRange(context));
        Assert.False(memory.TryUnmapDirectMemory(mappedAddress, mappingLength));
    }

    [Fact]
    public void Munmap_UnmapsReservedSubrangeAndPreservesRemainderMetadata()
    {
        if (!NativeGuestExecutionIsSupported)
        {
            return;
        }

        const ulong scratchAddress = 0x0000_6F20_0000_0000;
        const ulong reservedAddress = 0x0000_6F20_0010_0000;
        const ulong reservedLength = 0x40000;
        const ulong unmapLength = 0x30000;
        using var memory = new PhysicalVirtualMemory();
        Assert.True(memory.TryAllocateAtExact(
            scratchAddress,
            0x4000,
            executable: false,
            out var scratch));
        Assert.Equal(
            reservedAddress,
            memory.ReserveAt(
                reservedAddress,
                reservedLength,
                executable: false,
                allowAlternative: false));
        KernelMemoryCompatExports.RegisterReservedVirtualRange(reservedAddress, reservedLength);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = reservedAddress;
        context[CpuRegister.Rsi] = unmapLength;

        Assert.Equal(0, KernelMemoryCompatExports.KernelMunmap(context));

        context[CpuRegister.Rdi] = reservedAddress + unmapLength;
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = scratch;
        context[CpuRegister.Rcx] = 72;
        Assert.Equal(0, KernelMemoryCompatExports.KernelVirtualQuery(context));
        Span<byte> info = stackalloc byte[72];
        Assert.True(memory.TryRead(scratch, info));
        Assert.Equal(
            reservedAddress + unmapLength,
            BinaryPrimitives.ReadUInt64LittleEndian(info[0..8]));
        Assert.Equal(
            reservedAddress + reservedLength,
            BinaryPrimitives.ReadUInt64LittleEndian(info[8..16]));
        Assert.Equal(0, info[32]);
    }

    [Fact]
    public void MapDirectMemory_CarvesContainingReservationInVirtualQueryTable()
    {
        if (!NativeGuestExecutionIsSupported)
        {
            return;
        }

        const ulong scratchAddress = 0x0000_6F30_0000_0000;
        const ulong reservedAddress = 0x0000_6F30_0010_0000;
        const ulong reservedLength = 0x40000;
        const ulong mappingAddress = reservedAddress + 0x10000;
        const ulong mappingLength = 0x10000;
        using var memory = new PhysicalVirtualMemory();
        Assert.True(memory.TryAllocateAtExact(
            scratchAddress,
            0x4000,
            executable: false,
            out var scratch));
        Assert.Equal(
            reservedAddress,
            memory.ReserveAt(
                reservedAddress,
                reservedLength,
                executable: false,
                allowAlternative: false));
        KernelMemoryCompatExports.RegisterReservedVirtualRange(reservedAddress, reservedLength);
        Assert.True(memory.TryWriteUInt64(scratch, mappingAddress));
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = scratch;
        context[CpuRegister.Rsi] = mappingLength;
        context[CpuRegister.Rdx] = 0x02;
        context[CpuRegister.Rcx] = 0x0040_0010;
        context[CpuRegister.R8] = 0;
        context[CpuRegister.R9] = mappingLength;

        Assert.Equal(0, KernelMemoryCompatExports.KernelMapDirectMemory(context));

        AssertVirtualQueryState(context, memory, scratch, reservedAddress, reservedAddress, mappingAddress, 0x00);
        AssertVirtualQueryState(context, memory, scratch, mappingAddress, mappingAddress, mappingAddress + mappingLength, 0x12);
        AssertVirtualQueryState(
            context,
            memory,
            scratch,
            mappingAddress + mappingLength,
            mappingAddress + mappingLength,
            reservedAddress + reservedLength,
            0x00);

        context[CpuRegister.Rdi] = reservedAddress;
        context[CpuRegister.Rsi] = mappingLength * 2;
        Assert.Equal(0, KernelMemoryCompatExports.KernelMunmap(context));
        AssertVirtualQueryState(
            context,
            memory,
            scratch,
            mappingAddress + mappingLength,
            mappingAddress + mappingLength,
            reservedAddress + reservedLength,
            0x00);
    }

    [Fact]
    public void MapDirectMemory_FixedMappingReplacesOverlappingDirectViews()
    {
        if (!NativeGuestExecutionIsSupported)
        {
            return;
        }

        const ulong scratchAddress = 0x0000_6F40_0000_0000;
        const ulong mappingAddress = 0x0000_6F40_0010_0000;
        const ulong mappingLength = 0x80000;
        const ulong replacementAddress = mappingAddress + 0x30000;
        const ulong replacementLength = 0x80000;
        using var memory = new PhysicalVirtualMemory();
        Assert.True(memory.TryAllocateAtExact(scratchAddress, 0x4000, executable: false, out var scratch));
        var context = new CpuContext(memory, Generation.Gen5);

        Assert.True(memory.TryWriteUInt64(scratch, mappingAddress));
        context[CpuRegister.Rdi] = scratch;
        context[CpuRegister.Rsi] = mappingLength;
        context[CpuRegister.Rdx] = 0x02;
        context[CpuRegister.Rcx] = 0x0040_0010;
        context[CpuRegister.R8] = 0;
        context[CpuRegister.R9] = 0x10000;
        Assert.Equal(0, KernelMemoryCompatExports.KernelMapDirectMemory(context));

        Assert.True(memory.TryWriteUInt64(scratch, replacementAddress));
        context[CpuRegister.Rsi] = replacementLength;
        context[CpuRegister.R8] = 0x100000;
        Assert.Equal(0, KernelMemoryCompatExports.KernelMapDirectMemory(context));

        AssertVirtualQueryState(
            context,
            memory,
            scratch,
            mappingAddress,
            mappingAddress,
            replacementAddress,
            0x12);
        AssertVirtualQueryState(
            context,
            memory,
            scratch,
            replacementAddress,
            replacementAddress,
            replacementAddress + replacementLength,
            0x12);
    }

    [Fact]
    public void MapDirectMemory_FixedMappingUsesExactAddressRatherThanSearchAlignment()
    {
        if (!NativeGuestExecutionIsSupported)
        {
            return;
        }

        const ulong scratchAddress = 0x0000_6F44_0000_0000;
        const ulong arenaAddress = 0x0000_6F44_0010_0000;
        const ulong mappingAddress = 0x0000_6F44_0010_4000;
        const ulong mappingLength = 0x4000;
        using var memory = new PhysicalVirtualMemory();
        Assert.True(memory.TryAllocateAtExact(scratchAddress, 0x4000, executable: false, out var scratch));
        Assert.Equal(
            arenaAddress,
            memory.ReserveAt(
                arenaAddress,
                0x20000,
                executable: false,
                allowAlternative: false));
        KernelMemoryCompatExports.RegisterReservedVirtualRange(arenaAddress, 0x20000);
        Assert.True(memory.TryWriteUInt64(scratch, mappingAddress));
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = scratch;
        context[CpuRegister.Rsi] = mappingLength;
        context[CpuRegister.Rdx] = 0x02;
        context[CpuRegister.Rcx] = 0x0040_0010;
        context[CpuRegister.R8] = 0x140000;
        context[CpuRegister.R9] = 0x10000;

        Assert.Equal(0, KernelMemoryCompatExports.KernelMapDirectMemory(context));
        Assert.True(context.TryReadUInt64(scratch, out var mappedAddress));
        Assert.Equal(mappingAddress, mappedAddress);
        Assert.True(memory.IsAccessible(mappingAddress, mappingLength));
    }

    [Fact]
    public void MapDirectMemory_FixedNoOverwriteMapsFreeRange()
    {
        if (!NativeGuestExecutionIsSupported)
        {
            return;
        }

        const ulong scratchAddress = 0x0000_6F46_0000_0000;
        const ulong mappingAddress = 0x0000_6F46_0010_0000;
        using var memory = new PhysicalVirtualMemory();
        Assert.True(memory.TryAllocateAtExact(scratchAddress, 0x4000, executable: false, out var scratch));
        Assert.True(memory.TryWriteUInt64(scratch, mappingAddress));
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = scratch;
        context[CpuRegister.Rsi] = 0x4000;
        context[CpuRegister.Rdx] = 0x02;
        context[CpuRegister.Rcx] = 0x0040_0090;
        context[CpuRegister.R8] = 0x160000;
        context[CpuRegister.R9] = 0x4000;

        Assert.Equal(0, KernelMemoryCompatExports.KernelMapDirectMemory(context));
        Assert.True(memory.IsAccessible(mappingAddress, 0x4000));
    }

    [Fact]
    public void MapDirectMemory_FixedNoOverwritePreservesExistingDirectView()
    {
        if (!NativeGuestExecutionIsSupported)
        {
            return;
        }

        const ulong scratchAddress = 0x0000_6F48_0000_0000;
        const ulong mappingAddress = 0x0000_6F48_0010_0000;
        const ulong mappingLength = 0x40000;
        using var memory = new PhysicalVirtualMemory();
        Assert.True(memory.TryAllocateAtExact(scratchAddress, 0x4000, executable: false, out var scratch));
        Assert.True(memory.TryWriteUInt64(scratch, mappingAddress));
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = scratch;
        context[CpuRegister.Rsi] = mappingLength;
        context[CpuRegister.Rdx] = 0x02;
        context[CpuRegister.Rcx] = 0x0040_0010;
        context[CpuRegister.R8] = 0;
        context[CpuRegister.R9] = 0x4000;
        Assert.Equal(0, KernelMemoryCompatExports.KernelMapDirectMemory(context));
        Assert.True(memory.TryWrite(mappingAddress + 0x120, stackalloc byte[] { 0x53 }));

        Assert.True(memory.TryWriteUInt64(scratch, mappingAddress));
        context[CpuRegister.Rcx] = 0x0040_0090;
        context[CpuRegister.R8] = 0x100000;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_OUT_OF_MEMORY,
            KernelMemoryCompatExports.KernelMapDirectMemory(context));

        Span<byte> value = stackalloc byte[1];
        Assert.True(memory.TryRead(mappingAddress + 0x120, value));
        Assert.Equal(0x53, value[0]);
    }

    [Fact]
    public void MapDirectMemory_FixedNoOverwriteRejectsReservedOverlap()
    {
        if (!NativeGuestExecutionIsSupported)
        {
            return;
        }

        const ulong scratchAddress = 0x0000_6F4C_0000_0000;
        const ulong reservedAddress = 0x0000_6F4C_0010_0000;
        const ulong reservedLength = 0x8000;
        using var memory = new PhysicalVirtualMemory();
        Assert.True(memory.TryAllocateAtExact(scratchAddress, 0x4000, executable: false, out var scratch));
        Assert.Equal(
            reservedAddress,
            memory.ReserveAt(
                reservedAddress,
                reservedLength,
                executable: false,
                allowAlternative: false));
        KernelMemoryCompatExports.RegisterReservedVirtualRange(reservedAddress, reservedLength);
        Assert.True(memory.TryWriteUInt64(scratch, reservedAddress + 0x4000));
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = scratch;
        context[CpuRegister.Rsi] = 0x8000;
        context[CpuRegister.Rdx] = 0x02;
        context[CpuRegister.Rcx] = 0x0040_0090;
        context[CpuRegister.R8] = 0x180000;
        context[CpuRegister.R9] = 0x4000;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_OUT_OF_MEMORY,
            KernelMemoryCompatExports.KernelMapDirectMemory(context));
        Assert.True(memory.TryUnmapReservedMemory(reservedAddress, reservedLength));
    }

    [Theory]
    [InlineData(0x5000UL, 0UL)]
    [InlineData(0x4000UL, 0x1000UL)]
    public void MapDirectMemory_RejectsNonGuestPageAlignedRange(
        ulong mappingLength,
        ulong physicalOffset)
    {
        if (!NativeGuestExecutionIsSupported)
        {
            return;
        }

        const ulong scratchAddress = 0x0000_6F4E_0000_0000;
        const ulong mappingAddress = 0x0000_6F4E_0010_0000;
        using var memory = new PhysicalVirtualMemory();
        Assert.True(memory.TryAllocateAtExact(scratchAddress, 0x4000, executable: false, out var scratch));
        Assert.True(memory.TryWriteUInt64(scratch, mappingAddress));
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = scratch;
        context[CpuRegister.Rsi] = mappingLength;
        context[CpuRegister.Rdx] = 0x02;
        context[CpuRegister.Rcx] = 0x0040_0010;
        context[CpuRegister.R8] = physicalOffset;
        context[CpuRegister.R9] = 0x4000;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            KernelMemoryCompatExports.KernelMapDirectMemory(context));
        Assert.False(memory.IsAccessible(mappingAddress, 1));
    }

    [Fact]
    public void MapDirectMemory_FixedMappingReplacesPartialReservation()
    {
        if (!NativeGuestExecutionIsSupported)
        {
            return;
        }

        const ulong scratchAddress = 0x0000_6F50_0000_0000;
        const ulong reservedAddress = 0x0000_6F50_0010_0000;
        const ulong reservedLength = 0xE0000;
        const ulong mappingLength = 0x180000;
        using var memory = new PhysicalVirtualMemory();
        Assert.True(memory.TryAllocateAtExact(scratchAddress, 0x4000, executable: false, out var scratch));
        Assert.Equal(
            reservedAddress,
            memory.ReserveAt(reservedAddress, reservedLength, executable: false, allowAlternative: false));
        KernelMemoryCompatExports.RegisterReservedVirtualRange(reservedAddress, reservedLength);
        Assert.True(memory.TryWriteUInt64(scratch, reservedAddress));
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = scratch;
        context[CpuRegister.Rsi] = mappingLength;
        context[CpuRegister.Rdx] = 0x02;
        context[CpuRegister.Rcx] = 0x0040_0010;
        context[CpuRegister.R8] = 0x200000;
        context[CpuRegister.R9] = 0x10000;

        Assert.Equal(0, KernelMemoryCompatExports.KernelMapDirectMemory(context));

        AssertVirtualQueryState(
            context,
            memory,
            scratch,
            reservedAddress,
            reservedAddress,
            reservedAddress + mappingLength,
            0x12);
        Assert.True(memory.IsAccessible(reservedAddress, mappingLength));
    }

    private static void AssertVirtualQueryState(
        CpuContext context,
        PhysicalVirtualMemory memory,
        ulong infoAddress,
        ulong queryAddress,
        ulong expectedStart,
        ulong expectedEnd,
        byte expectedState)
    {
        context[CpuRegister.Rdi] = queryAddress;
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = infoAddress;
        context[CpuRegister.Rcx] = 72;
        Assert.Equal(0, KernelMemoryCompatExports.KernelVirtualQuery(context));
        Span<byte> info = stackalloc byte[72];
        Assert.True(memory.TryRead(infoAddress, info));
        Assert.Equal(expectedStart, BinaryPrimitives.ReadUInt64LittleEndian(info[0..8]));
        Assert.Equal(expectedEnd, BinaryPrimitives.ReadUInt64LittleEndian(info[8..16]));
        Assert.Equal(expectedState, info[32]);
    }

    private static bool NativeGuestExecutionIsSupported =>
        RuntimeInformation.ProcessArchitecture == Architecture.X64 &&
        (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());
}
