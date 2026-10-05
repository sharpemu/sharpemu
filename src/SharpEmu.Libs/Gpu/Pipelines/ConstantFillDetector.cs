// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;

namespace SharpEmu.Libs.Gpu.Pipelines;

// A compute program that stores one dword, read from a constant buffer, into every
// record of a buffer: the element index is group * 64 + thread.
//   v_lshl_add_u32   v0, s<group>, 6, v0
//   s_buffer_load_dword s<value>, s[<source>:+3], 0
//   s_waitcnt
//   v_mov_b32        v1, s<value>
//   buffer_store_format_x v1, v0, s[<destination>:+3], 0 idxen
//   s_endpgm
// Unreal clears DCC metadata this way, so the dispatch must reach the image cache as a
// clear; the metadata is not emulated and the stores alone would leave the image stale.
public sealed record ConstantFill(uint GroupScalarRegister, uint DestinationScalarResource, uint SourceScalarResource);

public static class ConstantFillDetector
{
    private const uint Shift64 = 134; // inline constant 6
    private const uint ZeroConstant = 128;
    private const uint NullScalar = 125;

    public static ConstantFill? Detect(Gen5ShaderProgram program)
    {
        var instructions = program.Instructions.Where(instruction => instruction.Opcode != "SWaitcnt").ToArray();
        if (instructions.Length != 5)
        {
            return null;
        }

        var index = instructions[0];
        if (index.Opcode != "VLshlAddU32" || index.Destinations is not [{ Kind: Gen5OperandKind.VectorRegister } indexRegister] ||
            index.Sources is not [{ Kind: Gen5OperandKind.ScalarRegister } group, { Kind: Gen5OperandKind.EncodedConstant, Value: Shift64 },
                { Kind: Gen5OperandKind.VectorRegister } thread] || thread.Value != indexRegister.Value)
        {
            return null;
        }

        var load = instructions[1];
        if (load.Opcode != "SBufferLoadDword" || load.Control is not Gen5ScalarMemoryControl { ImmediateOffsetBytes: 0, DynamicOffsetRegister: null } ||
            load.Destinations is not [{ Kind: Gen5OperandKind.ScalarRegister } value] ||
            load.Sources is not [{ Kind: Gen5OperandKind.ScalarRegister } source, { Kind: Gen5OperandKind.EncodedConstant, Value: NullScalar or ZeroConstant }])
        {
            return null;
        }

        var move = instructions[2];
        if (move.Opcode != "VMovB32" || move.Destinations is not [{ Kind: Gen5OperandKind.VectorRegister } data] ||
            move.Sources is not [{ Kind: Gen5OperandKind.ScalarRegister } moved] || moved.Value != value.Value)
        {
            return null;
        }

        var store = instructions[3];
        if (store.Opcode != "BufferStoreFormatX" ||
            store.Control is not Gen5BufferMemoryControl { DwordCount: 1, OffsetBytes: 0, IndexEnabled: true, OffsetEnabled: false, Typed: false } control ||
            control.VectorAddress != indexRegister.Value || control.VectorData != data.Value ||
            store.Sources is not [_, _, { Kind: Gen5OperandKind.EncodedConstant, Value: ZeroConstant }])
        {
            return null;
        }

        return instructions[4].Opcode == "SEndpgm" ? new ConstantFill(group.Value, control.ScalarResource, source.Value) : null;
    }
}
