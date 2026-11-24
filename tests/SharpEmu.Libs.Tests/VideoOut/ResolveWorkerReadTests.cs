// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

[Collection(SchedulingStateCollection.Name)]
public sealed class ResolveWorkerReadTests : IClassFixture<HeadlessVulkanFixture>
{
    private readonly HeadlessVulkan? _vulkan;

    public ResolveWorkerReadTests(HeadlessVulkanFixture fixture) => _vulkan = fixture.Vulkan;

    // A compute ticket's take checks again only the reads its worker logged, so a read the worker cannot serve refuses the
    // ticket; a draw's worker still reports the failed read to the resource materializer.
    [Fact]
    public void AReadAWorkerCannotServeRefusesATicketButNotADraw()
    {
        if (_vulkan is null || !OperatingSystem.IsWindows())
            return;

        using var presenter = new PresenterUnderTest(_vulkan);
        var (first, granule, _) = presenter.Harness.MapBackedSandwich();
        var hole = first + granule;
        var host = (ICommandStreamHost)presenter.Instance;
        var reader = (IShaderPipelineHost)presenter.Instance;

        var drawReads = new ResolveReadLog();
        host.BeginWorkerResolve(5, drawReads);
        try
        {
            Assert.False(reader.TryReadGuestWord(hole, out _));
        }
        finally
        {
            host.EndWorkerResolve();
        }

        var ticketReads = new ResolveReadLog();
        host.BeginWorkerResolve(long.MaxValue, ticketReads);
        try
        {
            var refusal = Assert.Throws<SerialResolveRequiredException>(() => reader.TryReadGuestWord(hole, out _));
            Assert.Equal("ticket read unreadable", refusal.Message);
        }
        finally
        {
            host.EndWorkerResolve();
        }

        Assert.Equal(0, drawReads.Count);
        Assert.Equal(0, ticketReads.Count);
    }
}
