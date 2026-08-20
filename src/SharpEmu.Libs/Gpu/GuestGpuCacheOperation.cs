// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu;

[Flags]
internal enum GuestGpuCacheDomain
{
    None = 0,
    Instruction = 1 << 0,
    Scalar = 1 << 1,
    Vector = 1 << 2,
    ShaderL1 = 1 << 3,
    ShaderL2 = 1 << 4,
    Color = 1 << 5,
    Depth = 1 << 6,
    Metadata = 1 << 7,
}

[Flags]
internal enum GuestGpuCacheAction
{
    None = 0,
    MakeAvailable = 1 << 0,
    MakeVisible = 1 << 1,
    Invalidate = 1 << 2,
    WriteBack = 1 << 3,
    Discard = 1 << 4,
}

internal readonly record struct GuestGpuCacheOperation(
    GuestGpuCacheDomain Domains,
    GuestGpuCacheAction Actions,
    ulong BaseAddress,
    ulong SizeBytes,
    bool CoversAllMemory,
    uint RawCbDbControl,
    uint RawGcrControl);
