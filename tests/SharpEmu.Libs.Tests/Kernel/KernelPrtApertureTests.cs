// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

/// <summary>
/// sceKernelSetPrtAperture used to validate the aperture against a fixed window at 0x1000000000
/// spanning 0xEC00000000. Nothing in the emulator allocates from that window:
/// sceKernelReserveVirtualRange - which is exactly how a title obtains a PRT aperture - searches
/// free host VA from 0x6_0000_0000 upwards, below where the window began and in practice far past
/// where it ended. Guest and host share one address space because guest code executes natively,
/// so a reserved range is a legitimate aperture wherever it landed.
///
/// The consequence was not only a wrong error code. On success the import dispatcher registers the
/// range for lazy commit, so a rejected call also left the aperture unbacked for later accesses.
/// </summary>
public sealed class KernelPrtApertureTests
{
    private const ulong MemoryBase = 0x6_0000_0000;

    /// <summary>
    /// The exact arguments Code Violet (PPSA26528) passes, taken from its compatibility report:
    /// aperture 0, a 256 GiB range at the address sceKernelReserveVirtualRange had just returned.
    /// This returned 0x80020003 and the title stopped during module init.
    /// </summary>
    [Fact]
    public void AcceptsTheRangeAReservationActuallyReturned()
    {
        var context = CreateContext();
        context[CpuRegister.Rdi] = 0;                      // aperture id
        context[CpuRegister.Rsi] = 0x00000289AC740000UL;   // base, as reserved
        context[CpuRegister.Rdx] = 0x0000004000000000UL;   // 256 GiB

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelRuntimeCompatExports.KernelSetPrtAperture(context));
    }

    /// <summary>
    /// A range low in the address space is equally valid — the reservation allocator starts at
    /// 0x6_0000_0000, below the old window entirely.
    /// </summary>
    [Fact]
    public void AcceptsARangeBelowTheOldWindowStart()
    {
        var context = CreateContext();
        context[CpuRegister.Rdi] = 1;
        context[CpuRegister.Rsi] = 0x0000000600000000UL;
        context[CpuRegister.Rdx] = 0x0000000040000000UL;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelRuntimeCompatExports.KernelSetPrtAperture(context));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(99)]
    public void RejectsAnApertureIdOutsideTheTable(int apertureId)
    {
        var context = CreateContext();
        context[CpuRegister.Rdi] = unchecked((ulong)(long)apertureId);
        context[CpuRegister.Rsi] = 0x0000000600000000UL;
        context[CpuRegister.Rdx] = 0x0000000040000000UL;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            KernelRuntimeCompatExports.KernelSetPrtAperture(context));
    }

    [Fact]
    public void RejectsAMisalignedBase()
    {
        var context = CreateContext();
        context[CpuRegister.Rdi] = 0;
        context[CpuRegister.Rsi] = 0x0000000600000001UL; // not 4 KiB aligned
        context[CpuRegister.Rdx] = 0x0000000040000000UL;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            KernelRuntimeCompatExports.KernelSetPrtAperture(context));
    }

    [Fact]
    public void RejectsANullBase()
    {
        var context = CreateContext();
        context[CpuRegister.Rdi] = 0;
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = 0x0000000040000000UL;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            KernelRuntimeCompatExports.KernelSetPrtAperture(context));
    }

    /// <summary>
    /// The one range check that survives: base + size must not wrap. The old window checks
    /// happened to prevent this as a side effect, so removing them without this would have opened
    /// a hole rather than closed one.
    /// </summary>
    [Fact]
    public void RejectsARangeThatWrapsTheAddressSpace()
    {
        var context = CreateContext();
        context[CpuRegister.Rdi] = 0;
        context[CpuRegister.Rsi] = 0xFFFFFFFFFFFFF000UL;
        context[CpuRegister.Rdx] = 0x0000000000010000UL;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            KernelRuntimeCompatExports.KernelSetPrtAperture(context));
    }

    private static CpuContext CreateContext() =>
        new(new FakeCpuMemory(MemoryBase, 0x1000), Generation.Gen5);
}
