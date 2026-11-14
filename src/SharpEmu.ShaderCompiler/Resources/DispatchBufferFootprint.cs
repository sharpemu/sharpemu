// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Resources;

// Abstract execution of address calculations, never execution of guest memory. Loaded data is
// unknown. The result includes every path and lane, including paths disabled by a device count.
// Refusing a program or exhausting the analysis budget leaves its ordinary host count read intact.
public static partial class DispatchBufferFootprint
{
    public readonly record struct Span(ulong Base, ulong Size);
    public sealed record Dispatch(uint GroupsX, uint GroupsY, uint GroupsZ,
        uint ThreadsX, uint ThreadsY, uint ThreadsZ, uint WaveSize, int ThreadIdCount,
        Gen5ComputeSystemRegisters Registers);

    public static bool TryEvaluate(Gen5ShaderProgram program, IReadOnlyList<uint> userData, uint userDataBase,
        Dispatch dispatch, IReadOnlySet<uint> accesses, out Dictionary<uint, Span> spans)
    {
        spans = [];
        if (program.IsFusedProgram || program.FunctionBufferAccesses.Count != 0 ||
            dispatch.GroupsX == 0 || dispatch.GroupsX > 256 || dispatch.GroupsY != 1 || dispatch.GroupsZ != 1 ||
            dispatch.ThreadsX == 0 || dispatch.ThreadsX > 1024 || dispatch.ThreadsY != 1 || dispatch.ThreadsZ != 1 ||
            dispatch.WaveSize is not (32 or 64) || dispatch.ThreadIdCount != 1 ||
            dispatch.Registers.ThreadGroupSizeRegister.HasValue || userDataBase + (ulong)userData.Count > 106 ||
            program.Instructions.Count == 0 || program.Instructions.Count > 2048)
            return false;

        var instructions = program.Instructions.ToDictionary(instruction => instruction.Pc);
        var steps = 0;
        for (uint group = 0; group < dispatch.GroupsX; group++)
        {
            var groupSteps = 0;
            var initial = new State(program.Instructions[0].Pc) { FullHigh = dispatch.WaveSize == 64 ? uint.MaxValue : 0 };
            for (var index = 0; index < userData.Count; index++) initial.S[userDataBase + index] = Word.Of(userData[index]);
            void SystemRegister(uint? register, uint value)
            {
                if (register is < 106) initial.S[register.Value] = Word.Of(value);
            }
            SystemRegister(dispatch.Registers.WorkGroupXRegister, group);
            SystemRegister(dispatch.Registers.WorkGroupYRegister, 0);
            SystemRegister(dispatch.Registers.WorkGroupZRegister, 0);
            initial.S[126] = Word.Of(uint.MaxValue);
            initial.S[127] = Word.Of(dispatch.WaveSize == 64 ? uint.MaxValue : 0);
            initial.V[0] = new(0, dispatch.ThreadsX - 1);
            var pending = new Stack<State>();
            var visited = new HashSet<State>();
            void Queue(State state)
            {
                if (visited.Add(state)) pending.Push(state.Copy());
            }
            Queue(initial);
            while (pending.TryPop(out var state))
            {
                while (true)
                {
                    // Groups have independent state. Bound each group and the entire background job separately.
                    if (++steps > 1_048_576 || ++groupSteps > 256_000) return false;
                    if (visited.Count > 4096) return false;
                    if (!instructions.TryGetValue(state.Pc, out var instruction)) return false;
                    var next = instruction.Pc + (uint)instruction.Words.Count * 4;
                    var opcode = instruction.Opcode;
                    if (opcode == "SEndpgm") break;
                    if (opcode is "SBranch" or "SCbranchScc0" or "SCbranchScc1" or "SCbranchExecz" or "SCbranchExecnz")
                    {
                        var condition = opcode == "SBranch" ? Word.Of(1) : opcode.Contains("Scc", StringComparison.Ordinal)
                            ? state.Scc : state.Active switch { 0 => Word.Of(0), 1 => Word.Of(1), _ => new Word(0, 1) };
                        var want = opcode is "SCbranchScc0" or "SCbranchExecz" ? 0u : 1u;
                        var target = unchecked(instruction.Pc + 4 + (uint)((short)instruction.Words[0] * 4));
                        if (condition.Min <= want && condition.Max >= want)
                        {
                            var taken = state.Copy();
                            taken.Pc = target;
                            if (opcode == "SCbranchExecz") taken.S[126] = taken.S[127] = Word.Of(0);
                            Queue(taken);
                        }
                        if (!condition.Exact || condition.Min != want)
                        {
                            state.Pc = next;
                            Queue(state);
                        }
                        // Both successors use the same deduplication. Continuing the fallthrough inline
                        // repeated entire loops when earlier arithmetic alternatives had already converged.
                        break;
                    }
                    else if (opcode is "SNop" or "SInstPrefetch" or "SWaitcnt" or "SBarrier") { }
                    else if (instruction.Control is Gen5ScalarMemoryControl)
                    {
                        foreach (var destination in instruction.Destinations) state.Write(destination, Word.Unknown, vectorMasked: false);
                    }
                    else if (instruction.Control is Gen5BufferMemoryControl buffer)
                    {
                        if (accesses.Contains(instruction.Pc) && state.Active != 0)
                        {
                            if (!TrySpan(state, instruction, buffer, out var span)) return false;
                            if (spans.TryGetValue(instruction.Pc, out var previous))
                            {
                                if (previous.Base != span.Base) return false;
                                span = span with { Size = Math.Max(span.Size, previous.Size) };
                            }
                            spans[instruction.Pc] = span;
                        }
                        foreach (var destination in instruction.Destinations) state.Write(destination, Word.Unknown);
                    }
                    else if (instruction.Control is Gen5DataShareControl { Gds: false })
                    {
                        foreach (var destination in instruction.Destinations) state.Write(destination, Word.Unknown);
                    }
                    else if (instruction.Encoding is Gen5ShaderEncoding.Sop1 or Gen5ShaderEncoding.Sop2 or Gen5ShaderEncoding.Sopc)
                    {
                        if (!Scalar(state, instruction)) return false;
                    }
                    else if (instruction.Encoding is Gen5ShaderEncoding.Vop1 or Gen5ShaderEncoding.Vop2 or Gen5ShaderEncoding.Vop3 or Gen5ShaderEncoding.Vopc)
                    {
                        if (!Vector(state, instruction, next, Queue)) return false;
                    }
                    else return false;
                    state.Pc = next;
                }
            }
        }
        // A selected access can be unreachable for these inputs; its empty span needs no residency.
        return true;
    }

    private static bool TrySpan(State state, Gen5ShaderInstruction instruction, Gen5BufferMemoryControl buffer, out Span span)
    {
        span = default;
        if (buffer.Typed || buffer.DwordCount is 0 or > 4 || buffer.OffsetBytes < 0 ||
            buffer.VectorAddress > (buffer.IndexEnabled && buffer.OffsetEnabled ? 254u : 255u) ||
            !(instruction.Opcode.StartsWith("BufferLoadDword", StringComparison.Ordinal) ||
              instruction.Opcode.StartsWith("BufferStoreDword", StringComparison.Ordinal)) || buffer.ScalarResource > 124)
            return false;
        var low = state.S[buffer.ScalarResource];
        var high = state.S[buffer.ScalarResource + 1];
        var format = state.S[buffer.ScalarResource + 3];
        if (!low.Exact || !high.Exact || !format.Exact || (high.Min & 0x80000000) != 0 || (format.Min & (1u << 23)) != 0)
            return false;
        var index = buffer.IndexEnabled ? state.V[buffer.VectorAddress] : Word.Of(0);
        var offset = buffer.OffsetEnabled ? state.V[buffer.VectorAddress + (buffer.IndexEnabled ? 1u : 0u)] : Word.Of(0);
        var scalar = instruction.Sources.Count > 2 ? state.Read(instruction.Sources[2]) : Word.Of(0);
        var stride = (high.Min >> 16) & 0x3FFF;
        var size = (ulong)index.Max * stride + offset.Max + scalar.Max + (uint)buffer.OffsetBytes + buffer.DwordCount * 4;
        // No modulo-address inference and no guessed cap: outside the analysis budget the caller reads the true count.
        var address = low.Min | ((ulong)(high.Min & 0xFFFF) << 32);
        if (size > DeviceAddressRangePlanner.MaxRangeBytes || size > 0x1_0000_0000_0000ul - address) return false;
        span = new(address, size);
        return true;
    }

    private static bool Scalar(State state, Gen5ShaderInstruction instruction)
    {
        var opcode = instruction.Opcode;
        var a = instruction.Sources.Count > 0 ? state.Read(instruction.Sources[0]) : Word.Unknown;
        var b = instruction.Sources.Count > 1 ? state.Read(instruction.Sources[1]) : Word.Unknown;
        if (instruction.Encoding == Gen5ShaderEncoding.Sopc)
        {
            var comparison = opcode switch
            {
                "SCmpEqU32" => "eq", "SCmpLgU32" => "ne", "SCmpLtU32" => "lt",
                "SCmpLeU32" => "le", "SCmpGtU32" => "gt", "SCmpGeU32" => "ge", _ => null,
            };
            if (comparison is null) return false;
            state.Scc = Word.Compare(comparison, a, b);
            return true;
        }
        if (instruction.Destinations is not [{ Kind: Gen5OperandKind.ScalarRegister } destination] || destination.Value >= 127)
            return false;
        var width = opcode.EndsWith("B64", StringComparison.Ordinal) ? 2 : 1;
        var oldCondition = state.Scc;
        // Read every source before writing an overlapping pair.
        var ah = instruction.Sources.Count > 0 ? state.High(instruction.Sources[0]) : Word.Unknown;
        var bh = instruction.Sources.Count > 1 ? state.High(instruction.Sources[1]) : Word.Unknown;
        for (var component = 0; component < width; component++)
        {
            var left = component == 0 ? a : ah;
            var right = component == 0 ? b : bh;
            Word result;
            switch (opcode)
            {
                case "SMovB32": case "SMovB64": result = left; break;
                case "SNotB32": case "SNotB64": result = left.Not(); break;
                case "SCselectB32": case "SCselectB64":
                    result = oldCondition.Exact ? (oldCondition.Min == 0 ? right : left) : left.Join(right); break;
                case "SBitset1B32": result = Word.Binary("or", state.S[destination.Value], Word.Binary("shl", Word.Of(1), left)); break;
                case "SLshl1AddU32": result = Word.Binary("add", Word.Binary("shl", left, Word.Of(1)), right); break;
                default:
                    var operation = opcode switch
                    {
                        "SLshlB32" => "shl", "SLshrB32" => "shr", "SAddI32" or "SAddU32" => "add",
                        "SSubI32" or "SSubU32" => "sub", "SMulI32" => "mul", "SMulHiU32" => "hi",
                        "SAndB32" or "SAndB64" or "SAndn2B64" => "and", "SOrB32" or "SOrB64" or "SNorB64" => "or",
                        "SXorB32" or "SXorB64" => "xor", _ => null,
                    };
                    if (operation is null) return false;
                    result = Word.Binary(operation, left, opcode == "SAndn2B64" ? right.Not() : right);
                    if (opcode == "SNorB64") result = result.Not();
                    break;
            }
            state.S[destination.Value + component] = result;
        }
        // Instructions that leave SCC unchanged must preserve a preceding comparison.
        if (opcode is not ("SMovB32" or "SMovB64" or "SCselectB32" or "SCselectB64" or "SMulI32" or "SMulHiU32" or "SBitset1B32"))
            state.Scc = new(0, 1);
        return true;
    }

    private static bool Vector(State state, Gen5ShaderInstruction instruction, uint next, Action<State> queue)
    {
        var opcode = instruction.Opcode;
        if (opcode.StartsWith("VCmp", StringComparison.Ordinal))
        {
            if (opcode.StartsWith("VCmpx", StringComparison.Ordinal))
            {
                // GFX10 CMPX writes EXEC, not VCC. A comparison cannot activate an inactive lane.
                if (state.Active != 0) state.S[126] = state.S[127] = Word.Unknown;
            }
            else
            {
                var destination = instruction.Control switch
                {
                    Gen5SdwaControl { ScalarDestination: { } register } => register,
                    Gen5Vop3Control { ScalarDestination: { } register } => register,
                    _ => 106u,
                };
                if (destination >= 127) return false;
                state.S[destination] = state.S[destination + 1] = Word.Unknown;
            }
            return true;
        }
        if (instruction.Destinations.Count != 1) return false;
        var dest = instruction.Destinations[0];
        var a = instruction.Sources.Count > 0 ? state.Read(instruction.Sources[0]) : Word.Unknown;
        var b = instruction.Sources.Count > 1 ? state.Read(instruction.Sources[1]) : Word.Unknown;
        var c = instruction.Sources.Count > 2 ? state.Read(instruction.Sources[2]) : Word.Unknown;
        var plain = instruction.Control is null or Gen5Vop3Control
            { AbsoluteMask: 0, NegateMask: 0, OutputModifier: 0, Clamp: false, OperandSelect: 0 };
        if (!plain)
        {
            // Modifiers/permutations are not interpreted. Poison the full destination, including SDWA preserve bits.
            if (dest.Kind != Gen5OperandKind.VectorRegister) return false;
            state.Write(dest, Word.Unknown);
            if (opcode == "VAddI32") state.S[106] = state.S[107] = Word.Unknown;
            if (instruction.Control is Gen5Vop3Control { ScalarDestination: { } poisonScalar })
            {
                if (poisonScalar >= 127) return false;
                state.S[poisonScalar] = state.S[poisonScalar + 1] = Word.Unknown;
            }
            return true;
        }
        if (opcode == "VReadfirstlaneB32")
        {
            // Undefined when no lane executes. Unknown also covers the implementation's inactive-lane behavior.
            state.Write(dest, state.Active == 0 ? Word.Unknown : a, vectorMasked: false);
            return true;
        }
        if (dest.Kind != Gen5OperandKind.VectorRegister) return false;
        Word result;
        switch (opcode)
        {
            case "VMovB32": result = a; break;
            case "VCndmaskB32": result = a.Join(b); break;
            case "VAndB32": result = Word.Binary("and", a, b); break;
            case "VXorB32": result = Word.Binary("xor", a, b); break;
            case "VAddI32": case "VAddU32": result = Word.Binary("add", a, b); break;
            case "VLshlrevB32": result = Word.Binary("shl", b, a); break;
            case "VLshrrevB32": result = Word.Binary("shr", b, a); break;
            case "VLshlAddU32": result = Word.Binary("add", Word.Binary("shl", a, b), c); break;
            case "VLshlOrU32": result = Word.Binary("or", Word.Binary("shl", a, b), c); break;
            case "VCvtF32U32": case "VRcpIflagF32": case "VMulF32": case "VCvtU32F32":
                if (!FloatResults(opcode, a, b, out var first, out var last)) return false;
                // Retain correlation through integer reciprocal/division sequences. An interval here would lose it.
                for (var bits = first; bits < last; bits++)
                {
                    var alternative = state.Copy();
                    alternative.Write(dest, Word.Of(bits));
                    alternative.Pc = next;
                    queue(alternative);
                }
                result = Word.Of(last);
                break;
            default: return false;
        }
        state.Write(dest, result);
        // VOP2 integer carry and VOP3B pair outputs must not leave old masks behind.
        if (instruction.Control is Gen5Vop3Control { ScalarDestination: { } scalar })
        {
            if (scalar >= 127) return false;
            state.S[scalar] = state.S[scalar + 1] = Word.Unknown;
        }
        else if (opcode == "VAddI32") state.S[106] = state.S[107] = Word.Unknown;
        return true;
    }

    private static bool FloatResults(string opcode, Word a, Word b, out uint first, out uint last)
    {
        first = last = 0;
        if (!a.Exact) return false;
        if (opcode == "VCvtU32F32")
        {
            var value = BitConverter.UInt32BitsToSingle(a.Min);
            first = last = float.IsNaN(value) || value <= 0 ? 0 : value >= 4294967296d ? uint.MaxValue : (uint)value;
            return true;
        }
        if (opcode == "VCvtF32U32")
        {
            // Exact integer conversion needs no floating-point rounding-mode assumptions.
            var converted = (float)a.Min;
            if ((double)converted != a.Min) return false;
            first = last = BitConverter.SingleToUInt32Bits(converted);
            return true;
        }
        var left = BitConverter.UInt32BitsToSingle(a.Min);
        if (!float.IsNormal(left) || left <= 0) return false;
        double exact;
        uint radius;
        if (opcode == "VRcpIflagF32")
        {
            if (left < Math.ScaleB(1, -126) || left > Math.ScaleB(1, 126)) return false;
            exact = 1d / left;
            // Vulkan OpFDiv permits 2.5 ULP. Eight adjacent representations also cover a binade boundary.
            radius = 8;
        }
        else
        {
            if (!b.Exact) return false;
            var right = BitConverter.UInt32BitsToSingle(b.Min);
            if (!float.IsNormal(right) || right <= 0) return false;
            exact = (double)left * right;
            radius = 1; // Both directed and nearest rounding of the exact 48-bit product.
        }
        var center = BitConverter.SingleToUInt32Bits((float)exact);
        if (center < 0x00800000 + radius || center >= 0x7F800000 - radius) return false;
        first = center - radius;
        last = center + radius;
        return true;
    }

    private sealed class State(uint pc) : IEquatable<State>
    {
        public uint Pc = pc;
        public Word[] S = Enumerable.Repeat(Word.Unknown, 128).ToArray();
        public Word[] V = Enumerable.Repeat(Word.Unknown, 256).ToArray();
        public Word Scc = new(0, 1);
        public uint FullHigh;
        public int Active => S[126].Max == 0 && S[127].Max == 0 ? 0 :
            S[126].Exact && S[126].Min == uint.MaxValue && S[127].Exact && S[127].Min == FullHigh ? 1 : 2;
        public State Copy()
        {
            var copy = (State)MemberwiseClone();
            copy.S = (Word[])S.Clone(); copy.V = (Word[])V.Clone();
            return copy;
        }
        public Word Read(Gen5Operand operand) => operand.Kind switch
        {
            Gen5OperandKind.ScalarRegister when operand.Value < 128 => S[operand.Value],
            Gen5OperandKind.VectorRegister when operand.Value < 256 => V[operand.Value],
            Gen5OperandKind.LiteralConstant => Word.Of(operand.Value),
            Gen5OperandKind.EncodedConstant when Gen5InlineConstants.TryDecode(operand.Value, out var value) => Word.Of(value),
            _ => Word.Unknown,
        };
        public Word High(Gen5Operand operand) => operand.Kind switch
        {
            Gen5OperandKind.ScalarRegister when operand.Value < 127 => S[operand.Value + 1],
            Gen5OperandKind.EncodedConstant when operand.Value is >= 193 and <= 208 => Word.Of(uint.MaxValue),
            Gen5OperandKind.EncodedConstant when Gen5InlineConstants.IsAperture(operand.Value) => Word.Of((uint)(Gen5InlineConstants.DecodeAperture64(operand.Value) >> 32)),
            Gen5OperandKind.EncodedConstant or Gen5OperandKind.LiteralConstant => Word.Of(0),
            _ => Word.Unknown,
        };
        public void Write(Gen5Operand destination, Word value, bool vectorMasked = true)
        {
            if (destination.Kind == Gen5OperandKind.ScalarRegister && destination.Value < 128) S[destination.Value] = value;
            else if (destination.Kind == Gen5OperandKind.VectorRegister && destination.Value < 256)
            {
                if (!vectorMasked || Active == 1) V[destination.Value] = value;
                else if (Active == 2) V[destination.Value] = V[destination.Value].Join(value);
            }
        }
        public bool Equals(State? other) => other is not null && Pc == other.Pc && Scc == other.Scc &&
            S.AsSpan().SequenceEqual(other.S) && V.AsSpan().SequenceEqual(other.V);
        public override bool Equals(object? other) => other is State state && Equals(state);
        public override int GetHashCode()
        {
            var hash = new HashCode(); hash.Add(Pc); hash.Add(Scc);
            foreach (var word in S) hash.Add(word);
            foreach (var word in V) hash.Add(word);
            return hash.ToHashCode();
        }
    }
}
