// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.IO.Hashing;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.Libs.Gpu.ShaderCache;

internal sealed record StaticGraphicsState(
    PipelineRenderingState Rendering,
    PipelineStaticParameters StaticParameters,
    IReadOnlyList<Gen5PixelOutputBinding> PixelOutputs,
    ShaderClipSpaceTransform ClipSpace,
    ulong Identity);

internal static class StaticStageInputs
{
    private const uint VertexUserDataBase = 8;
    private const ulong HeaderAddress = 0x20_0000_0000;
    private const uint SupportedPixelInputs = 0xFFFF;
    private const uint MaxPixelInputs = 32;
    private const uint DispatchInitiatorMask = 0xA038;
    private const uint DispatchInitiatorBase = 0x41;

    public static bool TryParse(CachedProgram program, out AgcShaderHeader header) =>
        AgcShaderHeader.TryParse(program.Program.Header, 0, out header);

    public static bool TryCompute(CachedProgram program, bool computeWave64Supported, out StageRecord record)
    {
        record = null!;
        if (!TryParse(program, out var header) || header.Type != AgcShaderType.Compute ||
            !TryApplyRegisters(header, out var banks))
        {
            return false;
        }

        var compute = banks.Shader.Compute;
        var shader = Registered(program, header);
        var initiator = (header.DispatchModifier & DispatchInitiatorMask) | DispatchInitiatorBase;
        record = new StageRecord
        {
            Stage = ShaderStage.Compute,
            Hash = program.Code.Hash,
            CodeSize = program.Code.CodeSize,
            Address = program.Code.Address,
            UserDataBase = 0,
            UserDataCount = compute.UserScalarCount,
            PushDataCursor = 0,
            Compute = ComputeStageInputResolver.Resolve(compute, shader, initiator, !computeWave64Supported, 0, 0, 0),
            ComputeSystemRegisters = ShaderPipelineCache.DecodeComputeSystemRegisters(compute),
        };
        return true;
    }

    public static ulong PixelSignature(AgcShaderHeader header)
    {
        uint colorFormat = 0, depthControl = 0, colorMask = 0;
        foreach (var register in header.ContextRegisters)
        {
            switch (register.Offset)
            {
                case ContextRegisterOffset.SpiShaderColFormat:
                    colorFormat = register.Value;
                    break;
                case ContextRegisterOffset.DbShaderControl:
                    depthControl = register.Value & 0x107;
                    break;
                case ContextRegisterOffset.CbShaderMask:
                    colorMask = register.Value;
                    break;
            }
        }

        Span<uint> values = [colorFormat, depthControl, colorMask];
        return XxHash3.HashToUInt64(System.Runtime.InteropServices.MemoryMarshal.AsBytes(values));
    }

    public static bool TryGraphics(
        CachedProgram vertex,
        CachedProgram pixel,
        StaticGraphicsState state,
        bool secondInterpolantVariant,
        out StageRecord vertexRecord,
        out StageRecord pixelRecord)
    {
        vertexRecord = pixelRecord = null!;
        if (!TryParse(vertex, out var vertexHeader) || vertexHeader.Type != AgcShaderType.Geometry || vertexHeader.HasVertexFetchTables ||
            !TryParse(pixel, out var pixelHeader) || pixelHeader.Type != AgcShaderType.Pixel ||
            !TryApplyRegisters(vertexHeader, out var banks) || !TryApplyRegisters(pixelHeader, banks))
        {
            return false;
        }

        var interpolators = AgcExports.ComputeInterpolantMapping(pixelHeader.InputSemantics, vertexHeader.OutputSemantics, secondInterpolantVariant);
        for (var index = 0u; index < (uint)interpolators.Length; index++)
        {
            if (!TryWrite(RegisterWriteTable.ContextIndirect, banks, ContextRegisterOffset.SpiPsInputCntl0 + index, interpolators[index]))
            {
                return false;
            }
        }

        var shaderInterface = banks.Context.ShaderInterface;
        var enable = shaderInterface.PixelInputEnable;
        var address = shaderInterface.PixelInputAddress;
        if (((enable | address) & ~SupportedPixelInputs) != 0 || enable != address ||
            !Gen5ShaderTranslator.TryDecodeProgram(pixel.Code.CreateContext(), pixel.Code.Address, out var pixelProgram, out _))
        {
            return false;
        }

        var attributeCount = ShaderPipelineCache.InterpolatedAttributeCount(pixelProgram);
        var inputCount = shaderInterface.PixelInputControl & 0x3Fu;
        if (inputCount == 0)
        {
            inputCount = Math.Min(attributeCount, MaxPixelInputs);
        }

        if (inputCount > MaxPixelInputs)
        {
            return false;
        }

        var headerMemory = new CpuContext(new ReplayCpuMemory([new CodeRange(HeaderAddress, pixelHeader.Bytes)]), Generation.Gen5);
        var info = PixelStageInputResolver.Resolve(
            headerMemory, Registered(pixel, pixelHeader), shaderInterface,
            new byte[PixelInputInfo.TargetCount], new ColorComponentMap[PixelInputInfo.TargetCount], inputCount);
        var requiredOutputs = Math.Max(attributeCount, ShaderPipelineCache.ReadVertexOutputCount(pixelProgram, info));
        var cntl = new uint[info.InputCount];
        Array.Copy(info.InterpolatorSettings, cntl, cntl.Length);
        pixelRecord = new StageRecord
        {
            Stage = ShaderStage.Pixel,
            Hash = pixel.Code.Hash,
            CodeSize = pixel.Code.CodeSize,
            Address = pixel.Code.Address,
            UserDataBase = 0,
            UserDataCount = banks.Shader.Pixel.Resource2.UserScalarCount,
            PushDataCursor = 0,
            Pixel = new PixelStageInputs(info.ScratchDwords, state.PixelOutputs, enable, info.CustomInterpolationMask, address, cntl),
        };
        vertexRecord = new StageRecord
        {
            Stage = ShaderStage.Vertex,
            Hash = vertex.Code.Hash,
            CodeSize = vertex.Code.CodeSize,
            Address = vertex.Code.Address,
            UserDataBase = VertexUserDataBase,
            UserDataCount = banks.Shader.Vertex.GeometryResource2.UserScalarCount,
            PushDataCursor = 0,
            Vertex = new VertexStageInputs(
                false, 0, 0, [], vertexHeader.ScratchDwords, (int)requiredOutputs, shaderInterface.VertexOutputControl, state.ClipSpace),
        };
        return true;
    }

    private static RegisteredShader Registered(CachedProgram program, AgcShaderHeader header) => new(
        program.Code.Address,
        HeaderAddress,
        program.Code.CodeSize,
        header.ScratchDwords,
        0,
        HeaderAddress + (ulong)header.InputSemanticsOffset,
        header.InputSemanticCount,
        0,
        0);

    private static bool TryApplyRegisters(AgcShaderHeader header, out RegisterBanks banks)
    {
        banks = new RegisterBanks(static message => new InvalidDataException(message));
        return TryApplyRegisters(header, banks);
    }

    private static bool TryApplyRegisters(AgcShaderHeader header, RegisterBanks banks)
    {
        foreach (var register in header.ShaderRegisters)
        {
            if (!TryWrite(RegisterWriteTable.ShaderIndirect, banks, register.Offset, register.Value))
            {
                return false;
            }
        }

        foreach (var register in header.ContextRegisters)
        {
            if (!TryWrite(RegisterWriteTable.ContextIndirect, banks, register.Offset, register.Value))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryWrite(RegisterWriter?[] table, RegisterBanks banks, uint offset, uint value)
    {
        if (offset >= (uint)table.Length || table[offset] is not { } writer)
        {
            return false;
        }

        try
        {
            writer(banks, offset, value);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }
}
