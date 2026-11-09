// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Buffers;

// Answers whether the device can create an image with the given format and usage.
public interface IImageFormatSupport
{
    bool TryGetImageFormatProperties(Format format, ImageType type, ImageTiling tiling, ImageUsageFlags usage, ImageCreateFlags flags, out ImageFormatProperties properties);
}
