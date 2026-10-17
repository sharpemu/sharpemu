// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Buffers;

public enum GpuBufferUsage : byte
{
    DeviceLocal,
    Upload,
    Download,
    Stream,
}
