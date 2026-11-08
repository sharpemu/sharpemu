// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.ShaderCompiler;

namespace SharpEmu.Libs.Gpu.Pipelines;

// The draw recognisers kept beside the ported cache: solid clears and fills.
internal sealed partial class ShaderPipelineCache
{
    // A float32x3 vertex position stream.
    private const uint PositionDataFormat = 13;
    private const uint PositionNumberFormat = 7;
    private const uint PositionExportTarget = 12;
    private const uint ColorExportTarget = 0;

    private static readonly bool _premultipliedFillClearEnabled =
        !string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_DISABLE_FILL_CLEAR"), "1", StringComparison.Ordinal);

    // A procedural fullscreen vertex program paired with a constant-color pixel program.
    private static bool IsProceduralFullscreenClearPair(
        Gen5ShaderProgram vertexProgram,
        VertexInputInfo vertexInfo,
        ShaderStageResources vertexStage,
        Gen5ShaderProgram pixelProgram,
        ShaderStageResources pixelStage)
    {
        var vertexResources = vertexStage.Program?.Resources?.Info;
        var pixelResources = pixelStage.Program?.Resources?.Info;
        if (vertexResources is null || pixelResources is null || vertexInfo.Attributes.Length != 0 ||
            vertexResources.Images.Count != 0 || pixelResources.Images.Count != 0 ||
            vertexResources.Buffers.Count != 0 || pixelResources.Buffers.Count != 0)
        {
            return false;
        }

        if (!HasExportTarget(vertexProgram, PositionExportTarget) || !HasExportTarget(pixelProgram, ColorExportTarget))
        {
            return false;
        }

        if (pixelProgram.Instructions.Count is 0 or > 8 || vertexProgram.Instructions.Count is 0 or > 48)
        {
            return false;
        }

        return pixelProgram.Instructions.All(IsBenignClearPixelInstruction) &&
               vertexProgram.Instructions.All(IsBenignProceduralVertexInstruction);
    }

    private static bool HasExportTarget(Gen5ShaderProgram program, uint target) =>
        program.Instructions.Any(instruction => instruction.Control is Gen5ExportControl export && export.Target == target);

    private static bool IsBenignClearPixelInstruction(Gen5ShaderInstruction instruction) =>
        instruction.Opcode is "SNop" or "SWaitcnt" or "SInstPrefetch" or "SEndpgm" or "VMovB32" ||
        instruction.Control is Gen5ExportControl { Target: ColorExportTarget };

    private static bool IsBenignProceduralVertexInstruction(Gen5ShaderInstruction instruction)
    {
        if (instruction.Control is Gen5BufferMemoryControl or Gen5ImageControl or Gen5GlobalMemoryControl or Gen5ScalarMemoryControl)
        {
            return false;
        }

        if (instruction.Control is Gen5ExportControl export)
        {
            // Position plus the primitive and parameter exports.
            return export.Target is PositionExportTarget or (>= 13 and < 32) or 20;
        }

        return instruction.Opcode is
            "SNop" or "SWaitcnt" or "SInstPrefetch" or "SEndpgm" or "SSendmsg" or
            "VMovB32" or "VAndB32" or "VAddI32" or "VLshlrevB32" or "VCvtF32I32" or "VCvtF32U32" ||
            instruction.Encoding is Gen5ShaderEncoding.Sop1 or Gen5ShaderEncoding.Sop2 or Gen5ShaderEncoding.Sopc or Gen5ShaderEncoding.Sopk or Gen5ShaderEncoding.Sopp;
    }

    // Opaque white unless the first user register carries a finite color in [0, 4].
    private static (float Red, float Green, float Blue, float Alpha) DecodeSolidClearColor(IReadOnlyList<uint> pixelUserData)
    {
        float red = 1f, green = 1f, blue = 1f, alpha = 1f;
        if (pixelUserData.Count > 0)
        {
            var bits = pixelUserData[0];
            if (bits != 0)
            {
                red = green = blue = alpha = BitConverter.UInt32BitsToSingle(bits);
                if (!float.IsFinite(red) || red < 0f || red > 4f)
                {
                    red = green = blue = alpha = 1f;
                }
            }
        }

        return (red, green, blue, alpha);
    }

    // An untextured transparent fill through premultiplied blending overwrites its targets.
    private static bool IsTransparentPremultipliedFill(
        ContextRegisters context,
        Gen5PixelOutputBinding[] outputs,
        VertexInputInfo vertexInfo,
        ShaderStageResources vertexStage,
        ShaderStageResources pixelStage)
    {
        var vertexResources = vertexStage.Program?.Resources?.Info;
        var pixelResources = pixelStage.Program?.Resources?.Info;
        var pixelUserData = pixelStage.Resources.UserData;
        if (!_premultipliedFillClearEnabled || vertexResources is null || pixelResources is null ||
            vertexResources.Images.Count != 0 || pixelResources.Images.Count != 0 ||
            vertexInfo.Attributes.Length != 0 || pixelUserData.Length < 4 || outputs.Length == 0)
        {
            return false;
        }

        foreach (var output in outputs)
        {
            var blend = context.BlendControls[output.GuestSlot];
            if (!blend.Enable || blend.ColorSourceFactor != 1 || blend.ColorDestinationFactor != 5 || blend.ColorFunction != 0)
            {
                return false;
            }
        }

        for (var index = 0; index < 4; index++)
        {
            if ((pixelUserData[index] & 0x7FFF_FFFFu) != 0)
            {
                return false;
            }
        }

        return true;
    }
}
