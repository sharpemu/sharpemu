// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Resources;

// Startup policy: changes resource planning and emitted shader code, never a per-dispatch value substitution.
// Compute programs read bounded scalar data that only the shader consumes on the GPU instead of through the flattened
// table; SHARPEMU_GPU_SCALAR_DATA=0 flattens those reads on the host.
public static class ScalarDataReadPolicy
{
    public static bool Enabled { get; } = Environment.GetEnvironmentVariable("SHARPEMU_GPU_SCALAR_DATA") != "0";
    public const uint CacheIdentity = 0x53445201;
}
