// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using Xunit;

using SharpEmu.Diagnostics.Util;

namespace SharpEmu.Diagnostics.Tests;

public class RingBufferTests
{
    [Fact]
    public void Add_AndGetRecent_ReturnsLastN()
    {
        var buf = new RingBuffer<int>(5);
        for (int i = 1; i <= 10; i++)
            buf.Add(i);

        var recent = buf.GetRecent(3);
        Assert.Equal(new[] { 8, 9, 10 }, recent);
    }

    [Fact]
    public void TotalCount_TracksAllAdds()
    {
        var buf = new RingBuffer<int>(3);
        for (int i = 0; i < 100; i++)
            buf.Add(i);
        Assert.Equal(100, buf.TotalCount);
    }

    [Fact]
    public void GetRecent_WhenEmpty_ReturnsEmpty()
    {
        var buf = new RingBuffer<int>(10);
        Assert.Empty(buf.GetRecent(5));
    }

    [Fact]
    public void GetRecent_WhenFewerThanRequested_ReturnsAll()
    {
        var buf = new RingBuffer<int>(10);
        buf.Add(1);
        buf.Add(2);
        var recent = buf.GetRecent(10);
        Assert.Equal(2, recent.Length);
    }
}
