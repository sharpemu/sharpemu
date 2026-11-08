// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

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

    [Theory]
    [InlineData(2144, 1208, 8, 102400)]
    [InlineData(512, 512, 4, 4096)]
    [InlineData(513, 512, 4, 8192)]
    public void NativeColorMetadata_UsesCompleteBlocks(uint width, uint height, uint bytes, ulong expected)
        => Assert.Equal(expected, NativeColorClear.SliceSize(width, height, bytes));
}
