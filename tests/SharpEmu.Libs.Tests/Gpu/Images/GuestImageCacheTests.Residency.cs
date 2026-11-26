// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Buffers;
using Silk.NET.Vulkan;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Images.ImageCacheTestSupport;
using Band = SharpEmu.Libs.Gpu.Buffers.GpuResidency.Band;
using Role = SharpEmu.Libs.Gpu.Buffers.GpuResidency.Role;

namespace SharpEmu.Libs.Tests.Gpu.Images;

public sealed partial class GuestImageCacheTests
{
    [Fact]
    public void ResidencyRestoresColdImagesOnUseAndIgnoresRetiredHandles()
    {
        var calls = new List<(ulong, float)>();
        var residency = new GpuResidency((memory, value) => calls.Add((memory.Handle, value)));
        residency.Advance(0);
        var texture = residency.Add(new DeviceMemory(1), Role.Texture, Band.Default);
        var shared = residency.Add(new DeviceMemory(2), Role.Slab, Band.Default);
        var staging = residency.Add(new DeviceMemory(3), Role.Upload, Band.Default);
        texture.Use(Role.Depth);
        residency.Advance(1);
        Assert.Equal(Band.High, texture.Applied);
        Assert.Equal(Band.High, shared.Applied);
        Assert.Equal(Band.Low, staging.Applied);
        residency.Advance(128);
        Assert.Equal(Band.Low, texture.Applied);
        Assert.Equal(Band.Default, shared.Applied);
        texture.Use(Role.Texture);
        Assert.Equal(Band.Default, texture.Applied);
        texture.Use(Role.WrittenImage);
        Assert.Equal(Band.High, texture.Applied);
        shared.Use(Role.Slab);
        Assert.Equal(Band.High, shared.Applied);
        calls.Clear();
        shared.Use(Role.Slab);
        texture.Use(Role.WrittenImage);
        Assert.Empty(calls);
        texture.Use(Role.IdleImage);
        Assert.Equal(Band.Low, texture.Applied);
        residency.Remove(texture.Memory);
        var replacement = residency.Add(texture.Memory, Role.Color, Band.Default);
        calls.Clear();
        texture.Use(Role.Texture);
        Assert.Empty(calls);
        Assert.Equal(Band.High, replacement.Applied);
    }

    [Fact]
    public void ResidencyUsesTheTighterCompleteBudgetSample()
    {
        (ulong, ulong) vk = (19400UL << 20, 8000UL << 20);
        (ulong, ulong) dxgi = (7000UL << 20, 6770UL << 20);
        Assert.Equal(dxgi, GuestImageCache.SelectBudget(vk, dxgi));
        Assert.Equal(vk, GuestImageCache.SelectBudget(vk, default));
        Assert.Equal(dxgi, GuestImageCache.SelectBudget(default, dxgi));
    }

    [Fact]
    public void ResidencyTrimsCompletedImageBackingAndRaisesItOnReuse()
    {
        if (!GatePrerequisites.Ready(_vulkan) || !ImageBackingPool.Enabled) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        var request = Color32(address);
        var id = harness.Acquire(ref request);
        var image = harness.Image(id);
        var entry = image.Backing.Residency;
        var heap = image.Backing.MemoryHeap;
        harness.Worker.Run(() => harness.Images.DeleteImageForTest(id));
        harness.Finish();
        if (entry is not null) Assert.Equal(Role.IdleImage, entry.Kind);
        if (_vulkan.DeviceInfo.Residency is not null)
            Assert.Equal(Band.Low, entry!.Applied);
        var next = harness.Acquire(ref request);
        Assert.Same(entry, harness.Image(next).Backing.Residency);
        if (entry is not null) Assert.Equal(Role.Texture, entry.Kind);
        if (entry is not null) Assert.Equal(Band.Default, entry.Applied);
        harness.Worker.Run(() => harness.Images.DeleteImageForTest(next));
        harness.Finish();
        _vulkan.AssertNoValidationMessages();
    }

    [Fact]
    public void ResidencyPromotesTheAllocationBackingAWrittenCacheRange()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        harness.Write(address, [7]);
        harness.Worker.Run(() =>
        {
            var stream = harness.Cache.ObtainBuffer(address, 128, isWritten: false).Buffer;
            Assert.Same(harness.Cache.GetUtilityBuffer(GpuBufferUsage.Stream), stream);
            if (stream.Residency is { } localStream)
            {
                Assert.Equal(Role.BoundBuffer, localStream.Kind);
                Assert.Equal(Band.High, localStream.Applied);
            }
            var buffer = harness.Cache.ObtainBuffer(address, 4096, isWritten: true).Buffer;
            harness.Images.RunGarbageCollector(endsFrame: false);
            var entry = buffer.Residency;
            if (entry is null)
            {
                Assert.True(_vulkan.DeviceInfo.Residency is null || !_vulkan.DeviceInfo.IsDeviceLocalHeap(_vulkan.DeviceInfo.GetMemoryHeapIndex(buffer.MemoryTypeIndex)));
                return;
            }
            Assert.True(entry.Kind is Role.Slab or Role.BoundBuffer);
            Assert.Equal(Band.High, entry.Applied);
        });
        harness.Finish();
        _vulkan.AssertNoValidationMessages();
    }

    [Fact]
    public void ResidencyReadsBudgetsForTheSelectedDevice()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        var device = _vulkan.DeviceInfo;
        var vk = device.ReadLocalBudget();
        var dxgi = device.DxgiBudget?.Read() ?? default;
        if (device.MemoryBudgetSupported && device.LocalBudgetHeap != uint.MaxValue) Assert.NotEqual(0UL, vk.Budget);
        if (device.DxgiBudget is not null) Assert.NotEqual(0UL, dxgi.Budget);
    }
}
