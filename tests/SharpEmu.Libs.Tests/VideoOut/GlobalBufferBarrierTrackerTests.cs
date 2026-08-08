// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class GlobalBufferBarrierTrackerTests
{
    [Fact]
    public void ReadOnlyWorkDoesNotRequireBarrierWhenElisionIsEnabled()
    {
        var tracker = new GlobalBufferBarrierTracker();

        tracker.MarkShaderWrites(writesGlobalMemory: false);

        Assert.False(tracker.ShouldRecordBarrier(elideRedundantBarriers: true));
    }

    [Fact]
    public void ShaderWriteRequiresExactlyOneBarrier()
    {
        var tracker = new GlobalBufferBarrierTracker();
        tracker.MarkShaderWrites(writesGlobalMemory: true);

        Assert.True(tracker.ShouldRecordBarrier(elideRedundantBarriers: true));
        tracker.BarrierRecorded(elideRedundantBarriers: true);

        Assert.False(tracker.ShouldRecordBarrier(elideRedundantBarriers: true));
    }

    [Fact]
    public void RepeatedWritesRemainPendingUntilBarrierIsRecorded()
    {
        var tracker = new GlobalBufferBarrierTracker();
        tracker.MarkShaderWrites(writesGlobalMemory: true);
        tracker.MarkShaderWrites(writesGlobalMemory: true);

        Assert.True(tracker.ShouldRecordBarrier(elideRedundantBarriers: true));
        tracker.BarrierRecorded(elideRedundantBarriers: true);

        Assert.False(tracker.ShouldRecordBarrier(elideRedundantBarriers: true));
    }

    [Fact]
    public void DisabledElisionAlwaysRecordsBarrier()
    {
        var tracker = new GlobalBufferBarrierTracker();

        Assert.True(tracker.ShouldRecordBarrier(elideRedundantBarriers: false));
        tracker.BarrierRecorded(elideRedundantBarriers: false);
        Assert.True(tracker.ShouldRecordBarrier(elideRedundantBarriers: false));
    }
}
