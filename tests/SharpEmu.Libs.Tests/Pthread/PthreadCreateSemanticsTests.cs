// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Pthread;

public sealed class PthreadCreateSemanticsTests
{
    private const ulong MemoryBase = 0x6_3000_0000;
    private const ulong ThreadOutAddress = MemoryBase + 0x100;

    [Fact]
    public void CreatePublishesNonzeroThreadIdInGuestVisibleObject()
    {
        var context = new CpuContext(new FakeCpuMemory(MemoryBase, 0x1000), Generation.Gen5);
        context[CpuRegister.Rdi] = ThreadOutAddress;

        Assert.Equal(0, KernelExports.PthreadCreate(context));
        Assert.True(context.TryReadUInt64(ThreadOutAddress, out var threadHandle));
        Assert.NotEqual(0UL, threadHandle);
        Assert.True(KernelPthreadState.TryGetThreadIdentity(threadHandle, out var identity));

        var guestVisibleThreadId = Marshal.ReadInt32(unchecked((nint)threadHandle));
        Assert.NotEqual(0, guestVisibleThreadId);
        Assert.Equal(unchecked((int)identity.UniqueId), guestVisibleThreadId);
    }
}
