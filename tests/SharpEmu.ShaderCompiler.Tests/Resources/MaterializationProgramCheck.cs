// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;
using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

internal static class MaterializationProgramCheck
{
    internal static long Checked;

    [ThreadStatic]
    private static bool _suspended;

    internal static bool Suspended => _suspended;

    internal static Suspension Suspend()
    {
        _suspended = true;
        return new Suspension();
    }

    internal readonly struct Suspension : IDisposable
    {
        public void Dispose() => _suspended = false;
    }

    [ModuleInitializer]
    internal static void Install() => ResourceMaterializer.ProgramCheck = Compare;

    private static void Compare(ShaderResourcePlan plan, ResourceRuntimeInputs inputs, List<DescriptorWords> values, uint[] table,
        DeviceAddressRange[] ranges)
    {
        if (_suspended)
        {
            return;
        }

        Interlocked.Increment(ref Checked);
        var additional = plan.WrittenRangeCount * ShaderResourcePlan.WrittenRangeDwordCount;
        if (!RuntimeValueEvaluator.EvaluateSources(plan, plan.MaterializationSources, inputs, plan.CleanFlatSlots, evaluateTable: true,
            out var expected, out var expectedTable, out _, additional))
        {
            throw new InvalidOperationException($"The program materialised a plan the evaluator refuses: hash=0x{plan.Hash:X16}.");
        }

        if (expected.Count != values.Count)
        {
            throw new InvalidOperationException($"The program produced {values.Count} sources, the evaluator {expected.Count}: hash=0x{plan.Hash:X16}.");
        }

        for (var index = 0; index < values.Count; index++)
        {
            if (!expected[index].SameAs(values[index]))
            {
                throw new InvalidOperationException($"Source {index} differs: hash=0x{plan.Hash:X16} program=[{string.Join(",", values[index].Dwords)}] " +
                    $"evaluator=[{string.Join(",", expected[index].Dwords)}].");
            }
        }

        if (!expectedTable.AsSpan().SequenceEqual(table))
        {
            throw new InvalidOperationException($"The flattened table differs: hash=0x{plan.Hash:X16}.");
        }

        var expectedRanges = DeviceAddressRangePlanner.Evaluate(plan, inputs);
        if (!expectedRanges.AsSpan().SequenceEqual(ranges))
        {
            throw new InvalidOperationException($"The device-address ranges differ: hash=0x{plan.Hash:X16}.");
        }
    }
}
