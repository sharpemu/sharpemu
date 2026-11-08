// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.RegularExpressions;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

[CollectionDefinition("DescriptorStallProfile", DisableParallelization = true)]
public sealed class DescriptorStallProfileCollection;

[Collection("DescriptorStallProfile")]
public sealed class DescriptorStallProfileTests
{
    [Fact]
    public void ExceptionalExitIsCountedOnlyWithFrameTrace()
    {
        static string Report()
        {
            using var output = new StringWriter();
            DescriptorStallProfile.WriteReport(output);
            return output.ToString();
        }
        var before = Report();
        Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            using var scope = DescriptorStallProfile.Measure(DescriptorStallProfile.Operation.Prepare, 123, 1, 2, 3);
            throw new InvalidOperationException();
        }));
        var after = Report();
        if (!RenderPhaseProfile.FrameTraceEnabled)
        {
            Assert.Empty(after);
            return;
        }
        static long Calls(string report) => long.Parse(Regex.Match(report, @"Prepare=[\d.]+ms/n(\d+)").Groups[1].Value);
        Assert.Equal(Calls(before) + 1, Calls(after));
        default(DescriptorStallProfile.Scope).Dispose();
        Assert.Equal(after, Report());
    }
}
