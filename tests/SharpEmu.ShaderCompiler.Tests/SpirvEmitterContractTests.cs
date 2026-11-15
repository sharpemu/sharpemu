// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests;

[Collection("SpirvOptimization")]
public sealed class SpirvEmitterContractTests
{
    [Fact]
    public void DontInlineCallKeepsItsInputStoreAndReloadsItsWrittenPrivateRegister()
    {
        var m = new SpirvModuleBuilder();
        m.AddCapability(SpirvCapability.Shader);
        var u = m.TypeInt(32, false);
        var register = Variable(m, u, SpirvStorageClass.Private);
        var output = Output(m, u);
        var a = m.Constant(u, 11);
        var b = m.Constant(u, 22);
        var helper = m.BeginFunction(m.TypeVoid(), m.TypeFunction(m.TypeVoid()), 2); // DontInline.
        m.AddLabel();
        var read = m.AddInstruction(SpirvOp.Load, u, register);
        m.AddStatement(SpirvOp.Store, output, read);
        m.AddStatement(SpirvOp.Store, register, b);
        End(m);

        var (main, _) = Vertex(m, register, output);
        m.AddStatement(SpirvOp.Store, register, a);
        m.AddInstruction(SpirvOp.FunctionCall, m.TypeVoid(), helper);
        var after = m.AddInstruction(SpirvOp.Load, u, register);
        Assert.NotEqual(a, after);
        m.AddStatement(SpirvOp.Store, output, after);
        End(m);

        var instructions = Instructions(m.Build()).ToArray();
        var call = Assert.Single(instructions.Where(i => i.Function == main && i.Op == SpirvOp.FunctionCall));
        Assert.Contains(instructions, i => i.Function == main && i.Op == SpirvOp.Store &&
            i.Words[0] == register && i.Words[1] == a && i.Offset < call.Offset);
        Assert.Contains(instructions, i => i.Function == helper && i.Op == SpirvOp.Load &&
            i.Words[2] == register);
        Assert.Contains(instructions, i => i.Function == main && i.Op == SpirvOp.Load &&
            i.Words[1] == after && i.Words[2] == register && i.Offset > call.Offset);
    }

    [Fact]
    public void SpecializationValueIsNeitherFoldedToItsDefaultNorHoistedFromItsBlock()
    {
        var m = new SpirvModuleBuilder();
        m.AddCapability(SpirvCapability.Shader);
        var u = m.TypeInt(32, false);
        var output = Output(m, u);
        var input = Variable(m, u, SpirvStorageClass.Input);
        m.AddDecoration(input, SpirvDecoration.Location, 1);
        var spec = m.SpecConstant(u, 7, 9);
        var (main, entry) = Vertex(m, input, output);
        var condition = m.AddInstruction(SpirvOp.INotEqual, m.TypeBool(),
            m.AddInstruction(SpirvOp.Load, u, input), m.Constant(u, 0));
        var run = m.AllocateId();
        var merge = m.AllocateId();
        m.AddStatement(SpirvOp.SelectionMerge, merge, 0);
        m.AddStatement(SpirvOp.BranchConditional, condition, run, merge);
        m.AddLabel(run);
        var sum = m.AddInstruction(SpirvOp.IAdd, u, spec, m.Constant(u, 1));
        Assert.NotEqual(m.Constant(u, 10), sum);
        m.AddStatement(SpirvOp.Store, output, sum);
        m.AddStatement(SpirvOp.Branch, merge);
        m.AddLabel(merge);
        End(m);

        var instructions = Instructions(m.Build()).ToArray();
        var addition = Assert.Single(instructions.Where(i => i.Op == SpirvOp.IAdd && i.Words[1] == sum));
        Assert.Equal(main, addition.Function);
        Assert.Equal(run, addition.Block);
        Assert.NotEqual(entry, addition.Block);
        Assert.Contains(instructions, i => i.Op == SpirvOp.SpecConstant && i.Words[1] == spec);
    }

    [Fact]
    public void TwoClockReadsRemainTwoDistinctEffectfulInstructions()
    {
        var m = new SpirvModuleBuilder();
        m.AddCapability(SpirvCapability.Shader);
        m.AddCapability(SpirvCapability.Int64);
        m.AddCapability(SpirvCapability.ShaderClockKhr);
        m.AddExtension("SPV_KHR_shader_clock");
        var u = m.TypeInt(32, false);
        var wide = m.TypeInt(64, false);
        var output = Output(m, wide);
        Vertex(m, output);
        var scope = m.Constant(u, 1);
        var first = m.AddInstruction(SpirvOp.ReadClockKhr, wide, scope);
        var second = m.AddInstruction(SpirvOp.ReadClockKhr, wide, scope);
        Assert.NotEqual(first, second);
        m.AddStatement(SpirvOp.Store, output, m.AddInstruction(SpirvOp.ISub, wide, second, first));
        End(m);
        var clocks = Instructions(m.Build()).Where(i => i.Op == SpirvOp.ReadClockKhr).ToArray();
        Assert.Equal(2, clocks.Length);
        Assert.Contains(clocks, i => i.Words[1] == first);
        Assert.Contains(clocks, i => i.Words[1] == second);
    }

    [Fact]
    public void ExtraSubgroupArithmeticIsNotMergedAsAnOrdinaryValue()
    {
        var m = new SpirvModuleBuilder();
        m.AddCapability(SpirvCapability.Shader);
        m.AddCapability(SpirvCapability.GroupNonUniform);
        m.AddCapability(SpirvCapability.GroupNonUniformArithmetic);
        var u = m.TypeInt(32, false);
        var output = Output(m, u);
        Vertex(m, output);
        var scope = m.Constant(u, 3);
        var one = m.Constant(u, 1);
        var first = m.AddInstruction(SpirvOp.GroupNonUniformIAdd, u, scope, 0, one);
        var second = m.AddInstruction(SpirvOp.GroupNonUniformIAdd, u, scope, 0, one);
        Assert.NotEqual(first, second);
        m.AddStatement(SpirvOp.Store, output, m.AddInstruction(SpirvOp.IAdd, u, first, second));
        End(m);
        var reductions = Instructions(m.Build()).Where(i => i.Op == SpirvOp.GroupNonUniformIAdd).ToArray();
        Assert.Equal(2, reductions.Length);
        Assert.Contains(reductions, i => i.Words[1] == first);
        Assert.Contains(reductions, i => i.Words[1] == second);
    }

    [Fact]
    public void ConditionalScratchStoreMergesBothIncomingValuesWithAPhi()
    {
        var m = new SpirvModuleBuilder();
        m.AddCapability(SpirvCapability.Shader);
        var u = m.TypeInt(32, false);
        var scratch = Variable(m, u, SpirvStorageClass.Private);
        var input = Variable(m, u, SpirvStorageClass.Input);
        m.AddDecoration(input, SpirvDecoration.Location, 1);
        var output = Output(m, u);
        Vertex(m, scratch, input, output);
        var zero = m.Constant(u, 0);
        var changed = m.Constant(u, 23);
        m.AddStatement(SpirvOp.Store, scratch, zero);
        var condition = m.AddInstruction(SpirvOp.INotEqual, m.TypeBool(),
            m.AddInstruction(SpirvOp.Load, u, input), zero);
        var yes = m.AllocateId();
        var no = m.AllocateId();
        var merge = m.AllocateId();
        m.AddStatement(SpirvOp.SelectionMerge, merge, 0);
        m.AddStatement(SpirvOp.BranchConditional, condition, yes, no);
        m.AddLabel(yes);
        m.AddStatement(SpirvOp.Store, scratch, changed);
        m.AddStatement(SpirvOp.Branch, merge);
        m.AddLabel(no);
        m.AddStatement(SpirvOp.Branch, merge);
        m.AddLabel(merge);
        var loaded = m.AddInstruction(SpirvOp.Load, u, scratch);
        m.AddStatement(SpirvOp.Store, output, loaded);
        End(m);
        Assert.True(m.InheritedBlocksHeld);

        var instructions = Instructions(m.Build()).ToArray();
        var phi = Assert.Single(instructions.Where(i => i.Op == SpirvOp.Phi && i.Words[1] == loaded));
        Assert.Equal(merge, phi.Block);
        var incoming = Enumerable.Range(0, (phi.Words.Length - 2) / 2)
            .Select(i => (Value: phi.Words[2 + i * 2], Block: phi.Words[3 + i * 2])).ToArray();
        Assert.Equal(2, incoming.Length);
        Assert.Contains((changed, yes), incoming);
        Assert.Contains((zero, no), incoming);
        Assert.Contains(instructions, i => i.Op == SpirvOp.Store && i.Words[0] == output && i.Words[1] == loaded);
    }

    [Fact]
    public void RuntimeValuesDoNotCrossFromMainIntoAnOutlinedFunction()
    {
        // Two sites share the body, so the module runs it as a DontInline function that both sites call. A linked body
        // reaches its buffer through a device address, and the caller stores through one after the calls: main and the
        // body each resolve their addresses with the page table's length, which every function reads itself.
        var caller = Program(
            MoveScalar(0, 14, 0x2000), MoveScalar(4, 15, 0),
            MoveScalar(8, 16, 0), MoveScalar(12, 17, 0),
            Sop1(16, "SSwappcB64", 14, Gen5Operand.Scalar(14)),
            MoveScalar(20, 14, 0x2000), MoveScalar(24, 15, 0),
            Sop1(28, "SSwappcB64", 14, Gen5Operand.Scalar(14)),
            GlobalAccess(32, "GlobalStoreDword", 20, vectorAddress: 5), EndProgram(40));
        var body = Program(
            BufferAccess(0, "BufferLoadDword", 4, vectorData: 6, offsetEnabled: true, vectorAddress: 5),
            BufferAccess(8, "BufferStoreDword", 4, vectorData: 6, offsetEnabled: true, vectorAddress: 5),
            Sop1(16, "SSetpcB64", 0, Gen5Operand.Scalar(14)));
        ShaderCallTarget[] Records() => [new(0x2000, 0, body), new(0x2000, 1, body)];
        var linked = Gen5ShaderCallLinker.Link(caller,
            [new(16, 14, 16, 14, 20, Records()), new(28, 14, 16, 14, 32, Records())]);
        var code = Compile(Request(linked));
        var instructions = Instructions(code).ToArray();
        var inspected = new SpirvModuleInspector(code);
        var main = Assert.Single(inspected.Names, p => p.Value == "main").Key;
        var pageTable = Assert.Single(inspected.Names, p => p.Value == "deviceAddressPageTable").Key;

        // One device store is main's; the other is the body's, in a DontInline function that main calls from both sites.
        const uint dontInline = 2;
        var devicePointers = instructions.Where(i => i.Op == SpirvOp.ConvertUToPtr).Select(i => i.Words[1]).ToHashSet();
        var deviceStores = instructions.Where(i => i.Op == SpirvOp.Store && devicePointers.Contains(i.Words[0])).ToArray();
        Assert.Contains(deviceStores, i => i.Function == main);
        var outlined = Assert.Single(deviceStores, i => i.Function != main).Function;
        Assert.Contains(instructions, i => i.Op == SpirvOp.Function && i.Words[1] == outlined && i.Words[2] == dontInline);
        Assert.Equal(2, instructions.Count(i => i.Function == main && i.Op == SpirvOp.FunctionCall && i.Words[2] == outlined));
        foreach (var function in new[] { main, outlined })
            Assert.Contains(instructions, i => i.Function == function && i.Op == SpirvOp.ArrayLength && i.Words[2] == pageTable);

        // An id defined in a function is used only in that function.
        var types = instructions.Where(i => (uint)i.Op is >= 19 and <= 39).Select(i => i.Words[0]).ToHashSet();
        var owners = instructions.Where(i => i.Function != 0 && i.Op != SpirvOp.Function && i.Words.Length >= 2 &&
            types.Contains(i.Words[0])).ToDictionary(i => i.Words[1], i => i.Function);
        foreach (var instruction in instructions.Where(i => i.Function != 0))
            foreach (var operand in IdOperands(instruction))
                if (owners.TryGetValue(operand, out var owner))
                    Assert.Equal(instruction.Function, owner);
    }

    [Fact]
    public void UndeclaredCompareExchangeLoopRequestsInheritanceFallbackAfterAnEarlyRead()
    {
        var inherited = CompareExchangeLoop(inherit: true);
        Assert.False(inherited.InheritedBlocksHeld);
        var fallback = CompareExchangeLoop(inherit: false);
        Assert.True(fallback.InheritedBlocksHeld);
        Assert.Contains(Instructions(fallback.Build()), i => i.Op == SpirvOp.AtomicCompareExchange);
    }

    private static SpirvModuleBuilder CompareExchangeLoop(bool inherit)
    {
        var m = new SpirvModuleBuilder { InheritBlockState = inherit };
        m.AddCapability(SpirvCapability.Shader);
        var u = m.TypeInt(32, false);
        var register = Variable(m, u, SpirvStorageClass.Private);
        var atomic = Variable(m, u, SpirvStorageClass.Workgroup);
        var function = m.BeginFunction(m.TypeVoid(), m.TypeFunction(m.TypeVoid()));
        m.AddEntryPoint(SpirvExecutionModel.GLCompute, function, "main", [register, atomic]);
        m.AddExecutionMode(function, SpirvExecutionMode.LocalSize, 1, 1, 1);
        m.AddLabel();
        var zero = m.Constant(u, 0);
        var one = m.Constant(u, 1);
        var header = m.AllocateId();
        var again = m.AllocateId();
        var done = m.AllocateId();
        m.AddStatement(SpirvOp.Store, register, one);
        m.AddStatement(SpirvOp.Branch, header);
        m.AddLabel(header); // Deliberately no DeclareLoopHeader: exercise the conservative retry guard.
        var value = m.AddInstruction(SpirvOp.Load, u, register);
        var previous = m.AddInstruction(SpirvOp.AtomicCompareExchange, u,
            atomic, m.Constant(u, 2), zero, zero, value, zero);
        var success = m.AddInstruction(SpirvOp.IEqual, m.TypeBool(), previous, zero);
        m.AddStatement(SpirvOp.LoopMerge, done, again, 0);
        m.AddStatement(SpirvOp.BranchConditional, success, done, again);
        m.AddLabel(again);
        m.AddStatement(SpirvOp.Store, register, m.Constant(u, 2));
        m.AddStatement(SpirvOp.Branch, header);
        m.AddLabel(done);
        End(m);
        return m;
    }

    private static byte[] Compile(ShaderCompileRequest request)
    {
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        return shader.Spirv;
    }

    private static uint Variable(SpirvModuleBuilder m, uint type, SpirvStorageClass storage) =>
        m.AddGlobalVariable(m.TypePointer(storage, type), storage);

    private static uint Output(SpirvModuleBuilder m, uint type)
    {
        var output = Variable(m, type, SpirvStorageClass.Output);
        m.AddDecoration(output, SpirvDecoration.Location, 0);
        return output;
    }

    private static (uint Function, uint Entry) Vertex(SpirvModuleBuilder m, params uint[] interfaces)
    {
        var function = m.BeginFunction(m.TypeVoid(), m.TypeFunction(m.TypeVoid()));
        m.AddEntryPoint(SpirvExecutionModel.Vertex, function, "main", interfaces);
        return (function, m.AddLabel());
    }

    private static void End(SpirvModuleBuilder m)
    {
        m.AddStatement(SpirvOp.Return);
        m.EndFunction();
    }

    private sealed record Instruction(SpirvOp Op, uint[] Words, int Offset, uint Function, uint Block);

    // The operands of a function's instruction that name ids; memory operands, extracted indices, an extended instruction's
    // number and merge controls are literals.
    private static IEnumerable<uint> IdOperands(Instruction instruction) => instruction.Op switch
    {
        SpirvOp.Load or SpirvOp.ArrayLength or SpirvOp.CompositeExtract => instruction.Words.Skip(2).Take(1),
        SpirvOp.Store => instruction.Words.Take(2),
        SpirvOp.CompositeInsert or SpirvOp.VectorShuffle => instruction.Words.Skip(2).Take(2),
        SpirvOp.ExtInst => instruction.Words.Skip(2).Take(1).Concat(instruction.Words.Skip(4)),
        SpirvOp.BranchConditional or SpirvOp.Switch or SpirvOp.ReturnValue => instruction.Words.Take(1),
        SpirvOp.Function or SpirvOp.Variable or SpirvOp.Label or SpirvOp.Branch or SpirvOp.SelectionMerge or SpirvOp.LoopMerge => [],
        _ => instruction.Words.Skip(2),
    };

    private static IEnumerable<Instruction> Instructions(byte[] code)
    {
        var words = new uint[code.Length / sizeof(uint)];
        Buffer.BlockCopy(code, 0, words, 0, code.Length);
        uint function = 0, block = 0;
        for (var offset = 5; offset < words.Length;)
        {
            var length = checked((int)(words[offset] >> 16));
            Assert.True(length > 0 && offset + length <= words.Length);
            var op = (SpirvOp)(words[offset] & 0xffff);
            var operands = words.AsSpan(offset + 1, length - 1).ToArray();
            if (op == SpirvOp.Function) function = operands[1];
            if (op == SpirvOp.Label) block = operands[0];
            yield return new Instruction(op, operands, offset, function, block);
            if (op == SpirvOp.FunctionEnd) { function = 0; block = 0; }
            offset += length;
        }
    }
}
