// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Ir;

namespace SharpEmu.ShaderCompiler.Resources;

public sealed partial class ScalarValueGraph
{
    // Snapshots are taken immediately after Builder.Run, before planning mutates
    // memory entries or substitutes nodes. References use snapshot-local indices,
    // never process-local IDs. Two-pass restoration preserves loop phi cycles.
    internal void WriteSnapshot(BinaryWriter writer)
    {
        var indices = new Dictionary<ScalarValue, int>(_values.Count);
        for (var i = 0; i < _values.Count; i++) indices.Add(_values[i], i);
        void Reference(ScalarValue? value) => writer.Write(value is null ? -1 : indices[value]);
        writer.Write(_values.Count);
        foreach (var value in _values)
        {
            writer.Write((byte)value.Kind);
            writer.Write((byte)value.Type);
            writer.Write((byte)value.Operation);
            writer.Write(value.Payload);
            writer.Write(value.Operands.Length);
            foreach (var operand in value.Operands) Reference(operand);
            writer.Write(value.PhiPredecessors.Length);
            foreach (var predecessor in value.PhiPredecessors) writer.Write(predecessor);
        }
        writer.Write(_internedValues.Count);
        foreach (var entry in _internedValues)
        {
            Reference(entry.Value);
            writer.Write(entry.Payload);
            writer.Write(entry.Identity);
        }
        writer.Write(Accesses.Length);
        foreach (var access in Accesses)
        {
            writer.Write(access is not null);
            if (access is null) continue;
            Reference(access.Handle);
            Reference(access.SamplerHandle);
            Reference(access.Read);
            Reference(access.Offset);
            Reference(access.Active);
        }
        writer.Write(BranchConditions.Count);
        foreach (var (pc, value) in BranchConditions) { writer.Write(pc); Reference(value); }
        writer.Write(_undefinedOrigins.Count);
        foreach (var (value, origin) in _undefinedOrigins)
        {
            Reference(value); writer.Write(origin.Pc); writer.Write(origin.Opcode);
        }
        writer.Write(_bitScanInstructions.Count);
        foreach (var (value, pc) in _bitScanInstructions) { Reference(value); writer.Write(pc); }
        writer.Write(BuilderInstruction.Pc);
        writer.Write(BuilderInstruction.Opcode ?? "");
    }

    internal static ScalarValueGraph ReadSnapshot(BinaryReader reader, Gen5ShaderProgram program,
        uint userDataBase, uint userDataCount, IReadOnlySet<uint>? fixedFunctionVertexLoads, uint waveSize)
    {
        var graph = new ScalarValueGraph(program,
            IrControlFlowGraph.Build(program.Instructions, Gen5IrBranchResolver.Instance),
            MemoryAccessTable.Build(program, fixedFunctionVertexLoads), userDataBase, userDataCount, waveSize);
        int Count(int maximum = 1_000_000)
        {
            var count = reader.ReadInt32();
            if (count < 0 || count > maximum || count > reader.BaseStream.Length - reader.BaseStream.Position)
                throw new InvalidDataException("Invalid graph snapshot count.");
            return count;
        }
        var count = Count();
        var operands = new int[count][];
        for (var i = 0; i < count; i++)
        {
            var kind = (ScalarValueKind)reader.ReadByte();
            var type = (ScalarValueType)reader.ReadByte();
            var operation = (ScalarOperation)reader.ReadByte();
            if (!Enum.IsDefined(kind) || !Enum.IsDefined(type) || !Enum.IsDefined(operation))
                throw new InvalidDataException("Invalid graph snapshot node.");
            var payload = reader.ReadUInt64();
            var refs = new int[Count(65536)];
            for (var j = 0; j < refs.Length; j++) refs[j] = reader.ReadInt32();
            operands[i] = refs;
            var predecessors = new int[Count(65536)];
            for (var j = 0; j < predecessors.Length; j++) predecessors[j] = reader.ReadInt32();
            if (kind == ScalarValueKind.Phi ? predecessors.Length != refs.Length : predecessors.Length != 0)
                throw new InvalidDataException("Invalid graph snapshot phi.");
            var value = ScalarValue.CreateInterned(kind, type, operation, payload, new ScalarValue[refs.Length]);
            if (kind == ScalarValueKind.Phi) value.SetPhiOperands(predecessors, value.Operands);
            graph._values.Add(value);
        }
        ScalarValue Node(int index) => (uint)index < (uint)count ? graph._values[index]
            : throw new InvalidDataException("Invalid graph snapshot reference.");
        ScalarValue? Reference()
        {
            var index = reader.ReadInt32();
            return index == -1 ? null : Node(index);
        }
        ScalarValue Required() => Reference() ?? throw new InvalidDataException("Missing graph snapshot node.");
        for (var i = 0; i < count; i++)
            for (var j = 0; j < operands[i].Length; j++) graph._values[i].Operands[j] = Node(operands[i][j]);
        var internedCount = Count(count);
        for (var i = 0; i < internedCount; i++)
        {
            var value = Required();
            var payload = reader.ReadUInt64();
            var identity = reader.ReadUInt64();
            var hash = InternHash(value.Kind, value.Type, payload, value.Operation, value.Operands, identity);
            var head = graph._interned.TryGetValue(hash, out var first) ? first : -1;
            graph._interned[hash] = graph._internedValues.Count;
            graph._internedValues.Add(new(value, payload, identity, head));
        }
        var accessCount = Count();
        if (accessCount != graph.Memory.Count) throw new InvalidDataException("Graph memory table changed.");
        graph.Accesses = new MemoryAccessBinding?[accessCount];
        for (var i = 0; i < accessCount; i++)
            if (reader.ReadBoolean()) graph.Accesses[i] = new(Reference(), Reference(), Reference(), Reference(), Reference());
        var branches = Count();
        for (var i = 0; i < branches; i++) graph.BranchConditions.Add(reader.ReadUInt32(), Required());
        var origins = Count(count);
        for (var i = 0; i < origins; i++) graph._undefinedOrigins.Add(Required(), (reader.ReadUInt32(), reader.ReadString()));
        var bitScans = Count(count);
        for (var i = 0; i < bitScans; i++) graph._bitScanInstructions.Add(Required(), reader.ReadUInt32());
        var pc = reader.ReadUInt32();
        var opcode = reader.ReadString();
        graph.BuilderInstruction = (pc, opcode.Length == 0 ? null! : opcode);
        if (reader.BaseStream.Position != reader.BaseStream.Length)
            throw new InvalidDataException("Trailing graph snapshot data.");
        return graph;
    }
}
