// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using SharpEmu.Libs.VideoOut;
using SharpEmu.ShaderCompiler.Vulkan;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanStorageImageContractTests
{
    [Theory]
    [InlineData(0u, 8u)]
    [InlineData(1u, 9u)]
    [InlineData(2u, 10u)]
    [InlineData(3u, 11u)]
    [InlineData(4u, 12u)]
    [InlineData(5u, 13u)]
    [InlineData(6u, 9u)]
    [InlineData(7u, 13u)]
    public void InvalidDescriptorFallbackPreservesInstructionDimension(
        uint instructionDimension,
        uint expectedTextureType)
    {
        Assert.Equal(
            expectedTextureType,
            AgcExports.GetFallbackTextureType(instructionDimension));
    }

    [Fact]
    public void StorageContractRejectsDimensionMismatch()
    {
        var contract = new VulkanVideoPresenter.SpirvStorageImageContract(
            SpirvImageFormat.Rgba8,
            VulkanVideoPresenter.StorageImageComponentKind.Float,
            SpirvImageDim.Dim3D);

        Assert.True(VulkanVideoPresenter.TryValidateStorageImageContract(
            contract,
            guestFormat: 10,
            guestNumberType: 0,
            guestType: VulkanVideoPresenter.Gen5TextureType3D,
            supportsStorage: true,
            out var format,
            out var matchingError));
        Assert.Equal(Format.R8G8B8A8Unorm, format);
        Assert.Empty(matchingError);

        Assert.False(VulkanVideoPresenter.TryValidateStorageImageContract(
            contract,
            guestFormat: 10,
            guestNumberType: 0,
            guestType: VulkanVideoPresenter.Gen5TextureType2D,
            supportsStorage: true,
            out _,
            out var mismatchError));
        Assert.Contains("dimension-mismatch", mismatchError, StringComparison.Ordinal);
    }
}
