// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

public sealed class KernelSchedulingCompatExportsTests
{
    private const ulong MemoryBase = 0x1_0000_0000;

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(-1)]
    public void PriorityBoundsMatchGuestKernelRange(int policy)
    {
        var ctx = new CpuContext(new FakeCpuMemory(MemoryBase, 0x1000), Generation.Gen5);
        ctx[CpuRegister.Rdi] = unchecked((ulong)policy);

        Assert.Equal(256, KernelSchedulingCompatExports.SchedGetPriorityMax(ctx));
        Assert.Equal(256UL, ctx[CpuRegister.Rax]);
        Assert.Equal(767, KernelSchedulingCompatExports.SchedGetPriorityMin(ctx));
        Assert.Equal(767UL, ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void KernelSync_IsDeterministicNoOp()
    {
        var ctx = new CpuContext(new FakeCpuMemory(MemoryBase, 0x1000), Generation.Gen5);
        ctx[CpuRegister.Rax] = ulong.MaxValue;

        Assert.Equal(0, KernelSchedulingCompatExports.KernelSync(ctx));
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);
    }

    [Theory]
    [InlineData("CBNtXOoef-E", "sched_get_priority_max")]
    [InlineData("m0iS6jNsXds", "sched_get_priority_min")]
    [InlineData("uvT2iYBBnkY", "sceKernelSync")]
    public void ExportsRegisterWithCatalogIdentity(string nid, string name)
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport(nid, out var export));
        Assert.Equal(name, export.Name);
        Assert.Equal("libKernel", export.LibraryName);
    }
}
