// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler;

// Enumerates architectural VGPR writes, including result registers which the
// decoder represents through a base destination plus instruction metadata.
// Analyses which preserve per-register facts must use this instead of looking
// only at Gen5ShaderInstruction.Destinations.
public static class Gen5VectorRegisterWrites
{
    public static IEnumerable<uint> Enumerate(Gen5ShaderInstruction instruction)
    {
        foreach (var destination in instruction.Destinations)
        {
            if (destination.Kind == Gen5OperandKind.VectorRegister)
            {
                yield return destination.Value;
            }
        }

        var firstVectorDestination = instruction.Destinations.FirstOrDefault(
            static destination => destination.Kind == Gen5OperandKind.VectorRegister);
        if (firstVectorDestination.Kind == Gen5OperandKind.VectorRegister &&
            instruction.Opcode is
                "VLshlrevB64" or
                "VLshrrevB64" or
                "VMadU64U32" or
                "VCvtF64I32" or
                "VRcpF64")
        {
            yield return firstVectorDestination.Value + 1;
        }

        if (instruction.Control is not Gen5ImageControl image ||
            firstVectorDestination.Kind != Gen5OperandKind.VectorRegister)
        {
            yield break;
        }

        var resultDwords = ImageResultDwords(instruction.Opcode, image);
        // VDST itself was yielded from Destinations above. MIMG decoding keeps
        // only that base register even when the instruction writes more data.
        for (uint component = 1; component < resultDwords; component++)
        {
            yield return image.VectorData + component;
        }
    }

    private static uint ImageResultDwords(string opcode, Gen5ImageControl image)
    {
        if (opcode is "ImageBvhIntersectRay" or "ImageBvh64IntersectRay")
        {
            return 4;
        }

        if (opcode.StartsWith("ImageAtomic", StringComparison.Ordinal))
        {
            return 1;
        }

        if (opcode == "ImageGetLod")
        {
            var mask = image.Dmask != 0 ? image.Dmask & 0x3u : 1u;
            return Math.Max(CountBits(mask), 1u);
        }

        if (opcode == "ImageGetResinfo")
        {
            return Math.Max(CountBits(image.Dmask & 0xFu), 1u);
        }

        var components = opcode.StartsWith("ImageGather", StringComparison.Ordinal)
            ? 4u
            : Math.Max(CountBits(image.Dmask & 0xFu), 1u);
        return image.D16 ? (components + 1) / 2 : components;
    }

    private static uint CountBits(uint value)
    {
        value -= (value >> 1) & 0x5555_5555u;
        value = (value & 0x3333_3333u) + ((value >> 2) & 0x3333_3333u);
        return (((value + (value >> 4)) & 0x0F0F_0F0Fu) * 0x0101_0101u) >> 24;
    }
}
