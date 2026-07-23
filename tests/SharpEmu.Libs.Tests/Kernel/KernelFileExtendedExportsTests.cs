// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

// Regression coverage for the sentinel leak described in #491: the POSIX-named
// exports in KernelFileExtendedExports.cs must translate a failed raw Orbis
// result into -1/errno, mirroring the open/fstat/close/read/write fix in #461.
// Shares KernelMemoryCompatExports' static fd table, so it runs in the same
// non-parallel collection as KernelMemoryCompatExportsTests.
[Collection(KernelMemoryCompatStateCollection.Name)]
public sealed class KernelFileExtendedExportsTests
{
    // The 0x8002xxxx OrbisGen2Result "not found" sentinel, misused as a fd -
    // the exact defect class these exports must no longer leak.
    private const ulong SentinelFd = 0x80020002;

    [Fact]
    public void PosixPread_BadDescriptorReturnsMinusOne()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong bufferAddress = memoryBase + 0x200;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = SentinelFd;
        context[CpuRegister.Rsi] = bufferAddress;
        context[CpuRegister.Rdx] = 0x40;
        context[CpuRegister.Rcx] = 0;

        var result = KernelMemoryCompatExports.PosixPread(context);

        Assert.Equal(-1, result);
        Assert.Equal(ulong.MaxValue, context[CpuRegister.Rax]);
    }

    [Fact]
    public void PosixPwrite_BadDescriptorReturnsMinusOne()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong bufferAddress = memoryBase + 0x200;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        memory.WriteCString(bufferAddress, "payload");
        context[CpuRegister.Rdi] = SentinelFd;
        context[CpuRegister.Rsi] = bufferAddress;
        context[CpuRegister.Rdx] = 0x7;
        context[CpuRegister.Rcx] = 0;

        var result = KernelMemoryCompatExports.PosixPwrite(context);

        Assert.Equal(-1, result);
        Assert.Equal(ulong.MaxValue, context[CpuRegister.Rax]);
    }

    [Fact]
    public void PosixFsync_BadDescriptorReturnsMinusOne()
    {
        const ulong memoryBase = 0x1_0000_0000;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = SentinelFd;

        var result = KernelMemoryCompatExports.PosixFsync(context);

        Assert.Equal(-1, result);
        Assert.Equal(ulong.MaxValue, context[CpuRegister.Rax]);
    }

    [Fact]
    public void PosixFdatasync_BadDescriptorReturnsMinusOne()
    {
        const ulong memoryBase = 0x1_0000_0000;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = SentinelFd;

        var result = KernelMemoryCompatExports.PosixFdatasync(context);

        Assert.Equal(-1, result);
        Assert.Equal(ulong.MaxValue, context[CpuRegister.Rax]);
    }

    [Fact]
    public void PosixFtruncate_BadDescriptorReturnsMinusOne()
    {
        const ulong memoryBase = 0x1_0000_0000;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = SentinelFd;
        context[CpuRegister.Rsi] = 0;

        var result = KernelMemoryCompatExports.PosixFtruncate(context);

        Assert.Equal(-1, result);
        Assert.Equal(ulong.MaxValue, context[CpuRegister.Rax]);
    }

    [Fact]
    public void PosixTruncate_MissingFileReturnsMinusOne()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong pathAddress = memoryBase + 0x100;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        memory.WriteCString(pathAddress, "/__sharpemu_test_missing__/truncate_target");
        context[CpuRegister.Rdi] = pathAddress;
        context[CpuRegister.Rsi] = 0;

        var result = KernelMemoryCompatExports.PosixTruncate(context);

        Assert.Equal(-1, result);
        Assert.Equal(ulong.MaxValue, context[CpuRegister.Rax]);
    }

    [Fact]
    public void PosixRename_MissingFileReturnsMinusOne()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong fromAddress = memoryBase + 0x100;
        const ulong toAddress = memoryBase + 0x200;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        memory.WriteCString(fromAddress, "/__sharpemu_test_missing__/rename_from");
        memory.WriteCString(toAddress, "/__sharpemu_test_missing__/rename_to");
        context[CpuRegister.Rdi] = fromAddress;
        context[CpuRegister.Rsi] = toAddress;

        var result = KernelMemoryCompatExports.PosixRename(context);

        Assert.Equal(-1, result);
        Assert.Equal(ulong.MaxValue, context[CpuRegister.Rax]);
    }

    [Fact]
    public void PosixDup_BadDescriptorReturnsMinusOne()
    {
        const ulong memoryBase = 0x1_0000_0000;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = SentinelFd;

        var result = KernelMemoryCompatExports.PosixDup(context);

        Assert.Equal(-1, result);
        Assert.Equal(ulong.MaxValue, context[CpuRegister.Rax]);
    }

    [Fact]
    public void PosixDup2_BadDescriptorReturnsMinusOne()
    {
        const ulong memoryBase = 0x1_0000_0000;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = SentinelFd;
        context[CpuRegister.Rsi] = 99;

        var result = KernelMemoryCompatExports.PosixDup2(context);

        Assert.Equal(-1, result);
        Assert.Equal(ulong.MaxValue, context[CpuRegister.Rax]);
    }

    [Fact]
    public void PosixFcntl_DupfdBadDescriptorReturnsMinusOne()
    {
        const ulong memoryBase = 0x1_0000_0000;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = SentinelFd;
        context[CpuRegister.Rsi] = 0; // F_DUPFD
        context[CpuRegister.Rdx] = 0;

        var result = KernelMemoryCompatExports.PosixFcntl(context);

        Assert.Equal(-1, result);
        Assert.Equal(ulong.MaxValue, context[CpuRegister.Rax]);
    }
}
