// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;

namespace SharpEmu.GnmMode.Shaders;

public static class GcnBufferDescriptor
{
    private static readonly uint[] Formats = BuildFormats();

    internal static bool TryNormalizeFormat(uint legacy, out uint unified)
    {
        unified = legacy < 8 ? 0u : legacy < Formats.Length ? Formats[legacy] : uint.MaxValue;
        return unified != uint.MaxValue;
    }

    public static uint[] Normalize(ReadOnlySpan<uint> words)
    {
        if (words.Length != 4)
            throw new InvalidDataException("A GCN buffer descriptor must contain four dwords.");
        if ((words[3] & GcnStorageImageLowering.ProjectionMask) == GcnStorageImageLowering.ProjectionMarker)
            return GcnStorageImageLowering.NormalizeProjection(words);
        if ((words[3] >> 30) != 0)
            return new uint[4];
        if ((words[1] & 0x80000000) != 0)
            throw new InvalidDataException("GCN swizzled buffer addressing requires legacy element-size lowering.");
        var result = words.ToArray();
        // GCN V# addresses are 40-bit; bits 40..47 are not RDNA's address extension.
        result[1] &= 0xFFFF00FF;
        var format = (words[3] >> 12) & 127;
        // Untyped byte buffers do not give NUM_FORMAT a meaning when DATA_FORMAT is zero.
        var unified = format < 8 ? 0u : Formats[format];
        if (unified == uint.MaxValue)
            throw new InvalidDataException($"Unsupported GCN buffer format: data={format >> 3} number={format & 7}.");
        result[3] = (words[3] & ~(127u << 12)) | (unified << 12);
        return result;
    }

    private static uint[] BuildFormats()
    {
        var result = new uint[128];
        Array.Fill(result, uint.MaxValue);
        // GFX6/7 DATA_FORMAT uses the same component layouts as the common format table.
        // Preserve only exact pairs: legacy SNORM_OGL has no equivalent here.
        for (var unified = 0u; unified <= 77; unified++)
            if (Gfx10UnifiedFormat.TryDecode(unified, out var data, out var number))
                result[(data << 3) | number] = unified;
        return result;
    }
}
