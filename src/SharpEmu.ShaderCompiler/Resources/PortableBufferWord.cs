// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Resources;

public static class PortableBufferWord
{
    public const int DwordCount = 2;
    public const uint StrideMask = 0x3FFF;

    public static bool ReadsFormatAtRuntime(BufferResource buffer) => buffer.Formatted && !buffer.Written;

    public static (uint Stride, uint DescriptorWord3) Pack(ReadOnlySpan<uint> descriptor)
    {
        if (descriptor.Length != 4 || (descriptor[3] >> 30) != 0)
        {
            return default;
        }

        return ((descriptor[1] >> 16) & StrideMask, descriptor[3]);
    }
}
