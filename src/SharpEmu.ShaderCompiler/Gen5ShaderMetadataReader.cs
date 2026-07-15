// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers.Binary;

namespace SharpEmu.ShaderCompiler;

public static class Gen5ShaderMetadataReader
{
    private const ulong ShaderUserDataOffset = 0x08;
    private const int ResourceClassCount = 4;
    private const int MaxMetadataEntries = 4096;

    public static bool TryRead(
        CpuContext ctx,
        ulong shaderHeaderAddress,
        out Gen5ShaderMetadata metadata)
    {
        metadata = default!;
        if (!TryReadUInt64(ctx, shaderHeaderAddress + ShaderUserDataOffset, out var userDataAddress) ||
            userDataAddress == 0 ||
            !TryReadUInt64(ctx, userDataAddress, out var directResourceOffsetsAddress))
        {
            return false;
        }

        var resourceOffsets = new ulong[ResourceClassCount];
        for (var resourceClass = 0; resourceClass < ResourceClassCount; resourceClass++)
        {
            if (!TryReadUInt64(
                    ctx,
                    userDataAddress + 0x08 + (ulong)(resourceClass * sizeof(ulong)),
                    out resourceOffsets[resourceClass]))
            {
                return false;
            }
        }

        if (!TryReadUInt16(ctx, userDataAddress + 0x28, out var extendedUserDataSize) ||
            !TryReadUInt16(ctx, userDataAddress + 0x2A, out var shaderResourceTableSize) ||
            !TryReadUInt16(ctx, userDataAddress + 0x2C, out var directResourceCount) ||
            directResourceCount > MaxMetadataEntries)
        {
            return false;
        }

        var resourceCounts = new ushort[ResourceClassCount];
        for (var resourceClass = 0; resourceClass < ResourceClassCount; resourceClass++)
        {
            if (!TryReadUInt16(
                    ctx,
                    userDataAddress + 0x2E + (ulong)(resourceClass * sizeof(ushort)),
                    out resourceCounts[resourceClass]) ||
                resourceCounts[resourceClass] > MaxMetadataEntries)
            {
                return false;
            }
        }

        var directResources = new Dictionary<uint, uint>();
        if (directResourceCount != 0)
        {
            if (directResourceOffsetsAddress == 0)
            {
                return false;
            }

            for (uint type = 0; type < directResourceCount; type++)
            {
                if (!TryReadUInt16(ctx, directResourceOffsetsAddress + type * sizeof(ushort), out var offset))
                {
                    return false;
                }

                if (offset != ushort.MaxValue)
                {
                    directResources[type] = offset;
                }
            }
        }

        var resources = new List<Gen5ShaderResourceMapping>();
        for (var resourceClass = 0; resourceClass < ResourceClassCount; resourceClass++)
        {
            var count = resourceCounts[resourceClass];
            if (count == 0)
            {
                continue;
            }

            if (resourceOffsets[resourceClass] == 0)
            {
                return false;
            }

            for (uint slot = 0; slot < count; slot++)
            {
                if (!TryReadUInt16(
                        ctx,
                        resourceOffsets[resourceClass] + slot * sizeof(ushort),
                        out var sharp))
                {
                    return false;
                }

                var offset = (uint)(sharp & 0x7FFF);
                if (offset == 0x7FFF)
                {
                    continue;
                }

                resources.Add(new Gen5ShaderResourceMapping(
                    (Gen5ShaderResourceKind)resourceClass,
                    slot,
                    offset,
                    (sharp & 0x8000) != 0));
            }
        }

        metadata = new Gen5ShaderMetadata(
            extendedUserDataSize,
            shaderResourceTableSize,
            directResources,
            resources);
        return true;
    }

    private static bool TryReadUInt16(CpuContext ctx, ulong address, out ushort value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ushort)];
        if (!ctx.Memory.TryRead(address, bytes))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        return true;
    }

    private static bool TryReadUInt64(CpuContext ctx, ulong address, out ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        if (!ctx.Memory.TryRead(address, bytes))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        return true;
    }
}
