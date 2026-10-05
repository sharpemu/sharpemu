// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class FiberStackBoundsTests
{
    [Fact]
    public void FiberSwitchesTouchGsOnlyWhereItHoldsTheTebStackBounds()
    {
        var code = DirectExecutionBackend.EmitFiberStackBoundsForTest();

        if (!OperatingSystem.IsWindows())
        {
            // On macOS gs addresses the pthread TSD: gs:[8] is errno's address. A fiber switch
            // must not store its stack bounds there.
            Assert.Empty(code);
            return;
        }

        // Windows: both TEB bounds (gs:[8] StackBase, gs:[16] StackLimit) are stored from r10.
        byte[] storeBase = [0x65, 0x4C, 0x89, 0x14, 0x25, 8, 0, 0, 0];
        byte[] storeLimit = [0x65, 0x4C, 0x89, 0x14, 0x25, 16, 0, 0, 0];
        Assert.True(code.AsSpan().IndexOf(storeBase) >= 0);
        Assert.True(code.AsSpan().IndexOf(storeLimit) >= 0);
    }
}
