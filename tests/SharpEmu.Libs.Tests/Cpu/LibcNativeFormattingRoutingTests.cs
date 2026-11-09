// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class LibcNativeFormattingRoutingTests
{
    [Fact]
    public void SprintfUsesTheNativeFormattingExportWhenAvailable()
    {
        Assert.True(DirectExecutionBackend.IsLibcNativeFormattingExport("sprintf"));
    }

    [Theory]
    [InlineData("printf")]
    [InlineData("snprintf")]
    [InlineData("vsprintf")]
    public void OtherFormattingExportsKeepTheirExistingRoute(string exportName)
    {
        Assert.False(DirectExecutionBackend.IsLibcNativeFormattingExport(exportName));
    }
}
