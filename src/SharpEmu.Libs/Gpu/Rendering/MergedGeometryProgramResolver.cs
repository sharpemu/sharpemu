// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Rendering;

// Gen5 merged ES+GS programs are identified by the geometry-stage enable bit,
// not by one exact SHADER_STAGES register value. AGC may preserve additional
// wave/stage bits while registering the same validated front/back code pair.
internal static class MergedGeometryProgramResolver
{
    private const uint GeometryStageEnable = 0x20;

    public static bool HasMergedGeometryStage(uint shaderStages) =>
        (shaderStages & GeometryStageEnable) != 0;

    public static bool ContinuationMatches(ulong geometryAddress, ulong continuationAddress) =>
        geometryAddress == 0 ||
        (continuationAddress != 0 && geometryAddress == continuationAddress);

    public static bool IsActive(
        uint shaderStages,
        ulong exportAddress,
        ulong geometryAddress,
        bool registered,
        ulong continuationAddress) =>
        HasMergedGeometryStage(shaderStages) &&
        exportAddress != 0 &&
        registered &&
        ContinuationMatches(geometryAddress, continuationAddress);
}
