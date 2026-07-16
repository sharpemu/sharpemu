// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SharpEmu.Core.Cpu.Native;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class GuestNativeReturnTests
{
    private const ulong GuestPointer = 0x0000_0071_CAFE_BA00;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static ulong ReturnGuestPointer() => GuestPointer;

    [Fact]
    public unsafe void NativeEntryAndCompletedContextPreserveFullPointerReturn()
    {
        var nativeEntry = typeof(DirectExecutionBackend).GetMethod(
            "CallNativeEntry",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(nativeEntry);
        Assert.Equal(typeof(ulong), nativeEntry.ReturnType);
        var target = (delegate* unmanaged[Cdecl]<ulong>)&ReturnGuestPointer;
        var boxedTarget = Pointer.Box((void*)target, typeof(void*));
        Assert.Equal(GuestPointer, Assert.IsType<ulong>(nativeEntry.Invoke(null, [boxedTarget])));

        var storeReturn = typeof(DirectExecutionBackend).GetMethod(
            "StoreCompletedGuestNativeReturn",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(storeReturn);

        var context = new CpuContext(new FakeCpuMemory(0x1_0000, 0x1000), Generation.Gen5);
        storeReturn.Invoke(null, [context, GuestPointer]);

        Assert.Equal(GuestPointer, context[CpuRegister.Rax]);
    }
}
