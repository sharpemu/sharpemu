// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcImageSamplerTests
{
    private static readonly uint[] CompareSampler =
        [0x00006092, 0x00FFF000, 0x06500000, 0x00000000];

    [Fact]
    public void ImageOperationDisablesNativeSamplerComparison()
    {
        var normalized = AgcExports.NormalizeSamplerDescriptorForImageOperation(
            CompareSampler);

        Assert.Equal(0u, normalized[0] & (0x7u << 12));
        Assert.Equal(CompareSampler[0] & ~(0x7u << 12), normalized[0]);
        Assert.Equal(CompareSampler.AsSpan(1).ToArray(), normalized.Skip(1));
    }

    [Fact]
    public void SamplerWithoutComparisonIsNotCopied()
    {
        uint[] sampler = [0x00000012, 0x00FFF000, 0x06500000, 0];

        var normalized = AgcExports.NormalizeSamplerDescriptorForImageOperation(
            sampler);

        Assert.Same(sampler, normalized);
    }
}
