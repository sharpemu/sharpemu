// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

public sealed partial class CompiledDeviceAddressRangeTests
{
    private static void SetRanges(ShaderResourcePlan plan, DeviceAddressRangePlan[] ranges) =>
        typeof(ShaderResourcePlan).GetProperty(nameof(ShaderResourcePlan.DeviceAddressRanges))!.SetValue(plan, ranges);

    private static ShaderResourcePlan Expanded(Scenario scenario)
    {
        var plan = scenario.Extract();
        scenario.Prepare?.Invoke(plan);
        var original = plan.DeviceAddressRanges.ToArray();
        Assert.NotEmpty(original);
        // More than 16K external roots, with shared successful and failed reads. Distinct trailing
        // roots also cross method boundaries after reference-identical roots have been compacted.
        var extra = Enumerable.Range(0, 128).Select(index => original[0] with
        {
            BaseLow = ScalarValue.MakeOperation(ScalarOperation.IAdd32, ScalarValueType.U32,
                ScalarValue.UserData(0), ScalarValue.ConstantOf((uint)index)),
            BaseHigh = ScalarValue.ConstantOf(0u),
        });
        SetRanges(plan, Enumerable.Range(0, 8193).SelectMany(_ => original).Concat(extra)
            .Select((range, index) => range with { Handle = (uint)index }).ToArray());
        return plan;
    }

    [Theory]
    [InlineData("table")]
    [InlineData("table_failures")]
    [InlineData("first_lane_read")]
    [InlineData("first_lane_select")]
    [InlineData("function")]
    public void PartitionedPlansPreserveRangesAndReadOrderAcrossMethodBoundaries(string name)
    {
        var scenario = Named(name);
        var plan = Expanded(scenario);
        Assert.Equal("too_large", CompiledDeviceAddressRanges.Build(plan, compileLarge: false).Refusal);
        var compiled = CompiledDeviceAddressRanges.Build(plan, compileLarge: true);
        Assert.Null(compiled.Refusal);
        Assert.True(compiled.Partitions > 1);
        Assert.True(compiled.RootCount > 16384);
        Assert.True(compiled.DistinctRootCount < compiled.RootCount);
        var expected = new Memory(scenario.Words);
        var actual = new Memory(scenario.Words);
        foreach (var address in scenario.FailOnce ?? [])
        {
            expected.FailOnce.Add(address);
            actual.FailOnce.Add(address);
        }
        var ranges = DeviceAddressRangePlanner.EvaluateInterpreted(plan, Inputs(scenario.UserData, expected.Read));
        Assert.Equal(ranges, compiled.Evaluate(plan, Inputs(scenario.UserData, actual.Read)));
        // Shared successful reads stay memoized across pieces; a failed read is retried in the same order as the reference.
        Assert.Equal(expected.Reads, actual.Reads);
    }

    [Fact]
    public void PartitioningRetainsTheOriginalDepthRefusal()
    {
        Assert.Equal("too_deep", CompiledDeviceAddressRanges.Build(DeepPlan(), compileLarge: true).Refusal);
    }

    [Fact]
    public void OneOversizedRootStillRefusesInsteadOfGeneratingAnOversizedMethod()
    {
        var plan = TablePlan();
        var nodes = Enumerable.Range(0, 2048).Select(_ => ScalarValue.UserData(0)).ToArray();
        while (nodes.Length > 1)
            nodes = Enumerable.Range(0, nodes.Length / 2).Select(index =>
                ScalarValue.MakeOperation(ScalarOperation.IAdd32, ScalarValueType.U32, nodes[index * 2], nodes[index * 2 + 1])).ToArray();
        SetRanges(plan, [plan.DeviceAddressRanges[0] with { BaseLow = nodes[0], BaseHigh = ScalarValue.ConstantOf(0u) }]);
        Assert.Equal("too_large", CompiledDeviceAddressRanges.Build(plan, compileLarge: true).Refusal);
    }

    [Fact]
    public void ExcessiveTotalRootsRefuseBeforeCompilingAnyPartition()
    {
        var plan = TablePlan();
        var range = plan.DeviceAddressRanges[0];
        SetRanges(plan, Enumerable.Range(0, 131073).Select(index => range with { Handle = (uint)index }).ToArray());
        Assert.Equal("large_root_budget", CompiledDeviceAddressRanges.Build(plan, compileLarge: true).Refusal);
    }

    [Fact]
    public void ExcessiveDistinctRootsRefuseEvenWithinTheExternalRootBudget()
    {
        var plan = TablePlan();
        var range = plan.DeviceAddressRanges[0];
        SetRanges(plan, Enumerable.Range(0, 32769).Select(index => range with
        {
            Handle = (uint)index,
            BaseLow = ScalarValue.UserData(0),
            BaseHigh = ScalarValue.UserData(1),
        }).ToArray());
        Assert.Equal("large_distinct_root_budget", CompiledDeviceAddressRanges.Build(plan, compileLarge: true).Refusal);
    }
}
