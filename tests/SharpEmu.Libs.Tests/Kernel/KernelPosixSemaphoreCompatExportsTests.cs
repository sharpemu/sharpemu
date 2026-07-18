// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

[CollectionDefinition("KernelPosixSemaphoreCompatExports", DisableParallelization = true)]
public sealed class KernelPosixSemaphoreCompatExportsCollectionDefinition;

[Collection("KernelPosixSemaphoreCompatExports")]
public sealed class KernelPosixSemaphoreCompatExportsTests : IDisposable
{
    private const int ErrnoInvalidArgument = 22;
    private const int ErrnoTryAgain = 35;
    private const int ErrnoTimedOut = 60;
    private const ulong MemoryBase = 0x0000_7FFF_7000_0000;
    private const ulong TlsBase = MemoryBase + 0x1000;
    private const ulong SemaphoreAddress = MemoryBase + 0x2000;
    private const ulong ValueAddress = MemoryBase + 0x3000;
    private const ulong TimeoutAddress = MemoryBase + 0x4000;
    private readonly FakeCpuMemory _memory = new(MemoryBase, 0x10_000);
    private readonly CpuContext _context;

    public KernelPosixSemaphoreCompatExportsTests()
    {
        KernelPosixSemaphoreCompatExports.ResetForTests();
        _context = new CpuContext(_memory, Generation.Gen5)
        {
            FsBase = TlsBase,
        };
    }

    [Fact]
    public void CountingLifecycle_TracksWaitPostAndValue()
    {
        Initialize(2);
        Assert.Equal(2, GetValue());

        _context[CpuRegister.Rdi] = SemaphoreAddress;
        Assert.Equal(0, KernelPosixSemaphoreCompatExports.PosixSemaphoreWait(_context));
        Assert.Equal(0UL, _context[CpuRegister.Rax]);
        Assert.Equal(0, KernelPosixSemaphoreCompatExports.PosixSemaphoreTryWait(_context));
        Assert.Equal(0UL, _context[CpuRegister.Rax]);
        Assert.Equal(0, GetValue());

        Assert.Equal(0, KernelPosixSemaphoreCompatExports.PosixSemaphoreTryWait(_context));
        Assert.Equal(ulong.MaxValue, _context[CpuRegister.Rax]);
        Assert.Equal(ErrnoTryAgain, ReadErrno());

        Assert.Equal(0, KernelPosixSemaphoreCompatExports.PosixSemaphorePost(_context));
        Assert.Equal(0UL, _context[CpuRegister.Rax]);
        Assert.Equal(1, GetValue());

        _context[CpuRegister.Rdi] = SemaphoreAddress;
        Assert.Equal(0, KernelPosixSemaphoreCompatExports.PosixSemaphoreDestroy(_context));
        Assert.Equal(0UL, _context[CpuRegister.Rax]);
    }

    [Fact]
    public void Init_RejectsValuesAboveSemaphoreMaximum()
    {
        _context[CpuRegister.Rdi] = SemaphoreAddress;
        _context[CpuRegister.Rsi] = 0;
        _context[CpuRegister.Rdx] = (ulong)int.MaxValue + 1;

        Assert.Equal(0, KernelPosixSemaphoreCompatExports.PosixSemaphoreInit(_context));
        Assert.Equal(ulong.MaxValue, _context[CpuRegister.Rax]);
        Assert.Equal(ErrnoInvalidArgument, ReadErrno());
    }

    [Fact]
    public void TimedWait_WithPastDeadlineReturnsTimedOutWithoutChangingCount()
    {
        Initialize(0);
        Assert.True(_context.TryWriteUInt64(TimeoutAddress, 0));
        Assert.True(_context.TryWriteUInt64(TimeoutAddress + sizeof(long), 0));
        _context[CpuRegister.Rdi] = SemaphoreAddress;
        _context[CpuRegister.Rsi] = TimeoutAddress;

        Assert.Equal(0, KernelPosixSemaphoreCompatExports.PosixSemaphoreTimedWait(_context));
        Assert.Equal(ulong.MaxValue, _context[CpuRegister.Rax]);
        Assert.Equal(ErrnoTimedOut, ReadErrno());
        Assert.Equal(0, GetValue());
    }

    [Fact]
    public void TimedWait_WithPastDeadlineStillConsumesAnAvailableCount()
    {
        Initialize(1);
        Assert.True(_context.TryWriteUInt64(TimeoutAddress, 0));
        Assert.True(_context.TryWriteUInt64(TimeoutAddress + sizeof(long), 0));
        _context[CpuRegister.Rdi] = SemaphoreAddress;
        _context[CpuRegister.Rsi] = TimeoutAddress;

        Assert.Equal(0, KernelPosixSemaphoreCompatExports.PosixSemaphoreTimedWait(_context));
        Assert.Equal(0UL, _context[CpuRegister.Rax]);
        Assert.Equal(0, GetValue());
    }

    [Fact]
    public void PublicNids_RegisterAsPosixSemaphoreExports()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        AssertExport(manager, "pDuPEf3m4fI", "sem_init");
        AssertExport(manager, "cDW233RAwWo", "sem_destroy");
        AssertExport(manager, "YCV5dGGBcCo", "sem_wait");
        AssertExport(manager, "WBWzsRifCEA", "sem_trywait");
        AssertExport(manager, "w5IHyvahg-o", "sem_timedwait");
        AssertExport(manager, "IKP8typ0QUk", "sem_post");
        AssertExport(manager, "Bq+LRV-N6Hk", "sem_getvalue");
    }

    public void Dispose() => KernelPosixSemaphoreCompatExports.ResetForTests();

    private void Initialize(uint count)
    {
        _context[CpuRegister.Rdi] = SemaphoreAddress;
        _context[CpuRegister.Rsi] = 0;
        _context[CpuRegister.Rdx] = count;
        Assert.Equal(0, KernelPosixSemaphoreCompatExports.PosixSemaphoreInit(_context));
        Assert.Equal(0UL, _context[CpuRegister.Rax]);
    }

    private int GetValue()
    {
        _context[CpuRegister.Rdi] = SemaphoreAddress;
        _context[CpuRegister.Rsi] = ValueAddress;
        Assert.Equal(0, KernelPosixSemaphoreCompatExports.PosixSemaphoreGetValue(_context));
        Assert.Equal(0UL, _context[CpuRegister.Rax]);
        Assert.True(_context.TryReadInt32(ValueAddress, out var value));
        return value;
    }

    private int ReadErrno()
    {
        Assert.True(_context.TryReadInt32(TlsBase + 0x40, out var value));
        return value;
    }

    private static void AssertExport(ModuleManager manager, string nid, string name)
    {
        Assert.True(manager.TryGetExport(nid, out var export));
        Assert.Equal(name, export.Name);
        Assert.Equal("libKernel", export.LibraryName);
    }
}
