// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Gpu.ShaderCache;

namespace SharpEmu.Libs.Tests.Gpu.ShaderCache;

internal static class AgcHeaderBuilder
{
    private const int SpecialsBytes = 48;

    public static readonly int[] PointerFields = [24, 32, 40, 48, 56];

    public static byte[] Build(
        byte type,
        int codeSize,
        (uint Offset, uint Value)[]? shaderRegisters = null,
        (uint Offset, uint Value)[]? contextRegisters = null,
        uint[]? inputSemantics = null,
        uint[]? outputSemantics = null,
        uint dispatchModifier = 0,
        ushort scratchDwords = 0)
    {
        shaderRegisters ??= [];
        contextRegisters ??= [];
        inputSemantics ??= [];
        outputSemantics ??= [];
        var shaderStart = AgcShaderHeader.FixedBytes;
        var contextStart = shaderStart + shaderRegisters.Length * 8;
        var specialsStart = contextStart + contextRegisters.Length * 8;
        var inputStart = specialsStart + SpecialsBytes;
        var outputStart = inputStart + inputSemantics.Length * 4;
        var size = outputStart + outputSemantics.Length * 4;
        var header = new byte[size];
        AgcShaderHeader.Signature.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(64), (uint)size);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(68), (uint)codeSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(80), (uint)inputSemantics.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(84), scratchDwords);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(86), (ushort)outputSemantics.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(88), SpecialsBytes);
        header[90] = type;
        header[91] = (byte)contextRegisters.Length;
        header[92] = (byte)shaderRegisters.Length;
        void Pointer(int field, int target, bool present)
        {
            if (present)
            {
                BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(field), target - field);
            }
        }

        Pointer(32, shaderStart, shaderRegisters.Length != 0);
        Pointer(24, contextStart, contextRegisters.Length != 0);
        Pointer(40, specialsStart, true);
        Pointer(48, inputStart, inputSemantics.Length != 0);
        Pointer(56, outputStart, outputSemantics.Length != 0);
        for (var index = 0; index < shaderRegisters.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(shaderStart + index * 8), shaderRegisters[index].Offset);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(shaderStart + index * 8 + 4), shaderRegisters[index].Value);
        }

        for (var index = 0; index < contextRegisters.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(contextStart + index * 8), contextRegisters[index].Offset);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(contextStart + index * 8 + 4), contextRegisters[index].Value);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(specialsStart + 16), dispatchModifier);
        for (var index = 0; index < inputSemantics.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(inputStart + index * 4), inputSemantics[index]);
        }

        for (var index = 0; index < outputSemantics.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(outputStart + index * 4), outputSemantics[index]);
        }

        return header;
    }

    public static byte[] WithAbsolutePointers(byte[] header, ulong headerAddress)
    {
        var absolute = (byte[])header.Clone();
        foreach (var field in PointerFields)
        {
            var relative = BinaryPrimitives.ReadInt64LittleEndian(absolute.AsSpan(field));
            if (relative != 0)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(absolute.AsSpan(field), headerAddress + (ulong)(field + relative));
            }
        }

        return absolute;
    }

    public static byte[] Code(uint[] words)
    {
        var code = new byte[words.Length * sizeof(uint)];
        for (var index = 0; index < words.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(code.AsSpan(index * sizeof(uint)), words[index]);
        }

        return code;
    }
}
