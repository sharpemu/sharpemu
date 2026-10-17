// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Rendering;

// Only draw preparation can handle this rejection. Image creation keeps its fatal checks.
internal sealed class DrawImageTypeMismatchException(
    ulong shaderHash,
    ulong imageAddress,
    ImageType imageType,
    ImageViewType viewType) : Exception(
        $"The draw image type does not support its view: hash=0x{shaderHash:X16} " +
        $"address=0x{imageAddress:X16} imageType={(int)imageType} viewType={(int)viewType}.")
{
    public (ulong ShaderHash, ImageType ImageType, ImageViewType ViewType) WarningKey =>
        (shaderHash, imageType, viewType);
}
