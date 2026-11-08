// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

[Collection(KernelMemoryCompatStateCollection.Name)]
public sealed class PosixMemoryAndSignalTests : IDisposable
{
    private readonly FlexibleBackingPool _previous =
        KernelMemoryCompatExports.SetFlexibleBackingForTests(new FlexibleBackingPool(0, 0x100000));

    public void Dispose() => KernelMemoryCompatExports.SetFlexibleBackingForTests(_previous);

    [Theory]
    [InlineData(Generation.Gen4)]
    [InlineData(Generation.Gen5)]
    public void AnonymousMmapUsesSharedFlexibleBackingAndRoundsToGuestPages(Generation generation)
    {
        using var test = new BackedKernelMemory();
        var context = new CpuContext(test.Memory, generation);
        SetMmapArguments(context, 1);
        Assert.Equal(0, KernelMemoryCompatExports.PosixMmap(context));
        var address = context[CpuRegister.Rax];
        Assert.NotEqual(0UL, address);
        Assert.Equal(0UL, address & 0x3FFF);
        Assert.True(test.Memory.IsBackedRange(address, 0x4000));
        var contents = new byte[0x4000];
        Assert.True(test.Memory.TryRead(address, contents));
        Assert.All(contents, value => Assert.Equal(0, value));
        Assert.True(test.Memory.TryWrite(address, new byte[] { 123 }));
        Assert.Equal(0, test.Unmap(address, 0x4000));
    }

    [Fact]
    public void FixedMmapReusesTheRequestedReservedRange()
    {
        using var test = new BackedKernelMemory();
        var address = test.Reserve(0x4000);
        SetMmapArguments(test.Context, 0x4000);
        test.Context[CpuRegister.Rdi] = address;
        test.Context[CpuRegister.Rcx] = 0x1012;
        Assert.Equal(0, KernelMemoryCompatExports.PosixMmap(test.Context));
        Assert.Equal(address, test.Context[CpuRegister.Rax]);
        Assert.True(test.Memory.IsBackedRange(address, 0x4000));
    }

    [Theory]
    [InlineData(0UL, 0x1002u, -1)]
    [InlineData(ulong.MaxValue, 0x1002u, -1)]
    [InlineData(0x4000UL, 0x1002u, 3)]
    [InlineData(0x4000UL, 0x2u, -1)]
    [InlineData(0x4000UL, 0x0D001002u, -1)]
    public void InvalidMmapReturnsMapFailedNotAKernelErrorPointer(ulong length, uint flags, int fd)
    {
        using var test = new BackedKernelMemory();
        SetMmapArguments(test.Context, length);
        test.Context[CpuRegister.Rcx] = flags;
        test.Context[CpuRegister.R8] = unchecked((ulong)(long)fd);
        Assert.Equal(-1, KernelMemoryCompatExports.PosixMmap(test.Context));
        Assert.Equal(ulong.MaxValue, test.Context[CpuRegister.Rax]);
    }

    [Fact]
    public void SignalMaskOperationsPreserveThePreviousMaskAndAllowAnAliasedOutput()
    {
        const ulong address = 0x1000;
        var memory = new FakeCpuMemory(address, 0x1000);
        var context = new CpuContext(memory, Generation.Gen4);
        context[CpuRegister.Rdi] = 3;
        context[CpuRegister.Rsi] = address;
        context[CpuRegister.Rdx] = 0;
        Assert.Equal(0, KernelRuntimeCompatExports.PosixSigprocmask(context));
        context[CpuRegister.Rdi] = address;
        Assert.Equal(0, KernelRuntimeCompatExports.PosixSigfillset(context));
        context[CpuRegister.Rdi] = 1;
        context[CpuRegister.Rsi] = address;
        context[CpuRegister.Rdx] = address;
        Assert.Equal(0, KernelRuntimeCompatExports.PosixSigprocmask(context));
        var previous = new byte[16];
        Assert.True(memory.TryRead(address, previous));
        Assert.All(previous, value => Assert.Equal(0, value));
        context[CpuRegister.Rsi] = 0;
        Assert.Equal(0, KernelRuntimeCompatExports.PosixSigprocmask(context));
        Assert.True(memory.TryRead(address, previous));
        Assert.All(previous, value => Assert.Equal(255, value));
        Assert.True(memory.TryWrite(address, new byte[16]));
        context[CpuRegister.Rdi] = 3;
        context[CpuRegister.Rsi] = address;
        context[CpuRegister.Rdx] = 0;
        Assert.Equal(0, KernelRuntimeCompatExports.PosixSigprocmask(context));
    }

    [Fact]
    public void PosixExportsAreAvailableToBothGenerations()
    {
        foreach (var generation in new[] { Generation.Gen4, Generation.Gen5 })
            foreach (var nid in new[] { "BPE9s9vQQXo", "VkTAsrZDcJ0", "aPcyptbOiZs" })
                Assert.Single(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(generation), export => export.Nid == nid);
    }

    private static void SetMmapArguments(CpuContext context, ulong length)
    {
        context[CpuRegister.Rdi] = 0;
        context[CpuRegister.Rsi] = length;
        context[CpuRegister.Rdx] = 3;
        context[CpuRegister.Rcx] = 0x1002;
        context[CpuRegister.R8] = ulong.MaxValue;
        context[CpuRegister.R9] = 0;
    }
}
