// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Images;

[Collection(SchedulingStateCollection.Name)]
public sealed class SamplerStoreTests : IClassFixture<HeadlessVulkanFixture>
{
    private readonly HeadlessVulkan? _vulkan;

    public SamplerStoreTests(HeadlessVulkanFixture fixture) => _vulkan = fixture.Vulkan;

    private static SamplerDescriptorWords Words(uint word0, uint word1 = 0, uint word2 = 0, uint word3 = 0) => new([word0, word1, word2, word3]);

    [Fact]
    public void Accessors_DecodeTheDescriptorFields()
    {
        var words = Words(1 | (2u << 3) | (3u << 6) | (3u << 9) | (5u << 12) | (1u << 15), 0x00ABC123, (1u << 26) | (2u << 22) | (3u << 20) | 0x1FFF, (2u << 30) | 7);
        Assert.Equal((1u, 2u, 3u), (words.ClampX, words.ClampY, words.ClampZ));
        Assert.Equal(3u, words.MaxAnisotropyRatio);
        Assert.Equal(5u, words.DepthCompareFunction);
        Assert.True(words.ForceUnnormalizedCoordinates);
        Assert.Equal((0x123u, 0xABCu), (words.MinLod, words.MaxLod));
        Assert.Equal((0x1FFFu, 3u, 2u, 1u), (words.LodBias, words.MagnifyFilter, words.MinifyFilter, words.MipFilter));
        Assert.Equal((7u, 2u), (words.BorderColorIndex, words.BorderColorType));
    }

    [Fact]
    public void GetSampler_CachesByTheFourWords()
    {
        if (!GatePrerequisites.Ready(_vulkan, samplerAnisotropy: true)) return;
        using var fatal = new FatalScope();
        using var store = new SamplerStore(_vulkan.DeviceInfo);
        var linear = Words(0, 0, (1u << 22) | (1u << 20));
        var first = store.GetSampler(linear);
        Assert.NotEqual(0UL, first.Handle);
        Assert.Equal(first, store.GetSampler(linear));
        Assert.Equal(1, store.Count);

        var anisotropic = Words(2u << 9, 0, (3u << 22) | (3u << 20) | (2u << 26));
        Assert.NotEqual(first, store.GetSampler(anisotropic));
        var unnormalized = Words(1u << 15, 0, (1u << 22) | (1u << 20) | (2u << 26));
        Assert.NotEqual(first, store.GetSampler(unnormalized));
        var tableBorder = Words(4, 0, 0, (3u << 30) | 5);
        Assert.NotEqual(0UL, store.GetSampler(tableBorder).Handle);
        Assert.Equal(4, store.Count);

        Assert.Throws<SchedulerFatalException>(() => store.GetSampler(Words(5u << 9, 0, (3u << 22) | (3u << 20))));
        Assert.Contains(fatal.Messages, message => message.Contains("anisotropy ratio is unknown"));
        Assert.Equal(4, store.Count);
        _vulkan.AssertNoValidationMessages();
    }
}
