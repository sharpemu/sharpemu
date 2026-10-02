// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Vulkan;
using System.Runtime.InteropServices;
using System.Text;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests;

// Programs with scalar control flow, shared by the translation tests here and the device tests that
// run them through both control-flow forms. Inputs are s8 and s9; results are stored from v4/v5
// through the buffer V# in s4..s7. Every instruction takes one 4-byte slot.
public sealed class Gen5StructuredControlFlowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RegisterFiles_BelongToTheFunctionExecutingTheProgram(bool forceDispatcher)
    {
        var code = Compile(NestedIfSharedMerge(), forceDispatcher);
        var words = MemoryMarshal.Cast<byte, uint>(code.AsSpan()).ToArray();
        var names = new Dictionary<uint, string>();
        var variables = new Dictionary<uint, uint>();
        for (var index = 5; index < words.Length; index += (int)(words[index] >> 16))
        {
            var length = (int)(words[index] >> 16);
            var opcode = (SpirvOp)(words[index] & 0xFFFF);
            if (opcode == SpirvOp.Name)
                names[words[index + 1]] = Encoding.UTF8.GetString(code.AsSpan((index + 2) * 4, (length - 2) * 4)).TrimEnd('\0');
            if (opcode == SpirvOp.Variable) variables[words[index + 2]] = words[index + 3];
        }
        foreach (var name in new[] { "sgpr", "vgpr" })
        {
            var id = Assert.Single(names, entry => entry.Value == name).Key;
            Assert.Equal((uint)SpirvStorageClass.Function, variables[id]);
        }
        Gen5LargeDispatcherValidationTests.ValidateWithSpirvToolsWhenAvailable(code);
    }

    private static Gen5Operand S(uint register) => Gen5Operand.Scalar(register);

    private static Gen5ShaderInstruction Store(uint pc, uint vector, int offset) =>
        BufferAccess(pc, "BufferStoreDword", 4, offset: offset, vectorData: vector);

    // for (i = 0; i < s8; i++) acc += (i & 1) != 0 ? i : 100;  stores acc, i.
    public static Gen5ShaderProgram LoopWithIfElse() => Program(
        MoveScalar(0, 10, 0),
        MoveScalar(4, 11, 0),
        Sopc(8, "SCmpLtU32", S(11), S(8)),
        Branch(12, "SCbranchScc0", 7),
        Sop2(16, "SAndB32", 12, S(11), Operand(1)),
        Branch(20, "SCbranchScc0", 2),
        Sop2(24, "SAddU32", 10, S(10), S(11)),
        Branch(28, "SBranch", 1),
        Sop2(32, "SAddU32", 10, S(10), Operand(100)),
        Sop2(36, "SAddU32", 11, S(11), Operand(1)),
        Branch(40, "SBranch", -9),
        MoveVectorFromScalar(44, 4, 10),
        MoveVectorFromScalar(48, 5, 11),
        Store(52, 4, 0),
        Store(56, 5, 4),
        EndProgram(60));

    public static (uint Acc, uint Second) LoopWithIfElseExpected(uint n, uint unused)
    {
        uint acc = 0;
        uint i = 0;
        for (; i < n; i++)
        {
            acc += (i & 1) != 0 ? i : 100u;
        }

        return (acc, i);
    }

    // Nested loops that leave the program early:
    // for (i < s8) for (j < i) { acc += j; if (acc == s9) { store acc, 0xDEAD; end } }  stores acc, i.
    public static Gen5ShaderProgram NestedLoopsWithEarlyExit() => Program(
        MoveScalar(0, 10, 0),
        MoveScalar(4, 11, 0),
        Sopc(8, "SCmpLtU32", S(11), S(8)),
        Branch(12, "SCbranchScc0", 10),
        MoveScalar(16, 12, 0),
        Sopc(20, "SCmpLtU32", S(12), S(11)),
        Branch(24, "SCbranchScc0", 5),
        Sop2(28, "SAddU32", 10, S(10), S(12)),
        Sopc(32, "SCmpEqU32", S(10), S(9)),
        Branch(36, "SCbranchScc1", 9),
        Sop2(40, "SAddU32", 12, S(12), Operand(1)),
        Branch(44, "SBranch", -7),
        Sop2(48, "SAddU32", 11, S(11), Operand(1)),
        Branch(52, "SBranch", -12),
        MoveVectorFromScalar(56, 4, 10),
        MoveVectorFromScalar(60, 5, 11),
        Store(64, 4, 0),
        Store(68, 5, 4),
        EndProgram(72),
        MoveVectorFromScalar(76, 4, 10),
        MoveVector(80, 5, 0xDEAD),
        Store(84, 4, 0),
        Store(88, 5, 4),
        EndProgram(92));

    public static (uint Acc, uint Second) NestedLoopsWithEarlyExitExpected(uint n, uint target)
    {
        uint acc = 0;
        for (uint i = 0; i < n; i++)
        {
            for (uint j = 0; j < i; j++)
            {
                acc += j;
                if (acc == target)
                {
                    return (acc, 0xDEAD);
                }
            }
        }

        return (acc, n);
    }

    // if (s8 & 1) { acc += 1; if (s8 & 2) acc += 10; } acc += 100;  the inner selection merges where
    // the outer one does.
    public static Gen5ShaderProgram NestedIfSharedMerge() => Program(
        MoveScalar(0, 10, 0),
        Sop2(4, "SAndB32", 12, S(8), Operand(1)),
        Branch(8, "SCbranchScc0", 4),
        Sop2(12, "SAddU32", 10, S(10), Operand(1)),
        Sop2(16, "SAndB32", 12, S(8), Operand(2)),
        Branch(20, "SCbranchScc0", 1),
        Sop2(24, "SAddU32", 10, S(10), Operand(10)),
        Sop2(28, "SAddU32", 10, S(10), Operand(100)),
        MoveVectorFromScalar(32, 4, 10),
        MoveVector(36, 5, 0),
        Store(40, 4, 0),
        Store(44, 5, 4),
        EndProgram(48));

    public static (uint Acc, uint Second) NestedIfSharedMergeExpected(uint n, uint unused)
    {
        uint acc = 0;
        if ((n & 1) != 0)
        {
            acc += 1;
            if ((n & 2) != 0)
            {
                acc += 10;
            }
        }

        return (acc + 100, 0);
    }

    // Two entries into one cycle (A <-> B), so the graph is irreducible and keeps the dispatcher.
    public static Gen5ShaderProgram IrreducibleCycle() => Program(
        MoveScalar(0, 10, 0),
        Sopc(4, "SCmpEqU32", S(8), Operand(0)),
        Branch(8, "SCbranchScc1", 3),
        Sop2(12, "SAddU32", 10, S(10), Operand(1)),
        Sopc(16, "SCmpLtU32", S(10), S(9)),
        Branch(20, "SCbranchScc0", 3),
        Sop2(24, "SAddU32", 10, S(10), Operand(2)),
        Sopc(28, "SCmpLtU32", S(10), S(9)),
        Branch(32, "SCbranchScc1", -6),
        MoveVectorFromScalar(36, 4, 10),
        MoveVector(40, 5, 0),
        Store(44, 4, 0),
        Store(48, 5, 4),
        EndProgram(52));

    public static (uint Acc, uint Second) IrreducibleCycleExpected(uint n, uint limit)
    {
        uint acc = 0;
        var inB = n == 0;
        while (true)
        {
            if (!inB)
            {
                acc += 1;
                if (!(acc < limit))
                {
                    break;
                }
            }

            inB = false;
            acc += 2;
            if (!(acc < limit))
            {
                break;
            }
        }

        return (acc, 0);
    }

    public static IEnumerable<object[]> StructuredPrograms()
    {
        yield return [nameof(LoopWithIfElse)];
        yield return [nameof(NestedLoopsWithEarlyExit)];
        yield return [nameof(NestedIfSharedMerge)];
    }

    public static Gen5ShaderProgram Create(string name) => name switch
    {
        nameof(LoopWithIfElse) => LoopWithIfElse(),
        nameof(NestedLoopsWithEarlyExit) => NestedLoopsWithEarlyExit(),
        nameof(NestedIfSharedMerge) => NestedIfSharedMerge(),
        nameof(IrreducibleCycle) => IrreducibleCycle(),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    public static (uint Acc, uint Second) Expected(string name, uint input0, uint input1) => name switch
    {
        nameof(LoopWithIfElse) => LoopWithIfElseExpected(input0, input1),
        nameof(NestedLoopsWithEarlyExit) => NestedLoopsWithEarlyExitExpected(input0, input1),
        nameof(NestedIfSharedMerge) => NestedIfSharedMergeExpected(input0, input1),
        nameof(IrreducibleCycle) => IrreducibleCycleExpected(input0, input1),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [MemberData(nameof(StructuredPrograms))]
    public void StructurableProgramIsEmittedWithoutTheDispatcher(string name)
    {
        var structured = Compile(Create(name), forceDispatcher: false);
        var dispatcher = Compile(Create(name), forceDispatcher: true);

        // The structured body is its own function, called once from the entry point.
        Assert.Contains(SpirvOp.FunctionCall, Gen5LargeDispatcherValidationTests.ReadSpirvOpcodes(structured));
        Assert.DoesNotContain(SpirvOp.FunctionCall, Gen5LargeDispatcherValidationTests.ReadSpirvOpcodes(dispatcher));
        foreach (var code in new[] { structured, dispatcher })
        {
            Gen5LargeDispatcherValidationTests.ValidateStructuredControlFlow(code);
            Gen5LargeDispatcherValidationTests.ValidateWithSpirvToolsWhenAvailable(code);
        }
    }

    [Fact]
    public void IrreducibleProgramKeepsTheDispatcher()
    {
        var code = Compile(IrreducibleCycle(), forceDispatcher: false);
        Assert.DoesNotContain(SpirvOp.FunctionCall, Gen5LargeDispatcherValidationTests.ReadSpirvOpcodes(code));
        Gen5LargeDispatcherValidationTests.ValidateStructuredControlFlow(code);
        Gen5LargeDispatcherValidationTests.ValidateWithSpirvToolsWhenAvailable(code);
    }

    [Fact]
    public void LoopsAreDeclaredWithLoopMerges()
    {
        var opcodes = Gen5LargeDispatcherValidationTests.ReadSpirvOpcodes(Compile(NestedLoopsWithEarlyExit(), forceDispatcher: false));
        Assert.Equal(2, opcodes.Count(static opcode => opcode == SpirvOp.LoopMerge));
    }

    // A loop that can leave through a chain of diamonds, each of whose sides flows into the next.
    // Copying that exit tail at the loop exit doubles at every diamond.
    public static Gen5ShaderProgram LoopExitThroughDiamondChain(int diamonds)
    {
        var instructions = new List<Gen5ShaderInstruction>
        {
            MoveScalar(0, 10, 0),
            MoveScalar(4, 11, 0),
            Sop2(8, "SAddU32", 11, S(11), Operand(1)),
            Sopc(12, "SCmpEqU32", S(11), S(9)),
            Branch(16, "SCbranchScc1", 3), // -> 32, the diamond chain
            Sopc(20, "SCmpLtU32", S(11), S(8)),
            Branch(24, "SCbranchScc1", -5), // -> 8
            EndProgram(28),
        };
        uint pc = 32;
        for (var diamond = 0; diamond < diamonds; diamond++)
        {
            instructions.Add(Sopc(pc, "SCmpEqU32", S(11), Operand((uint)diamond)));
            instructions.Add(Branch(pc + 4, "SCbranchScc1", 2));
            instructions.Add(Sop2(pc + 8, "SAddU32", 10, S(10), Operand(1)));
            instructions.Add(Branch(pc + 12, "SBranch", 1));
            instructions.Add(Sop2(pc + 16, "SAddU32", 10, S(10), Operand(2)));
            pc += 20;
        }

        instructions.Add(MoveVectorFromScalar(pc, 4, 10));
        instructions.Add(Store(pc + 4, 4, 0));
        instructions.Add(EndProgram(pc + 8));
        return Program([.. instructions]);
    }

    [Fact]
    public void ExitTailCopiesStayBounded()
    {
        var structured = Compile(LoopExitThroughDiamondChain(14), forceDispatcher: false);
        var dispatcher = Compile(LoopExitThroughDiamondChain(14), forceDispatcher: true);
        Assert.True(
            structured.Length <= dispatcher.Length * 4,
            $"structured={structured.Length} bytes, dispatcher={dispatcher.Length} bytes");
        Gen5LargeDispatcherValidationTests.ValidateStructuredControlFlow(structured);
        Gen5LargeDispatcherValidationTests.ValidateWithSpirvToolsWhenAvailable(structured);
    }

    // The control-flow skeleton of a Yotei compute shader: early execz exits to the final
    // S_ENDPGM before and after a loop, and vccnz skips over the loop.
    public static Gen5ShaderProgram EarlyExitsAroundLoop() => Program(
        Nop(0), Branch(4, "SCbranchExecz", 22), Nop(8), Branch(12, "SCbranchExecz", 20), Nop(16),
        Branch(20, "SCbranchVccnz", 10), Nop(24), Branch(28, "SCbranchVccnz", 8), Nop(32), Nop(36),
        Branch(40, "SCbranchVccz", 3), Nop(44), Branch(48, "SBranch", -4), Nop(52), Nop(56), Nop(60),
        Branch(64, "SCbranchExecz", 7), Nop(68), Branch(72, "SCbranchExecz", 3), Nop(76), Nop(80),
        Nop(84), Nop(88), Nop(92), EndProgram(96));

    [Fact]
    public void EarlyExitsAroundLoopAreStructured()
    {
        var code = Compile(EarlyExitsAroundLoop(), forceDispatcher: false);
        Assert.Contains(SpirvOp.FunctionCall, Gen5LargeDispatcherValidationTests.ReadSpirvOpcodes(code));
        Gen5LargeDispatcherValidationTests.ValidateStructuredControlFlow(code);
        Gen5LargeDispatcherValidationTests.ValidateWithSpirvToolsWhenAvailable(code);
    }

    // One arm reaches the shared block directly; the other either reaches it or ends the program
    // on its own, so the shared block post-dominates nothing.
    public static Gen5ShaderProgram EarlyReturnBesideSharedBlock() => Program(
        Nop(0), Branch(4, "SCbranchScc1", 5), Nop(8), Branch(12, "SCbranchScc1", 1),
        Branch(16, "SBranch", 2), Nop(20), EndProgram(24), Nop(28), EndProgram(32));

    [Fact]
    public void EarlyReturnBesideSharedBlockIsStructured()
    {
        var code = Compile(EarlyReturnBesideSharedBlock(), forceDispatcher: false);
        Assert.Contains(SpirvOp.FunctionCall, Gen5LargeDispatcherValidationTests.ReadSpirvOpcodes(code));
        Gen5LargeDispatcherValidationTests.ValidateStructuredControlFlow(code);
        Gen5LargeDispatcherValidationTests.ValidateWithSpirvToolsWhenAvailable(code);
    }

    internal static byte[] Compile(Gen5ShaderProgram program, bool forceDispatcher)
    {
        var (plan, resources, layout) = Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            LocalSizeX = 1,
            ThreadCountX = 1,
            ForceDispatcher = forceDispatcher,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        return shader.Spirv;
    }
}
