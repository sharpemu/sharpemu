// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Pthread;

public sealed class PthreadSemaphoreSemanticsTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong SemaphoreAddress = MemoryBase + 0x100;
    private const ulong ValueAddress = MemoryBase + 0x200;

    [Theory]
    [InlineData("C36iRE0F5sE", "scePthreadSemWait")]
    [InlineData("aishVAiFaYM", "scePthreadSemPost")]
    [InlineData("H2a+IN9TP0E", "scePthreadSemTrywait")]
    [InlineData("GEnUkDZoUwY", "scePthreadSemInit")]
    [InlineData("Vwc+L05e6oE", "scePthreadSemDestroy")]
    public void RegistryResolvesPthreadSemaphoreExports(string nid, string exportName)
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(
            Generation.Gen4 | Generation.Gen5));

        Assert.True(manager.TryGetExport(nid, out var export));
        Assert.Equal(exportName, export.Name);
        Assert.Equal("libKernel", export.LibraryName);
    }

    [Fact]
    public void PthreadSemPostAndWaitShareSemaphoreState()
    {
        var context = CreateContext();
        InitializeSemaphore(context, initialCount: 0);

        context[CpuRegister.Rdi] = SemaphoreAddress;
        Assert.Equal(0, KernelSemaphoreCompatExports.PthreadSemPost(context));
        Assert.Equal(0UL, context[CpuRegister.Rax]);
        Assert.Equal(1, ReadSemaphoreValue(context));

        context[CpuRegister.Rdi] = SemaphoreAddress;
        Assert.Equal(0, KernelSemaphoreCompatExports.PthreadSemWait(context));
        Assert.Equal(0UL, context[CpuRegister.Rax]);
        Assert.Equal(0, ReadSemaphoreValue(context));

        DestroySemaphore(context);
    }

    [Fact]
    public void PthreadSemTryWaitConsumesAvailableTokenAndReturnsTryAgainWhenEmpty()
    {
        var context = CreateContext();
        InitializeSemaphore(context, initialCount: 1);

        context[CpuRegister.Rdi] = SemaphoreAddress;
        Assert.Equal(0, KernelSemaphoreCompatExports.PthreadSemTryWait(context));
        Assert.Equal(0, ReadSemaphoreValue(context));

        context[CpuRegister.Rdi] = SemaphoreAddress;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_TRY_AGAIN,
            KernelSemaphoreCompatExports.PthreadSemTryWait(context));
        Assert.Equal(unchecked((ulong)(int)OrbisGen2Result.ORBIS_GEN2_ERROR_TRY_AGAIN),
            context[CpuRegister.Rax]);

        DestroySemaphore(context);
    }

    [Fact]
    public void PthreadSemInitRejectsNonPrivateFlag()
    {
        var context = CreateContext();
        context[CpuRegister.Rdi] = SemaphoreAddress;
        context[CpuRegister.Rsi] = 1;
        context[CpuRegister.Rdx] = 0;
        context[CpuRegister.Rcx] = 0;

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            KernelSemaphoreCompatExports.PthreadSemInit(context));
        Assert.Equal(unchecked((ulong)(int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT),
            context[CpuRegister.Rax]);
    }

    [Fact]
    public void PthreadSemDestroyClearsSemaphoreAndRejectsSecondDestroy()
    {
        var context = CreateContext();
        InitializeSemaphore(context, initialCount: 0);

        context[CpuRegister.Rdi] = SemaphoreAddress;
        Assert.Equal(0, KernelSemaphoreCompatExports.PthreadSemDestroy(context));
        Assert.True(context.TryReadUInt32(SemaphoreAddress, out var handle));
        Assert.Equal(0U, handle);

        context[CpuRegister.Rdi] = SemaphoreAddress;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            KernelSemaphoreCompatExports.PthreadSemDestroy(context));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PthreadSemOperationRejectsInvalidSemaphore(bool post)
    {
        var context = CreateContext();
        context[CpuRegister.Rdi] = SemaphoreAddress;

        var result = post
            ? KernelSemaphoreCompatExports.PthreadSemPost(context)
            : KernelSemaphoreCompatExports.PthreadSemWait(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, result);
        Assert.Equal(unchecked((ulong)(int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT),
            context[CpuRegister.Rax]);
    }

    private static CpuContext CreateContext() => new(new FakeCpuMemory(MemoryBase, 0x1000), Generation.Gen5);

    private static void InitializeSemaphore(CpuContext context, uint initialCount)
    {
        context[CpuRegister.Rdi] = SemaphoreAddress;
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = initialCount;
        context[CpuRegister.Rcx] = 0;
        Assert.Equal(0, KernelSemaphoreCompatExports.PthreadSemInit(context));
    }

    private static int ReadSemaphoreValue(CpuContext context)
    {
        context[CpuRegister.Rdi] = SemaphoreAddress;
        context[CpuRegister.Rsi] = ValueAddress;
        Assert.Equal(0, KernelSemaphoreCompatExports.PosixSemGetValue(context));
        Assert.True(context.TryReadUInt32(ValueAddress, out var value));
        return unchecked((int)value);
    }

    private static void DestroySemaphore(CpuContext context)
    {
        context[CpuRegister.Rdi] = SemaphoreAddress;
        Assert.Equal(0, KernelSemaphoreCompatExports.PthreadSemDestroy(context));
    }
}
