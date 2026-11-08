// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Images;
using Silk.NET.Vulkan;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Images.ImageCacheTestSupport;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed unsafe partial class RenderHostDeviceTests
{
    [Fact]
    public void NativeColorClear_FindImageConsumesCompletedGpuMetadata()
    {
        if (!Ready()) return;
        using var presenter = new PresenterUnderTest(_vulkan);
        presenter.LoadRenderingCommands();
        var harness = presenter.Harness;
        var address = harness.MapBacked(0x20000, ReadWrite);
        var metadataAddress = address + 0x10000;
        harness.Write(address, Bytes((ushort)0, (ushort)0, (ushort)0, (ushort)0));
        harness.Write(metadataAddress, Enumerable.Repeat((byte)0xff, 4096).ToArray());
        var request = AsColorTarget(LinearRequest(address, 256, Format.R16G16B16A16Sfloat,
            GuestPixelFormat.Bits16_16_16_16Float, GuestImageType.Color2D, new Extent3D(1, 1, 1), 1, 8, 1));
        request.Description.Metadata.Kind = MetadataKind.Dcc;
        request.Description.Metadata.Range = new GuestSpan(metadataAddress, 4096);
        request.Description.Metadata.NativeColorClear = true;
        var image = presenter.Run(() =>
        {
            var (buffer, offset) = harness.Cache.ObtainBuffer(metadataAddress, 4096, isWritten: true);
            buffer.Fill(offset, 4096, 0x40404040);
            var identifier = presenter.RenderHost.FindImage(ref request, exactFormat: false);
            harness.Images.AcquireColorTargetView(identifier, request);
            return harness.Images.GetImage(identifier);
        });
        Assert.Equal(Bytes((ushort)0, (ushort)0, (ushort)0, (ushort)0x3c00), harness.ReadImageBytes(image));
        Assert.Equal(uint.MaxValue, harness.ReadUInt32(metadataAddress));
        harness.Shutdown();
    }
}
