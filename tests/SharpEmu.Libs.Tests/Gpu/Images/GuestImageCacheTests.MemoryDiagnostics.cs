// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Buffers;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Images.ImageCacheTestSupport;

namespace SharpEmu.Libs.Tests.Gpu.Images;

public sealed partial class GuestImageCacheTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ImageMemoryReport_MatchesBufferOverlapRetention(bool bufferOverlap)
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        harness.Write(address, Bytes(0x12345678u));
        var request = Color32(address);
        var identifier = harness.Acquire(ref request);
        if (bufferOverlap)
            harness.Worker.Run(() => Assert.NotNull(harness.Cache.ObtainBuffer(address, 4, isWritten: true).Buffer));
        harness.MarkGpuWritten(identifier);
        var image = harness.Image(identifier);
        Assert.True(image.SafeToDownload);
        Assert.Equal(bufferOverlap, harness.Cache.HasGpuDirtyBytes(address, 4));

        var reportMethod = typeof(GuestImageCache).GetMethod("ReportImageMemory", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(reportMethod);
        using var output = new StringWriter();
        var previousError = Console.Error;
        try
        {
            Console.SetError(output);
            harness.Worker.Run(() =>
            {
                using var held = harness.Images.HoldLockForTest();
                harness.Images.SetCollectionThresholds(0, 0, ulong.MaxValue, 81);
                harness.Images.ResetRecency(new[] { identifier }, 81);
                reportMethod.Invoke(harness.Images, ["test", true, 81UL]);
            });
        }
        finally
        {
            Console.SetError(previousError);
        }

        var largest = Assert.Single(output.ToString().Split('\n'),
            line => line.Contains($"ImageMemoryLargest address=0x{address:X} ", StringComparison.Ordinal));
        Assert.EndsWith(bufferOverlap ? "retention=dirty-buffer-overlap" : "retention=requires-readback", largest.TrimEnd());
        Assert.True(image.SafeToDownload);
        Assert.Equal(bufferOverlap, harness.Cache.HasGpuDirtyBytes(address, 4));
        harness.Worker.Run(() => harness.Images.RunGarbageCollector());
        Assert.Equal(bufferOverlap, harness.Images.Contains(identifier));
        if (bufferOverlap)
            Assert.True(image.IsGpuModified);
        harness.Shutdown();
    }
}
