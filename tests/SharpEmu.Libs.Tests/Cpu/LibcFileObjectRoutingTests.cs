// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
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
}
