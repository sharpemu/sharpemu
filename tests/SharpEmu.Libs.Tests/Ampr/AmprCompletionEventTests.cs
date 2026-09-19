// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Ampr;

public sealed class AmprCompletionEventTests
{
    [Fact]
    public void EventFiltersUseTheRequiredAbiValues()
    {
        Assert.Equal(-25, KernelEventQueueCompatExports.KernelEventFilterAmpr);
        Assert.Equal(-30, KernelEventQueueCompatExports.KernelEventFilterAmprSystem);
    }
}
