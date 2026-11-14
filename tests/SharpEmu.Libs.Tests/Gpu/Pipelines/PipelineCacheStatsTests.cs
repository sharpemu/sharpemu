// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Pipelines;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

// The profiles are measurement only: with their environment switches off every call must be inert.
public sealed class PipelineCacheStatsTests
{
    [Fact]
    public void WhenOff_NoMissIsTimedAndNothingThrows()
    {
        Assert.False(PipelineCacheStats.Enabled);
        Assert.Equal(0, PipelineCacheStats.MissBegin());
        PipelineCacheStats.Lookup(PipelineCacheStats.Kind.GraphicsPipeline, hit: false);
        PipelineCacheStats.MissEnd(PipelineCacheStats.Kind.GraphicsPipeline, 0, "ignored");
    }
}
