// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler;

public enum Gen5FormatNumericKind
{
    Float,
    Sint,
    Uint,
}

public readonly record struct Gen5DecodedUnifiedFormat(
    uint UnifiedFormat,
    uint DataFormat,
    uint NumberFormat,
    uint BytesPerTexel,
    uint BlockWidth = 1,
    uint BlockHeight = 1,
    uint BytesPerBlock = 0)
{
    public bool IsBlockCompressed => BytesPerBlock != 0;

    public Gen5FormatNumericKind NumericKind => NumberFormat switch
    {
        4 => Gen5FormatNumericKind.Uint,
        5 => Gen5FormatNumericKind.Sint,
        _ => Gen5FormatNumericKind.Float,
    };

    public ulong GetByteCount(uint width, uint height) =>
        IsBlockCompressed
            ? checked(
                (((ulong)width + BlockWidth - 1) / BlockWidth) *
                (((ulong)height + BlockHeight - 1) / BlockHeight) *
                BytesPerBlock)
            : checked((ulong)width * height * BytesPerTexel);
}

/// <summary>
/// Adds storage sizing information to the exact GFX10 unified-format table.
/// </summary>
public static class Gen5UnifiedFormat
{
    public static bool TryDecode(
        uint unifiedFormat,
        out Gen5DecodedUnifiedFormat decoded)
    {
        if (!Gfx10UnifiedFormat.TryDecode(
                unifiedFormat,
                out var dataFormat,
                out var numberFormat))
        {
            decoded = default;
            return false;
        }

        var bytesPerBlock = unifiedFormat switch
        {
            169 or 170 or 175 or 176 => 8u,
            >= 171 and <= 174 or >= 177 and <= 182 => 16u,
            _ => 0u,
        };
        if (bytesPerBlock != 0)
        {
            decoded = new Gen5DecodedUnifiedFormat(
                unifiedFormat,
                dataFormat,
                numberFormat,
                0,
                BlockWidth: 4,
                BlockHeight: 4,
                BytesPerBlock: bytesPerBlock);
            return true;
        }

        var bytesPerTexel = GetBytesPerTexel(dataFormat);
        decoded = new Gen5DecodedUnifiedFormat(
            unifiedFormat,
            dataFormat,
            numberFormat,
            bytesPerTexel);
        return bytesPerTexel != 0;
    }

    private static uint GetBytesPerTexel(uint dataFormat) =>
        dataFormat switch
        {
            1 => 1,
            2 or 3 or 16 or 17 or 18 or 19 => 2,
            4 or 5 or 6 or 7 or 8 or 9 or 10 or 34 => 4,
            11 or 12 => 8,
            13 => 12,
            14 => 16,
            _ => 0,
        };
}
