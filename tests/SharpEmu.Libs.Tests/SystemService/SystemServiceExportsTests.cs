// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.SystemService;
using Xunit;

namespace SharpEmu.Libs.Tests.SystemService;

public sealed class SystemServiceExportsTests
{
    private const ulong MemoryBase = 0x1_0000_0000;

    public SystemServiceExportsTests()
    {
        SystemServiceExports.ResetForTests();
    }

    [Fact]
    public void GetNoticeScreenSkipFlagWritesOneByteAtMemoryBoundary()
    {
        var memory = new FakeCpuMemory(MemoryBase, 1);
        var context = new CpuContext(memory, Generation.Gen5);
        Assert.True(memory.TryWrite(MemoryBase, new byte[] { 0xA5 }));
        context[CpuRegister.Rdi] = MemoryBase;

        Assert.Equal(0, SystemServiceExports.SystemServiceGetNoticeScreenSkipFlag(context));
        Assert.Equal(0UL, context[CpuRegister.Rax]);

        Span<byte> flag = stackalloc byte[1];
        Assert.True(memory.TryRead(MemoryBase, flag));
        Assert.Equal(0, flag[0]);
    }

    [Fact]
    public void SetNoticeScreenSkipFlagRoundTripsThroughGetter()
    {
        var memory = new FakeCpuMemory(MemoryBase, 2);
        var context = new CpuContext(memory, Generation.Gen5)
        {
            [CpuRegister.Rdi] = 1,
        };

        Assert.Equal(0, SystemServiceExports.SystemServiceSetNoticeScreenSkipFlag(context));

        context[CpuRegister.Rdi] = MemoryBase;
        Assert.Equal(0, SystemServiceExports.SystemServiceGetNoticeScreenSkipFlag(context));

        Span<byte> flag = stackalloc byte[1];
        Assert.True(memory.TryRead(MemoryBase, flag));
        Assert.Equal(1, flag[0]);
    }
}
