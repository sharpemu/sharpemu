// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using System.Text.RegularExpressions;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

[CollectionDefinition("ResourceMaterializationProfile", DisableParallelization = true)]
public sealed class ResourceMaterializationProfileCollection;

[Collection("ResourceMaterializationProfile")]
public sealed class ResourceMaterializationProfileTests
{
    [Theory]
    [InlineData(null, null, false)]
    [InlineData("1", null, false)]
    [InlineData(null, "1", false)]
    [InlineData("1", "0", false)]
    [InlineData("0", "1", false)]
    [InlineData("1", "1", true)]
    public void DetailedMeasurementsRequireBothSwitches(string? performance, string? frameTrace, bool expected)
        => Assert.Equal(expected, ResourceMaterializationProfile.IsEnabled(performance, frameTrace));

    [Fact]
    public void ScopeRecordsAllocationAndExceptionalExitWhenEnabled()
    {
        var before = ReadReport();
        Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            using var measurement = ResourceMaterializationProfile.Measure(ResourceMaterializationProfile.Phase.Total);
            GC.KeepAlive(new byte[4096]);
            throw new InvalidOperationException();
        }));
        var after = ReadReport();
        if (!ResourceMaterializationProfile.Enabled)
        {
            Assert.Empty(after);
            return;
        }
        Assert.Equal(ReadTotalCalls(before) + 1, ReadTotalCalls(after));
        Assert.True(ReadCounter(after, "allocated_bytes") - ReadCounter(before, "allocated_bytes") >= 4096);
        default(ResourceMaterializationProfile.Scope).Dispose();
        Assert.Equal(after, ReadReport());
    }

    [Fact]
    public void ActivityCountsSourcesWithoutChangingThem()
    {
        bool[] activeSources = [true, false, false, true];
        var before = ReadReport();
        ResourceMaterializationProfile.RecordActivity(activeSources);
        var after = ReadReport();
        Assert.Equal(new[] { true, false, false, true }, activeSources);
        if (!ResourceMaterializationProfile.Enabled)
        {
            Assert.Empty(after);
            return;
        }
        Assert.Equal(ReadCounter(before, "branch_evaluations") + 1, ReadCounter(after, "branch_evaluations"));
        Assert.Equal(ReadCounter(before, "descriptor_sources") + 4, ReadCounter(after, "descriptor_sources"));
        Assert.Equal(ReadCounter(before, "inactive_sources") + 2, ReadCounter(after, "inactive_sources"));
    }

    [Fact]
    public void ConcurrentScopesDoNotLoseCalls()
    {
        var before = ReadReport();
        Parallel.For(0, 1000, _ =>
        {
            using var measurement = ResourceMaterializationProfile.Measure(ResourceMaterializationProfile.Phase.Total);
        });
        var after = ReadReport();
        if (!ResourceMaterializationProfile.Enabled)
        {
            Assert.Empty(after);
            return;
        }
        Assert.Equal(ReadTotalCalls(before) + 1000, ReadTotalCalls(after));
    }

    [Fact]
    public void ReportUsesInvariantDecimalSeparators()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var report = ReadReport();
            if (ResourceMaterializationProfile.Enabled)
                Assert.Matches(@"Total=\d+\.\d{3}ms/n\d+", report);
            else
                Assert.Empty(report);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    private static string ReadReport()
    {
        using var output = new StringWriter();
        ResourceMaterializationProfile.WriteReport(output);
        return output.ToString();
    }

    private static long ReadTotalCalls(string report) =>
        long.Parse(Regex.Match(report, @"Total=[\d.]+ms/n(\d+)").Groups[1].Value, CultureInfo.InvariantCulture);

    private static long ReadCounter(string report, string name) =>
        long.Parse(Regex.Match(report, $@"\b{name}=(\d+)").Groups[1].Value, CultureInfo.InvariantCulture);
}
