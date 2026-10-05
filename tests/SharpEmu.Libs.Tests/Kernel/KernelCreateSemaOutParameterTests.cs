// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

/// <summary>
/// <c>SceKernelSema</c> is a 64-bit opaque pointer, so <c>sceKernelCreateSema</c> has to write the
/// guest's whole variable. Writing only the low half leaves the upper four bytes holding whatever
/// was there before the call — the sibling creators (<c>sceKernelCreateEventFlag</c>,
/// <c>sceKernelCreateEqueue</c>) both write 64 bits.
/// </summary>
public sealed class KernelCreateSemaOutParameterTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong SemaphoreAddress = MemoryBase + 0x100;
    private const ulong NameAddress = MemoryBase + 0x200;

    /// <summary>
    /// The guest's variable is uninitialised in practice. Pre-fill it so a partial write is
    /// visible as surviving garbage rather than hiding behind zeroed memory.
    /// </summary>
    [Fact]
    public void WritesTheWholeSixtyFourBitHandleVariable()
    {
        var context = CreateContext();
        Assert.True(context.TryWriteUInt64(SemaphoreAddress, 0xDEAD_BEEF_DEAD_BEEFUL));

        Assert.Equal(0, CreateSema(context, "sema-full-width"));

        Assert.True(context.TryReadUInt64(SemaphoreAddress, out var written));
        Assert.Equal(0UL, written >> 32);
        Assert.NotEqual(0UL, written & 0xFFFF_FFFFUL);
    }

    /// <summary>
    /// The handle written out has to be the one the semaphore calls answer to, or the guest holds
    /// a token nothing accepts.
    /// </summary>
    [Fact]
    public void WritesAHandleTheSemaphoreCallsAccept()
    {
        var context = CreateContext();
        Assert.Equal(0, CreateSema(context, "sema-round-trip", initialCount: 1));
        Assert.True(context.TryReadUInt64(SemaphoreAddress, out var handle));

        context[CpuRegister.Rdi] = handle;
        context[CpuRegister.Rsi] = 1;
        context[CpuRegister.Rdx] = 0;
        Assert.Equal(0, KernelSemaphoreCompatExports.KernelWaitSema(context));

        context[CpuRegister.Rdi] = handle;
        Assert.Equal(0, KernelSemaphoreCompatExports.KernelDeleteSema(context));
    }

    /// <summary>Two semaphores must not come back with the same handle.</summary>
    [Fact]
    public void HandsOutDistinctHandles()
    {
        var context = CreateContext();

        Assert.Equal(0, CreateSema(context, "sema-a"));
        Assert.True(context.TryReadUInt64(SemaphoreAddress, out var first));

        Assert.Equal(0, CreateSema(context, "sema-b"));
        Assert.True(context.TryReadUInt64(SemaphoreAddress, out var second));

        Assert.NotEqual(first, second);

        foreach (var handle in new[] { first, second })
        {
            context[CpuRegister.Rdi] = handle;
            Assert.Equal(0, KernelSemaphoreCompatExports.KernelDeleteSema(context));
        }
    }

    private static int CreateSema(CpuContext context, string name, int initialCount = 0)
    {
        WriteName(context, name);
        context[CpuRegister.Rdi] = SemaphoreAddress;
        context[CpuRegister.Rsi] = NameAddress;
        context[CpuRegister.Rdx] = 0;
        context[CpuRegister.Rcx] = unchecked((ulong)initialCount);
        context[CpuRegister.R8] = 4;
        context[CpuRegister.R9] = 0;
        return KernelSemaphoreCompatExports.KernelCreateSema(context);
    }

    private static void WriteName(CpuContext context, string name)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(name + "\0");
        Assert.True(context.Memory.TryWrite(NameAddress, bytes));
    }

    private static CpuContext CreateContext() =>
        new(new FakeCpuMemory(MemoryBase, 0x1000), Generation.Gen5);
}
