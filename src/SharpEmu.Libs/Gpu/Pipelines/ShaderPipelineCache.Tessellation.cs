// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.Libs.Gpu.Pipelines;

internal sealed partial class ShaderPipelineCache
{
    internal static uint[] MergedHullUserData(UserScalarRegisters registers, uint declaredCount, ulong backAddress)
    {
        var front = UserData(registers, declaredCount, false, 0, "merged-hull");
        // Carry the architectural s0:s1 pair independently of s8 and above.
        // Lowering copies these appended inputs before executing guest code.
        var data = new uint[front.Length + 2];
        front.CopyTo(data, 0);
        data[front.Length] = (uint)backAddress;
        data[front.Length + 1] = (uint)(backAddress >> 32);
        return data;
    }

    internal static bool IsMergedTessellationMask(uint stages)
    {
        // LS_EN=1, HS_EN=1, ES_EN=1, DYNAMIC_HS=1, PRIMGEN_EN=1,
        // PRIMGEN_PASSTHRU_EN=1, without a geometry or legacy vertex stage.
        const uint mask = 1u | (1u << 2) | (1u << 3) | (1u << 8) | (1u << 13) | (1u << 25);
        return (stages & ~((1u << 22) | (1u << 23))) == mask;
    }

    private TessellationDrawPrograms PrepareTessellationHull(VertexStageRegisters vertex,
        ShaderInterfaceRegisters shaderInterface, Gen5TessellationInfo tessellation)
    {
        var address = vertex.LocalAddress != 0 ? vertex.LocalAddress : vertex.HullAddress;
        var registered = _registry.Require(address, "merged-hull");
        if (!registered.IsFused)
            throw SubmissionScheduler.Fatal("The tessellation path requires a registered merged local/hull program.");
        var count = vertex.HullResource2.UserScalarCount;
        // A merged front may consume more user data than the back's RSRC2.
        // Reading its own declared count preserves the guest's AGC header.
        if (!Gen5ShaderTranslator.TryGetFusedProgramParts(_context, address, out var frontHeader, out _, out _) ||
            !_context.TryReadUInt64(frontHeader + 0x20, out var shRegisters) ||
            !_context.TryReadUInt32(frontHeader + 0x5C, out var shCount))
            throw SubmissionScheduler.Fatal("The merged hull front has an unreadable register header.");
        for (uint index = 0; index < (shCount & 0xFF); index++)
        {
            if (!_context.TryReadUInt32(shRegisters + index * 8, out var offset) ||
                !_context.TryReadUInt32(shRegisters + index * 8 + 4, out var value))
                throw SubmissionScheduler.Fatal("The merged hull front has an unreadable register entry.");
            if (offset == ShaderRegisterOffset.SpiShaderPgmRsrc2Hs)
                count = Math.Max(count, HullResource2.Decode(value).UserScalarCount);
        }
        var source = PrepareSource(address, ShaderStage.Compute, "merged-hull", vertex.HullUserScalars,
            count, false, VertexUserDataBase);
        source = source with { UserData = MergedHullUserData(vertex.HullUserScalars, count, vertex.HullUserDataAddress) };
        var decoded = _programs.Decode(source);
        if (decoded.FusedContinuationPc is not { } entry ||
            !Gen5TessellationHullInfo.TryDecode(shaderInterface.LocalHullConfiguration, entry + 8, out var hull, out var error))
            throw SubmissionScheduler.Fatal("The merged hull has an invalid continuation or workgroup configuration.");
        var threads = (hull.PatchesPerGroup * Math.Max(hull.InputControlPoints, hull.OutputControlPoints) + 63) & ~63u;
        var input = new ComputeInputInfo
        {
            ThreadsX = threads, ThreadsY = 1, ThreadsZ = 1, WaveSize = 64, ThreadIdCount = 1,
            LocalDataShareDwords = (uint)vertex.HullResource2.LocalDataShareSize * 128,
            ScratchDwords = registered.ScratchDwords, NeedsLocalDataShareBarriers = true,
        };
        var cursor = 0u;
        var handle = _programs.GetOrCompile(source, new StageCompileOptions
            { ComputeInfo = input, TessellationHull = hull }, ref cursor, out var stage);
        input.Stage = stage;
        return new(tessellation, hull, new ComputeProgram { Program = handle, Input = input });
    }
}
