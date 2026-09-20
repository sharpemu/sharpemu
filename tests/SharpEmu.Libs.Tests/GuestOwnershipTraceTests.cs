// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class GuestOwnershipTraceTests
{
    [Theory]
    [InlineData("[null]")]
    [InlineData("[{\"Caller\":null,\"Import\":\"free\",\"Name\":\"release\"}]")]
    [InlineData("[{\"Caller\":\"100\",\"Import\":\"free\",\"Name\":\"release\",\"Regions\":null}]")]
    [InlineData("[{\"Caller\":\"100\",\"Import\":\"free\",\"Name\":\"release\",\"Regions\":[null]}]")]
    [InlineData("[{\"Caller\":\"100\",\"Import\":\"free\",\"Name\":\"release\",\"Regions\":[{\"Name\":\"object\",\"Address\":null}]}]")]
    public void RejectsInvalidPlanStructureBeforeCapture(string plan)
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, plan);
            Assert.Throws<ArgumentException>(() => GuestOwnershipTrace.Load(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void DocumentedPlanLoadsAndCapturesExpectedRegions()
    {
        using var resource = typeof(GuestOwnershipTraceTests).Assembly.GetManifestResourceStream("GuestOwnershipTraceGuide");
        Assert.NotNull(resource);
        using var reader = new StreamReader(resource);
        var guide = reader.ReadToEnd();
        var start = guide.IndexOf("```json", StringComparison.Ordinal);
        Assert.True(start >= 0);
        start += "```json".Length;
        var end = guide.IndexOf("```", start, StringComparison.Ordinal);
        Assert.True(end > start);
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, guide[start..end]);
            var trace = GuestOwnershipTrace.Load(path);
            Assert.True(trace.ShouldCapture("free", 0x100, false));
            Assert.False(trace.ShouldCapture("free", 0x100, true));
            var context = new CpuContext(new FakeCpuMemory(0x1000, 0x1000), Generation.Gen5);
            Assert.True(context.TryWriteUInt64(0x1108, 0x1200));
            trace.Record(context, "free", 0x100, false, 0, 7, new() { ["arg0"] = 0x1100 });
            using var output = new StringWriter();
            trace.Write(output);
            Assert.Contains("name=object address=0x0000000000001100 requested=64 readable=True", output.ToString());
            Assert.Contains("name=linked-object address=0x0000000000001200 requested=32 readable=True", output.ToString());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void LoadsConfiguredCapturePoints()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """[{"Caller":"100","Import":"free","Name":"release","Regions":[]}]""");
            var trace = GuestOwnershipTrace.Load(path);
            Assert.True(trace.IncludesCaller(0x100));
            Assert.True(trace.IncludesImport("free"));
            Assert.True(trace.ShouldCapture("free", 0x100, false));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("r12/8+10", true, 0x1210UL)]
    [InlineData("r12+20/0", true, 0x1300UL)]
    [InlineData("rbx*8", true, 16UL)]
    [InlineData("r12-10", true, 0x10F0UL)]
    [InlineData("0-1", false, 0UL)]
    [InlineData("FFFFFFFFFFFFFFFF+1", false, 0UL)]
    [InlineData("FFFFFFFFFFFFFFFF*2", false, 0UL)]
    [InlineData("r12/10000", false, 0UL)]
    [InlineData("unknown", false, 0UL)]
    public void EvaluatesCheckedAddresses(string expression, bool expected, ulong expectedValue)
    {
        var context = new CpuContext(new FakeCpuMemory(0x1000, 0x1000), Generation.Gen5);
        context.TryWriteUInt64(0x1108, 0x1200);
        context.TryWriteUInt64(0x1120, 0x1300);
        var registers = new Dictionary<string, ulong> { ["r12"] = 0x1100, ["rbx"] = 2 };
        Assert.Equal(expected, GuestOwnershipTrace.TryEvaluate(context, registers, expression, out var value));
        if (expected) Assert.Equal(expectedValue, value);
    }

    [Fact]
    public void KeepsFirstEventsAndReportsPerPointOverflow()
    {
        var trace = new GuestOwnershipTrace([
            new() { Caller = "100", Import = "free", Name = "release", Capacity = 1,
                Regions = [new() { Name = "descriptor", Address = "arg0", Length = "8" }] },
            new() { Caller = "200", Import = "free", Name = "other", Capacity = 1 }
        ]);
        var context = new CpuContext(new FakeCpuMemory(0x1000, 0x1000), Generation.Gen5);
        var registers = new Dictionary<string, ulong> { ["arg0"] = 0x1100 };
        context.TryWriteUInt64(0x1100, 42);
        trace.Record(context, "free", 0x100, false, 0, 7, registers);
        trace.Record(context, "free", 0x100, false, 0, 7, registers);
        trace.Record(context, "free", 0x200, false, 0, 7, registers);
        trace.Record(context, "free", 0x200, true, 0, 7, registers);
        using var output = new StringWriter();
        trace.Write(output);
        Assert.Contains("ownership-summary count=2", output.ToString());
        Assert.Contains("name=release count=1 dropped=1", output.ToString());
        Assert.Contains("bytes=2A00000000000000", output.ToString());
    }

    [Fact]
    public void ReportsUnreadableRegionsWithoutInventingContents()
    {
        var trace = new GuestOwnershipTrace([new() { Caller = "100", Import = "free", Name = "release",
            Regions = [new() { Name = "bad", Address = "3000", Length = "8" }] }]);
        var context = new CpuContext(new FakeCpuMemory(0x1000, 0x1000), Generation.Gen5);
        trace.Record(context, "free", 0x100, false, 0, 7, []);
        using var output = new StringWriter();
        trace.Write(output);
        Assert.Contains("readable=False truncated=False bytes=", output.ToString());
    }

    [Fact]
    public void CapturesVectorMembersAndRejectsReversedBounds()
    {
        var trace = new GuestOwnershipTrace([new() { Caller = "100", Import = "free", Name = "retirement",
            Regions = [
                new() { Name = "entries", Address = "1100", End = "1108", FollowPointers = true },
                new() { Name = "reversed", Address = "1108", End = "1100" }
            ] }]);
        var context = new CpuContext(new FakeCpuMemory(0x1000, 0x1000), Generation.Gen5);
        context.TryWriteUInt64(0x1100, 0x1200);
        trace.Record(context, "free", 0x100, false, 0, 7, []);
        using var output = new StringWriter();
        trace.Write(output);
        Assert.Contains("name=entries[0] address=0x0000000000001200 requested=64 readable=True", output.ToString());
        Assert.Contains("name=reversed address=0x0000000000001108 requested=0 readable=False", output.ToString());
        Assert.False(trace.ShouldCapture("memcpy", 0x100, false));
        Assert.False(trace.ShouldCapture("free", 0x100, true));
    }
}
