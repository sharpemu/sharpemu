// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Images.ImageCacheTestSupport;

namespace SharpEmu.Libs.Tests.Gpu.Buffers;

[Collection(SchedulingStateCollection.Name)]
public sealed class GuestBufferBdaRegistrationTests : IClassFixture<HeadlessVulkanFixture>
{
    private readonly HeadlessVulkan? _vulkan;

    public GuestBufferBdaRegistrationTests(HeadlessVulkanFixture fixture) => _vulkan = fixture.Vulkan;

    // FindBuffer is the admission path used by BdaFaultProcessor's completion callback.
    // It creates an owner and publishes its address, without uploading guest bytes.
    // The next PrepareBda must upload it even with the same mappings and no CPU write.
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void NewDeviceAddressOwnerReceivesFirstUpload(bool regionAlreadyExists, bool registerAfterFirstPreparation)
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        Assert.Equal(0UL, GuestGpuMemoryHook.TraceAddress);
        using var harness = new CacheHarness(_vulkan);
        const ulong size = 0x10000;
        var address = harness.MapBacked(size, ReadWrite);
        var expected = Enumerable.Range(0, (int)size).Select(index => (byte)(37 + index * 13)).ToArray();
        harness.Write(address, expected);
        var spans = new List<GuestSpan> { new(address, size) };
        var mapping = SharpEmu.Libs.Gpu.Buffers.GuestBufferCache.MappingKey(spans);

        var buffer = harness.Worker.Run(() =>
        {
            if (regionAlreadyExists)
                Assert.True(harness.Cache.HasCpuDirtyPages(address, size));

            if (registerAfterFirstPreparation)
                harness.Cache.PrepareBda(spans, mapping);

            var owner = harness.Cache.GetBuffer(harness.Cache.FindBuffer(address, size));
            Assert.True(harness.Cache.IsRegionRegistered(address, size));
            harness.Cache.PrepareBda(spans, mapping);

            // Query only after the preparation under test. Before registration, this query
            // would create a missing tracker region and itself advance CpuDirtyEpoch.
            Assert.False(harness.Cache.HasCpuDirtyPages(address, size));
            Assert.False(harness.Cache.HasGpuDirtyPages(address, size));
            return owner;
        });

        Assert.Equal(expected, harness.ReadBack(buffer, buffer.Offset(address), size));
        harness.Shutdown();
    }
}
