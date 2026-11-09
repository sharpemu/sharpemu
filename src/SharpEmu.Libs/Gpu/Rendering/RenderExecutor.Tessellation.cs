// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Vulkan;
using SharpEmu.Libs.VideoOut;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Rendering;

public sealed partial class RenderExecutor
{
    private void DrawTessellationIndexed(ulong submitId, RegisterBanks banks, in DrawIndexedArguments arguments, ResolvedDraw record)
    {
        var count = arguments.IndexCount;
        var instances = arguments.InstanceCount;
        var vertexOffset = unchecked(arguments.BaseVertex +
            (arguments.OffsetSource == DrawOffsetSource.IndirectArguments ? 0 : (int)banks.UserConfig.IndexOffset));
        var firstInstance = arguments.FirstInstance;
        var address = arguments.IndexAddress;
        var size = arguments.IndexTypeAndSize switch
        {
            (uint)GuestIndexType.Index8 => 1u,
            (uint)GuestIndexType.Index16 => 2u,
            (uint)GuestIndexType.Index32 => 4u,
            _ => throw _host.Fatal("The tessellation index format is unsupported."),
        };
        if (arguments.IndirectArgumentsAddress != 0)
        {
            Span<byte> data = stackalloc byte[20];
            if (!_host.TryReadGuest(arguments.IndirectArgumentsAddress, data))
                throw _host.Fatal("The tessellation indirect draw arguments are unreadable.");
            count = BinaryPrimitives.ReadUInt32LittleEndian(data);
            instances = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
            var firstIndex = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
            if (!arguments.UnboundedIndexBuffer && (ulong)firstIndex + count > arguments.IndexCount)
                throw _host.Fatal("The tessellation indirect draw exceeds the supplied index buffer.");
            address = checked(address + (ulong)firstIndex * size);
            vertexOffset = BinaryPrimitives.ReadInt32LittleEndian(data[12..]);
            firstInstance = BinaryPrimitives.ReadUInt32LittleEndian(data[16..]);
        }
        DrawTessellation(submitId, banks, count, instances, address, size, vertexOffset, firstInstance, record);
    }

    private void DrawTessellation(ulong submitId, RegisterBanks banks, uint count, uint instances,
        ulong indexAddress, uint indexSize, int vertexOffset, uint firstInstance, ResolvedDraw record)
    {
        if (count == 0 || instances == 0) { Record(record); return; }
        ValidateDrawRegisters(banks);
        var draw = new DrawCall("DrawTessellation", RecordedOperation.DrawIndexAuto, count, instances, firstInstance);
        ref var state = ref record.State;
        state.Begin(-1);
        if (!TryResolveDrawTargets(banks, draw, ref state, record)) { record.ResetBindings = true; Record(record); return; }
        record.TargetsResolved = true;
        ResolveShaderPrograms(banks, ref state);
        if (!state.Programs.Available) { record.ResetBindings = true; Record(record); return; }
        var emission = new DrawEmission(false, 0, 0, 0);
        FillDraw(record, banks, draw, ref state, PrimitiveTopology.PatchList, emission, default, false, false, false);
        record.Kind = ResolvedDrawKind.Tessellation;
        record.TessellationIndexAddress = indexAddress;
        record.TessellationIndexSize = indexSize;
        record.TessellationVertexOffset = vertexOffset;
        record.WritesMemory = true;
        Record(record);
    }
}
