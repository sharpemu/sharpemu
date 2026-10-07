// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Buffers;

public enum GpuBufferUsage : byte
{
    DeviceLocal,
    Upload,
    Download,
    Stream,
    // Device-local memory the host can map: guest buffers on unified-memory devices, read back without a copy.
    Unified,
}
