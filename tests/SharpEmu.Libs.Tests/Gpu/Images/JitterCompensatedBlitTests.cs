// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Upscaling;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using SharpEmu.Libs.VideoOut.Upscaling;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Images;

[Collection(SchedulingStateCollection.Name)]
public sealed unsafe class JitterCompensatedBlitTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    [Fact]
    public void RemovesSubpixelShiftAndClampsToRenderRectangle()
    {
        const Format colorFormat = Format.R16G16B16A16Sfloat;
        const Format outputFormat = Format.B10G11R11UfloatPack32;
        if (fixture.Vulkan is not { SupportsDynamicRendering: true } v)
        {
            return;
        }
        v.Vk.GetPhysicalDeviceFeatures(v.Physical, out var supported);
        var format3 = new FormatProperties3 { SType = StructureType.FormatProperties3 };
        var format2 = new FormatProperties2 { SType = StructureType.FormatProperties2, PNext = &format3 };
        v.Vk.GetPhysicalDeviceFormatProperties2(v.Physical, outputFormat, &format2);
        if (!supported.ShaderStorageImageWriteWithoutFormat ||
            (format3.OptimalTilingFeatures & FormatFeatureFlags2.StorageWriteWithoutFormatBit) == 0)
        {
            return;
        }

        using var harness = new ImageTestHarness(v);
        Assert.True(v.Vk.TryGetDeviceExtension(v.Instance, v.Device, out KhrPushDescriptor descriptors));
        using var blit = new JitterCompensatedBlit(v.Vk, v.Physical, v.Device, descriptors);
        var color = ImageDescription.Create();
        color.PixelFormat = colorFormat;
        color.GuestFormat = GuestPixelFormat.Bits16_16_16_16Float;
        color.Extent = new Extent3D(6, 5, 1);
        color.Pitch = 6;
        color.BytesPerBlock = 8;
        color.Data = new GuestSpan(ArrayBackedSpace.Base, ImageTestHarness.WholeImageBytes(color));
        var output = ImageDescription.Create();
        output.PixelFormat = outputFormat;
        output.GuestFormat = GuestPixelFormat.Bits11_11_10Float;
        output.Extent = new Extent3D(6, 4, 1);
        output.Pitch = 6;
        output.BytesPerBlock = 4;
        output.Data = new GuestSpan(ArrayBackedSpace.Base + 0x1000, ImageTestHarness.WholeImageBytes(output));
        var colorImage = harness.CreateImage(color);
        var outputImage = harness.CreateImage(output);

        var texels = new Half[6 * 5 * 4];
        for (var y = 0; y < 5; y++)
        {
            for (var x = 0; x < 6; x++)
            {
                texels[(y * 6 + x) * 4] = (Half)(2 * x + 8 * y);
            }
        }
        var outputCopies = ImageTestHarness.WholeImageCopies(output, 0);
        harness.UploadImage(colorImage, MemoryMarshal.AsBytes(texels.AsSpan()), ImageTestHarness.WholeImageCopies(color, 0));
        harness.UploadImage(outputImage, new byte[6 * 4 * 4], outputCopies);

        try
        {
            Assert.True(harness.Run(() =>
            {
                var command = new CommandBuffer(harness.Scheduler.Current.Handle);
                colorImage.Transition(AccessFlags.ShaderReadBit, null, command);
                outputImage.Transition(AccessFlags.ShaderWriteBit, null, command);
                var colorBinding = Describe(colorImage, ImageViewDescription.Default with { Format = colorFormat }, out var colorView);
                var outputBinding = Describe(outputImage,
                    ImageViewDescription.Default with { Format = outputFormat, Usage = ImageUsageFlags.StorageBit }, out var outputView);
                var parameters = new byte[SuperResolutionPacket.ParameterBytes];
                BinaryPrimitives.WriteSingleLittleEndian(parameters.AsSpan(0x100 - SuperResolutionPacket.ParameterOffset), 0.25f);
                BinaryPrimitives.WriteSingleLittleEndian(parameters.AsSpan(0x104 - SuperResolutionPacket.ParameterOffset), -0.25f);
                var frame = new SuperResolutionFrame
                {
                    Dispatch = new SuperResolutionDispatch(1, 5, 3, 0, false, [], parameters),
                    Color = colorBinding,
                    Output = outputBinding,
                    RenderX = 1,
                    RenderY = 1,
                    RenderWidth = 3,
                    RenderHeight = 2,
                    OutputWidth = 5,
                    OutputHeight = 3,
                };
                return blit.TryUpscale(command, frame, colorView, outputView);
            }));
            var result = harness.ReadImage(outputImage, outputCopies, output.Data.Size);
            Assert.InRange(ReadRed(result, 2, 1), 14.45f, 14.55f);
            Assert.InRange(ReadRed(result, 0, 0), 10.0f, 10.2f);
            Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(5 * 4)));
            harness.AssertNoValidationMessages();
        }
        finally
        {
            harness.Finish();
        }
    }

    private static SuperResolutionImage Describe(CachedImage image, in ImageViewDescription requested,
        out ImageViewDescription view)
    {
        var handle = image.GetOrCreateView(requested, out view);
        var backing = image.Backing;
        return new SuperResolutionImage(backing.Handle.Handle, handle.Handle, (uint)view.Format,
            backing.Extent.Width, backing.Extent.Height, 1, 1, (uint)backing.Usage, 0, false, false);
    }

    private static float ReadRed(ReadOnlySpan<byte> output, int x, int y)
    {
        var red = BinaryPrimitives.ReadUInt32LittleEndian(output[((6 * y + x) * 4)..]) & 0x7ff;
        return MathF.ScaleB(1f + (red & 63) / 64f, (int)(red >> 6) - 15);
    }
}
