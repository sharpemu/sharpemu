// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.LibcStdio;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class LibcProcessArgumentExportsTests
{
    [Fact]
    public void GetArgcAndGetArgvExposeConfiguredProcessImageName()
    {
        HleDataSymbols.ConfigureProcessImageName("compat-test-eboot.bin");
        try
        {
            var context = new CpuContext(new NullCpuMemory(), Generation.Gen5);

            Assert.Equal(1, LibcProcessArgumentExports.GetArgc(context));
            Assert.Equal(1UL, context[CpuRegister.Rax]);

            Assert.Equal(0, LibcProcessArgumentExports.GetArgv(context));
            var argv = unchecked((nint)context[CpuRegister.Rax]);
            Assert.NotEqual(0, argv);
            var argv0 = Marshal.ReadIntPtr(argv);
            Assert.NotEqual(0, argv0);
            Assert.Equal("compat-test-eboot.bin", Marshal.PtrToStringUTF8(argv0));
            Assert.Equal(0, Marshal.ReadIntPtr(argv, nint.Size));
        }
        finally
        {
            HleDataSymbols.ConfigureProcessImageName("eboot.bin");
        }
    }

    [Fact]
    public void ProcessArgumentAndFileLockExportsHaveExpectedIdentities()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        AssertExport(manager, "iKJMWrAumPE", "getargc", "libKernel");
        AssertExport(manager, "FJmglmTMdr4", "getargv", "libKernel");
        AssertExport(manager, "vZkmJmvqueY", "_Lockfilelock", "libc");
        AssertExport(manager, "0x7rx8TKy2Y", "_Unlockfilelock", "libc");
    }

    [Fact]
    public void FileLockCompatibilityCallsAreSideEffectFreeAndSuccessful()
    {
        var context = new CpuContext(new NullCpuMemory(), Generation.Gen5);
        context[CpuRegister.Rdi] = 0x1234_5678;

        Assert.Equal(0, LibcStdioExports.LockFileLock(context));
        Assert.Equal(0UL, context[CpuRegister.Rax]);
        Assert.Equal(0, LibcStdioExports.UnlockFileLock(context));
        Assert.Equal(0UL, context[CpuRegister.Rax]);
    }

    private static void AssertExport(
        ModuleManager manager,
        string nid,
        string name,
        string library)
    {
        Assert.True(manager.TryGetExport(nid, out var export));
        Assert.Equal(name, export.Name);
        Assert.Equal(library, export.LibraryName);
    }

    private sealed class NullCpuMemory : ICpuMemory
    {
        public bool TryRead(ulong virtualAddress, Span<byte> destination) => false;
        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source) => false;
    }
}
