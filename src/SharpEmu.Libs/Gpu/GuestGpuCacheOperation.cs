// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu;

[Flags]
internal enum GuestGpuCacheDomain
{
    None = 0,
    Instruction = 1 << 0,
    Scalar = 1 << 1,
    Vector = 1 << 2,
    ShaderL1 = 1 << 3,
    ShaderL2 = 1 << 4,
    Color = 1 << 5,
    Depth = 1 << 6,
    Metadata = 1 << 7,
}

[Flags]
internal enum GuestGpuCacheAction
{
    None = 0,
    MakeAvailable = 1 << 0,
    MakeVisible = 1 << 1,
    Invalidate = 1 << 2,
    WriteBack = 1 << 3,
    Discard = 1 << 4,
}

internal enum GuestGpuCacheScope
{
    Shared,
    Unshared,
}

internal enum GuestGpuCacheOrder
{
    Parallel,
    LowToHigh,
    HighToLow,
}

internal readonly record struct GuestGpuCacheOperation(
    GuestGpuCacheDomain Domains,
    GuestGpuCacheAction Actions,
    ulong BaseAddress,
    ulong SizeBytes,
    bool CoversAllMemory,
    uint RawCbDbControl,
    uint RawGcrControl,
    GuestGpuCacheScope Scope = GuestGpuCacheScope.Shared,
    GuestGpuCacheOrder Order = GuestGpuCacheOrder.Parallel);

internal static class GuestGpuCacheOperationBatcher
{
    public static void AddOrMerge(
        List<GuestGpuCacheOperation> operations,
        GuestGpuCacheOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operations);

        var merged = Normalize(operation);
        for (var index = operations.Count - 1; index >= 0; index--)
        {
            if (!CanMerge(operations[index], merged))
            {
                continue;
            }

            merged = Merge(operations[index], merged);
            operations.RemoveAt(index);
        }

        operations.Add(merged);
        operations.Sort(static (left, right) =>
            left.BaseAddress.CompareTo(right.BaseAddress));
    }

    internal static bool CanMerge(
        GuestGpuCacheOperation left,
        GuestGpuCacheOperation right)
    {
        if (left.Domains != right.Domains ||
            left.Actions != right.Actions ||
            left.RawCbDbControl != right.RawCbDbControl ||
            left.RawGcrControl != right.RawGcrControl ||
            left.Scope != right.Scope ||
            left.Order != right.Order)
        {
            return false;
        }

        if (left.CoversAllMemory || right.CoversAllMemory)
        {
            return true;
        }

        var leftEnd = SaturatingEnd(left.BaseAddress, left.SizeBytes);
        var rightEnd = SaturatingEnd(right.BaseAddress, right.SizeBytes);
        return left.BaseAddress <= rightEnd && right.BaseAddress <= leftEnd;
    }

    private static GuestGpuCacheOperation Merge(
        GuestGpuCacheOperation left,
        GuestGpuCacheOperation right)
    {
        if (left.CoversAllMemory || right.CoversAllMemory)
        {
            return left with
            {
                BaseAddress = 0,
                SizeBytes = ulong.MaxValue,
                CoversAllMemory = true,
            };
        }

        var start = Math.Min(left.BaseAddress, right.BaseAddress);
        var end = Math.Max(
            SaturatingEnd(left.BaseAddress, left.SizeBytes),
            SaturatingEnd(right.BaseAddress, right.SizeBytes));
        return left with
        {
            BaseAddress = start,
            SizeBytes = end == ulong.MaxValue ? ulong.MaxValue : end - start,
        };
    }

    private static GuestGpuCacheOperation Normalize(GuestGpuCacheOperation operation) =>
        operation.CoversAllMemory
            ? operation with { BaseAddress = 0, SizeBytes = ulong.MaxValue }
            : operation;

    private static ulong SaturatingEnd(ulong address, ulong size) =>
        address > ulong.MaxValue - size ? ulong.MaxValue : address + size;
}
