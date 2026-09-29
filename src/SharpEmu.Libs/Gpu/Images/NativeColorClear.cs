// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Images;

internal static class NativeColorClear
{
    internal static ulong SliceSize(uint width, uint height, uint bytesPerPixel)
    {
        var block = bytesPerPixel switch
        {
            1 => (Width: 1024u, Height: 1024u),
            2 => (Width: 1024u, Height: 512u),
            4 => (Width: 512u, Height: 512u),
            8 => (Width: 512u, Height: 256u),
            16 => (Width: 256u, Height: 256u),
            _ => (Width: 0u, Height: 0u),
        };
        if (width == 0 || height == 0 || block.Width == 0) return 0;
        return ((width + (ulong)block.Width - 1) / block.Width) *
            ((height + (ulong)block.Height - 1) / block.Height) * 4096;
    }

    internal static bool TryDecode(byte code, Format format, bool alphaOnLeastSignificantBits, out ClearColorValue color)
    {
        color = default;
        if (code is not (0x00 or 0x40 or 0x80 or 0xc0)) return false;
        var rgb = (code & 0x80) != 0 ? 1f : 0f;
        var alpha = (code & 0x40) != 0 ? 1f : 0f;
        var first = alphaOnLeastSignificantBits ? alpha : rgb;
        var last = alphaOnLeastSignificantBits ? rgb : alpha;
        ClearColorValue? decoded = format switch
        {
            Format.R8Unorm or Format.R16Unorm or Format.R16Sfloat or Format.R32Sfloat =>
                new ClearColorValue(last, rgb, rgb, last),
            Format.R8G8Unorm or Format.R16G16Unorm or Format.R16G16Sfloat or Format.R32G32Sfloat =>
                new ClearColorValue(first, last, rgb, last),
            Format.B8G8R8A8Unorm or Format.B8G8R8A8Srgb or Format.A2R10G10B10UnormPack32 or
                Format.A1R5G5B5UnormPack16 or Format.R5G6B5UnormPack16 =>
                new ClearColorValue(rgb, rgb, first, last),
            Format.R4G4B4A4UnormPack16 => new ClearColorValue(last, rgb, rgb, first),
            Format.R8G8B8A8Unorm or Format.R8G8B8A8Srgb or Format.A2B10G10R10UnormPack32 or
                Format.R16G16B16A16Unorm or Format.R16G16B16A16Sfloat or Format.R32G32B32A32Sfloat or
                Format.B10G11R11UfloatPack32 => new ClearColorValue(first, rgb, rgb, last),
            _ => null,
        };
        color = decoded.GetValueOrDefault();
        return decoded.HasValue;
    }

    internal static bool IsUniform(ReadOnlySpan<byte> bytes, byte code) => !bytes.IsEmpty && bytes.IndexOfAnyExcept(code) < 0;
}
