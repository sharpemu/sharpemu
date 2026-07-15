// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Pad;
using Xunit;

namespace SharpEmu.Libs.Tests.Pad;

public sealed class HostWindowInputTests
{
    [Theory]
    [InlineData(-1.0f, 0)]
    [InlineData(0.0f, 128)]
    [InlineData(1.0f, 255)]
    public void ToStickByteMapsFullSilkRange(float value, int expected)
    {
        Assert.Equal((byte)expected, HostWindowInput.ToStickByte(value));
    }
}
