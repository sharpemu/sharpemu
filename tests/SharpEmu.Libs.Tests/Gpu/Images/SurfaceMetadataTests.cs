// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Images;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Images;

public sealed class SurfaceMetadataTests
{
    [Theory]
    [InlineData(31u)]
    [InlineData(32u)]
    [InlineData(63u)]
    [InlineData(64u)]
    [InlineData(255u)]
    public void ClearSlices_ConsumeIndependentlyAndResetOnNewFill(uint slice)
    {
        var metadata = new SurfaceMetadata { ClearMask = uint.MaxValue };
        Assert.True(metadata.IsSliceClear(slice));
        metadata.SetSliceClear(slice, false);
        Assert.False(metadata.IsSliceClear(slice));
        Assert.True(metadata.IsSliceClear(slice + 1));
        Assert.True(metadata.IsSliceClear(slice - 1));
        metadata.SetSliceClear(slice, true);
        Assert.True(metadata.IsSliceClear(slice));
        metadata.SetSliceClear(slice, false);
        metadata.ClearMask = uint.MaxValue;
        Assert.True(metadata.IsSliceClear(slice));
        metadata.ClearMask = 0;
        Assert.False(metadata.IsSliceClear(slice));
        metadata.SetSliceClear(slice, true);
        Assert.True(metadata.IsSliceClear(slice));
        Assert.False(metadata.IsSliceClear(slice + 1));
    }

    [Theory]
    [InlineData(64u)]
    [InlineData(65u)]
    [InlineData(257u)]
    public void ClearSlices_ConsumeTheWholeVolume(uint count)
    {
        var metadata = new SurfaceMetadata { ClearMask = uint.MaxValue };
        for (uint slice = 0; slice < count; slice++)
        {
            Assert.True(metadata.IsSliceClear(slice));
            metadata.SetSliceClear(slice, false);
        }
        for (uint slice = 0; slice < count; slice++) Assert.False(metadata.IsSliceClear(slice));
    }
}
