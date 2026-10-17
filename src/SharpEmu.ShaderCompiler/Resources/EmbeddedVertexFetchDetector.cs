// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Generic;
using System.Linq;

namespace SharpEmu.ShaderCompiler.Resources;

// One buffer load of a vertex prolog that reads an attribute's stream.
public sealed record EmbeddedVertexFetchLoad(uint Pc, int AttributeId, uint Components, IReadOnlyList<uint> PrologLoads);

// The vertex prolog's fetch loads and the user registers that carry the draw offsets.
public sealed class EmbeddedVertexFetchPlan
{
    public List<EmbeddedVertexFetchLoad> Loads { get; } = [];
    public int VertexOffsetScalarRegister { get; set; } = ShaderResourceInfo.NoScalarRegister;
    public int InstanceOffsetScalarRegister { get; set; } = ShaderResourceInfo.NoScalarRegister;

    // Fixed-function attributes replace these table loads. Keep instruction addresses
    // unchanged so branch targets and vertex-input bindings still refer to the same code.
    public Gen5ShaderProgram RemoveReplacedTableLoads(Gen5ShaderProgram program)
    {
        var replacedLoads = Loads.SelectMany(load => load.PrologLoads).ToHashSet();
        return new Gen5ShaderProgram(program.Address, program.Instructions.Select(instruction =>
            replacedLoads.Contains(instruction.Pc) && instruction.Control is Gen5ScalarMemoryControl
                ? instruction with { Encoding = Gen5ShaderEncoding.Sopp, Opcode = "SNop", Sources = [], Destinations = [], Control = null }
                : instruction).ToArray());
    }
}

// Finds the attribute and buffer table walks of an embedded vertex fetch and the
// buffer loads they end in, so the host can bind them as fixed-function attributes.
public static class EmbeddedVertexFetchDetector
{
    private const int ScalarRegisterCount = 108;
    private const int VectorRegisterCount = 256;
    private const uint VccLow = 106;
    private const uint VccHigh = 107;

    private enum ValueType : byte
    {
        Unknown,
        Constant,
        AttributeTable,
        Attribute,
        BufferTable,
        Buffer,
        Index,
    }

    private struct ScalarInfo
    {
        public ValueType Type;
        public int AttributeId;
        public uint Value;
        public List<uint>? PrologLoads;

        public readonly ScalarInfo Copy() => new()
        {
            Type = Type,
            AttributeId = AttributeId,
            Value = Value,
            PrologLoads = PrologLoads is null ? null : [.. PrologLoads],
        };
    }

    public static EmbeddedVertexFetchPlan Detect(
        Gen5ShaderProgram program,
        int attributeTableRegister,
        int bufferTableRegister,
        uint userDataBase,
        uint userDataCount,
        uint waveSize)
    {
        var plan = new EmbeddedVertexFetchPlan();
        var vertexOffsetCandidate = -1;
        var instanceOffsetCandidate = -1;
        var vertexOffsetConflict = false;
        var instanceOffsetConflict = false;
        var scalars = new ScalarInfo[ScalarRegisterCount];
        var vectors = new ValueType[VectorRegisterCount];
        var lanes = new Dictionary<(uint Register, uint Lane), ScalarInfo>();
        var trackLanes = !program.Instructions.Any(instruction => HasBranch(instruction.Opcode));

        void Mark(int register, ValueType type)
        {
            if (register >= 0 && register < ScalarRegisterCount)
            {
                scalars[register].Type = type;
            }
        }

        Mark(attributeTableRegister, ValueType.AttributeTable);
        Mark(attributeTableRegister + 1, ValueType.AttributeTable);
        Mark(bufferTableRegister, ValueType.BufferTable);
        Mark(bufferTableRegister + 1, ValueType.BufferTable);

        foreach (var instruction in program.Instructions)
        {
            var destination = instruction.Destinations.Count != 0 ? instruction.Destinations[0] : default(Gen5Operand?);
            var source0 = instruction.Sources.Count > 0 ? instruction.Sources[0] : default(Gen5Operand?);
            var source1 = instruction.Sources.Count > 1 ? instruction.Sources[1] : default(Gen5Operand?);
            var source2 = instruction.Sources.Count > 2 ? instruction.Sources[2] : default(Gen5Operand?);

            // Fetch shaders accumulate the draw's vertex offset in v0, or in v5 and the
            // instance offset in v8 under the user-data-at-s8 layout.
            var vertexIndexAccumulator = IsVector(destination) && (destination!.Value.Value == 0 || (userDataBase == 8 && destination.Value.Value == 5));
            var instanceIndexAccumulator = IsVector(destination) && destination!.Value.Value == (userDataBase == 8 ? 8u : 3u);
            var indexOffsetAdd = (vertexIndexAccumulator || instanceIndexAccumulator) && IsTrackedScalarRegister(source0) &&
                ((instruction.Opcode == "VAddI32" && IsVector(source1) && source1!.Value.Value == destination!.Value.Value) ||
                 (userDataBase == 8 && destination!.Value.Value is 5 or 8 && instruction.Opcode == "VSadU32" &&
                  IsVector(source2) && source2!.Value.Value == destination.Value.Value &&
                  TryConstant(scalars, source1, out var sadZero) && sadZero == 0));
            if (plan.Loads.Count == 0 && indexOffsetAdd)
            {
                var register = ScalarRegister(source0!.Value);
                if (register >= userDataBase && register - userDataBase < userDataCount)
                {
                    ref var candidate = ref (vertexIndexAccumulator ? ref vertexOffsetCandidate : ref instanceOffsetCandidate);
                    ref var conflict = ref (vertexIndexAccumulator ? ref vertexOffsetConflict : ref instanceOffsetConflict);
                    if (candidate >= 0 && candidate != (int)register)
                    {
                        conflict = true;
                    }
                    else
                    {
                        candidate = (int)register;
                    }
                }
            }

            switch (instruction.Opcode)
            {
                case "VWritelaneB32":
                    if (IsVector(destination) && destination!.Value.Value < VectorRegisterCount)
                    {
                        vectors[destination.Value.Value] = ValueType.Unknown;
                    }

                    if (trackLanes && IsVector(destination) && IsTrackedScalarRegister(source0) && ScalarRegister(source0!.Value) < ScalarRegisterCount &&
                        TryConstant(scalars, source1, out var lane))
                    {
                        lanes[(destination!.Value.Value, Lane(lane, waveSize))] = scalars[ScalarRegister(source0.Value)].Copy();
                    }
                    else if (IsVector(destination))
                    {
                        ClearLanes(lanes, destination!.Value.Value);
                    }

                    break;
                case "VReadlaneB32":
                    if (trackLanes && IsTrackedScalarRegister(destination) && ScalarRegister(destination!.Value) < ScalarRegisterCount && IsVector(source0) &&
                        TryConstant(scalars, source1, out var readLane))
                    {
                        scalars[ScalarRegister(destination.Value)] = lanes.TryGetValue((source0!.Value.Value, Lane(readLane, waveSize)), out var stored)
                            ? stored.Copy()
                            : default;
                    }
                    else if (IsTrackedScalarRegister(destination))
                    {
                        ClearScalars(scalars, destination!.Value, 1);
                    }

                    break;
                case "SMovB32":
                    if (IsTrackedScalarRegister(destination) && IsTrackedScalarRegister(source0) && ScalarRegister(source0!.Value) < ScalarRegisterCount)
                    {
                        scalars[ScalarRegister(destination!.Value)] = scalars[ScalarRegister(source0.Value)].Copy();
                    }
                    else if (IsTrackedScalarRegister(destination))
                    {
                        if (TryConstant(scalars, source0, out var value))
                        {
                            scalars[ScalarRegister(destination!.Value)] = new ScalarInfo { Type = ValueType.Constant, Value = value };
                        }
                        else
                        {
                            ClearScalars(scalars, destination!.Value, 1);
                        }
                    }

                    break;
                case "SMovkI32":
                    if (IsTrackedScalarRegister(destination))
                    {
                        scalars[ScalarRegister(destination!.Value)] = new ScalarInfo
                        {
                            Type = ValueType.Constant,
                            Value = unchecked((uint)(short)instruction.Sources[0].Value),
                        };
                    }

                    break;
                default:
                    if (instruction.Control is Gen5ScalarMemoryControl scalarLoad && instruction.Opcode.StartsWith("SLoadDword", StringComparison.Ordinal))
                    {
                        ApplyScalarLoad(instruction, scalarLoad, scalars);
                    }
                    else if (instruction.Opcode == "VCndmaskB32")
                    {
                        if (IsVector(destination) && destination!.Value.Value < VectorRegisterCount)
                        {
                            ClearLanes(lanes, destination.Value.Value);
                            if (IsVector(source0) && source0!.Value.Value == 8 && IsVector(source1) && source1!.Value.Value == 5)
                            {
                                vectors[destination.Value.Value] = ValueType.Index;
                            }
                        }
                    }
                    else if (IsAttributePropagationAlu(instruction.Opcode))
                    {
                        if (IsTrackedScalarRegister(destination) && IsTrackedScalarRegister(source0) && ScalarRegister(source0!.Value) < ScalarRegisterCount &&
                            scalars[ScalarRegister(source0.Value)].Type == ValueType.Attribute)
                        {
                            scalars[ScalarRegister(destination!.Value)] = scalars[ScalarRegister(source0.Value)].Copy();
                        }
                        else if (IsTrackedScalarRegister(destination))
                        {
                            if (TryConstant(scalars, source0, out var left) && TryConstant(scalars, source1, out var right))
                            {
                                scalars[ScalarRegister(destination!.Value)] = new ScalarInfo
                                {
                                    Type = ValueType.Constant,
                                    Value = instruction.Opcode switch
                                    {
                                        "SAndB32" => left & right,
                                        "SLshlB32" => left << (int)(right & 31),
                                        "SBfeU32" => left >> (int)(right & 31),
                                        _ => unchecked(left + right),
                                    },
                                };
                            }
                            else
                            {
                                ClearScalars(scalars, destination!.Value, 1);
                            }
                        }
                    }
                    else if (instruction.Control is Gen5BufferMemoryControl buffer && IsFetchBufferLoad(instruction.Opcode))
                    {
                        if (buffer.VectorAddress < VectorRegisterCount && vectors[buffer.VectorAddress] == ValueType.Index &&
                            buffer.ScalarResource < ScalarRegisterCount && scalars[buffer.ScalarResource].Type == ValueType.Buffer)
                        {
                            var table = scalars[buffer.ScalarResource];
                            if (plan.Loads.Count == 0)
                            {
                                if (!vertexOffsetConflict)
                                {
                                    plan.VertexOffsetScalarRegister = vertexOffsetCandidate;
                                }

                                if (!instanceOffsetConflict)
                                {
                                    plan.InstanceOffsetScalarRegister = instanceOffsetCandidate;
                                }
                            }

                            plan.Loads.Add(new EmbeddedVertexFetchLoad(instruction.Pc, table.AttributeId, Math.Max(buffer.DwordCount, 1u), table.PrologLoads is null ? [] : [.. table.PrologLoads]));
                        }
                    }

                    break;
            }

            if (instruction.Opcode == "VMovreldB32")
            {
                lanes.Clear();
            }
            else if (instruction.Opcode != "VWritelaneB32")
            {
                foreach (var written in instruction.Destinations)
                {
                    if (written.Kind == Gen5OperandKind.VectorRegister)
                    {
                        ClearLanes(lanes, written.Value);
                    }
                }
            }
        }

        return plan;
    }

    // A scalar load through the attribute table yields attribute records; one through
    // the buffer table, or through a loaded attribute, yields buffer records.
    private static void ApplyScalarLoad(Gen5ShaderInstruction instruction, Gen5ScalarMemoryControl control, ScalarInfo[] scalars)
    {
        var destination = instruction.Destinations.Count != 0 ? instruction.Destinations[0] : default(Gen5Operand?);
        var source0 = instruction.Sources.Count > 0 ? instruction.Sources[0] : default(Gen5Operand?);
        var source1 = instruction.Sources.Count > 1 ? instruction.Sources[1] : default(Gen5Operand?);
        if (!IsTrackedScalarRegister(destination))
        {
            return;
        }

        var register = ScalarRegister(destination!.Value);
        var count = Math.Max(control.DestinationCount, 1u);
        bool TryOffset(out uint rawOffset)
        {
            rawOffset = 0;
            uint baseOffset = 0;
            if (source1 is { } offsetOperand && !TryConstant(scalars, offsetOperand, out baseOffset))
            {
                return false;
            }

            var value = (ulong)baseOffset + unchecked((uint)control.ImmediateOffsetBytes);
            if (value > uint.MaxValue)
            {
                return false;
            }

            rawOffset = (uint)value;
            return true;
        }

        if (IsTrackedScalarRegister(source0) && ScalarRegister(source0!.Value) < ScalarRegisterCount && scalars[ScalarRegister(source0.Value)].Type == ValueType.AttributeTable)
        {
            if (TryOffset(out var rawOffset))
            {
                var index = (int)(rawOffset / 4);
                for (uint component = 0; component < count && register + component < ScalarRegisterCount; component++)
                {
                    scalars[register + component] = new ScalarInfo
                    {
                        Type = ValueType.Attribute,
                        AttributeId = index + (int)component,
                        PrologLoads = [instruction.Pc],
                    };
                }
            }
            else
            {
                ClearScalars(scalars, destination.Value, count);
            }
        }
        else if (IsTrackedScalarRegister(source0) && ScalarRegister(source0!.Value) < ScalarRegisterCount && scalars[ScalarRegister(source0.Value)].Type == ValueType.BufferTable)
        {
            if (TryOffset(out var rawOffset))
            {
                for (uint component = 0; component < count && register + component < ScalarRegisterCount; component++)
                {
                    scalars[register + component] = new ScalarInfo
                    {
                        Type = ValueType.Buffer,
                        AttributeId = (int)((rawOffset + component * 4) / 16),
                        PrologLoads = [instruction.Pc],
                    };
                }
            }
            else if (source1 is { } attribute && IsTrackedScalarRegister(attribute) && ScalarRegister(attribute) < ScalarRegisterCount &&
                     scalars[ScalarRegister(attribute)].Type == ValueType.Attribute && (control.ImmediateOffsetBytes & 3) == 0)
            {
                var loaded = scalars[ScalarRegister(attribute)];
                for (uint component = 0; component < count && register + component < ScalarRegisterCount; component++)
                {
                    scalars[register + component] = new ScalarInfo
                    {
                        Type = ValueType.Buffer,
                        AttributeId = loaded.AttributeId,
                        PrologLoads = [.. loaded.PrologLoads ?? [], instruction.Pc],
                    };
                }
            }
            else
            {
                ClearScalars(scalars, destination.Value, count);
            }
        }
        else
        {
            ClearScalars(scalars, destination.Value, count);
        }
    }

    private static bool HasBranch(string opcode) =>
        opcode is "SSetpcB64" or "SBranch" or "SCbranchScc0" or "SCbranchScc1" or "SCbranchVccz" or "SCbranchVccnz" or
            "SCbranchExecz" or "SCbranchExecnz";

    private static bool IsFetchBufferLoad(string opcode) =>
        opcode is "BufferLoadFormatX" or "BufferLoadFormatXy" or "BufferLoadFormatXyz" or "BufferLoadFormatXyzw";

    private static bool IsAttributePropagationAlu(string opcode) =>
        opcode is "SBfeU32" or "SAndB32" or "SAddI32" or "SAddU32" or "SLshlB32";

    // Track general scalar registers and VCC. M0 and EXEC remain shader state,
    // but cannot carry table records in this analysis.
    private static bool IsTrackedScalarRegister(Gen5Operand? operand) =>
        operand is { Kind: Gen5OperandKind.ScalarRegister, Value: < ScalarRegisterCount };

    private static bool IsVector(Gen5Operand? operand) => operand is { Kind: Gen5OperandKind.VectorRegister };

    private static uint ScalarRegister(Gen5Operand operand) => operand.Value;

    private static uint Lane(uint lane, uint waveSize) => waveSize is 32 or 64 ? lane % waveSize : lane;

    private static bool TryConstant(ScalarInfo[] scalars, Gen5Operand? operand, out uint value)
    {
        value = 0;
        if (operand is not { } source)
        {
            return false;
        }

        switch (source.Kind)
        {
            case Gen5OperandKind.LiteralConstant:
                value = source.Value;
                return true;
            case Gen5OperandKind.EncodedConstant:
                if (source.Value == 125)
                {
                    value = 0;
                    return true;
                }

                return Gen5InlineConstants.TryDecode(source.Value, out value);
            case Gen5OperandKind.ScalarRegister when source.Value < ScalarRegisterCount && scalars[source.Value].Type == ValueType.Constant:
                value = scalars[source.Value].Value;
                return true;
            default:
                return false;
        }
    }

    private static void ClearScalars(ScalarInfo[] scalars, Gen5Operand destination, uint count)
    {
        if (destination.Kind != Gen5OperandKind.ScalarRegister)
        {
            return;
        }

        for (uint index = 0; index < count && destination.Value + index < ScalarRegisterCount; index++)
        {
            scalars[destination.Value + index] = default;
        }
    }

    private static void ClearLanes(Dictionary<(uint Register, uint Lane), ScalarInfo> lanes, uint register)
    {
        foreach (var key in lanes.Keys.Where(key => key.Register == register).ToArray())
        {
            lanes.Remove(key);
        }
    }
}
