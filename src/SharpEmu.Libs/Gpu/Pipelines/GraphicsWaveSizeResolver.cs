// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Pipelines;

// Decodes the guest wave-mode bits carried outside the shader program registers.
internal static class GraphicsWaveSizeResolver
{
    private const uint VertexWave32Bit = 0x0040_0000;
    private const uint PixelWave32Bit = 1u << 15;

    public static uint Vertex(uint shaderStages) =>
        (shaderStages & VertexWave32Bit) != 0 ? 32u : 64u;

    public static uint Pixel(uint pixelInputControl) =>
        (pixelInputControl & PixelWave32Bit) != 0 ? 32u : 64u;
}
