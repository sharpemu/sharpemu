// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Images.ImageCacheTestSupport;

namespace SharpEmu.Libs.Tests.Gpu.Buffers;

// A device-address program that reaches only its handles' windows sweeps the mapped spans inside them.
[Collection(SchedulingStateCollection.Name)]
public sealed class GuestBufferWindowSweepTests : IClassFixture<HeadlessVulkanFixture>
{
    private readonly HeadlessVulkan? _vulkan;

    public GuestBufferWindowSweepTests(HeadlessVulkanFixture fixture) => _vulkan = fixture.Vulkan;

    [Fact]
    public void TheMappedSpansAreCutToTheMergedWindows()
    {
        var spans = new List<GuestSpan> { new(0x1000, 0x3000), new(0x10000, 0x2000), new(0x20000, 0x1000) };
        var windows = new List<(ulong Start, ulong End)> { (0x11000, 0x18000), (0x2000, 0x3000), (0x2800, 0x11800) };
        var clipped = new List<GuestSpan>();

        GuestBufferCache.ClipSpansToWindows(spans, windows, clipped);

        Assert.Equal([new GuestSpan(0x2000, 0x2000), new GuestSpan(0x10000, 0x2000)], clipped);
        Assert.Equal([(0x2000ul, 0x18000ul)], windows);
    }

    [Fact]
    public void NoWindowsLeaveNothingToSweep()
    {
        var clipped = new List<GuestSpan> { new(1, 1) };

        GuestBufferCache.ClipSpansToWindows([new GuestSpan(0x1000, 0x1000)], [], clipped);

        Assert.Empty(clipped);
    }

    // A windowed preparation uploads the dirty pages inside its windows and leaves the others dirty for a later sweep.
    [Fact]
    public void AWindowedPreparationSweepsOnlyItsWindows()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x40000, ReadWrite);
        var (first, firstOffset) = harness.Worker.Run(() => harness.Cache.ObtainBuffer(address, 0x8000, isWritten: false));
        var (second, secondOffset) = harness.Worker.Run(() => harness.Cache.ObtainBuffer(address + 0x20000, 0x8000, isWritten: false));
        var spans = new List<GuestSpan> { new(address, 0x40000) };
        var mapping = GuestBufferCache.MappingKey(spans);
        harness.Worker.Run(() => harness.Cache.PrepareBda(spans, mapping));

        Assert.True(harness.Store.MarkCpuWrite(address + 0x1000, 8));
        harness.Write(address + 0x1000, Pattern(0x20, 1));
        Assert.True(harness.Store.MarkCpuWrite(address + 0x21000, 8));
        harness.Write(address + 0x21000, Pattern(0x20, 2));
        var window = new List<GuestSpan> { new(address, 0x10000) };
        harness.Worker.Run(() => harness.Cache.PrepareWindowedBda(window, GuestBufferCache.MappingKey(window), mapping));

        Assert.Equal(Pattern(0x20, 1), harness.ReadBack(first, firstOffset + 0x1000, 0x20));
        Assert.False(harness.Cache.HasCpuDirtyPages(address + 0x1000, 0x20));
        Assert.True(harness.Cache.HasCpuDirtyPages(address + 0x21000, 0x20));
        Assert.NotEqual(Pattern(0x20, 2), harness.ReadBack(second, secondOffset + 0x1000, 0x20));

        harness.Worker.Run(() => harness.Cache.PrepareBda(spans, mapping));
        Assert.Equal(Pattern(0x20, 2), harness.ReadBack(second, secondOffset + 0x1000, 0x20));
        Assert.False(harness.Cache.HasCpuDirtyPages(address + 0x21000, 0x20));
        harness.Shutdown();
    }

    // The window's own state: a second windowed preparation with nothing new written sweeps nothing, and a write inside the
    // window is taken by the next one.
    [Fact]
    public void AWindowKeepsItsOwnSweepState()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x40000, ReadWrite);
        var (first, firstOffset) = harness.Worker.Run(() => harness.Cache.ObtainBuffer(address, 0x8000, isWritten: false));
        var spans = new List<GuestSpan> { new(address, 0x40000) };
        var mapping = GuestBufferCache.MappingKey(spans);
        var window = new List<GuestSpan> { new(address, 0x10000) };
        var windowMapping = GuestBufferCache.MappingKey(window);
        harness.Worker.Run(() => harness.Cache.PrepareWindowedBda(window, windowMapping, mapping));
        var sweeps = Interlocked.Read(ref GuestBufferCache.BdaSweeps);
        harness.Worker.Run(() => harness.Cache.PrepareWindowedBda(window, windowMapping, mapping));
        Assert.Equal(sweeps, Interlocked.Read(ref GuestBufferCache.BdaSweeps));

        Assert.True(harness.Store.MarkCpuWrite(address + 0x2000, 8));
        harness.Write(address + 0x2000, Pattern(0x20, 3));
        harness.Worker.Run(() => harness.Cache.PrepareWindowedBda(window, windowMapping, mapping));
        Assert.Equal(Pattern(0x20, 3), harness.ReadBack(first, firstOffset + 0x2000, 0x20));
        harness.Shutdown();
    }

    private static byte[] Pattern(int size, byte seed) => Enumerable.Range(0, size).Select(index => (byte)(seed * 37 + index)).ToArray();
}
