// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Images;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Images;

public sealed class MemoryBudgetTests
{
    [Fact]
    public void AvailableMemoryUsesBudgetMinusCurrentUsage()
    {
        Assert.Equal(8UL << 30, GpuDeviceInfo.CalculateAvailableBytes(12UL << 30, 4UL << 30));
        Assert.Equal(0UL, GpuDeviceInfo.CalculateAvailableBytes(4UL << 30, 5UL << 30));
    }

    [Fact]
    public void ImageCacheLeavesMarginAndCapsHeapFallback()
    {
        Assert.Equal(6UL << 30, GuestImageCache.ComputeImageCacheBudget(8UL << 30, hasMemoryBudget: true));
        Assert.Equal(4UL << 30, GuestImageCache.ComputeImageCacheBudget(12UL << 30, hasMemoryBudget: false));
    }
}
