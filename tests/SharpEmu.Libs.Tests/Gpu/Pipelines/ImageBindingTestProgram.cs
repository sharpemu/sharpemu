// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

internal static class ImageBindingTestProgram
{
    public static readonly StableImageDescriptorLimits Limits = new(16, 0, 4, 3, 19);
    public static readonly uint[][] Partitions =
    [
        [0, 1, 2, 1, 2, 1, 1, 1, 2, 2, 2, 2, 2, 2, 2, 2],
        [0, 1, 1, 1, 4, 1, 1, 1, 8, 8, 8, 8, 8, 8, 8, 8],
    ];

    public static uint[] Code()
    {
        // One sampler from +512, then sixteen image descriptors at +32*i.
        var words = new List<uint> { 0xF4080600, 0xFA000200 };
        for (uint index = 0; index < 16; index++)
        {
            words.AddRange([0xF40C0400, 0xFA000000 | (index * 32), // s_load_dwordx8 s[16:23], s[0:1]
                0xF09C0F08, 0x00C40400]); // image_sample_lz v[4:7], v[0:1], s[16:23], s[24:27], 2D
        }
        words.Add(0xBF810000);
        return [.. words];
    }

    public static void WriteDescriptors(PipelineTestGuest guest, ulong address, uint[] partition)
    {
        for (var index = 0; index < 16; index++)
            guest.WriteWords(address + (ulong)index * 32,
                0x20u + partition[index], 77u << 20, 3u | (3u << 14), 0xFACu | (9u << 28), 0, 0, 0, 0);
        guest.WriteWords(address + 512, 0, 0, 0, 0);
    }
}
