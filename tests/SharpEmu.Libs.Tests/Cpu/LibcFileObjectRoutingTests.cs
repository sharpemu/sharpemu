// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;
using SharpEmu.HLE;
using System.Reflection;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class LibcFileObjectRoutingTests
{
    [Theory]
    [InlineData("fgetpos")]
    [InlineData("fsetpos")]
    [InlineData("setvbuf")]
    [InlineData("_Lockfilelock")]
    [InlineData("fgetwc")]
    public void FileObjectExportsRequireOneStdioImplementation(string exportName)
    {
        Assert.True(DirectExecutionBackend.IsLibcFileObjectExport(exportName));
    }

    [Theory]
    [InlineData("memcpy")]
    [InlineData("malloc")]
    public void NonFileExportsDoNotUseTheFileObjectGuard(string exportName)
    {
        Assert.False(DirectExecutionBackend.IsLibcFileObjectExport(exportName));
    }

    [NativeX64Fact]
    public void RegisteredFgetposRoutesToExecutableGuestLibcSymbol()
    {
        const string nid = "SHlt7EhOtqA";
        const ulong target = 0x8000_1000;
        var modules = new ModuleManager();
        modules.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));
        modules.Freeze();
        Assert.True(modules.TryGetExport(nid, out var export));
        Assert.Equal("fgetpos", export.Name);

        var memory = new VirtualMemory();
        memory.Map(target, 0x100, 0, [0xC3], ProgramHeaderFlags.Read | ProgramHeaderFlags.Execute);
        var context = new CpuContext(memory, Generation.Gen5);

        using var backend = new DirectExecutionBackend(modules);
        var backendType = typeof(DirectExecutionBackend);
        const BindingFlags instancePrivate = BindingFlags.Instance | BindingFlags.NonPublic;
        backendType.GetField("_cpuContext", instancePrivate)!.SetValue(backend, context);
        backendType.GetMethod("InitializeRuntimeSymbolIndex", instancePrivate)!.Invoke(
            backend,
            [new Dictionary<string, ulong>(StringComparer.Ordinal) { [nid] = target }]);

        var arguments = new object?[] { nid, 0UL, string.Empty };
        var routed = Assert.IsType<bool>(backendType
            .GetMethod("TryResolveDirectImportTarget", instancePrivate)!
            .Invoke(backend, arguments));

        Assert.True(routed);
        Assert.Equal(target, Assert.IsType<ulong>(arguments[1]));
        Assert.Equal(nid, Assert.IsType<string>(arguments[2]));
    }
}
