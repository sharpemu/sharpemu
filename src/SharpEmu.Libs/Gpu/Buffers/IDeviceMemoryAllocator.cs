// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Buffers;

public interface IDeviceMemoryAllocator
{
    Result AllocateMemory(in MemoryAllocateInfo info, out DeviceMemory memory);
    void FreeMemory(DeviceMemory memory);
}
