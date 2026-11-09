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
    [Fact]
    public void ReadCountersAreExactAndGated()
    {
        var before = ReadReport();
        Parallel.For(0, 100, _ =>
        {
            ResourceMaterializationProfile.CountRead(ResourceMaterializationProfile.ReadCounter.GroupSucceeded);
            ResourceMaterializationProfile.CountRead(ResourceMaterializationProfile.ReadCounter.GroupWords, 8);
        });
        var after = ReadReport();
        if (!ResourceMaterializationProfile.Enabled) { Assert.Empty(after); return; }
        Assert.Equal(ReadCounter(before, "GroupSucceeded") + 100, ReadCounter(after, "GroupSucceeded"));
        Assert.Equal(ReadCounter(before, "GroupWords") + 800, ReadCounter(after, "GroupWords"));
        Assert.Contains("[RESOURCE_READ_COUNTS] cumulative=1 sampled=0", after);
    }

    [Fact]
    public void TransactionRejectionReportIsGatedAndResets()
    {
        ResourceMaterializationCache.TakeReport();
        var (plan, _, _) = ResourceTestProgram.Prepare(FlattenedReadReuseTests.RepeatedReadProgram(4), userDataCount: 9);
        ResourceMaterializationCache.RecordTransactionBuild(plan, false, "structured_plan");
        ResourceMaterializationCache.RecordTransactionBuild(plan, false, "structured_plan");
        var report = ResourceMaterializationCache.TakeReport();
        Assert.Equal(Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_PERFORMANCE") == "1" ? 1 : 0,
            Regex.Matches(report, "RESOURCE_TRANSACTION_REJECTED").Count);
        Assert.DoesNotContain("RESOURCE_TRANSACTION_REJECTED", ResourceMaterializationCache.TakeReport());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TransactionCacheTimingReportsHitsRefreshesAndResetsWithoutChangingOutput(bool wholeTransaction)
    {
        ResourceMaterializationCache.TakeReport();
        var (plan, _, _) = ResourceTestProgram.Prepare(FlattenedReadReuseTests.RepeatedReadProgram(4), userDataCount: 9);
        plan.CompileEvaluatorNow();
        if (!wholeTransaction)
        {
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            typeof(ShaderResourcePlan).GetField("_compiledTransaction", flags)!.SetValue(plan, null);
            typeof(ShaderResourcePlan).GetField("_compiledTableTransaction", flags)!.SetValue(plan, null);
        }
        var memory = new TestWordMemory { Words = [11, 22, 33, 44] };
        bool ResidentRead(ulong address, Span<byte> destination, bool clean)
        {
            for (var offset = 0; offset < destination.Length; offset += 4)
            {
                if (!memory.Read(address + (ulong)offset, out var word)) return false;
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(destination[offset..], word);
            }
            return true;
        }
        var inputs = new ResourceRuntimeInputs
        {
            UserData = new uint[] { 0x1000, 0, 0, 0, 0, 0, 0, 0, 0 },
            ReadMemory = memory.Read,
            ReadCleanMemory = memory.Read,
            ReadResidentMemory = ResidentRead,
        };
        var cache = new ResourceMaterializationCache();
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(cache.Materialize(plan, inputs, ResidentRead, ref snapshot, ref specialization, out _));
        var original = snapshot;
        Assert.True(cache.Materialize(plan, inputs, ResidentRead, ref snapshot, ref specialization, out _));
        Assert.Same(original, snapshot);
        memory.Words[0] = 55;
        Assert.True(cache.Materialize(plan, inputs, ResidentRead, ref snapshot, ref specialization, out _));
        Assert.Equal(11u, original.FlattenedResourceTable[0]);
        Assert.Equal(55u, snapshot.FlattenedResourceTable[0]);
        Assert.Equal((1L, 1L, 1L), (cache.Hits, cache.Misses, cache.TableRefreshes));
        var report = ResourceMaterializationCache.TakeReport();
        if (Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_PERFORMANCE") != "1")
        {
            Assert.DoesNotContain("RESOURCE_CACHE_TIME", report);
            Assert.DoesNotContain("RESOURCE_CACHE_DETAIL", report);
            Assert.DoesNotContain("RESOURCE_CHANGED_WORD", report);
            Assert.DoesNotContain("RESOURCE_EVALUATION_PATH", report);
            return;
        }
        Assert.Contains("scope=all calls=3", report);
        Assert.Contains("address=0x0000000000001000 old=0x0000000B new=0x00000037 role=table_only", report);
        Assert.Contains("RESOURCE_EVALUATION_PATH", report);
        if (System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported &&
            Environment.GetEnvironmentVariable("SHARPEMU_SRT_NATIVE") != "0")
        {
            Assert.Contains("RESOURCE_READ_CONSUMER", report);
            Assert.Contains("table:0", report);
        }
        foreach (var phase in new[] { "ValidateRead", "ValidateCompare", "RefreshRead", "RefreshCompare", "RefreshEvaluate", "RefreshVerify" })
            Assert.Matches($@"phase={phase} calls=[1-9]\d*", report);
        var reset = ResourceMaterializationCache.TakeReport();
        Assert.Contains("scope=all calls=0", reset);
        Assert.DoesNotContain("RESOURCE_CHANGED_WORD", reset);
        Assert.DoesNotContain("RESOURCE_EVALUATION_PATH", reset);
        for (uint value = 100; value < 112; value++)
        {
            memory.Words[0] = value;
            Assert.True(cache.Materialize(plan, inputs, ResidentRead, ref snapshot, ref specialization, out _));
            Assert.Equal(value, snapshot.FlattenedResourceTable[0]);
        }
        var bounded = ResourceMaterializationCache.TakeReport();
        Assert.Equal(4, Regex.Matches(bounded, "RESOURCE_CHANGED_WORD").Count);
    }

    [Fact]
    public void PendingConsumersAreBoundedAndResetWithTheReport()
    {
        ResourceMaterializationCache.TakeReport();
        for (ulong address = 0; address < 300; address++)
            ResourceMaterializationCache.QueueReadConsumer(1, ShaderStage.Compute, address);
        var count = 0;
        for (ulong address = 0; address < 300; address++)
            if (ResourceMaterializationCache.TakeReadConsumer(1, ShaderStage.Compute, address)) count++;
        Assert.Equal(Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_PERFORMANCE") == "1" ? 256 : 0, count);
        ResourceMaterializationCache.QueueReadConsumer(1, ShaderStage.Compute, 1000);
        ResourceMaterializationCache.TakeReport();
        Assert.False(ResourceMaterializationCache.TakeReadConsumer(1, ShaderStage.Compute, 1000));
        ResourceMaterializationCache.QueueReadConsumer(1, ShaderStage.Compute, 1000);
        Assert.Equal(Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_PERFORMANCE") == "1",
            ResourceMaterializationCache.TakeReadConsumer(1, ShaderStage.Compute, 1000));
        ResourceMaterializationCache.TakeReport();
    }

    [Fact]
    public void ConcurrentConsumerConsumptionDoesNotLoseOrDuplicateSamples()
    {
        ResourceMaterializationCache.TakeReport();
        var taken = 0;
        Parallel.For(0, 128, index =>
        {
            ResourceMaterializationCache.QueueReadConsumer(1, ShaderStage.Compute, (ulong)index);
            if (ResourceMaterializationCache.TakeReadConsumer(1, ShaderStage.Compute, (ulong)index))
                Interlocked.Increment(ref taken);
            Assert.False(ResourceMaterializationCache.TakeReadConsumer(1, ShaderStage.Compute, (ulong)index));
        });
        Assert.Equal(Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_PERFORMANCE") == "1" ? 128 : 0, taken);
        ResourceMaterializationCache.TakeReport();
    }

    [Fact]
    public void SampledScopesBoundTheirCountAndRecordExceptionalExit()
    {
        var before = ReadReport();
        for (var index = 0; index < ResourceMaterializationProfile.SampleStride; index++)
            Assert.Throws<InvalidOperationException>((Action)(() =>
            {
                using var scope = ResourceMaterializationProfile.MeasureSampled(ResourceMaterializationProfile.Phase.ReadRecording);
                throw new InvalidOperationException();
            }));
        var after = ReadReport();
        if (!ResourceMaterializationProfile.Enabled) { Assert.Empty(after); return; }
        long Calls(string report) => long.Parse(Regex.Match(report, @"ReadRecording=[\d.]+ms/n(\d+)").Groups[1].Value);
        Assert.Equal(Calls(before) + 1, Calls(after));
        Assert.Contains("sample_stride=64", after);
    }

    [Fact]
    public void CacheTimingReportsHitsRefreshesAndResetsWithoutChangingOutput()
    {
        ResourceMaterializationCache.TakeReport();
        var (plan, _, _) = ResourceTestProgram.Prepare(FlattenedReadReuseTests.RepeatedReadProgram(4), userDataCount: 9);
        plan.CompileEvaluatorNow();
        var memory = new TestWordMemory { Words = [11, 22, 33, 44] };
        bool ResidentRead(ulong address, Span<byte> destination, bool clean)
        {
            for (var offset = 0; offset < destination.Length; offset += 4)
            {
                if (!memory.Read(address + (ulong)offset, out var word)) return false;
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(destination[offset..], word);
            }
            return true;
        }
        var inputs = new ResourceRuntimeInputs
        {
            UserData = new uint[] { 0x1000, 0, 0, 0, 0, 0, 0, 0, 0 },
            ReadMemory = memory.Read,
            ReadCleanMemory = memory.Read,
            ReadResidentMemory = ResidentRead,
        };
        var cache = new ResourceMaterializationCache();
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(cache.Materialize(plan, inputs, ResidentRead, ref snapshot, ref specialization, out _));
        var original = snapshot;
        Assert.True(cache.Materialize(plan, inputs, ResidentRead, ref snapshot, ref specialization, out _));
        Assert.Same(original, snapshot);
        memory.Words[0] = 55;
        Assert.True(cache.Materialize(plan, inputs, ResidentRead, ref snapshot, ref specialization, out _));
        Assert.Equal(11u, original.FlattenedResourceTable[0]);
        Assert.Equal(55u, snapshot.FlattenedResourceTable[0]);
        Assert.Equal((1L, 1L, 1L), (cache.Hits, cache.Misses, cache.TableRefreshes));
        var report = ResourceMaterializationCache.TakeReport();
        if (Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_PERFORMANCE") != "1")
        {
            Assert.DoesNotContain("RESOURCE_CACHE_TIME", report);
            Assert.DoesNotContain("RESOURCE_CACHE_DETAIL", report);
            Assert.DoesNotContain("RESOURCE_CHANGED_WORD", report);
            Assert.DoesNotContain("RESOURCE_EVALUATION_PATH", report);
            return;
        }
        Assert.Contains("scope=all calls=3", report);
        Assert.Contains("address=0x0000000000001000 old=0x0000000B new=0x00000037 role=table_only", report);
        Assert.Contains("RESOURCE_EVALUATION_PATH", report);
        if (System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported &&
            Environment.GetEnvironmentVariable("SHARPEMU_SRT_NATIVE") != "0")
        {
            Assert.Contains("RESOURCE_READ_CONSUMER", report);
            Assert.Contains("table:0", report);
        }
        foreach (var phase in new[] { "ValidateRead", "ValidateCompare", "RefreshRead", "RefreshCompare", "RefreshEvaluate", "RefreshVerify" })
            Assert.Matches($@"phase={phase} calls=[1-9]\d*", report);
        var reset = ResourceMaterializationCache.TakeReport();
        Assert.Contains("scope=all calls=0", reset);
        Assert.DoesNotContain("RESOURCE_CHANGED_WORD", reset);
        Assert.DoesNotContain("RESOURCE_EVALUATION_PATH", reset);
        for (uint value = 100; value < 112; value++)
        {
            memory.Words[0] = value;
            Assert.True(cache.Materialize(plan, inputs, ResidentRead, ref snapshot, ref specialization, out _));
            Assert.Equal(value, snapshot.FlattenedResourceTable[0]);
        }
        var bounded = ResourceMaterializationCache.TakeReport();
        Assert.Equal(4, Regex.Matches(bounded, "RESOURCE_CHANGED_WORD").Count);
    }

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
