// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class Gen5TextureDescriptorTests
{
    private const ulong MemoryBase = 0x1_0000_0000;

    [Theory]
    [InlineData(22u, 4u, 7u)]
    [InlineData(36u, 6u, 7u)]
    [InlineData(56u, 10u, 0u)]
    [InlineData(69u, 12u, 4u)]
    [InlineData(71u, 12u, 7u)]
    public void TextureDescriptor_NormalizesUnifiedFormat(
        uint unifiedFormat,
        uint expectedDataFormat,
        uint expectedNumberFormat)
    {
        var fields = Descriptor(
            address: 0x201E_0000,
            width: 1920,
            height: 1080,
            unifiedFormat,
            tileMode: 27);

        Assert.True(
            AgcExports.TryDecodeTextureDescriptor(fields, out var descriptor));
        Assert.Equal(expectedDataFormat, descriptor.Format);
        Assert.Equal(expectedNumberFormat, descriptor.NumberType);
        Assert.Equal(0x201E_0000UL, descriptor.Address);
        Assert.Equal(1920u, descriptor.Width);
        Assert.Equal(1080u, descriptor.Height);
    }

    [Fact]
    public void UnreadableSampledImage_PreservesDescriptorForQueuedGpuAlias()
    {
        var fields = Descriptor(
            address: 0x7000_0000,
            width: 16,
            height: 16,
            unifiedFormat: 22,
            tileMode: 9);
        Assert.True(
            AgcExports.TryDecodeTextureDescriptor(fields, out var descriptor));
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);

        Assert.True(
            AgcExports.TryCreateGuestDrawTexture(
                ctx,
                descriptor,
                isStorage: false,
                mipLevel: 0,
                imageDimension: 1,
                samplerDescriptor: [],
                out var texture));
        Assert.Equal(descriptor.Address, texture.Address);
        Assert.Equal(descriptor.Width, texture.Width);
        Assert.Equal(descriptor.Height, texture.Height);
        Assert.Equal(4u, texture.Format);
        Assert.Equal(7u, texture.NumberType);
        Assert.Empty(texture.RgbaPixels);
        Assert.True(texture.IsFallback);
    }

    [Fact]
    public void Texture2DArrayDescriptor_DecodesResourceAndViewLayerRanges()
    {
        var fields = Descriptor(
            address: 0x201E_0000,
            width: 32,
            height: 16,
            unifiedFormat: 56,
            tileMode: 9,
            resourceType: 13,
            baseArray: 3,
            lastArray: 7,
            maxMip: 4,
            baseLevel: 1,
            lastLevel: 3);

        Assert.True(
            AgcExports.TryDecodeTextureDescriptor(fields, out var descriptor));
        Assert.True(descriptor.Is2DArray);
        Assert.Equal(3u, descriptor.BaseArray);
        Assert.Equal(7u, descriptor.LastArray);
        Assert.Equal(8u, descriptor.ResourceArrayLayers);
        Assert.Equal(5u, descriptor.ViewArrayLayers);
        Assert.Equal(0u, descriptor.ArrayPitch);
        Assert.Equal(4u, descriptor.MaxMip);
        Assert.Equal(3u, descriptor.MipLevels);
        Assert.Equal(5u, descriptor.ResourceMipLevels);
    }

    [Fact]
    public void Texture2DArrayDescriptor_RejectsInvertedLayerRange()
    {
        var fields = Descriptor(
            address: 0x201E_0000,
            width: 32,
            height: 16,
            unifiedFormat: 56,
            tileMode: 9,
            resourceType: 13,
            baseArray: 8,
            lastArray: 7);

        Assert.False(
            AgcExports.TryDecodeTextureDescriptor(fields, out _));
    }

    [Fact]
    public void Gfx1013Texture2DDescriptor_DoesNotInterpretReservedWord4Bit13AsPitch()
    {
        var fields = Descriptor(
            address: 0x201E_0000,
            width: 32,
            height: 16,
            unifiedFormat: 56,
            tileMode: 0);
        fields[4] |= 1u << 13;

        Assert.True(
            AgcExports.TryDecodeTextureDescriptor(fields, out var descriptor));
        Assert.Equal(32u, descriptor.Pitch);
    }

    [Fact]
    public void NonPlaceholderTexture2DDescriptorWithArrayShape_IsRejected()
    {
        var fields = Descriptor(
            address: MemoryBase,
            width: 4,
            height: 2,
            unifiedFormat: 56,
            tileMode: 9);
        Assert.True(
            AgcExports.TryDecodeTextureDescriptor(fields, out var descriptor));
        var memory = new FakeCpuMemory(MemoryBase, 0x1_0000);
        var ctx = new CpuContext(memory, Generation.Gen5);

        Assert.False(
            AgcExports.TryCreateGuestDrawTexture(
                ctx,
                descriptor,
                isStorage: false,
                mipLevel: 0,
                imageDimension: 5,
                samplerDescriptor: [],
                out _));
    }

    [Fact]
    public void Texture2DArrayUpload_DetilesFullPhysicalLayerSpans()
    {
        const uint width = 4;
        const uint height = 2;
        const uint resourceLayers = 4;
        var fields = Descriptor(
            address: MemoryBase,
            width,
            height,
            unifiedFormat: 56,
            tileMode: 9,
            resourceType: 13,
            baseArray: 1,
            lastArray: resourceLayers - 1);
        Assert.True(
            AgcExports.TryDecodeTextureDescriptor(fields, out var descriptor));
        var layout = Gfx10TextureLayout.Create(
            width,
            height,
            resourceLayers,
            bytesPerElement: 4);
        var source = new byte[checked((int)layout.GuestSpanBytes)];
        Array.Fill(source, (byte)0xE7);
        var expected = new byte[checked((int)layout.TightSizeBytes)];
        var expectedOffset = 0;
        for (uint layer = 0; layer < resourceLayers; layer++)
        {
            for (uint y = 0; y < height; y++)
            {
                for (uint x = 0; x < width; x++)
                {
                    var guestOffset = checked(
                        (int)layout.GetGuestByteOffset(x, y, layer));
                    for (var component = 0; component < 4; component++)
                    {
                        var value = checked((byte)(
                            1 + (layer * 32) + (y * 8) + (x * 4) +
                            (uint)component));
                        source[guestOffset + component] = value;
                        expected[expectedOffset++] = value;
                    }
                }
            }
        }
        var memory = new FakeCpuMemory(MemoryBase, source.Length);
        Assert.True(memory.TryWrite(MemoryBase, source));
        var ctx = new CpuContext(memory, Generation.Gen5);

        Assert.True(
            AgcExports.TryCreateGuestDrawTexture(
                ctx,
                descriptor,
                isStorage: false,
                mipLevel: 0,
                imageDimension: 5,
                samplerDescriptor: [],
                out var texture));
        Assert.False(texture.IsFallback);
        Assert.True(texture.IsArray);
        Assert.Equal(resourceLayers, texture.ResourceArrayLayers);
        Assert.Equal(1u, texture.BaseArrayLayer);
        Assert.Equal(3u, texture.ViewArrayLayers);
        Assert.Equal(expected.Length, texture.RgbaPixels.Length);
        Assert.Equal(expected, texture.RgbaPixels);
    }

    [Fact]
    public void Texture2DArrayDim1View_PreservesPhysicalLayersAndZeroInitialization()
    {
        const uint width = 4;
        const uint height = 2;
        const uint resourceLayers = 8;
        var fields = Descriptor(
            address: MemoryBase,
            width,
            height,
            unifiedFormat: 56,
            tileMode: 9,
            resourceType: 13,
            baseArray: 3,
            lastArray: resourceLayers - 1);
        Assert.True(
            AgcExports.TryDecodeTextureDescriptor(fields, out var descriptor));
        var layout = Gfx10TextureLayout.Create(
            width,
            height,
            resourceLayers,
            bytesPerElement: 4);
        var expectedBytes = checked((int)layout.TightSizeBytes);
        var memory = new FakeCpuMemory(
            MemoryBase,
            checked((int)layout.GuestSpanBytes));
        var ctx = new CpuContext(memory, Generation.Gen5);

        Assert.True(
            AgcExports.TryCreateGuestDrawTexture(
                ctx,
                descriptor,
                isStorage: true,
                mipLevel: 0,
                imageDimension: 1,
                samplerDescriptor: [],
                out var texture));
        Assert.False(texture.IsFallback);
        Assert.False(texture.IsArray);
        Assert.Equal(resourceLayers, texture.ResourceArrayLayers);
        Assert.Equal(3u, texture.BaseArrayLayer);
        Assert.Equal(1u, texture.ViewArrayLayers);
        Assert.Equal(expectedBytes, texture.RgbaPixels.Length);
        Assert.All(texture.RgbaPixels, value => Assert.Equal(0, value));
    }

    [Fact]
    public void Texture3DDescriptor_DecodesAndDetilesThickVolume()
    {
        const uint width = 15;
        const uint height = 8;
        const uint depth = 32;
        var fields = Descriptor(
            address: MemoryBase,
            width,
            height,
            unifiedFormat: 71,
            tileMode: 9,
            resourceType: 10,
            lastArray: depth - 1);
        Assert.True(
            AgcExports.TryDecodeTextureDescriptor(fields, out var descriptor));
        Assert.True(descriptor.Is3D);
        Assert.Equal(depth, descriptor.Depth);
        Assert.Equal(1u, descriptor.ResourceArrayLayers);

        var layout = Gfx10Texture3DLayout.Create(
            width,
            height,
            depth,
            bytesPerElement: 8);
        var memory = new FakeCpuMemory(
            MemoryBase,
            checked((int)layout.GuestSpanBytes));
        var ctx = new CpuContext(memory, Generation.Gen5);

        Assert.True(
            AgcExports.TryCreateGuestDrawTexture(
                ctx,
                descriptor,
                isStorage: true,
                mipLevel: 0,
                imageDimension: 2,
                samplerDescriptor: [],
                out var texture));
        Assert.False(texture.IsFallback);
        Assert.Equal(GuestImageKind.Type3D, texture.ImageKind);
        Assert.Equal(depth, texture.Depth);
        Assert.Equal(layout.TightSizeBytes, (ulong)texture.RgbaPixels.Length);
        Assert.All(texture.RgbaPixels, value => Assert.Equal(0, value));
    }

    [Fact]
    public void CubeDescriptor_Mode9CpuUploadUsesAddresslessFallback()
    {
        const uint size = 16;
        var fields = Descriptor(
            address: MemoryBase,
            width: size,
            height: size,
            unifiedFormat: 56,
            tileMode: 9,
            resourceType: 11,
            lastArray: 5);
        Assert.True(
            AgcExports.TryDecodeTextureDescriptor(fields, out var descriptor));
        Assert.True(descriptor.IsCube);
        Assert.Equal(6u, descriptor.ResourceArrayLayers);
        Assert.Equal(6u, descriptor.ViewArrayLayers);

        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);

        Assert.True(
            AgcExports.TryCreateGuestDrawTexture(
                ctx,
                descriptor,
                isStorage: false,
                mipLevel: 0,
                imageDimension: 3,
                samplerDescriptor: [],
                out var texture));
        Assert.True(texture.IsFallback);
        Assert.Equal(0UL, texture.Address);
        Assert.Equal(1u, texture.Width);
        Assert.Equal(1u, texture.Height);
        Assert.Equal(GuestImageKind.Cube, texture.ImageKind);
        Assert.Equal(6u, texture.ResourceArrayLayers);
        Assert.Equal(6u, texture.ViewArrayLayers);
        Assert.Equal(24, texture.RgbaPixels.Length);
        Assert.All(texture.RgbaPixels, value => Assert.Equal(0, value));
    }

    [Fact]
    public void UnsupportedCpuTileMode_SampledBindingPreservesGpuAliasIdentity()
    {
        var fields = Descriptor(
            address: MemoryBase,
            width: 4,
            height: 2,
            unifiedFormat: 56,
            tileMode: 27);
        Assert.True(
            AgcExports.TryDecodeTextureDescriptor(fields, out var descriptor));
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);

        Assert.True(
            AgcExports.TryCreateGuestDrawTexture(
                ctx,
                descriptor,
                isStorage: false,
                mipLevel: 0,
                imageDimension: 1,
                samplerDescriptor: [],
                out var texture));

        Assert.True(texture.IsFallback);
        Assert.False(texture.IsStorage);
        Assert.Equal(MemoryBase, texture.Address);
        Assert.Equal(4u, texture.Width);
        Assert.Equal(2u, texture.Height);
        Assert.Equal(descriptor.Format, texture.Format);
        Assert.Equal(descriptor.NumberType, texture.NumberType);
        Assert.Equal(27u, texture.TileMode);
        Assert.Equal(4u, texture.Pitch);
        Assert.Equal(descriptor.MipLevels, texture.MipLevels);
        Assert.Equal(descriptor.ResourceArrayLayers, texture.ResourceArrayLayers);
        Assert.Equal(descriptor.Depth, texture.Depth);
        Assert.Equal(GuestImageKind.Type2D, texture.ImageKind);
        Assert.Empty(texture.RgbaPixels);
    }

    [Fact]
    public void UnsupportedCpuTileMode_OversizedSampledBindingUsesSmallFallback()
    {
        var fields = Descriptor(
            address: MemoryBase,
            width: 8192,
            height: 8192,
            unifiedFormat: 56,
            tileMode: 27);
        Assert.True(
            AgcExports.TryDecodeTextureDescriptor(fields, out var descriptor));
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);

        Assert.True(
            AgcExports.TryCreateGuestDrawTexture(
                ctx,
                descriptor,
                isStorage: false,
                mipLevel: 0,
                imageDimension: 1,
                samplerDescriptor: [],
                out var texture));

        Assert.True(texture.IsFallback);
        Assert.Equal(0UL, texture.Address);
        Assert.Equal(1u, texture.Width);
        Assert.Equal(1u, texture.Height);
    }

    [Theory]
    [InlineData(24u, 22u)]
    [InlineData(27u, 36u)]
    public void UnsupportedCpuTileMode_StorageBindingUsesGpuOnlyImage(
        uint tileMode,
        uint unifiedFormat)
    {
        var fields = Descriptor(
            address: MemoryBase,
            width: 4,
            height: 2,
            unifiedFormat,
            tileMode);
        Assert.True(
            AgcExports.TryDecodeTextureDescriptor(fields, out var descriptor));
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);

        Assert.True(
            AgcExports.TryCreateGuestDrawTexture(
                ctx,
                descriptor,
                isStorage: true,
                mipLevel: 0,
                imageDimension: 1,
                samplerDescriptor: [],
                out var texture));
        Assert.Equal(MemoryBase, texture.Address);
        Assert.Equal(tileMode, texture.TileMode);
        Assert.Equal(descriptor.Format, texture.Format);
        Assert.Equal(descriptor.NumberType, texture.NumberType);
        Assert.True(texture.IsStorage);
        Assert.False(texture.IsFallback);
        Assert.Empty(texture.RgbaPixels);
    }

    [Fact]
    public void AddresslessStorageBindingIsRejected()
    {
        var fields = Descriptor(
            address: MemoryBase,
            width: 4,
            height: 2,
            unifiedFormat: 56,
            tileMode: 0);
        Assert.True(
            AgcExports.TryDecodeTextureDescriptor(fields, out var descriptor));
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);

        Assert.False(
            AgcExports.TryCreateGuestDrawTexture(
                ctx,
                descriptor with { Address = 0 },
                isStorage: true,
                mipLevel: 0,
                imageDimension: 1,
                samplerDescriptor: [],
                out _));
    }

    [Fact]
    public void SupportedStorageBindingPreservesGuestAddress()
    {
        var fields = Descriptor(
            address: MemoryBase,
            width: 4,
            height: 2,
            unifiedFormat: 56,
            tileMode: 0);
        Assert.True(
            AgcExports.TryDecodeTextureDescriptor(fields, out var descriptor));
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);

        Assert.True(
            AgcExports.TryCreateGuestDrawTexture(
                ctx,
                descriptor,
                isStorage: true,
                mipLevel: 0,
                imageDimension: 1,
                samplerDescriptor: [],
                out var texture));
        Assert.Equal(MemoryBase, texture.Address);
        Assert.True(texture.IsStorage);
        Assert.False(texture.IsFallback);
    }

    [Theory]
    [InlineData(2u, (int)GuestImageKind.Type3D, 1u)]
    [InlineData(3u, (int)GuestImageKind.Cube, 6u)]
    [InlineData(5u, (int)GuestImageKind.Type2D, 1u)]
    public void IncompatibleSampledPlaceholder_PreservesTexelInShapedProxy(
        uint imageDimension,
        int expectedKindValue,
        uint expectedSlices)
    {
        var expectedKind = (GuestImageKind)expectedKindValue;
        var fields = Descriptor(
            address: MemoryBase,
            width: 1,
            height: 1,
            unifiedFormat: 56,
            tileMode: 9);
        Assert.True(
            AgcExports.TryDecodeTextureDescriptor(fields, out var descriptor));
        var source = new byte[checked((int)Gfx10TextureLayout.BlockSizeBytes)];
        byte[] expectedTexel = [0xFF, 0x00, 0x80, 0xFF];
        expectedTexel.CopyTo(source, 0);
        var memory = new FakeCpuMemory(MemoryBase, source.Length);
        Assert.True(memory.TryWrite(MemoryBase, source));
        var ctx = new CpuContext(memory, Generation.Gen5);
        uint[] sampler = [0x0000_0692, 0x00FF_F000, 0x0600_0000, 0x4000_0000];

        Assert.True(
            AgcExports.TryCreateGuestDrawTexture(
                ctx,
                descriptor,
                isStorage: false,
                mipLevel: 0,
                imageDimension,
                samplerDescriptor: sampler,
                out var texture));
        Assert.True(texture.IsFallback);
        Assert.Equal(0UL, texture.Address);
        Assert.Equal(expectedKind, texture.ImageKind);
        Assert.Equal(expectedSlices, texture.ResourceArrayLayers);
        Assert.Equal(imageDimension == 5, texture.IsArray);
        Assert.Equal(expectedSlices, texture.ViewArrayLayers);
        Assert.Equal(sampler[0], texture.Sampler.Word0);
        Assert.Equal(sampler[1], texture.Sampler.Word1);
        Assert.Equal(sampler[2], texture.Sampler.Word2);
        Assert.Equal(sampler[3], texture.Sampler.Word3);
        var expectedPixels = Enumerable.Range(0, checked((int)expectedSlices))
            .SelectMany(_ => expectedTexel)
            .ToArray();
        Assert.Equal(expectedPixels, texture.RgbaPixels);
    }

    [Theory]
    [InlineData(2u)]
    [InlineData(3u)]
    [InlineData(5u)]
    public void StorageType9PlaceholderShapeMismatch_IsRejected(uint imageDimension)
    {
        var fields = Descriptor(
            address: MemoryBase,
            width: 1,
            height: 1,
            unifiedFormat: 56,
            tileMode: 9);
        Assert.True(
            AgcExports.TryDecodeTextureDescriptor(fields, out var descriptor));
        var memory = new FakeCpuMemory(
            MemoryBase,
            checked((int)Gfx10TextureLayout.BlockSizeBytes));
        var ctx = new CpuContext(memory, Generation.Gen5);

        Assert.False(
            AgcExports.TryCreateGuestDrawTexture(
                ctx,
                descriptor,
                isStorage: true,
                mipLevel: 0,
                imageDimension,
                samplerDescriptor: [],
                out _));
    }

    [Fact]
    public void UnsupportedTileType9PlaceholderShapeMismatch_IsRejected()
    {
        var fields = Descriptor(
            address: MemoryBase,
            width: 1,
            height: 1,
            unifiedFormat: 56,
            tileMode: 27);
        Assert.True(
            AgcExports.TryDecodeTextureDescriptor(fields, out var descriptor));
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);

        Assert.False(
            AgcExports.TryCreateGuestDrawTexture(
                ctx,
                descriptor,
                isStorage: false,
                mipLevel: 0,
                imageDimension: 5,
                samplerDescriptor: [],
                out _));
    }

    [Fact]
    public void UnreadableType9PlaceholderShapeMismatch_IsRejected()
    {
        var fields = Descriptor(
            address: 0x7000_0000,
            width: 1,
            height: 1,
            unifiedFormat: 56,
            tileMode: 9);
        Assert.True(
            AgcExports.TryDecodeTextureDescriptor(fields, out var descriptor));
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);

        Assert.False(
            AgcExports.TryCreateGuestDrawTexture(
                ctx,
                descriptor,
                isStorage: false,
                mipLevel: 0,
                imageDimension: 5,
                samplerDescriptor: [],
                out _));
    }

    [Fact]
    public void UnsupportedFormatType9PlaceholderShapeMismatch_IsRejected()
    {
        var fields = Descriptor(
            address: MemoryBase,
            width: 1,
            height: 1,
            unifiedFormat: 131,
            tileMode: 9);
        Assert.True(
            AgcExports.TryDecodeTextureDescriptor(fields, out var descriptor));
        Assert.Equal(131u, descriptor.Format);
        Assert.Equal(7u, descriptor.NumberType);
        var memory = new FakeCpuMemory(
            MemoryBase,
            checked((int)Gfx10TextureLayout.BlockSizeBytes));
        var ctx = new CpuContext(memory, Generation.Gen5);

        Assert.False(
            AgcExports.TryCreateGuestDrawTexture(
                ctx,
                descriptor,
                isStorage: false,
                mipLevel: 0,
                imageDimension: 5,
                samplerDescriptor: [],
                out _));
    }

    [Theory]
    [InlineData(2u, 1u, 0u, 0u, 0u)]
    [InlineData(1u, 2u, 0u, 0u, 0u)]
    [InlineData(1u, 1u, 1u, 1u, 1u)]
    [InlineData(1u, 1u, 0u, 1u, 1u)]
    [InlineData(1u, 1u, 0u, 0u, 1u)]
    public void NonPlaceholderType9ShapeMismatch_IsRejected(
        uint width,
        uint height,
        uint baseLevel,
        uint lastLevel,
        uint maxMip)
    {
        var fields = Descriptor(
            address: MemoryBase,
            width,
            height,
            unifiedFormat: 56,
            tileMode: 9,
            baseLevel: baseLevel,
            lastLevel: lastLevel,
            maxMip: maxMip);
        Assert.True(
            AgcExports.TryDecodeTextureDescriptor(fields, out var descriptor));
        var memory = new FakeCpuMemory(
            MemoryBase,
            checked((int)Gfx10TextureLayout.BlockSizeBytes));
        var ctx = new CpuContext(memory, Generation.Gen5);

        Assert.False(
            AgcExports.TryCreateGuestDrawTexture(
                ctx,
                descriptor,
                isStorage: false,
                mipLevel: 0,
                imageDimension: 5,
                samplerDescriptor: [],
                out _));
    }

    private static uint[] Descriptor(
        ulong address,
        uint width,
        uint height,
        uint unifiedFormat,
        uint tileMode,
        uint resourceType = 9,
        uint baseArray = 0,
        uint lastArray = 0,
        uint arrayPitch = 0,
        uint maxMip = 0,
        uint baseLevel = 0,
        uint lastLevel = 0)
    {
        Assert.InRange(width, 1u, 16384u);
        Assert.InRange(height, 1u, 16384u);
        Assert.Equal(0UL, address & 0xFF);
        var widthMinusOne = width - 1;
        var heightMinusOne = height - 1;
        return
        [
            (uint)(address >> 8),
            ((widthMinusOne & 0x3u) << 30) |
                ((unifiedFormat & 0x1FFu) << 20),
            ((widthMinusOne >> 2) & 0xFFFu) |
                ((heightMinusOne & 0x3FFFu) << 14),
            ((baseLevel & 0xFu) << 12) |
                ((lastLevel & 0xFu) << 16) |
                ((tileMode & 0x1Fu) << 20) |
                ((resourceType & 0xFu) << 28) |
                0xFACu,
            (lastArray & 0x1FFFu) | ((baseArray & 0x1FFFu) << 16),
            (arrayPitch & 0xFu) | ((maxMip & 0xFu) << 4),
            0,
            0,
        ];
    }
}
