// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler;

/// <summary>
/// The Gen5 (gfx10) inline-constant operand table, shared by every codegen so backends
/// cannot drift on constant semantics.
/// </summary>
public static class Gen5InlineConstants
{
    public const uint SharedBase = 235;
    public const uint SharedLimit = 236;
    public const uint PrivateBase = 237;
    public const uint PrivateLimit = 238;

    // Generated addresses use virtual segments outside global memory.
    public const ulong SharedApertureBase = 1ul << 48;
    public const ulong PrivateApertureBase = 2ul << 48;

    // Guest literal tags are distinct from the generated segments.
    public const uint SharedApertureHigh = 0x8000_0000;
    public const uint PrivateApertureHigh = 0x7000_0000;
    public const ulong SharedFlatApertureBase = (ulong)SharedApertureHigh << 32;
    public const ulong PrivateFlatApertureBase = (ulong)PrivateApertureHigh << 32;
    public const int ApertureShift = 28;

    public static bool IsSharedApertureHigh(uint high) =>
        high >> ApertureShift == SharedApertureHigh >> ApertureShift;

    public static bool IsPrivateApertureHigh(uint high) =>
        high >> ApertureShift == PrivateApertureHigh >> ApertureShift;

    public static bool IsAperture(uint encoded) => encoded is >= SharedBase and <= PrivateLimit;

    public static bool IsSharedAperture(uint encoded) => encoded is SharedBase or SharedLimit;

    public static bool TryDecodeAperture(uint encoded, out ulong value)
    {
        value = encoded switch
        {
            SharedBase => SharedApertureBase,
            SharedLimit => SharedApertureBase | uint.MaxValue,
            PrivateBase => PrivateApertureBase,
            PrivateLimit => PrivateApertureBase | uint.MaxValue,
            _ => 0,
        };
        return IsAperture(encoded);
    }

    public static ulong DecodeAperture64(uint encoded) =>
        TryDecodeAperture(encoded, out var value)
            ? value
            : throw new ArgumentOutOfRangeException(nameof(encoded));

    public static bool TryDecode(uint encoded, out uint value)
    {
        if (IsAperture(encoded))
        {
            value = (uint)(DecodeAperture64(encoded) >> 32);
            return true;
        }

        // POPS_EXITING_WAVE_ID: no primitive-ordered pixel shading is modeled.
        if (encoded == 239)
        {
            value = 0;
            return true;
        }

        if (encoded == 125)
        {
            value = 0;
            return true;
        }

        if (encoded is >= 128 and <= 192)
        {
            value = encoded - 128;
            return true;
        }

        if (encoded is >= 193 and <= 208)
        {
            value = unchecked((uint)-(int)(encoded - 192));
            return true;
        }

        var floatingPoint = encoded switch
        {
            240 => 0.5f,
            241 => -0.5f,
            242 => 1.0f,
            243 => -1.0f,
            244 => 2.0f,
            245 => -2.0f,
            246 => 4.0f,
            247 => -4.0f,
            248 => 1.0f / (2.0f * MathF.PI),
            _ => float.NaN,
        };
        if (float.IsNaN(floatingPoint))
        {
            value = 0;
            return false;
        }

        value = BitConverter.SingleToUInt32Bits(floatingPoint);
        return true;
    }
}
