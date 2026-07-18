// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler;

public enum Gen5GraphicsStageMode
{
    Vertex,
    NggPassthrough,
}

public static class Gen5GraphicsAbi
{
    public const uint Wave32LaneCount = 32;
    public const uint MergedWaveInfoScalarRegister = 3;
    public const uint MergedWaveInfoEsLaneCountMask = 0xFF;

    public static uint SeedNggPassthroughMergedWaveInfo(uint mergedWaveInfo) =>
        (mergedWaveInfo & ~MergedWaveInfoEsLaneCountMask) | Wave32LaneCount;
}

public static class Gen5GraphicsStageClassifier
{
    public const uint VgtShaderStagesEnRegister = 0x2D5;

    private const uint PrimgenEnable = 1u << 13;
    private const uint PrimgenPassthroughEnable = 1u << 25;

    public static bool TryClassify(
        IReadOnlyDictionary<uint, uint> contextRegisters,
        out Gen5GraphicsStageMode stage,
        out string error)
    {
        contextRegisters.TryGetValue(VgtShaderStagesEnRegister, out var stagesEnabled);
        var primitiveGenerationEnabled = (stagesEnabled & PrimgenEnable) != 0;
        var passthroughEnabled = (stagesEnabled & PrimgenPassthroughEnable) != 0;
        if (!primitiveGenerationEnabled && !passthroughEnabled)
        {
            stage = Gen5GraphicsStageMode.Vertex;
            error = string.Empty;
            return true;
        }

        if (primitiveGenerationEnabled && passthroughEnabled)
        {
            stage = Gen5GraphicsStageMode.NggPassthrough;
            error = string.Empty;
            return true;
        }

        stage = default;
        error = passthroughEnabled
            ? $"invalid passthrough stage VGT_SHADER_STAGES_EN=0x{stagesEnabled:X8}"
            : $"unsupported primitive-generation stage VGT_SHADER_STAGES_EN=0x{stagesEnabled:X8}";
        return false;
    }
}
