// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Images;

namespace SharpEmu.Libs.Tests.Gpu.Images;

// Raw register and descriptor words for the request builders, encoded the way the guest writes them.
internal static class RegisterWords
{
    // Destination selects X=R, Y=G, Z=B, W=A.
    public const uint IdentitySelect = 0xFAC;

    public static ColorTargetWords Color(
        ulong address,
        uint width,
        uint height,
        GuestTileMode tile = GuestTileMode.Linear,
        uint maxMip = 0,
        uint mipLevel = 0,
        uint sliceMax = 0,
        uint sliceStart = 0,
        uint samplesLog2 = 0,
        uint fragmentsLog2 = 0,
        uint dimension = 1,
        uint depth = 0,
        ChannelLayout layout = ChannelLayout.Bits8_8_8_8,
        ChannelType type = ChannelType.UNorm)
    {
        var view = sliceStart | (sliceMax << 13) | (mipLevel << 26);
        var info = ((uint)layout << 2) | ((uint)type << 8);
        var attrib = (samplesLog2 << 12) | (fragmentsLog2 << 15);
        var attrib2 = (height - 1) | ((width - 1) << 14) | (maxMip << 28);
        var attrib3 = depth | ((uint)tile << 14) | (dimension << 24);
        return new ColorTargetWords(address, view, info, attrib, attrib2, attrib3, 0, 0, 0, 0, 0);
    }

    public static DepthTargetWords Depth(
        ulong depthBase,
        uint width,
        uint height,
        GuestDepthFormat format = GuestDepthFormat.Z32Float,
        ulong stencilBase = 0,
        bool depthTest = true,
        bool depthWrite = true,
        bool depthClear = false,
        uint samplesLog2 = 0)
    {
        var zInfo = (uint)format | (samplesLog2 << 2);
        var stencilInfo = stencilBase != 0 ? 1u : 0u;
        var size = (width - 1) | ((height - 1) << 16);
        var renderControl = depthClear ? 1u : 0u;
        var depthControl = (depthTest ? 2u : 0u) | (depthWrite ? 4u : 0u) | (7u << 4);
        return new DepthTargetWords(zInfo, stencilInfo, 0, size, true, 0, renderControl, depthControl, depthBase, depthBase, stencilBase, stencilBase, 0);
    }

    public static uint[] Texture(
        ulong address,
        GuestPixelFormat format,
        uint width,
        uint height,
        GuestImageType type = GuestImageType.Color2D,
        GuestTileMode tile = GuestTileMode.Linear,
        uint baseLevel = 0,
        uint lastLevel = 0,
        uint maxMip = 0,
        uint layers = 1,
        uint baseArray = 0)
    {
        var shifted = address >> 8;
        var lastX = width - 1;
        var lastY = height - 1;
        return
        [
            (uint)shifted,
            ((uint)(shifted >> 32) & 0xFF) | ((uint)format << 20) | ((lastX & 0x3) << 30),
            ((lastX >> 2) & 0xFFF) | (lastY << 14),
            IdentitySelect | (baseLevel << 12) | (lastLevel << 16) | ((uint)tile << 20) | ((uint)type << 28),
            (layers - 1) | (baseArray << 16),
            maxMip << 4,
            0,
            0,
        ];
    }
}
