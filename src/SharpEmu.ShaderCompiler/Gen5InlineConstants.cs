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

    // The PS5 FLAT apertures, as title shaders hard-code them: a high dword of
    // 0x7xxxxxxx selects LDS and 0x8xxxxxxx scratch. Guest device addresses fit in
    // 40 bits and never reach them.
    public const uint SharedApertureHigh = 0x7000_0000;
    public const uint PrivateApertureHigh = 0x8000_0000;

    // Each aperture is the window of high dwords sharing its top nibble.
    public const int ApertureShift = 28;

    public static bool IsSharedApertureHigh(uint high) => high >> ApertureShift == SharedApertureHigh >> ApertureShift;

    public static bool IsPrivateApertureHigh(uint high) => high >> ApertureShift == PrivateApertureHigh >> ApertureShift;

    public static bool IsAperture(uint encoded) => encoded is >= SharedBase and <= PrivateLimit;

    public static bool IsSharedAperture(uint encoded) => encoded is SharedBase or SharedLimit;

    // The 64-bit aperture value: a base has a zero low dword, a limit spans the
    // whole 32-bit offset range. A 32-bit read returns the high dword.
    public static ulong DecodeAperture64(uint encoded)
    {
        var high = IsSharedAperture(encoded) ? SharedApertureHigh : PrivateApertureHigh;
        var low = encoded is SharedLimit or PrivateLimit ? uint.MaxValue : 0u;
        return ((ulong)high << 32) | low;
    }

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
