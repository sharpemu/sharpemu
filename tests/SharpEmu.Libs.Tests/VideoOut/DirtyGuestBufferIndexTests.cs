// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class DirtyGuestBufferIndexTests
{
    [Fact]
    public void CopyCandidatesFiltersByQueue()
    {
        var index = new DirtyGuestBufferIndex<object>();
        var graphics = new object();
        var compute = new object();
        var candidates = new List<object>();

        index.Mark(graphics, "graphics");
        index.Mark(compute, "compute");

        index.CopyCandidates("graphics", candidates);

        Assert.Equal([graphics], candidates);
    }

    [Fact]
    public void RepeatedMarksDoNotDuplicateCandidates()
    {
        var index = new DirtyGuestBufferIndex<object>();
        var allocation = new object();
        var candidates = new List<object>();

        index.Mark(allocation, "compute");
        index.Mark(allocation, "compute");
        index.CopyCandidates("compute", candidates);

        Assert.Equal([allocation], candidates);
        Assert.Equal(1, index.AllocationCount);
        Assert.Equal(1, index.QueueCount);
    }

    [Fact]
    public void RemovingOneQueuePreservesOtherQueueMembership()
    {
        var index = new DirtyGuestBufferIndex<object>();
        var allocation = new object();
        var candidates = new List<object>();

        index.Mark(allocation, "graphics");
        index.Mark(allocation, "compute");
        index.Remove(allocation, "graphics");

        index.CopyCandidates("graphics", candidates);
        Assert.Empty(candidates);
        index.CopyCandidates("compute", candidates);
        Assert.Equal([allocation], candidates);
        Assert.Equal(1, index.AllocationCount);
    }

    [Fact]
    public void RemovingAllocationClearsAllQueueMembership()
    {
        var index = new DirtyGuestBufferIndex<object>();
        var allocation = new object();
        var candidates = new List<object>();

        index.Mark(allocation, "graphics");
        index.Mark(allocation, "compute");
        index.Remove(allocation);

        index.CopyCandidates(null, candidates);
        Assert.Empty(candidates);
        Assert.Equal(0, index.AllocationCount);
        Assert.Equal(0, index.QueueCount);
    }
}
