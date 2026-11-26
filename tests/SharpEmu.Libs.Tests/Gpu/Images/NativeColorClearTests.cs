// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Images;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Images;

public sealed class NativeColorClearTests
{
    [Fact]
    public void UniformMetadataScanRequiresAllBytesAndRejectsMixedChunks()
    {
        var scan = new UniformMetadataScan(0x40, 4);
        Assert.False(scan.Complete);
        Assert.True(scan.Accept([0x40, 0x40]));
        Assert.False(scan.Complete);
        Assert.True(scan.Accept([0x40, 0x40]));
        Assert.True(scan.Complete);

        var mixed = new UniformMetadataScan(0x40, 4);
        Assert.True(mixed.Accept([0x40, 0x40]));
        Assert.False(mixed.Accept([0x40, 0x80]));
        Assert.False(mixed.Complete);
        Assert.False(mixed.Accept([0x40, 0x40]));

        var excessive = new UniformMetadataScan(0x40, 1);
        Assert.False(excessive.Accept([0x40, 0x40]));
        Assert.False(excessive.Complete);
        Assert.False(new UniformMetadataScan(0x40, 0).Complete);
    }

    [Theory]
    [InlineData(Format.R16G16Sfloat, false, 0, 1, 0, 1)]
    [InlineData(Format.R16Sfloat, false, 1, 0, 0, 1)]
    [InlineData(Format.R16Sfloat, true, 0, 0, 0, 0)]
    [InlineData(Format.B8G8R8A8Unorm, true, 0, 0, 1, 0)]
    [InlineData(Format.R4G4B4A4UnormPack16, false, 1, 0, 0, 0)]
    [InlineData(Format.R16G16B16A16Sfloat, false, 0, 0, 0, 1)]
    public void NativeColorClear_DecodesChannelPlacement(Format format, bool alphaLow,
        float red, float green, float blue, float alpha)
    {
        Assert.True(NativeColorClear.TryDecode(0x40, format, alphaLow, out var color));
        Assert.Equal(new ClearColorValue(red, green, blue, alpha), color);
        Assert.False(NativeColorClear.TryDecode(0xff, format, alphaLow, out _));
        Assert.False(NativeColorClear.TryDecode(0, Format.Undefined, alphaLow, out _));
    }

    [Fact]
    public void BlockFirstElementKey_IsRecognizedOnlyForDcc()
    {
        Assert.True(NativeColorClear.IsBlockFirstElement(0x10, cmask: false));
        Assert.False(NativeColorClear.IsBlockFirstElement(0x10, cmask: true));
        foreach (var code in new byte[] { 0x00, 0x20, 0x40, 0x80, 0xc0, 0xff, 0x11, 0x01 })
        {
            Assert.False(NativeColorClear.IsBlockFirstElement(code, cmask: false));
        }

        // The color-code decoder keeps its codes and does not take 0x10.
        Assert.False(NativeColorClear.TryDecode(0x10, Format.R16G16Sfloat, false, out _));
        Assert.True(NativeColorClear.TryDecode(0x80, Format.R16G16Sfloat, false, out _));
    }

    [Fact]
    public void BlockFirstElementKey_MixedKeysAreNotUniform()
    {
        var scan = new UniformMetadataScan(NativeColorClear.DccBlockFirstElementCode, 4);
        Assert.True(scan.Accept([0x10, 0x10]));
        Assert.False(scan.Accept([0x10, 0xff]));
        Assert.False(scan.Complete);
        var uniform = new UniformMetadataScan(NativeColorClear.DccBlockFirstElementCode, 4);
        Assert.True(uniform.Accept([0x10, 0x10, 0x10, 0x10]));
        Assert.True(uniform.Complete);
    }

    [Fact]
    public void BlockFirstElementRefusal_NamesWhatTheExpansionCannotProve()
    {
        var tiled = ImageCacheTestSupport.AsColorTarget(ImageCacheTestSupport.LinearRequest(0x10000, 0x4000, Format.R16G16Sfloat,
            GuestPixelFormat.Bits16_16UNorm, GuestImageType.Color2D, new Extent3D(64, 64, 1), 1, 4, 1));
        Assert.Contains("linear", NativeColorClear.BlockFirstElementRefusal(tiled));
        tiled.Description.TileMode = GuestTileMode.RenderTarget;
        Assert.Null(NativeColorClear.BlockFirstElementRefusal(tiled));

        var texture = tiled;
        texture.Role = ImageRole.Texture;
        // The texture unit decompresses DCC as the color block does: a sampled surface expands too.
        Assert.Null(NativeColorClear.BlockFirstElementRefusal(texture));
        var misaligned = tiled;
        misaligned.Description.Data = new GuestSpan(0x10080, 0x4000);
        Assert.Contains("aligned", NativeColorClear.BlockFirstElementRefusal(misaligned));
        var multisampled = tiled;
        multisampled.Description.Samples = 4;
        Assert.NotNull(NativeColorClear.BlockFirstElementRefusal(multisampled));
        var mips = tiled;
        mips.Description.Resources = new SubresourceCount(2, 1);
        Assert.Contains("mip", NativeColorClear.BlockFirstElementRefusal(mips));
        var volume = tiled;
        volume.Description.Type = GuestImageType.Color3D;
        Assert.NotNull(NativeColorClear.BlockFirstElementRefusal(volume));
    }

    [Theory]
    [InlineData(2144, 1208, 8, 102400)]
    [InlineData(512, 512, 4, 4096)]
    [InlineData(513, 512, 4, 8192)]
    public void NativeColorMetadata_UsesCompleteBlocks(uint width, uint height, uint bytes, ulong expected)
        => Assert.Equal(expected, NativeColorClear.SliceSize(width, height, bytes));
}
