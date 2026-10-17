// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Scheduling;

namespace SharpEmu.Libs.Gpu.Pipelines;

// The pixel stage's static inputs: interpolators, system inputs and the color targets it exports to.
public static class PixelStageInputResolver
{
    private const uint InputPerspectiveSample = 0x0001;
    private const uint InputPerspectiveCenter = 0x0002;
    private const uint InputPerspectiveCentroid = 0x0004;
    private const uint InputPerspectivePull = 0x0008;
    private const uint InputLinearSample = 0x0010;
    private const uint InputLinearCenter = 0x0020;
    private const uint InputLinearCentroid = 0x0040;
    private const uint InputLineStipple = 0x0080;
    private const uint InputPositionX = 0x0100;
    private const uint InputPositionY = 0x0200;
    private const uint InputPositionZ = 0x0400;
    private const uint InputPositionW = 0x0800;
    private const uint InputFrontFace = 0x1000;
    private const uint InputAncillary = 0x2000;
    private const uint InputSampleCoverage = 0x4000;
    private const uint InputPositionFixedPoint = 0x8000;
    private const uint SupportedInputBits =
        InputPerspectiveSample | InputPerspectiveCenter | InputPerspectiveCentroid | InputPerspectivePull |
        InputLinearSample | InputLinearCenter | InputLinearCentroid | InputLineStipple |
        InputPositionX | InputPositionY | InputPositionZ | InputPositionW | InputFrontFace |
        InputAncillary | InputSampleCoverage | InputPositionFixedPoint;

    // The first register after the interpolation inputs the hardware fills.
    public static uint CountSystemInputRegisters(ShaderInterfaceRegisters shaderInterface, ulong shaderAddress)
    {
        var enable = shaderInterface.PixelInputEnable;
        var address = shaderInterface.PixelInputAddress;
        if ((enable & ~SupportedInputBits) != 0 || (address & ~SupportedInputBits) != 0 || enable != address)
        {
            throw SubmissionScheduler.Fatal(
                $"The pixel input registers are not supported: shader=0x{shaderAddress:X16} enable=0x{enable:X8} address=0x{address:X8}.");
        }

        var registers = 0u;
        if ((address & InputPerspectiveSample) != 0) registers += 2;
        if ((address & InputPerspectiveCenter) != 0) registers += 2;
        if ((address & InputPerspectiveCentroid) != 0) registers += 2;
        if ((address & InputPerspectivePull) != 0) registers += 3;
        if ((address & InputLinearSample) != 0) registers += 2;
        if ((address & InputLinearCenter) != 0) registers += 2;
        if ((address & InputLinearCentroid) != 0) registers += 2;
        if ((address & InputLineStipple) != 0) registers += 1;
        return registers;
    }

    public static PixelInputInfo Resolve(
        CpuContext context,
        RegisteredShader shader,
        ShaderInterfaceRegisters shaderInterface,
        ReadOnlySpan<byte> targetOutputModes,
        ReadOnlySpan<ColorComponentMap> targetExportMapping,
        uint inputCount)
    {
        var activeInputs = shaderInterface.PixelInputEnable & shaderInterface.PixelInputAddress;
        var customMask = 0u;
        var semanticCount = Math.Min(Math.Min(shader.InputSemanticsCount, inputCount), (uint)PixelInputInfo.InterpolatorCount);
        for (var index = 0u; index < semanticCount; index++)
        {
            if (!context.TryReadUInt32(shader.InputSemanticsAddress + index * sizeof(uint), out var word))
            {
                throw SubmissionScheduler.Fatal($"The pixel input semantics are unreadable: shader=0x{shader.CodeAddress:X16} index={index}.");
            }

            var semantic = new ShaderInputSemantic(word);
            if (semantic.IsCustom && semantic.IsHalfFloat == 0)
            {
                customMask |= 1u << (int)index;
            }
        }

        var interpolators = new uint[PixelInputInfo.InterpolatorCount];
        for (var index = 0u; index < inputCount; index++)
        {
            // An unwritten slot maps attribute n to parameter n.
            interpolators[index] = (shaderInterface.PixelInterpolatorWritten & (1u << (int)index)) != 0
                ? shaderInterface.PixelInterpolatorSettings[index]
                : index;
        }

        var outputModes = new byte[PixelInputInfo.TargetCount];
        var mappings = new ColorComponentMap[PixelInputInfo.TargetCount];
        for (var index = 0; index < PixelInputInfo.TargetCount; index++)
        {
            outputModes[index] = targetOutputModes[index];
            mappings[index] = outputModes[index] != 0 ? targetExportMapping[index] : default;
        }

        var control = shaderInterface.DepthShaderControl;
        return new PixelInputInfo
        {
            InputCount = inputCount,
            SystemInputBase = CountSystemInputRegisters(shaderInterface, shader.CodeAddress),
            CustomInterpolationMask = customMask,
            PerspectiveCenterRegister = (activeInputs & InputPerspectiveCenter) != 0
                ? ((activeInputs & InputPerspectiveSample) != 0 ? 2u : 0u)
                : PixelInputInfo.NoPerspectiveCenterRegister,
            InterpolatorSettings = interpolators,
            TargetOutputModes = outputModes,
            TargetExportMappings = mappings,
            ScratchDwords = shader.ScratchDwords,
            PositionX = (activeInputs & InputPositionX) != 0,
            PositionY = (activeInputs & InputPositionY) != 0,
            PositionZ = (activeInputs & InputPositionZ) != 0,
            PositionW = (activeInputs & InputPositionW) != 0,
            FrontFace = (activeInputs & InputFrontFace) != 0,
            SampleShading = (activeInputs & (InputPerspectiveSample | InputLinearSample)) != 0,
            NoPerspective = (activeInputs & InputLinearCenter) != 0,
            KillEnable = control.KillEnable,
            DepthExportEnable = control.DepthExportEnable,
            SampleMaskExportEnable = control.MaskExportEnable,
            EarlyDepth = control.DepthExportOrder == 1 && !control.KillEnable && !control.DepthExportEnable && !control.MaskExportEnable,
            ExecuteOnNoop = control.ExecuteOnNoop,
        };
    }
}
