// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

public sealed class KernelHostWaitTests
{
    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(-5.0, 0)]
    [InlineData(0.001, 1)]
    [InlineData(0.9, 1)]
    [InlineData(1.0, 1)]
    [InlineData(1.2, 2)]
    [InlineData(10.0, 10)]
    public void SubMillisecondRemaindersRoundUpInsteadOfSpinning(double milliseconds, int expected)
    {
        Assert.Equal(expected, KernelHostWait.RoundUpMilliseconds(TimeSpan.FromMilliseconds(milliseconds)));
    }
}
