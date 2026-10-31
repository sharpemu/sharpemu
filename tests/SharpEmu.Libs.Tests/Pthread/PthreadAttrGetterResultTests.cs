// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Pthread;

/// <summary>
/// The pthread_attr_get* and scePthreadGetprio getters hand their value back through the
/// out pointer and return 0. An export that writes RAX keeps that RAX over its return
/// value, so a getter that also put the value in RAX returned it as an error code: Black
/// Myth: Wukong's Mono took pthread_attr_getstacksize's stack size for a failure and
/// stopped at boot with "pthread_attr_getstacksize failed".
/// </summary>
public sealed class PthreadAttrGetterResultTests
{
    private const ulong MemoryBase = 0x6_0030_0000;
    private const ulong AttrAddress = MemoryBase + 0x100;
    private const ulong OutAddress = MemoryBase + 0x200;
    private const ulong OutSizeAddress = MemoryBase + 0x208;

    public static TheoryData<string> Getters => new()
    {
        "getstacksize",
        "getstack",
        "getguardsize",
        "getdetachstate",
        "getaffinity",
    };

    private static CpuContext NewContext() =>
        new(new FakeCpuMemory(MemoryBase, 0x1000), Generation.Gen5);

    [Theory]
    [MemberData(nameof(Getters))]
    public void AnAttrGetterHandsTheGuestZero(string getter)
    {
        var context = NewContext();
        context[CpuRegister.Rdi] = AttrAddress;
        Assert.Equal(0, KernelPthreadExtendedCompatExports.PthreadAttrInit(context));

        context[CpuRegister.Rdi] = AttrAddress;
        context[CpuRegister.Rsi] = OutAddress;
        context[CpuRegister.Rdx] = OutSizeAddress;
        context.ClearRaxWriteFlag();
        var result = getter switch
        {
            "getstacksize" => KernelPthreadExtendedCompatExports.PthreadAttrGetstacksizePOSIX(context),
            "getstack" => KernelPthreadExtendedCompatExports.PthreadAttrGetstackPOSIX(context),
            "getguardsize" => KernelPthreadExtendedCompatExports.PthreadAttrGetguardsize(context),
            "getdetachstate" => KernelPthreadExtendedCompatExports.PthreadAttrGetdetachstatePOSIX(context),
            _ => KernelPthreadExtendedCompatExports.PthreadAttrGetaffinity(context),
        };

        // What the guest sees in RAX: a written RAX wins over the return value.
        Assert.Equal(0, result);
        Assert.Equal(0UL, context.WasRaxWritten ? context[CpuRegister.Rax] : unchecked((ulong)result));
    }
}
