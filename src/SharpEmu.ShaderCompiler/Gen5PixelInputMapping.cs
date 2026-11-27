// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler;

public static class Gen5PixelInputMapping
{
    public static uint PackedLocations(Gen5ShaderProgram program, ReadOnlySpan<uint> controls)
    {
        var mask = 0u;
        foreach (var instruction in program.Instructions)
        {
            if (instruction.Control is Gen5InterpolationControl interpolation && IsHalfInterpolation(instruction.Opcode))
            {
                var control = interpolation.Attribute < (uint)controls.Length
                    ? controls[(int)interpolation.Attribute]
                    : interpolation.Attribute;
                // SPI_PS_INPUT_CNTL.FP16_INTERP_MODE interprets each exported dword as two halves.
                if (interpolation.HighAttribute || (control & (1u << 19)) != 0)
                    mask |= 1u << (int)(control & 0x1Fu);
            }
        }
        return mask;
    }

    public static bool IsHalfInterpolation(string opcode) =>
        opcode is "VInterpP1llF16" or "VInterpP1lvF16" or "VInterpP2F16";

    public static uint[] ResolveLocations(
        ReadOnlySpan<uint> controls,
        ReadOnlySpan<uint> activeInputs)
    {
        var locations = new uint[activeInputs.Length];
        ResolveLocations(controls, activeInputs, locations);
        return locations;
    }

    // Fills `locations` (one slot per active input) without allocating.
    public static void ResolveLocations(
        ReadOnlySpan<uint> controls,
        ReadOnlySpan<uint> activeInputs,
        Span<uint> locations)
    {
        Span<bool> usedLocations = stackalloc bool[32];

        for (var index = 0; index < activeInputs.Length; index++)
        {
            var input = activeInputs[index];
            var location = input < (uint)controls.Length
                ? controls[(int)input] & 0x1Fu
                : input;
            if (location < 32u && usedLocations[(int)location])
            {
                location = input;
                while (location < 32u && usedLocations[(int)location])
                {
                    location++;
                }

                if (location >= 32u)
                {
                    throw new InvalidOperationException(
                        "pixel input locations exceed the hardware interface limit");
                }
            }

            locations[index] = location;
            if (location < 32u)
            {
                usedLocations[(int)location] = true;
            }
        }
    }
}
