// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;

namespace SharpEmu.Libs.Agc;

public static partial class AgcExports
{
    [SysAbiExport(
        Nid = "NKIzURsgV7I",
        ExportName = "sceAgcGetGsOversubscription",
        Target = Generation.Gen5,
        LibraryName = "libSceAgcDriver")]
    public static int GetGsOversubscription(CpuContext ctx)
    {
        var output = ctx[CpuRegister.Rdi];
        var shader = ctx[CpuRegister.Rsi];
        var budget = (uint)ctx[CpuRegister.Rdx];
        if (output == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var pcAllocation = 0u;
        var lateAllocation = 0u;
        // These endpoints initialize both descriptors without dereferencing the shader.
        if (budget == uint.MaxValue)
        {
            pcAllocation = 0x7FF;
            lateAllocation = 0x7F_0000;
        }
        else if (budget != 0)
        {
            ctx.GetXmmRegister(0, out var factorBits, out _);
            var factor = BitConverter.UInt32BitsToSingle((uint)factorBits);
            if (!float.IsFinite(factor) ||
                !TryGetGsOccupancy(ctx, shader, out var stages, out var baseline, out var expanded))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
            }

            var minimum = Math.Min(baseline.Vertex, baseline.Export);
            var waveShift = (stages & 0x0040_0000) != 0 ? 5 : 6;
            var maximum = Math.Min(Math.Min(expanded.Vertex, expanded.Export), Math.Min(budget >> waveShift, 1024));
            var headroom = maximum > minimum ? maximum - minimum : 0;
            var scaled = MathF.FusedMultiplyAdd(factor, headroom, minimum);
            if (!(scaled >= 0 && scaled < 4294967296f))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
            }

            var target = (uint)scaled;
            if (target > minimum && target < baseline.Export)
            {
                var range = Math.Max(expanded.Vertex - baseline.Vertex, 1);
                var lines = Math.Max(Math.Min(((ulong)(target - baseline.Vertex) << 10) / range, 1024), 1);
                pcAllocation = ((uint)lines * 2 - 1) & 0x7FF;
                lateAllocation = 0x7F_0000;
            }
            else if (target > minimum)
            {
                var range = Math.Max(expanded.Export - baseline.Export, 1);
                var waves = Math.Min((ulong)(target - baseline.Export) * 127 / range, 127);
                pcAllocation = 0x7FF;
                lateAllocation = (uint)waves << 16;
            }
        }

        // The result is two bank-specific register descriptors, not two scalar values.
        Span<byte> registers = stackalloc byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(registers, UserConfigRegisterOffset.ParameterOversubscription);
        BinaryPrimitives.WriteUInt32LittleEndian(registers[4..], pcAllocation);
        BinaryPrimitives.WriteUInt32LittleEndian(registers[8..], ShaderRegisterOffset.SpiShaderPgmRsrc4Gs);
        BinaryPrimitives.WriteUInt32LittleEndian(registers[12..], lateAllocation);
        if (!ctx.Memory.TryWrite(output, registers))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceAgc($"agc.gs_oversubscription output=0x{output:X16} shader=0x{shader:X16} budget={budget} pc_alloc=0x{pcAllocation:X8} late_alloc=0x{lateAllocation:X8}");
        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
    }

    private readonly record struct GsOccupancy(uint Vertex, uint Export);

    private static bool TryGetGsOccupancy(CpuContext ctx, ulong shader, out uint stages, out GsOccupancy baseline, out GsOccupancy expanded)
    {
        stages = 0;
        baseline = expanded = default;
        if (shader == 0 ||
            !TryReadUInt64(ctx, shader + ShaderCxRegistersOffset, out var table) || table == 0 ||
            !TryReadByte(ctx, shader + 0x5B, out var count) ||
            !TryReadUInt64(ctx, shader + ShaderSpecialsOffset, out var specials) || specials == 0 ||
            !TryReadUInt32(ctx, specials + 12, out stages))
        {
            return false;
        }

        uint onchip = 0, subgroup = 0, vertexOutput = 0, clipOutput = 0, maxOutput = 0;
        var found = 0;
        for (var index = 0; index < count; index++)
        {
            if (!TryReadUInt32(ctx, table + (ulong)index * 8, out var offset) ||
                !TryReadUInt32(ctx, table + (ulong)index * 8 + 4, out var value))
            {
                return false;
            }

            switch (offset)
            {
                case ContextRegisterOffset.VgtGsOnchipCntl: onchip = value; found |= 1; break;
                case ContextRegisterOffset.GeNggSubgroupCntl: subgroup = value; found |= 2; break;
                case ContextRegisterOffset.SpiVsOutConfig: vertexOutput = value; found |= 4; break;
                case ContextRegisterOffset.PaClVsOutCntl: clipOutput = value; found |= 8; break;
                case ContextRegisterOffset.GeMaxOutputPerSubgroup: maxOutput = value; found |= 16; break;
            }
        }

        var outputWords = ((maxOutput & 0x3FF) + 31) >> 5;
        if (found != 31 || outputWords == 0)
        {
            return false;
        }

        var work = ((((onchip >> 11) & 0x7FF) * (subgroup & 0x1FF)) + 31) >> 5;
        var waves = Math.Max(work, outputWords);
        if ((stages & 0x0040_0000) == 0) waves >>= 1;
        waves = Math.Max(waves, 1);
        var exports = 1 + ((clipOutput >> 21) & 1) + ((clipOutput >> 22) & 1) + ((clipOutput >> 23) & 1);
        var parameters = ((vertexOutput >> 2) & 0xF) + 1;
        var vertexCapacity = (vertexOutput & 0x80) != 0 ? 2048 : 1024 / parameters;
        var expandedVertexCapacity = (vertexOutput & 0x80) != 0 ? 2048 : 2048 / parameters;
        baseline = new GsOccupancy(waves * (vertexCapacity / outputWords), waves * ((128 / exports * 4) / outputWords));
        expanded = new GsOccupancy(waves * (expandedVertexCapacity / outputWords), waves * ((382 / exports * 4) / outputWords));
        return true;
    }
}
