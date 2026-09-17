// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

// Regression coverage for the sentinel leak described in #491: these
// libc/POSIX-named exports must translate a failed raw OrbisGen2Result into
// -1/errno (see KernelMemoryCompatExports.PosixFailure), mirroring the fix
// already applied to open/fstat/close/read/write/stat in #461. Leaking the
// raw 0x8002xxxx sentinel through RAX makes a libc caller treat it as a
// "successful" non-negative return instead of a POSIX failure.
public sealed class KernelFileExtendedExportsTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const int MemorySize = 0x2000;
    private const uint NeverOpenedFd = 0x80020002; // the not-found sentinel misused as an fd

    [Fact]
    public void PosixPread_BadDescriptorReturnsMinusOne()
    {
        var (memory, context) = NewContext();
        context[CpuRegister.Rdi] = unchecked((ulong)NeverOpenedFd);
        context[CpuRegister.Rsi] = MemoryBase + 0x200;
        context[CpuRegister.Rdx] = 0x40;
        context[CpuRegister.Rcx] = 0;

        var result = KernelMemoryCompatExports.PosixPread(context);

        AssertPosixFailure(context, result);
    }

    [Fact]
    public void PosixPwrite_BadDescriptorReturnsMinusOne()
    {
        var (memory, context) = NewContext();
        memory.WriteCString(MemoryBase + 0x200, "payload");
        context[CpuRegister.Rdi] = unchecked((ulong)NeverOpenedFd);
        context[CpuRegister.Rsi] = MemoryBase + 0x200;
        context[CpuRegister.Rdx] = 0x7;
        context[CpuRegister.Rcx] = 0;

        var result = KernelMemoryCompatExports.PosixPwrite(context);

        AssertPosixFailure(context, result);
    }

    [Fact]
    public void PosixFtruncate_BadDescriptorReturnsMinusOne()
    {
        var (_, context) = NewContext();
        context[CpuRegister.Rdi] = unchecked((ulong)NeverOpenedFd);
        context[CpuRegister.Rsi] = 0;

        var result = KernelMemoryCompatExports.PosixFtruncate(context);

        AssertPosixFailure(context, result);
    }

    [Fact]
    public void PosixTruncate_MissingFileReturnsMinusOne()
    {
        var (memory, context) = NewContext();
        var pathAddress = MemoryBase + 0x100;
        memory.WriteCString(pathAddress, "/__sharpemu_test_missing__/save.dat");
        context[CpuRegister.Rdi] = pathAddress;
        context[CpuRegister.Rsi] = 0;

        var result = KernelMemoryCompatExports.PosixTruncate(context);

        AssertPosixFailure(context, result);
    }

    [Fact]
    public void PosixFsync_BadDescriptorReturnsMinusOne()
    {
        var (_, context) = NewContext();
        context[CpuRegister.Rdi] = unchecked((ulong)NeverOpenedFd);

        var result = KernelMemoryCompatExports.PosixFsync(context);

        AssertPosixFailure(context, result);
    }

    [Fact]
    public void PosixFdatasync_BadDescriptorReturnsMinusOne()
    {
        var (_, context) = NewContext();
        context[CpuRegister.Rdi] = unchecked((ulong)NeverOpenedFd);

        var result = KernelMemoryCompatExports.PosixFdatasync(context);

        AssertPosixFailure(context, result);
    }

    [Fact]
    public void PosixRename_MissingSourceReturnsMinusOne()
    {
        var (memory, context) = NewContext();
        var fromAddress = MemoryBase + 0x100;
        var toAddress = MemoryBase + 0x300;
        memory.WriteCString(fromAddress, "/__sharpemu_test_missing__/from.dat");
        memory.WriteCString(toAddress, "/__sharpemu_test_missing__/to.dat");
        context[CpuRegister.Rdi] = fromAddress;
        context[CpuRegister.Rsi] = toAddress;

        var result = KernelMemoryCompatExports.PosixRename(context);

        AssertPosixFailure(context, result);
    }

    [Fact]
    public void PosixDup_BadDescriptorReturnsMinusOne()
    {
        var (_, context) = NewContext();
        context[CpuRegister.Rdi] = unchecked((ulong)NeverOpenedFd);

        var result = KernelMemoryCompatExports.PosixDup(context);

        AssertPosixFailure(context, result);
    }

    [Fact]
    public void PosixDup2_BadOldDescriptorReturnsMinusOne()
    {
        var (_, context) = NewContext();
        context[CpuRegister.Rdi] = unchecked((ulong)NeverOpenedFd);
        context[CpuRegister.Rsi] = 5;

        var result = KernelMemoryCompatExports.PosixDup2(context);

        AssertPosixFailure(context, result);
    }

    [Fact]
    public void PosixFcntl_DupfdOnBadDescriptorReturnsMinusOne()
    {
        var (_, context) = NewContext();
        const int fDupFd = 0;
        context[CpuRegister.Rdi] = unchecked((ulong)NeverOpenedFd);
        context[CpuRegister.Rsi] = fDupFd;
        context[CpuRegister.Rdx] = 0;

        var result = KernelMemoryCompatExports.PosixFcntl(context);

        AssertPosixFailure(context, result);
    }

    private static (FakeCpuMemory Memory, CpuContext Context) NewContext()
    {
        var memory = new FakeCpuMemory(MemoryBase, MemorySize);
        return (memory, new CpuContext(memory, Generation.Gen5));
    }

    // A libc failure must be exactly -1 with RAX == (ulong)-1, not the raw
    // 0x8002xxxx OrbisGen2Result sentinel a guest would store as a "valid"
    // fd/count and later misuse.
    private static void AssertPosixFailure(CpuContext context, int result)
    {
        Assert.Equal(-1, result);
        Assert.Equal(ulong.MaxValue, context[CpuRegister.Rax]);
    }
}
