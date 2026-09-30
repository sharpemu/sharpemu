// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Images;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Images;

public sealed class VolumeUploadFilterTests
{
    [Theory]
    [InlineData(null, 64, 32, 5, true)]
    [InlineData("0", 1280, 95, 5, false)]
    [InlineData("32", 32, 32, 32, true)]
    [InlineData("32", 32, 32, 5, false)]
    [InlineData("128", 128, 128, 128, false)]
    [InlineData("1280x95x5", 1280, 95, 5, true)]
    [InlineData("1280x95x5", 1280, 95, 6, false)]
    [InlineData("1280x95", 1280, 95, 5, false)]
    [InlineData("1280x0x5", 1280, 0, 5, false)]
    [InlineData("4294967296x95x5", 1280, 95, 5, false)]
    [InlineData("invalid", 32, 32, 32, false)]
    public void SelectsOnlyAllowedDimensions(string? filter, uint width, uint height, uint depth, bool expected)
    {
        Assert.Equal(expected, GuestImageCache.MatchesVolumeUploadExtent(filter, width, height, depth));
    }
}
