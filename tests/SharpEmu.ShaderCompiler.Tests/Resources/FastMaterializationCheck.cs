// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;
using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

internal static class FastMaterializationCheck
{
    internal static long Checked;

    [ThreadStatic]
    private static bool _running;

    [ModuleInitializer]
    internal static void Install() => ResourceMaterializer.MaterializedCheck = Compare;

    private static void Compare(ShaderResourcePlan plan, ResourceRuntimeInputs inputs, ResourceSnapshot expected, ResourceSpecialization expectedSpecialization)
    {
        if (_running || MaterializationProgramCheck.Suspended || !ResourceMaterializer.CanMaterializeFast(plan))
        {
            return;
        }

        _running = true;
        try
        {
            for (var pass = 0; pass < 2; pass++)
            {
                var fast = new ResourceSnapshot();
                var specialization = new ResourceSpecialization();
                if (!ResourceMaterializer.MaterializeFast(plan, inputs, ref fast, ref specialization, out _))
                {
                    throw new InvalidOperationException($"The fast path refused a plan the full path materialised: hash=0x{plan.Hash:X16}.");
                }

                Interlocked.Increment(ref Checked);
                CompareOne(plan, expected, expectedSpecialization, fast, specialization);
            }
        }
        finally
        {
            _running = false;
        }
    }

    private static void CompareOne(ShaderResourcePlan plan, ResourceSnapshot expected, ResourceSpecialization expectedSpecialization, ResourceSnapshot fast,
        ResourceSpecialization specialization)
    {
        if (!expectedSpecialization.Equals(specialization))
        {
            throw new InvalidOperationException($"The fast specialization differs: hash=0x{plan.Hash:X16}.");
        }

        Same("buffers", plan, expected.Buffers, fast.Buffers);
        Same("samplers", plan, expected.Samplers, fast.Samplers);
        if (expected.Images.Length != fast.Images.Length)
        {
            throw new InvalidOperationException($"The fast image count differs: hash=0x{plan.Hash:X16}.");
        }

        for (var index = 0; index < fast.Images.Length; index++)
        {
            var words = (uint[])fast.Images[index].Clone();
            var current = expected.Images[index];
            if (fast.MipStatistics is { } statistics && words.Length >= 7)
            {
                words[5] = (words[5] & ~ResourceSnapshot.MipStatisticsEnableWordMask) | (current[5] & ResourceSnapshot.MipStatisticsEnableWordMask);
                words[6] = (words[6] & ~ResourceSnapshot.MipStatisticsSlotWordMask) | (current[6] & ResourceSnapshot.MipStatisticsSlotWordMask);
                if (statistics[index] != ResourceSnapshot.MipStatisticsOf(current[5], current[6]))
                {
                    throw new InvalidOperationException($"The fast mip statistics of image {index} differ: hash=0x{plan.Hash:X16}.");
                }
            }

            if (!words.AsSpan().SequenceEqual(current))
            {
                throw new InvalidOperationException($"The fast image {index} differs: hash=0x{plan.Hash:X16}.");
            }
        }

        if (!expected.FlattenedResourceTable.AsSpan().SequenceEqual(fast.FlattenedResourceTable) || !expected.UserData.AsSpan().SequenceEqual(fast.UserData) ||
            !expected.DeviceAddressRanges.AsSpan().SequenceEqual(fast.DeviceAddressRanges))
        {
            throw new InvalidOperationException($"The fast table, user data or ranges differ: hash=0x{plan.Hash:X16}.");
        }
    }

    private static void Same(string what, ShaderResourcePlan plan, uint[][] expected, uint[][] actual)
    {
        if (expected.Length != actual.Length)
        {
            throw new InvalidOperationException($"The fast {what} count differs: hash=0x{plan.Hash:X16}.");
        }

        for (var index = 0; index < actual.Length; index++)
        {
            if (!expected[index].AsSpan().SequenceEqual(actual[index]))
            {
                throw new InvalidOperationException($"The fast {what} {index} differ: hash=0x{plan.Hash:X16}.");
            }
        }
    }
}
