// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Buffers;
using Silk.NET.Vulkan;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Images.ImageCacheTestSupport;

namespace SharpEmu.Libs.Tests.Gpu.Images;

public sealed partial class GuestImageCacheTests
{
    [Fact]
    public void ImageHistoryTrace_ReportsBufferFillsAndVolumeAcquisition()
    {
        if (!ImageClearTrace.Enabled) return;
        if (!GatePrerequisites.Ready(_vulkan)) return;
        Assert.True(_vulkan.ValidationEnabled);
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        var request = LinearRequest(address, 256, Format.R32Uint, GuestPixelFormat.Bits32UInt,
            GuestImageType.Color3D, new Extent3D(1, 1, 1), 1, sizeof(uint), 1);
        using var output = new StringWriter();
        var previous = Console.Error;
        try
        {
            Console.SetError(output);
            harness.Worker.Run(() =>
            {
                harness.Images.SetMetadataTraceSubmission(2, 42);
                harness.Cache.FillBuffer(address, 256, 0x12345678u, false);
            });
            var identifier = harness.Acquire(ref request);
            Assert.Equal(Bytes(0x12345678u), harness.ReadImageBytes(harness.Image(identifier)));
            var repeated = request;
            Assert.Equal(identifier, harness.Find(ref repeated));
            var beforeHit = harness.Images.LookupMemoCounters;
            harness.Worker.Run(() => harness.Images.SetMetadataTraceSubmission(2, 43));
            repeated = request;
            Assert.Equal(identifier, harness.Find(ref repeated));
            Assert.Equal(beforeHit.Hits + 1, harness.Images.LookupMemoCounters.Hits);
            harness.Worker.Run(() => harness.Images.TraceWritersOf("test", address, 256));
            harness.Shutdown();
            harness.Images.Dispose();
        }
        finally
        {
            Console.SetError(previous);
        }
        var text = output.ToString();
        Assert.Contains("operation=buffer-fill", text);
        Assert.Contains("queue=2 submission=42", text);
        Assert.Contains("operation=create", text);
        Assert.Contains("operation=acquire", text);
        Assert.Contains("VolumeImageHistory", text);
        Assert.Contains(text.Split('\n'), line => line.Contains("operation=lookup") && line.Contains("submission=43"));
    }
}
