// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;

namespace SharpEmu.Libs.Agc;

/// <summary>Prospero/AGC IndexType helpers (gpu_defs / renderDraw index8 expand).</summary>
internal static class AgcIndexHelpers
{
    internal enum ProsperoIndexType : uint
    {
        Index16 = 0,
        Index32 = 1,
        Index8 = 2,
    }

    internal static ProsperoIndexType Decode(uint raw) =>
        (raw & 0x3u) switch
        {
            1 => ProsperoIndexType.Index32,
            2 => ProsperoIndexType.Index8,
            _ => ProsperoIndexType.Index16,
        };

    internal static int GetGuestStrideBytes(ProsperoIndexType indexType) =>
        indexType switch
        {
            ProsperoIndexType.Index32 => sizeof(uint),
            ProsperoIndexType.Index8 => sizeof(byte),
            _ => sizeof(ushort),
        };

    /// <summary>Expand guest u8 indices to host u16 (Vulkan/Metal bindable).</summary>
    internal static void ExpandIndex8ToU16(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (destination.Length < source.Length * sizeof(ushort))
        {
            throw new ArgumentException("destination too small for u8->u16 expansion.");
        }

        for (var index = 0; index < source.Length; index++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(
                destination.Slice(index * sizeof(ushort), sizeof(ushort)),
                source[index]);
        }
    }
}
