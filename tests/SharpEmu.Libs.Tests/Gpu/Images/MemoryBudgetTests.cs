// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Images;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Images;

public sealed class MemoryBudgetTests
{
    private const ulong MiB = 1UL << 20;
    private const ulong GiB = 1UL << 30;

    [Fact]
    public void AvailableMemoryUsesBudgetMinusCurrentUsage()
    {
        Assert.Equal(8 * GiB, GpuDeviceInfo.CalculateAvailableBytes(12 * GiB, 4 * GiB));
        Assert.Equal(0UL, GpuDeviceInfo.CalculateAvailableBytes(4 * GiB, 5 * GiB));
        Assert.Equal(0UL, GpuDeviceInfo.CalculateAvailableBytes(4 * GiB, 4 * GiB));
    }

    [Fact]
    public void ImageCacheLeavesAQuarterOfWhatIsFree()
    {
        Assert.Equal(6 * GiB, GuestImageCache.ComputeImageCacheBudget(8 * GiB, hasMemoryBudget: true));
        Assert.Equal(9 * GiB, GuestImageCache.ComputeImageCacheBudget(12 * GiB, hasMemoryBudget: true));
    }

    [Fact]
    public void ImageCacheMarginHasAFloor()
    {
        // A quarter of 1 GiB is below the 512 MiB floor.
        Assert.Equal(512 * MiB, GuestImageCache.ComputeImageCacheBudget(GiB, hasMemoryBudget: true));
        // Less free memory than the floor keeps half of it, never less than 1 MiB.
        Assert.Equal(200 * MiB, GuestImageCache.ComputeImageCacheBudget(400 * MiB, hasMemoryBudget: true));
        Assert.Equal(MiB, GuestImageCache.ComputeImageCacheBudget(0, hasMemoryBudget: true));
    }

    [Fact]
    public void WithoutAMemoryBudgetTheHeapSizeIsCappedConservatively()
    {
        Assert.Equal(4 * GiB, GuestImageCache.ComputeImageCacheBudget(12 * GiB, hasMemoryBudget: false));
        Assert.Equal(3 * GiB, GuestImageCache.ComputeImageCacheBudget(4 * GiB, hasMemoryBudget: false));
    }
}
