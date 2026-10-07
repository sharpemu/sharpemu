// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Generic;
using System.Linq;
using SharpEmu.ShaderCompiler.Ir;

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

        public readonly bool SameValue(ScalarInfo other) => Type == other.Type &&
            (Type == ValueType.Unknown || (AttributeId == other.AttributeId && Value == other.Value &&
                (PrologLoads ?? []).SequenceEqual(other.PrologLoads ?? [])));
    }

    private sealed class TrackingState
    {
        public ScalarInfo[] Scalars = new ScalarInfo[ScalarRegisterCount];
        public ValueType[] Vectors = new ValueType[VectorRegisterCount];
        public Dictionary<(uint Register, uint Lane), ScalarInfo> Lanes = [];
        public int VertexOffset = -1;
        public int InstanceOffset = -1;

        public TrackingState Copy() => new()
        {
            Scalars = (ScalarInfo[])Scalars.Clone(),
            Vectors = (ValueType[])Vectors.Clone(),
            Lanes = new(Lanes),
            VertexOffset = VertexOffset,
            InstanceOffset = InstanceOffset,
        };

        public void Merge(TrackingState other)
        {
            for (var index = 0; index < Scalars.Length; index++)
                if (!Scalars[index].SameValue(other.Scalars[index])) Scalars[index] = default;
            for (var index = 0; index < Vectors.Length; index++)
                if (Vectors[index] != other.Vectors[index]) Vectors[index] = ValueType.Unknown;
            foreach (var key in Lanes.Keys.ToArray())
                if (!other.Lanes.TryGetValue(key, out var value) || !Lanes[key].SameValue(value))
                    Lanes.Remove(key);
            if (VertexOffset != other.VertexOffset) VertexOffset = -2;
            if (InstanceOffset != other.InstanceOffset) InstanceOffset = -2;
        }

        public bool SameValue(TrackingState other) =>
            VertexOffset == other.VertexOffset && InstanceOffset == other.InstanceOffset &&
            Scalars.Where((value, index) => !value.SameValue(other.Scalars[index])).Any() == false &&
            Vectors.SequenceEqual(other.Vectors) && Lanes.Count == other.Lanes.Count &&
            Lanes.All(pair => other.Lanes.TryGetValue(pair.Key, out var value) && pair.Value.SameValue(value));
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
        // Unknown targets can enter any instruction. Do not rewrite a program with unresolved transfers.
        if (program.Instructions.Any(instruction => instruction.Opcode is
            "SSetpcB64" or "SSwappcB64" or "SRfeB64" or "SCallB64")) return plan;
        var graph = IrControlFlowGraph.Build(program.Instructions, Gen5IrBranchResolver.Instance);
        if (graph.Blocks.Count == 0) return plan;
        var initial = new TrackingState();

        void Mark(int register, ValueType type)
        {
            if (register >= 0 && register < ScalarRegisterCount)
            {
                initial.Scalars[register].Type = type;
            }
        }

        Mark(attributeTableRegister, ValueType.AttributeTable);
        Mark(attributeTableRegister + 1, ValueType.AttributeTable);
        Mark(bufferTableRegister, ValueType.BufferTable);
        Mark(bufferTableRegister + 1, ValueType.BufferTable);

        var blocks = graph.Blocks.Select(block => program.Instructions
            .Where(instruction => instruction.Pc >= block.StartPc && instruction.Pc < block.EndPc).ToArray()).ToArray();
        var inputs = new TrackingState?[blocks.Length];
        var outputs = new TrackingState?[blocks.Length];
        var pending = new Queue<int>();
        var queued = new bool[blocks.Length];
        pending.Enqueue(0);
        queued[0] = true;
        while (pending.TryDequeue(out var blockIndex))
        {
            queued[blockIndex] = false;
            TrackingState? incoming = blockIndex == 0 ? initial.Copy() : null;
            foreach (var predecessor in graph.Predecessors[blockIndex])
            {
                if (outputs[predecessor] is not { } previous) continue;
                if (incoming is null) incoming = previous.Copy();
                else incoming.Merge(previous);
            }
            if (incoming is null) continue;
            inputs[blockIndex] = incoming.Copy();
            ProcessBlock(blocks[blockIndex], incoming, collect: false);
            if (outputs[blockIndex] is { } old && old.SameValue(incoming)) continue;
            outputs[blockIndex] = incoming;
            foreach (var successor in graph.Successors[blockIndex])
                if (!queued[successor])
                {
                    pending.Enqueue(successor);
                    queued[successor] = true;
                }
        }
        for (var blockIndex = 0; blockIndex < blocks.Length; blockIndex++)
            if (inputs[blockIndex] is { } input) ProcessBlock(blocks[blockIndex], input, collect: true);
        return plan;

        void ProcessBlock(Gen5ShaderInstruction[] instructions, TrackingState state, bool collect)
        {
            var scalars = state.Scalars;
            var vectors = state.Vectors;
            var lanes = state.Lanes;
            foreach (var instruction in instructions)
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
                if (indexOffsetAdd)
                {
                    var register = ScalarRegister(source0!.Value);
                    if (register >= userDataBase && register - userDataBase < userDataCount)
                    {
                        ref var candidate = ref (vertexIndexAccumulator ? ref state.VertexOffset : ref state.InstanceOffset);
                        candidate = candidate == -1 || candidate == (int)register ? (int)register : -2;
                    }
                }

                switch (instruction.Opcode)
                {
                    case "VWritelaneB32":
                        if (IsVector(destination) && destination!.Value.Value < VectorRegisterCount)
                        {
                            vectors[destination.Value.Value] = ValueType.Unknown;
                        }

                        if (IsVector(destination) && IsTrackedScalarRegister(source0) && ScalarRegister(source0!.Value) < ScalarRegisterCount &&
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
                        if (IsTrackedScalarRegister(destination) && ScalarRegister(destination!.Value) < ScalarRegisterCount && IsVector(source0) &&
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
                    case "SCmovB64":
                        if (IsTrackedScalarRegister(destination))
                        {
                            ClearScalars(scalars, destination!.Value, 2);
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
                                vectors[destination.Value.Value] = ValueType.Unknown;
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
                            if (collect && buffer.VectorAddress < VectorRegisterCount && vectors[buffer.VectorAddress] == ValueType.Index &&
                                buffer.ScalarResource < ScalarRegisterCount && scalars[buffer.ScalarResource].Type == ValueType.Buffer)
                            {
                                var table = scalars[buffer.ScalarResource];
                                if (plan.Loads.Count == 0)
                                {
                                    if (state.VertexOffset >= 0)
                                    {
                                        plan.VertexOffsetScalarRegister = state.VertexOffset;
                                    }

                                    if (state.InstanceOffset >= 0)
                                    {
                                        plan.InstanceOffsetScalarRegister = state.InstanceOffset;
                                    }
                                }

                                plan.Loads.Add(new EmbeddedVertexFetchLoad(instruction.Pc, table.AttributeId, Math.Max(buffer.DwordCount, 1u), table.PrologLoads is null ? [] : [.. table.PrologLoads]));
                            }
                        }
                        else
                        {
                            foreach (var written in instruction.Destinations)
                                if (IsTrackedScalarRegister(written))
                                    ClearScalars(scalars, written, instruction.Opcode.Contains("64", StringComparison.Ordinal) ? 2u : 1u);
                        }

                        break;
                }

                if (instruction.Opcode == "VMovreldB32")
                {
                    lanes.Clear();
                    Array.Clear(vectors);
                }
                else if (instruction.Opcode != "VWritelaneB32")
                {
                    foreach (var written in instruction.Destinations)
                    {
                        if (written.Kind == Gen5OperandKind.VectorRegister)
                        {
                            ClearLanes(lanes, written.Value);
                            if (instruction.Opcode != "VCndmaskB32" && written.Value < VectorRegisterCount)
                                vectors[written.Value] = ValueType.Unknown;
                        }
                    }
                }
            }

        }
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
