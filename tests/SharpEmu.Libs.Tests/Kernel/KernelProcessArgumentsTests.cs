// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

public sealed class KernelProcessArgumentsTests
{
    private const ulong TestBase = 0x2_0000_0000;

    [Fact]
    public void GetArgc_ReturnsConfiguredCount()
    {
        var memory = new FakeCpuMemory(TestBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);

        KernelExports.ConfigureProcessArguments(3, 0x5000);
        try
        {
            var result = KernelExports.GetArgc(context);

            Assert.Equal(3, result);
            Assert.Equal(3UL, context[CpuRegister.Rax]);
        }
        finally
        {
            KernelExports.ResetProcessArguments();
        }
    }

    [Fact]
    public void GetArgv_ReturnsConfiguredAddress()
    {
        var memory = new FakeCpuMemory(TestBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);

        KernelExports.ConfigureProcessArguments(2, 0x7000);
        try
        {
            var result = KernelExports.GetArgv(context);

            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
            Assert.Equal(0x7000UL, context[CpuRegister.Rax]);
        }
        finally
        {
            KernelExports.ResetProcessArguments();
        }
    }

    [Fact]
    public void GetArgcAndGetArgv_Fallback_InitializesValidGuestMemory()
    {
        var memory = new FakeCpuMemory(TestBase, 0x10000);
        var context = new CpuContext(memory, Generation.Gen5);

        KernelExports.ResetProcessArguments();

        var argc = KernelExports.GetArgc(context);
        Assert.Equal(1, argc);
        Assert.Equal(1UL, context[CpuRegister.Rax]);

        var result = KernelExports.GetArgv(context);
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);

        var argvAddress = context[CpuRegister.Rax];
        Assert.NotEqual(0UL, argvAddress);

        // argv[0] should be a pointer to "eboot.bin\0"
        Assert.True(context.TryReadUInt64(argvAddress, out var stringAddress));
        Assert.NotEqual(0UL, stringAddress);
        Assert.True(context.TryReadNullTerminatedUtf8(stringAddress, 64, out var imageName));
        Assert.Equal("eboot.bin", imageName);

        // argv[1] should be NULL terminator
        Assert.True(context.TryReadUInt64(argvAddress + 8, out var nullTerminator));
        Assert.Equal(0UL, nullTerminator);

        KernelExports.ResetProcessArguments();
    }

    [Fact]
    public void SysAbiRegistry_RegistersGetArgcAndGetArgvWithLibc()
    {
        var exports = SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5);

        var getArgc = Assert.Single(exports, e => e.Nid == "iKJMWrAumPE");
        Assert.Equal("getargc", getArgc.Name);
        Assert.Equal("libc", getArgc.LibraryName);
        Assert.True(getArgc.PreferLle);

        var getArgv = Assert.Single(exports, e => e.Nid == "FJmglmTMdr4");
        Assert.Equal("getargv", getArgv.Name);
        Assert.Equal("libc", getArgv.LibraryName);
        Assert.True(getArgv.PreferLle);
    }
}
