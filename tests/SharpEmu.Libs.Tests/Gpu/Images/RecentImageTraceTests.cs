// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Images;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Images;

public sealed class RecentImageTraceTests
{
    [Fact]
    public void Record_RetainsRecentStatesAfterCapacityIsReached()
    {
        var trace = new RecentImageTrace(2);
        trace.Record("first", "old first");
        trace.Record("second", "second state");
        trace.Record("first", "updated first");
        trace.Record("third", "late scene");
        using var output = new StringWriter();
        trace.WriteTo(output, "test");
        var text = output.ToString();
        Assert.Contains("recent=2 evicted=1", text);
        Assert.DoesNotContain("old first", text);
        Assert.DoesNotContain("second state", text);
        Assert.True(text.IndexOf("updated first", StringComparison.Ordinal) <
            text.IndexOf("late scene", StringComparison.Ordinal));
    }
}
