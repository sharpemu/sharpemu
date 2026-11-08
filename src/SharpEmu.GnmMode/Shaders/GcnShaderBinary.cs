// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;

namespace SharpEmu.GnmMode.Shaders;

public readonly record struct GcnShaderBinary(ulong InfoAddress, uint CodeSize, byte ShaderType)
{
    public static GcnShaderBinary Read(ICpuMemory memory, ulong codeAddress)
    {
        const uint maximumCodeSize = 1024 * 1024;
        Span<byte> head = stackalloc byte[8];
        if (codeAddress == 0 || !memory.TryRead(codeAddress, head) ||
            BinaryPrimitives.ReadUInt32LittleEndian(head) != 0xBEEB03FF)
            throw new InvalidDataException("The GCN binary has no OrbShdr locator.");

        var offset = ((ulong)BinaryPrimitives.ReadUInt32LittleEndian(head[4..]) + 1) * 8;
        if (offset > maximumCodeSize || offset < 8 || codeAddress > ulong.MaxValue - offset - 28)
            throw new InvalidDataException("The GCN metadata offset is outside the shader object.");
        var infoAddress = codeAddress + offset;
        Span<byte> info = stackalloc byte[28];
        if (!memory.TryRead(infoAddress, info) || !info[..7].SequenceEqual("OrbShdr"u8))
            throw new InvalidDataException("The GCN OrbShdr metadata is unreadable or has an invalid signature.");
        if (info[7] != 7)
            throw new InvalidDataException($"Unsupported GCN binary version {info[7]}.");

        var flags = BinaryPrimitives.ReadUInt32LittleEndian(info[8..]);
        var size = flags >> 8;
        if (size < 12 || size % 4 != 0 || size > offset)
            throw new InvalidDataException($"Invalid GCN code size 0x{size:X} (metadata offset 0x{offset:X}).");
        return new GcnShaderBinary(infoAddress, size, (byte)((flags >> 2) & 15));
    }
}
