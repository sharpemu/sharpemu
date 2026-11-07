// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;

namespace SharpEmu.ShaderCompiler.Resources;

// Chooses the branch-free device-address lowering for a compute program that reads many dwords through device
// addresses. Gen5SpirvTranslator's compact memory access was first used only by programs with shader calls (#1120);
// the default lowering gives every device-address dword its own page-table branch, unmapped-page branch, conditional
// load and fault atomic. A 26 KB compute program with ~2,000 such dwords became tens of thousands of SPIR-V blocks,
// and one native driver compilation of it held gigabytes of host memory for minutes. Compact access resolves a page
// with a select, reads an unmade access from the host's fallback address and drops it, shares one page lookup between
// the dwords of a single access, and reports the invocation's missing pages when it ends. The program then binds
// DescriptorBindingKind.DeviceAddressFallback (ShaderResourceInfo.UsesDeviceAddressFallback).
//
// The count is of device-address dwords, not of memory instructions: a program whose buffers are mostly bound keeps the
// default lowering (and its existing driver-cache entries), since compact access makes such a module larger without
// removing a significant number of blocks.
public static class CompactMemoryAccessPolicy
{
    // Device-address dwords of a compute program at or above which compact access is chosen. The count undercounts what
    // the default lowering emits (it resolved 2.5 to 8 times as many pages in the programs measured). In the Ghost of Yotei
    // compute corpus no program counts between 80 and 280, and the programs at 281 or more lowered by default to 9,000 or
    // more blocks each.
    public const int DefaultThreshold = 256;

    public const string EnvironmentVariable = "SHARPEMU_COMPACT_MEMORY_ACCESS_THRESHOLD";

    // Read once: one process never mixes two lowerings of the same program.
    public static int Threshold { get; } = ParseThreshold(Environment.GetEnvironmentVariable(EnvironmentVariable));

    // Unset or empty keeps the default; "off" or 0 disables the policy; a positive count replaces the threshold.
    public static int ParseThreshold(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DefaultThreshold;
        }

        var text = value.Trim();
        if (string.Equals(text, "off", StringComparison.OrdinalIgnoreCase))
        {
            return int.MaxValue;
        }

        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var threshold))
        {
            throw new FormatException($"{EnvironmentVariable} must be a non-negative dword count or 'off', not '{value}'.");
        }

        return threshold == 0 ? int.MaxValue : threshold;
    }

    // Data dwords of the tracked accesses that go through the device-address page table: buffers whose descriptors are
    // read from device memory, and global, flat and scalar-address accesses. Scratch is the invocation's own.
    public static int CountDeviceAddressDwords(ShaderResourcePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var count = 0;
        foreach (var memory in plan.Memory.Entries)
        {
            if (memory.PlanningOnly)
            {
                continue;
            }

            if (memory.DeviceDescriptor ||
                memory.Kind is MemoryResourceKind.Global or MemoryResourceKind.Flat or MemoryResourceKind.ScalarAddress)
            {
                count += (int)Math.Max(1u, memory.DataDwords);
            }
        }

        return count;
    }

    public static bool Prefers(ShaderResourcePlan plan, ShaderStage stage) => Prefers(plan, stage, Threshold);

    public static bool Prefers(ShaderResourcePlan plan, ShaderStage stage, int threshold) =>
        stage == ShaderStage.Compute && threshold != int.MaxValue && CountDeviceAddressDwords(plan) >= threshold;
}
