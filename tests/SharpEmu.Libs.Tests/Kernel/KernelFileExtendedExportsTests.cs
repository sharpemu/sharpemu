// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

[Collection(KernelMemoryCompatStateCollection.Name)]
public sealed class KernelFileExtendedExportsTests
{
    private const ulong GuestMemoryBase = 0x1_0000_0000;
    private const ulong PathAddress = GuestMemoryBase + 0x100;
    private const ulong BufferAddress = GuestMemoryBase + 0x200;
    private const ulong DestinationPathAddress = GuestMemoryBase + 0x300;
    private const ulong GuestFsBase = GuestMemoryBase + 0x800;
    private const ulong TlsErrnoOffset = 0x40;
    private const int Enoent = 2;
    private const int Ebadf = 9;

    [Theory]
    [InlineData(nameof(KernelMemoryCompatExports.PosixPread))]
    [InlineData(nameof(KernelMemoryCompatExports.PosixPwrite))]
    [InlineData(nameof(KernelMemoryCompatExports.PosixFsync))]
    [InlineData(nameof(KernelMemoryCompatExports.PosixFtruncate))]
    [InlineData(nameof(KernelMemoryCompatExports.PosixDup))]
    [InlineData(nameof(KernelMemoryCompatExports.PosixDup2))]
    [InlineData(nameof(KernelMemoryCompatExports.PosixFcntl))]
    public void PosixFdExport_BadDescriptorReturnsMinusOneWithEbadf(string exportName)
    {
        var memory = new FakeCpuMemory(GuestMemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5)
        {
            FsBase = GuestFsBase,
        };
        context[CpuRegister.Rdi] = 0x80020002; // never-opened / sentinel fd

        int result;
        switch (exportName)
        {
            case nameof(KernelMemoryCompatExports.PosixPread):
                context[CpuRegister.Rsi] = BufferAddress;
                context[CpuRegister.Rdx] = 1;
                result = KernelMemoryCompatExports.PosixPread(context);
                break;
            case nameof(KernelMemoryCompatExports.PosixPwrite):
                context[CpuRegister.Rsi] = BufferAddress;
                context[CpuRegister.Rdx] = 1;
                result = KernelMemoryCompatExports.PosixPwrite(context);
                break;
            case nameof(KernelMemoryCompatExports.PosixFsync):
                result = KernelMemoryCompatExports.PosixFsync(context);
                break;
            case nameof(KernelMemoryCompatExports.PosixFtruncate):
                result = KernelMemoryCompatExports.PosixFtruncate(context);
                break;
            case nameof(KernelMemoryCompatExports.PosixDup):
                result = KernelMemoryCompatExports.PosixDup(context);
                break;
            case nameof(KernelMemoryCompatExports.PosixDup2):
                context[CpuRegister.Rsi] = 100;
                result = KernelMemoryCompatExports.PosixDup2(context);
                break;
            case nameof(KernelMemoryCompatExports.PosixFcntl):
                context[CpuRegister.Rsi] = 0; // F_DUPFD
                result = KernelMemoryCompatExports.PosixFcntl(context);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(exportName));
        }

        AssertPosixFailure(context, result, Ebadf);
    }

    [Theory]
    [InlineData(nameof(KernelMemoryCompatExports.PosixTruncate))]
    [InlineData(nameof(KernelMemoryCompatExports.PosixRename))]
    public void PosixPathExport_MissingPathReturnsMinusOneWithEnoent(string exportName)
    {
        var memory = new FakeCpuMemory(GuestMemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5)
        {
            FsBase = GuestFsBase,
        };
        memory.WriteCString(PathAddress, "/__sharpemu_test_missing__/source");
        context[CpuRegister.Rdi] = PathAddress;

        int result;
        switch (exportName)
        {
            case nameof(KernelMemoryCompatExports.PosixTruncate):
                context[CpuRegister.Rsi] = 0;
                result = KernelMemoryCompatExports.PosixTruncate(context);
                break;
            case nameof(KernelMemoryCompatExports.PosixRename):
                memory.WriteCString(DestinationPathAddress, "/__sharpemu_test_missing__/destination");
                context[CpuRegister.Rsi] = DestinationPathAddress;
                result = KernelMemoryCompatExports.PosixRename(context);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(exportName));
        }

        AssertPosixFailure(context, result, Enoent);
    }

    private static void AssertPosixFailure(CpuContext context, int result, int expectedErrno)
    {
        Assert.Equal(-1, result);
        Assert.Equal(ulong.MaxValue, context[CpuRegister.Rax]);
        Assert.True(context.TryReadInt32(GuestFsBase + TlsErrnoOffset, out var errno));
        Assert.Equal(expectedErrno, errno);
    }
}
