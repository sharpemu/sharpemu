// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Buffers;
using Silk.NET.Vulkan;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Images.ImageCacheTestSupport;

namespace SharpEmu.Libs.Tests.Gpu.Images;

public sealed partial class GuestImageCacheTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void BindingProvenance_ReportsSampleChangesAndUnmapNotifications(bool changeBytes, bool unmap)
    {
        if (!ImageClearTrace.Enabled) return;
        if (!GatePrerequisites.Ready(_vulkan)) return;
        Assert.True(_vulkan.ValidationEnabled);
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        harness.Write(address, Bytes(0x12345678u));
        var earlier = LinearRequest(address, 256, Format.R32Uint, GuestPixelFormat.Bits32UInt,
            GuestImageType.Color2D, new Extent3D(1, 1, 1), 1, sizeof(uint), 1);
        harness.Worker.Run(() => harness.Images.TraceTextureBinding(0x21, 3, new uint[] { 0x12345678 }, earlier));
        if (changeBytes) harness.Write(address, Bytes(0x87654321u));
        if (unmap) harness.Worker.Run(() => ((IGuestImageStore)harness.Images).Unregister(address, 0x10000));
        var volume = earlier;
        volume.Description.Type = GuestImageType.Color3D;
        volume.View = volume.View with { Type = ImageViewType.Type3D };
        using var output = new StringWriter();
        var previous = Console.Error;
        try
        {
            Console.SetError(output);
            var identifier = harness.Acquire(ref volume);
            Assert.Equal(Bytes(changeBytes ? 0x87654321u : 0x12345678u), harness.ReadImageBytes(harness.Image(identifier)));
            harness.Shutdown();
        }
        finally
        {
            Console.SetError(previous);
        }
        var text = output.ToString();
        Assert.Contains("BindingProvenance", text);
        Assert.Contains("shader=0x0000000000000021 image=3", text);
        Assert.Contains($"match={!changeBytes} unmapsBetween={(unmap ? 1 : 0)}", text);
    }
}
