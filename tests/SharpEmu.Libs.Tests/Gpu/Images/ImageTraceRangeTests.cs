// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Images;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Images;

public sealed class ImageTraceRangeTests
{
    [Theory]
    [InlineData(0x1800ul, 0x1000ul, 0x2800ul)]
    [InlineData(0x1000ul, 0x1800ul, 0x2800ul)]
    public void FollowFootprint_RetainsWritesBeyondThePreviousRange(ulong address, ulong size, ulong end)
    {
        GuestSpan[] original = [new GuestSpan(0x1000, 0x1000)];
        var ranges = ImageTraceRange.IncludeFootprint(original, address, size);
        Assert.NotNull(ranges);
        Assert.Equal(new GuestSpan(0x1000, end - 0x1000), Assert.Single(ranges));
        Assert.Equal(new GuestSpan(0x1000, 0x1000), Assert.Single(original));
        var history = new ResourceAccessHistory(() => ranges);
        history.Record(Entry(0, "cpu-write-notification", 0x2400, 4));
        Assert.Single(history.Writes.Read());
    }

    [Fact]
    public void FollowFootprint_MergesAtCapacityButRejectsAnotherSeparateRange()
    {
        var ranges = Enumerable.Range(0, ImageTraceRange.MaximumRanges)
            .Select(index => new GuestSpan((ulong)(index + 1) * 0x1000, 0x100)).ToArray();
        var merged = ImageTraceRange.IncludeFootprint(ranges, 0x1080, 0x1080);
        Assert.NotNull(merged);
        Assert.Equal(ImageTraceRange.MaximumRanges - 1, merged.Length);
        Assert.Equal(new GuestSpan(0x1000, 0x1100), merged[0]);
        Assert.Null(ImageTraceRange.IncludeFootprint(ranges, 0x20000, 4));
        Assert.Same(ranges, ImageTraceRange.IncludeFootprint(ranges, 0x1000, 4));
        Assert.Same(ranges, ImageTraceRange.IncludeFootprint(ranges, ulong.MaxValue, 4));
    }

    [Fact]
    public void Parse_AcceptsSizeAndEndForms()
    {
        var ranges = ImageTraceRange.Parse(" 0x2036720000+0x200000 ; 1000-3000,", null);
        Assert.Equal(new[] { new GuestSpan(0x2036720000, 0x200000), new GuestSpan(0x1000, 0x2000) }, ranges);
    }

    [Theory]
    [InlineData("0x1000")]
    [InlineData("0x1000+0")]
    [InlineData("0x3000-0x1000")]
    [InlineData("zz+0x10")]
    [InlineData("0xFFFFFFFFFFFFFFFF+0x2")]
    public void Parse_RejectsInvalidEntriesWithWarning(string entry)
    {
        using var errors = new StringWriter();
        var ranges = ImageTraceRange.Parse($"{entry},0x4000+0x10", errors);
        Assert.Equal(new GuestSpan(0x4000, 0x10), Assert.Single(ranges));
        Assert.Contains($"'{entry}'", errors.ToString());
    }

    [Fact]
    public void Parse_LimitsRangeCount()
    {
        using var errors = new StringWriter();
        var entries = Enumerable.Range(0, ImageTraceRange.MaximumRanges + 2).Select(index => $"{index + 1:X}000+0x10");
        Assert.Equal(ImageTraceRange.MaximumRanges, ImageTraceRange.Parse(string.Join(',', entries), errors).Count);
        Assert.Contains("accepts 16 ranges", errors.ToString());
    }

    [Fact]
    public void Overlaps_UsesHalfOpenRanges()
    {
        GuestSpan[] ranges = [new GuestSpan(0x1000, 0x1000)];
        Assert.True(ImageTraceRange.Overlaps(ranges, 0x1FFF, 1));
        Assert.True(ImageTraceRange.Overlaps(ranges, 0x0800, 0x0801));
        Assert.False(ImageTraceRange.Overlaps(ranges, 0x2000, 0x10));
        Assert.False(ImageTraceRange.Overlaps(ranges, 0x0800, 0x0800));
        Assert.False(ImageTraceRange.Overlaps(ranges, 0x1800, 0));
    }

    [Fact]
    public void FilteredHistory_RetainsOnlyOverlappingEventsDuringWriteFlood()
    {
        GuestSpan[] ranges = [new GuestSpan(0x2036720000, 0x200000)];
        var history = new ResourceAccessHistory(() => ranges);
        history.Record(Entry(0, "create", 0x2036720000, 0x200000));
        for (ulong sequence = 1; sequence < 100_000; sequence++)
            history.Record(Entry(sequence, "cpu-write-notification", 0x1000_0000 + sequence * 4096, 64));
        history.Record(Entry(100_000, "cpu-write-notification", 0x2036730000, 64));
        history.Record(Entry(100_001, "color-acquire", 0x2036710000, 0x20000));

        Assert.Equal("create", Assert.Single(history.Lifecycle.Read()).Operation);
        Assert.Equal(0x2036730000ul, Assert.Single(history.Writes.Read()).Address);
        Assert.Equal(0ul, history.Writes.Dropped);
        Assert.Equal("color-acquire", Assert.Single(history.Bindings.Read()).Operation);
    }

    [Fact]
    public void ExplicitEmptyFilter_RetainsNothing()
    {
        var history = new ResourceAccessHistory(() => Array.Empty<GuestSpan>());
        history.Record(Entry(0, "create", 0x2036720000, 0x200000));
        history.Record(Entry(1, "cpu-write-notification", 0x2036730000, 64));
        Assert.Equal(0, history.Lifecycle.Count);
        Assert.Equal(0, history.Writes.Count);
    }

    [Fact]
    public void ProductionFilter_KeepsEarlierEventsBeforeTheFollowRangeIsKnown()
    {
        IReadOnlyList<GuestSpan> ranges = Array.Empty<GuestSpan>();
        var history = new ResourceAccessHistory(() => ImageTraceRange.SelectActiveFilter(ranges));
        history.Record(Entry(0, "create", 0x1000, 0x100));
        ranges = new[] { new GuestSpan(0x1000, 0x100) };
        history.Record(Entry(1, "cpu-write-notification", 0x2000, 4));
        history.Record(Entry(2, "cpu-write-notification", 0x1040, 4));
        Assert.Single(history.Lifecycle.Read());
        Assert.Equal(0x1040ul, Assert.Single(history.Writes.Read()).Address);
    }

    [Fact]
    public void ParseFollowTargets_AcceptsHashAndIndex()
    {
        using var errors = new StringWriter();
        var targets = ImageTraceRange.ParseFollowTargets("0x0F48CA4B466361E8:3; a0b9fb03e50f70cb:12, bad, 0x10:-1", errors);
        Assert.Equal(new[] { (0x0F48CA4B466361E8ul, 3), (0xA0B9FB03E50F70CBul, 12) }, targets);
        Assert.Contains("'bad'", errors.ToString());
        Assert.Contains("'0x10:-1'", errors.ToString());
        Assert.True(ImageTraceRange.IsFollowTarget(targets, 0x0F48CA4B466361E8, 3));
        Assert.False(ImageTraceRange.IsFollowTarget(targets, 0x0F48CA4B466361E8, 2));
    }

    [Fact]
    public void WriterOperations_UseSeparateRingsAndKeepDistinctSources()
    {
        var history = new ResourceAccessHistory();
        history.Record(Entry(0, "command-copy", 0x2036EE0000, 0x1000) with { Source = 0x5000 });
        history.Record(Entry(1, "command-copy", 0x2036EE0000, 0x1000) with { Source = 0x6000 });
        history.Record(Entry(2, "command-write-WriteDataValues", 0x2036EE0000, 4));
        history.Record(Entry(3, "shader-buffer-write", 0x2036EE0000, 0x200000) with { ShaderHash = 0xABCD });

        Assert.Equal(new[] { 0x5000ul, 0x6000ul, 0ul }, history.CommandWrites.Read().Select(entry => entry.Source));
        Assert.Equal(0xABCDul, Assert.Single(history.ShaderWrites.Read()).ShaderHash);
        Assert.Equal(0, history.Bindings.Count);
        Assert.Equal(0, history.GpuWrites.Count);
    }

    private static ResourceHistoryEntry Entry(ulong sequence, string operation, ulong address, ulong size)
        => new(sequence, 1, 0, 1, operation, address, size, default, default, ImageRole.Texture);
}
