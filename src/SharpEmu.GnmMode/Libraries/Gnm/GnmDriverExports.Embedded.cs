// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using SharpEmu.GnmMode.Libraries.VideoOut;
using SharpEmu.HLE;

namespace SharpEmu.GnmMode.Libraries.Gnm;

public static partial class GnmDriverExports
{
    private static readonly ConditionalWeakTable<ICpuMemory, EmbeddedShader> _embeddedVertexShaders = new();
    private static readonly ConditionalWeakTable<ICpuMemory, EmbeddedShader[]> _embeddedPixelShaders = new();
    private static readonly byte[] _embeddedVertexBinary = CreateEmbeddedVertexBinary();
    private static readonly byte[][] _embeddedPixelBinaries = [CreateEmbeddedPixelBinary(15), CreateEmbeddedPixelBinary(3)];

    private sealed class EmbeddedShader
    {
        public object Gate { get; } = new();
        public ulong Address { get; set; }
    }

    internal static void ResetEmbeddedShaders(ICpuMemory memory)
    {
        var root = UnwrapMemory(memory);
        _embeddedVertexShaders.Remove(root);
        _embeddedPixelShaders.Remove(root);
    }

    public static int SetEmbeddedVsShader(CpuContext context)
    {
        if ((uint)context[CpuRegister.Rdx] != 0)
            return context.SetReturn(unchecked((int)0x8EEE00FF));
        var modifier = (uint)context[CpuRegister.Rcx];
        if (context[CpuRegister.Rdi] == 0 || (uint)context[CpuRegister.Rsi] < 29 || (modifier & 0xFCFFFC3F) != 0)
            return context.SetReturn(-1);
        var memory = UnwrapMemory(context.Memory);
        var shader = _embeddedVertexShaders.GetValue(memory, static _ => new EmbeddedShader());
        if (!TryGetEmbeddedShader(context, shader, _embeddedVertexBinary, out var address))
            return context.SetReturn(-1);
        Span<uint> registers = [(uint)(address >> 8), (uint)(address >> 40), 0xC0000, 4, 0, 4, 0];
        return WriteGraphicsShader(context, GraphicsStage.Vs, registers, modifier, 29);
    }

    public static int SetEmbeddedPsShader(CpuContext context)
    {
        var id = (uint)context[CpuRegister.Rdx];
        if (id > 1)
            return context.SetReturn(unchecked((int)0x8EEE00FF));
        if (context[CpuRegister.Rdi] == 0 || (uint)context[CpuRegister.Rsi] < 40)
            return context.SetReturn(-1);
        var shaders = _embeddedPixelShaders.GetValue(UnwrapMemory(context.Memory), static _ => [new EmbeddedShader(), new EmbeddedShader()]);
        if (!TryGetEmbeddedShader(context, shaders[id], _embeddedPixelBinaries[id], out var address))
            return context.SetReturn(-1);
        Span<uint> registers = [(uint)(address >> 8), (uint)(address >> 40), id == 0 ? 0xC0000u : 0x2000u,
            id == 0 ? 4u : 0u, 0, id == 0 ? 4u : 2u, 2, 2, 0, 0, 0x10, id == 0 ? 15u : 3u];
        return WriteGraphicsShader(context, GraphicsStage.Ps, registers, 0, 40, pixel350: true, bindPixel: true);
    }

    private static bool TryGetEmbeddedShader(CpuContext context, EmbeddedShader shader, byte[] binary, out ulong address)
    {
        lock (shader.Gate)
        {
            if (shader.Address == 0)
            {
                if (!GnmVideoOutExports.TryAllocateTrackedShadow(context, (ulong)binary.Length, out address) ||
                    !UnwrapMemory(context.Memory).TryWrite(address, binary))
                    return false;
                shader.Address = address;
            }
            address = shader.Address;
            return true;
        }
    }

    private static byte[] CreateEmbeddedVertexBinary()
    {
        ReadOnlySpan<uint> code =
        [
            0xBEEB03FF, 7,
            EmbeddedVop2(27, 1, 129, 0),
            EmbeddedVop2(27, 0, 194, 0),
            EmbeddedVop2(26, 1, 129, 1),
            EmbeddedVop1(5, 1, 257),
            EmbeddedVop1(5, 0, 256),
            EmbeddedVop2(3, 1, 243, 1),
            EmbeddedVop2(3, 0, 243, 0),
            EmbeddedVop1(1, 2, 128),
            EmbeddedVop1(1, 3, 242),
            0xF80008CF, 0x03020001,
            0xF800020F, 0x03030303,
            0xBF810000,
        ];
        return CreateEmbeddedBinary(code, 1);
    }

    private static byte[] CreateEmbeddedPixelBinary(uint mask) => CreateEmbeddedBinary(
        [0xBEEB03FF, 0, EmbeddedVop1(1, 2, 128), 0xF8001800 | mask, 0x02020202, 0xBF810000], 0);

    private static byte[] CreateEmbeddedBinary(ReadOnlySpan<uint> code, uint shaderType)
    {
        var size = code.Length * sizeof(uint);
        var info = (size + 7) & ~7;
        var binary = new byte[info + 28];
        for (var index = 0; index < code.Length; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(binary.AsSpan(index * sizeof(uint)), code[index]);
        BinaryPrimitives.WriteUInt32LittleEndian(binary.AsSpan(4), (uint)info / 8 - 1);
        "OrbShdr"u8.CopyTo(binary.AsSpan(info));
        binary[info + 7] = 7;
        BinaryPrimitives.WriteUInt32LittleEndian(binary.AsSpan(info + 8), ((uint)size << 8) | (shaderType << 2) | 1);
        return binary;
    }

    private static uint EmbeddedVop1(uint opcode, uint target, uint source) =>
        0x7E000000 | (target << 17) | (opcode << 9) | source;

    private static uint EmbeddedVop2(uint opcode, uint target, uint source, uint vector) =>
        (opcode << 25) | (target << 17) | (vector << 9) | source;
}
