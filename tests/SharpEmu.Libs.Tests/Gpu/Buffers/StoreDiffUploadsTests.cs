// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Buffers;

[Collection(SchedulingStateCollection.Name)]
public sealed class StoreDiffUploadsTests : IClassFixture<HeadlessVulkanFixture>
{
    private const ulong Page = GuestBufferCache.CachingPageSize;
    private const GuestPageProtection ReadWrite = GuestPageProtection.Read | GuestPageProtection.Write;

    private readonly HeadlessVulkan? _vulkan;

    public StoreDiffUploadsTests(HeadlessVulkanFixture fixture) => _vulkan = fixture.Vulkan;

    private static List<(int Offset, int Length)> Runs(byte[] shadow, byte[] current)
    {
        var runs = new List<(int Offset, int Length)>();
        StoreDiffUploads.ComputeRuns(shadow, current, runs);
        return runs;
    }

    [Fact]
    public void AnIdenticalPageHasNoRuns() =>
        Assert.Empty(Runs(new byte[4096], new byte[4096]));

    [Fact]
    public void OneChangedDwordIsOneAlignedRun()
    {
        var current = new byte[4096];
        current[0x105] = 7;
        Assert.Equal(new (int Offset, int Length)[] { (0x100, 8) }, Runs(new byte[4096], current));
    }

    [Fact]
    public void ChangesUnderTheGapMerge_AndFartherOnesStaySeparate()
    {
        var near = new byte[4096];
        near[0x100] = 1;
        near[0x128] = 1;
        Assert.Equal(new (int Offset, int Length)[] { (0x100, 0x30) }, Runs(new byte[4096], near));

        var far = new byte[4096];
        far[0x100] = 1;
        far[0x100 + 200] = 1;
        Assert.Equal(new (int Offset, int Length)[] { (0x100, 8), (0x100 + 200 & ~7, 8) }, Runs(new byte[4096], far));
    }

    [Fact]
    public void ChangesAtTheFirstAndLastBytesAreRuns()
    {
        var current = new byte[4096];
        current[0] = 1;
        current[4095] = 1;
        Assert.Equal(new (int Offset, int Length)[] { (0, 8), (4088, 8) }, Runs(new byte[4096], current));
    }

    // A page reported as stored keeps the shader's bytes through a later upload: only the dword the CPU changed
    // reaches the buffer.
    [Fact]
    public void AnUploadOfAReportedPageSendsOnlyTheCpuChange()
    {
        if (_vulkan is null) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        var (buffer, offset) = harness.Worker.Run(() => harness.Cache.ObtainBuffer(address, 2 * Page, isWritten: false));

        // The CPU wrote the page, then the report arrives: the page stays with the CPU and is tracked.
        harness.Write(address + 0x100, [1, 1, 1, 1]);
        harness.Cache.InvalidateMemory(address + 0x100, 4);
        harness.Cache.NoteBufferStores([address], Page);
        Assert.True(harness.Cache.TrackedPages >= 1);

        // The first upload of the tracked page is whole and makes the shadow.
        harness.Worker.Run(() => harness.Cache.ObtainBuffer(address, 2 * Page, isWritten: false));
        Assert.Equal(1, harness.Cache.ShadowPages);

        // A shader stores into the buffer; the CPU changes another dword of the same page.
        harness.Worker.Run(() =>
        {
            var staging = harness.Cache.GetUtilityBuffer(GpuBufferUsage.Upload);
            var source = staging.Copy([0x78, 0x56, 0x34, 0x12], 4);
            buffer.CopyFrom(harness.Scheduler.Current, staging, source, offset + 0x200, 4, Silk.NET.Vulkan.AccessFlags.HostWriteBit);
        });
        harness.Write(address + 0x100, [2, 2, 2, 2]);
        harness.Cache.InvalidateMemory(address + 0x100, 4);
        harness.Worker.Run(() => harness.Cache.ObtainBuffer(address, 2 * Page, isWritten: false));

        Assert.Equal(new byte[] { 2, 2, 2, 2 }, harness.ReadBack(buffer, offset + 0x100, 4));
        Assert.Equal(new byte[] { 0x78, 0x56, 0x34, 0x12 }, harness.ReadBack(buffer, offset + 0x200, 4));
        Assert.Equal(1, harness.Cache.DiffPages);
        Assert.Equal(8, harness.Cache.DiffBytes);
        harness.Shutdown();
    }
}

