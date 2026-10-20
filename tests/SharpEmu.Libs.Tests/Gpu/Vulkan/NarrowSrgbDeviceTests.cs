// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Images;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Vulkan;

// 8-bit and 8_8 sRGB render targets use the native host format when the device offers it as a
// blendable, sampled colour attachment, so the gamma is applied on writes and reads. The UNORM
// stand-in applies none.
public sealed unsafe class NarrowSrgbDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    [Theory]
    [InlineData(GuestPixelFormat.Bits8Srgb, Format.R8Srgb, Format.R8Unorm, 1u)]
    [InlineData(GuestPixelFormat.Bits8_8Srgb, Format.R8G8Srgb, Format.R8G8Unorm, 2u)]
    public void ClearingALinearValueWritesTheSrgbEncoding(GuestPixelFormat guest, Format native, Format standIn, uint bytes)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan)) return;
        using var harness = new ImageTestHarness(vulkan);
        var host = GuestPixelFormats.HostFormat(guest);
        var features = harness.Device.GetFormatProperties(native).OptimalTilingFeatures;
        const FormatFeatureFlags Required =
            FormatFeatureFlags.ColorAttachmentBit | FormatFeatureFlags.ColorAttachmentBlendBit |
            FormatFeatureFlags.SampledImageBit | FormatFeatureFlags.SampledImageFilterLinearBit |
            FormatFeatureFlags.TransferSrcBit | FormatFeatureFlags.TransferDstBit;
        var supported = (features & Required) == Required;
        // The probe runs when the device object is made; another device in the process may have
        // switched the lookup since, so the selection is checked only through the support rule.
        GuestPixelFormats.UseNativeNarrowSrgb(supported);
        host = GuestPixelFormats.HostFormat(guest);
        Assert.Equal(supported ? native : standIn, host);
        if (!supported) return;

        var description = ImageDescription.Create();
        description.Data = new GuestSpan(ArrayBackedSpace.Base, 4096);
        description.PixelFormat = host;
        description.GuestFormat = guest;
        description.Extent = new Extent3D(1, 1, 1);
        description.Resources = new SubresourceCount(1, 1);
        description.Pitch = 1;
        description.BytesPerBlock = bytes;
        var image = harness.CreateImage(description);
        harness.Run(() =>
        {
            var command = new CommandBuffer(harness.Scheduler.Current.Handle);
            image.Transition(ImageLayout.TransferDstOptimal, AccessFlags.TransferWriteBit, null, command);
            var color = new ClearColorValue(0.5f, 0.5f, 0.5f, 0.5f);
            var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
            harness.Vk.CmdClearColorImage(command, image.Backing.Handle, ImageLayout.TransferDstOptimal, &color, 1, &range);
        });
        var data = harness.ReadImage(image, ImageTestHarness.WholeImageCopies(description, 0), bytes);

        // Linear 0.5 encodes to 188 in sRGB; stored as UNORM it would be 128.
        Assert.All(data, value => Assert.InRange(value, 187, 189));
        harness.AssertNoValidationMessages();
    }
}
