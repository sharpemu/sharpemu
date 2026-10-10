// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler;

// Runtime ABI shared by the merged hull kernel and the native domain pipeline.
public static class Gen5TessellationData
{
    public const uint DwordCount = 19;
    public const uint FirstPatch = 0, PatchCount = 1, VertexOffset = 2, InstanceId = 3;
    public const uint IndexAddress = 4, IndexSize = 6;
    public const uint FactorAddress = 7, FactorBytes = 9;
    public const uint OffchipOffset = 10, FactorOffset = 11;
    public const uint MinimumLevel = 12, MaximumLevel = 13;
    public const uint IndexByteOffset = 14;
    // One dispatch runs several hull groups. Each takes its own off-chip slot and its own
    // run of the factor ring, as the hardware gives each threadgroup its buffer and offset.
    // GroupPatches of zero is a single group in slot zero.
    public const uint GroupPatches = 15, OffchipSlotBytes = 16, FactorGroupBytes = 17;
    // Patches of one instance. FirstPatch and PatchCount then number the patches of all
    // instances in a row, and each thread splits its patch into an instance and a patch
    // within it. Zero keeps a single instance.
    public const uint PatchesPerInstance = 18;
}
