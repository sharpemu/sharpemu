// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Pthread;

public sealed class PthreadRwlockSemanticsTests
{
    [Fact]
    public void SceTrywrlock_RegistersAndAcquiresFreeLock()
    {
        const ulong memoryBase = 0x6_0000_0000;
        const ulong rwlockAddress = memoryBase + 0x100;
        var memory = new FakeCpuMemory(memoryBase, 0x4000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = rwlockAddress;

        Assert.Equal(0, KernelPthreadExtendedCompatExports.PthreadRwlockInit(context));

        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));
        Assert.True(manager.TryGetExport("bIHoZCTomsI", out var export));
        Assert.Equal("scePthreadRwlockTrywrlock", export.Name);
        Assert.Equal("libKernel", export.LibraryName);

        Assert.True(manager.TryDispatch("bIHoZCTomsI", context, out var result));
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
        Assert.Equal(0, KernelPthreadExtendedCompatExports.PthreadRwlockUnlock(context));
        Assert.Equal(0, KernelPthreadExtendedCompatExports.PthreadRwlockDestroy(context));
    }

    [Fact]
    public async Task SceTrywrlock_ReturnsBusyWhileAnotherThreadReads()
    {
        const ulong memoryBase = 0x6_0001_0000;
        const ulong rwlockAddress = memoryBase + 0x100;
        var memory = new FakeCpuMemory(memoryBase, 0x4000);
        var initializationContext = new CpuContext(memory, Generation.Gen5);
        initializationContext[CpuRegister.Rdi] = rwlockAddress;
        Assert.Equal(0, KernelPthreadExtendedCompatExports.PthreadRwlockInit(initializationContext));

        using var readerReady = new ManualResetEventSlim(false);
        using var releaseReader = new ManualResetEventSlim(false);
        var readerTask = Task.Factory.StartNew(
            () =>
            {
                var readerContext = new CpuContext(memory, Generation.Gen5);
                readerContext[CpuRegister.Rdi] = rwlockAddress;
                var lockResult = KernelPthreadExtendedCompatExports.PthreadRwlockRdlock(readerContext);
                readerReady.Set();
                releaseReader.Wait(TimeSpan.FromSeconds(5));
                var unlockResult = lockResult == 0
                    ? KernelPthreadExtendedCompatExports.PthreadRwlockUnlock(readerContext)
                    : int.MinValue;
                return (lockResult, unlockResult);
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        try
        {
            Assert.True(readerReady.Wait(TimeSpan.FromSeconds(5)));
            var writerContext = new CpuContext(memory, Generation.Gen5);
            writerContext[CpuRegister.Rdi] = rwlockAddress;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY,
                KernelPthreadExtendedCompatExports.PthreadRwlockTrywrlock(writerContext));
        }
        finally
        {
            releaseReader.Set();
        }

        Assert.Equal((0, 0), await readerTask.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, KernelPthreadExtendedCompatExports.PthreadRwlockDestroy(initializationContext));
    }
}
