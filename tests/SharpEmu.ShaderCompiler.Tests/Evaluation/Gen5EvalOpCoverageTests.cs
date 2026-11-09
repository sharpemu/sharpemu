// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Evaluation;
using Xunit;
using static SharpEmu.ShaderCompiler.Evaluation.Gen5EvaluableLowering;
using static SharpEmu.ShaderCompiler.Tests.Evaluation.EvaluationTestSupport;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Evaluation;

// Every opcode the lowering accepts runs through the interpreter, so a table entry without an interpreter arm fails here.
public sealed class Gen5EvalOpCoverageTests
{
    private const uint Vcc = 106;
    private const uint Result = 28;

    private static readonly string[] Compares = ["VCmpGtU32", "VCmpLtI32", "VCmpxGtU32", "SCmpLtU32", "SCmpGtI32", "SCmpkLtU32", "SCmpkEqI32"];
    private static readonly string[] OneSource = ["VMovB32", "VCvtF32U32", "VCvtU32F32", "VRcpIflagF32"];
    private static readonly string[] ThreeSources = ["VLshlAddU32", "VAndOrB32", "VAdd3U32", "VBfiB32", "VMadU32U24"];

    private static IEnumerable<string> AllOpcodes => Entries.Select(entry => entry.Opcode).Concat(Compares);

    public static TheoryData<string> Opcodes => [.. AllOpcodes];

    [Theory]
    [MemberData(nameof(Opcodes))]
    public void EveryLoweredOpcode_RunsOnTheInterpreter(string opcode)
    {
        var program = Gen5EvaluableProgram.Create(Program([.. ProgramFor(opcode)]));
        Assert.True(program.IsEvaluable, program.Refusal);

        var outcome = Gen5DispatchEvaluator.Evaluate(program, Request(), Memory());

        Assert.Equal(Gen5EvaluationRefusal.None, outcome.Refusal);
    }

    [Fact]
    public void EveryGen5EvalOp_IsProducedByTheLowering()
    {
        var produced = new HashSet<Gen5EvalOp>();
        foreach (var opcode in AllOpcodes)
        {
            produced.UnionWith(Gen5EvaluableProgram.Create(Program([.. ProgramFor(opcode)])).Instructions.Select(instruction => instruction.Op));
        }

        Assert.Empty(Enum.GetValues<Gen5EvalOp>().Except(produced));
    }

    private static Gen5DispatchRequest Request() => new()
    {
        UserData = [.. DwordBuffer(SourceAddress, 256), .. DwordBuffer(DestinationAddress, 1024)],
        ThreadsX = 64,
    };

    private static EvaluationMemory Memory()
    {
        var memory = new EvaluationMemory();
        memory.WriteWords(SourceAddress, Enumerable.Range(0, 256).Select(index => 0x100u + (uint)index).ToArray());
        return memory;
    }

    private static Gen5Operand Float(float value) => new(Gen5OperandKind.LiteralConstant, BitConverter.SingleToUInt32Bits(value));

    private static uint Dwords(string opcode) => opcode[^2] == 'x' ? (uint)(opcode[^1] - '0') : 1u;

    // Lanes store v4 at lane * 4; a control opcode runs after that store, every other one before it.
    private static IEnumerable<Gen5ShaderInstruction> ProgramFor(string opcode)
    {
        var instructions = new List<Gen5ShaderInstruction>
        {
            MoveScalar(0, 20, 5),
            MoveScalar(0, 21, 3),
            MoveScalar(0, 22, 7),
            MoveScalar(0, 23, 2),
            Sop1(0, "SMovB32", 24, Operand((uint)SourceAddress)),
            Sop1(0, "SMovB32", 25, Operand((uint)(SourceAddress >> 32))),
            Sop1(0, "SMovB64", 26, Operand(3)),
            Vop1(0, "VMovB32", 1, Gen5Operand.Vector(0)),
            MoveVector(0, 2, 9),
            MoveVector(0, 3, 4),
            MoveVector(0, 4, 1),
            MoveVector(0, 5, 2),
            MoveVector(0, 6, 3),
            MoveVector(0, 7, 4),
            Vopc(0, "VCmpGtU32", Operand(100), 1),
            Sopc(0, "SCmpLtU32", Gen5Operand.Scalar(20), Gen5Operand.Scalar(21)),
        };
        var compare = opcode.StartsWith("VCmp", StringComparison.Ordinal) || opcode.StartsWith("SCmp", StringComparison.Ordinal);
        var form = compare ? Form.VectorInteger : Entries.Single(entry => entry.Opcode == opcode).Form;
        var store = form == Form.Buffer && opcode.Contains("Store", StringComparison.Ordinal);
        if (form != Form.Control)
        {
            instructions.AddRange(Body(opcode, form, compare));
        }

        if (!store)
        {
            instructions.Add(Vop2(0, "VLshlrevB32", 9, Operand(2), Gen5Operand.Vector(0)));
            instructions.Add(BufferAccess(0, "BufferStoreDword", 4, vectorData: 4, offsetEnabled: true, vectorAddress: 9));
        }

        if (form == Form.Control)
        {
            instructions.Add(opcode.StartsWith("SBranch", StringComparison.Ordinal) || opcode.StartsWith("SCbranch", StringComparison.Ordinal)
                ? Branch(0, opcode, 1)
                : new Gen5ShaderInstruction(0, Gen5ShaderEncoding.Sopp, opcode, [0u], [], [], null));
        }

        instructions.Add(EndProgram(0));
        return instructions.Select((instruction, index) => instruction with { Pc = (uint)index * 8 });
    }

    private static IEnumerable<Gen5ShaderInstruction> Body(string opcode, Form form, bool compare)
    {
        if (compare)
        {
            yield return opcode switch
            {
                _ when opcode.StartsWith("SCmpk", StringComparison.Ordinal) =>
                    new Gen5ShaderInstruction(0, Gen5ShaderEncoding.Sopk, opcode, [3u], [], [Gen5Operand.Scalar(20)], null),
                _ when opcode.StartsWith("SCmp", StringComparison.Ordinal) => Sopc(0, opcode, Gen5Operand.Scalar(20), Gen5Operand.Scalar(21)),
                _ => Vopc(0, opcode, Gen5Operand.Vector(2), 1),
            };
            yield break;
        }

        switch (form)
        {
            case Form.Scalar:
                yield return Sop2(0, opcode, Result, Gen5Operand.Scalar(20), Gen5Operand.Scalar(21));
                break;
            case Form.Scalar64:
                yield return Sop2(0, opcode, Result, Gen5Operand.Scalar(26), Gen5Operand.Scalar(20));
                break;
            case Form.ScalarImmediate:
                yield return new Gen5ShaderInstruction(0, Gen5ShaderEncoding.Sopk, opcode, [5u], [], [Gen5Operand.Scalar(Result)], null);
                break;
            case Form.ScalarMemory:
                yield return opcode.StartsWith("SBufferLoad", StringComparison.Ordinal)
                    ? ScalarBufferLoad(0, 0, Result, Dwords(opcode))
                    : ScalarLoad(0, 24, Result, Dwords(opcode));
                break;
            case Form.VectorLane:
                yield return ReadFirstLane(0, Result, 1);
                break;
            case Form.VectorInteger when opcode == "VMadU64U32":
                yield return new Gen5ShaderInstruction(0, Gen5ShaderEncoding.Vop3, opcode, [0u, 0u],
                    [Gen5Operand.Vector(1), Gen5Operand.Vector(2), Gen5Operand.Vector(6)], [Gen5Operand.Vector(4)], new Gen5Vop3Control(0, 0, 0, false, 0, Vcc));
                yield break;
            case Form.VectorInteger when ThreeSources.Contains(opcode):
                yield return Vop3(0, opcode, 4, Gen5Operand.Vector(1), Gen5Operand.Vector(2), Gen5Operand.Vector(3));
                yield break;
            case Form.VectorInteger:
                yield return OneSource.Contains(opcode) ? Vop1(0, opcode, 4, Gen5Operand.Vector(1)) : Vop2(0, opcode, 4, Gen5Operand.Vector(1), Gen5Operand.Vector(2));
                yield break;
            case Form.VectorFloat:
                yield return OneSource.Contains(opcode) ? Vop1(0, opcode, 4, Float(4f)) : Vop2(0, opcode, 4, Float(1.5f), Float(2f));
                yield break;
            default:
            {
                var dwords = Dwords(opcode);
                var shift = opcode.Contains("Load", StringComparison.Ordinal) ? 4u : dwords switch { 1 => 2u, 2 => 3u, _ => 4u };
                yield return Vop2(0, "VLshlrevB32", 8, Operand(shift), Gen5Operand.Vector(0));
                yield return BufferAccess(0, opcode, opcode.Contains("Load", StringComparison.Ordinal) ? 0u : 4u, dwords: dwords, vectorData: 4, offsetEnabled: true, vectorAddress: 8);
                yield break;
            }
        }

        yield return Vop1(0, "VMovB32", 4, Gen5Operand.Scalar(Result));
    }
}
