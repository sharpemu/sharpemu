// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Ir;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5ShaderCallLinkerTests
{
    [Fact]
    public void Link_PreservesGuestValuesAndResolvesEachReturn()
    {
        var caller = Program(Sop1(0, "SSwappcB64", 14, Gen5Operand.Scalar(14)), EndProgram(4));
        var body = Program(ScalarLoad(0, 16, 0), ScalarLoad(8, 14, 2),
            ScalarBufferLoad(16, 4, 8), Sop1(24, "SSetpcB64", 0, Gen5Operand.Scalar(14)));
        const ulong returnAddress = 0x1234567800000004;
        var targets = new[] { new ShaderCallTarget(0x2000, 0xABCDEF0100003000, body),
            new ShaderCallTarget(0x2000, 0xABCDEF0100004000, body) };
        var linked = Gen5ShaderCallLinker.Link(caller, [new(0, 14, 16, 14, returnAddress, targets)]);
        Assert.DoesNotContain(linked.Instructions, instruction => instruction.Opcode is "SSwappcB64" or "SSetpcB64");
        var matches = linked.Instructions.Where(instruction => instruction.Control is ShaderCallMatchControl).ToArray();
        Assert.Equal(2, matches.Length);
        Assert.All(matches, instruction => Assert.True(Gen5IrBranchResolver.Instance.IsConditional(instruction)));
        Assert.Equal(matches[1].Pc, ((ShaderCallMatchControl)matches[0].Control!).TargetPc);
        var fault = Assert.Single(linked.Instructions, instruction => instruction.Control is ShaderCallFaultControl);
        Assert.Equal(fault.Pc, ((ShaderCallMatchControl)matches[1].Control!).TargetPc);
        Assert.All(linked.Instructions.Where(instruction => instruction.Control is ShaderLinkedBranchControl),
            instruction => Assert.Equal(linked.Instructions[^1].Pc, ((ShaderLinkedBranchControl)instruction.Control!).TargetPc));
        var entries = linked.Instructions.Select(instruction => instruction.Control).OfType<ShaderCallEntryControl>().ToArray();
        Assert.Equal(targets.Select(target => target.Argument), entries.Select(entry => entry.Argument));
        Assert.All(entries, entry => Assert.Equal(returnAddress, entry.ReturnAddress));
        var graph = ScalarValueGraph.Build(linked, 0, 16);
        Assert.True(graph.Memory.TryGetIndex(matches[0].Pc + 8, 0, out var argumentIndex));
        Assert.True(graph.Memory.TryGetIndex(matches[0].Pc + 16, 0, out var returnIndex));
        AssertPair(targets[0].Argument, graph.Accesses[argumentIndex]!.Handle!);
        AssertPair(returnAddress, graph.Accesses[returnIndex]!.Handle!);
        Assert.Equal(2, linked.FunctionBufferAccesses.Count);
        Assert.All(linked.FunctionBufferAccesses, pc => Assert.Equal("SBufferLoadDword", linked.Instructions.Single(instruction => instruction.Pc == pc).Opcode));
    }

    [Fact]
    public void Link_ResolvesTargetsBeyondTheGuestBranchRange()
    {
        var caller = Program(Sop1(0, "SSwappcB64", 14, Gen5Operand.Scalar(14)), EndProgram(4));
        var body = Program(Enumerable.Range(0, 520).Select(index => Nop((uint)index * 4))
            .Append(Sop1(2080, "SSetpcB64", 0, Gen5Operand.Scalar(14))).ToArray());
        var targets = Enumerable.Range(0, 65).Select(index => new ShaderCallTarget(0x2000, (ulong)index, body)).ToArray();
        var linked = Gen5ShaderCallLinker.Link(caller, [new(0, 14, 16, 14, 4, targets)]);
        var firstReturn = linked.Instructions.First(instruction => instruction.Control is ShaderLinkedBranchControl);
        Assert.True(Gen5IrBranchResolver.Instance.TryGetBranchTarget(firstReturn, out var target));
        Assert.Equal(linked.Instructions[^1].Pc, target);
        Assert.True(target - firstReturn.Pc > short.MaxValue * 4u);
    }

    [Theory]
    [InlineData("SSwappcB64")]
    [InlineData("SCallB64")]
    [InlineData("SGetpcB64")]
    [InlineData("SRfeB64")]
    [InlineData("SMovreldB32")]
    public void Link_RejectsUnsupportedFunctionControl(string opcode)
    {
        var body = Program(Sop1(0, opcode, 0, Gen5Operand.Scalar(0)), Sop1(4, "SSetpcB64", 0, Gen5Operand.Scalar(14)));
        Assert.Throws<InvalidOperationException>(() => Link(body));
    }

    [Fact]
    public void Link_RejectsChangedOrUnresolvedReturnAddresses()
    {
        Assert.Throws<InvalidOperationException>(() => Link(Program(MoveScalar(0, 15, 0), Sop1(4, "SSetpcB64", 0, Gen5Operand.Scalar(14)))));
        Assert.Throws<InvalidOperationException>(() => Link(Program(Sop1(0, "SSetpcB64", 0, Gen5Operand.Scalar(12)))));
        Assert.Throws<InvalidOperationException>(() => Link(Program(Nop(0))));
        Assert.Throws<InvalidOperationException>(() => Link(Program(Branch(0, "SBranch", 100), Sop1(4, "SSetpcB64", 0, Gen5Operand.Scalar(14)))));
    }

    [Fact]
    public void Link_RejectsMissingTargetsAndOverlappingArgumentRegisters()
    {
        var caller = Program(Sop1(0, "SSwappcB64", 14, Gen5Operand.Scalar(14)), EndProgram(4));
        Assert.Throws<InvalidOperationException>(() => Gen5ShaderCallLinker.Link(caller, []));
        var target = new ShaderCallTarget(0x2000, 0, Program(Sop1(0, "SSetpcB64", 0, Gen5Operand.Scalar(14))));
        Assert.Throws<InvalidOperationException>(() => Gen5ShaderCallLinker.Link(caller, [new(0, 14, 15, 14, 4, [target])]));
        Assert.Throws<InvalidOperationException>(() => Gen5ShaderCallLinker.Link(caller with { IsFusedProgram = true }, [new(0, 14, 16, 14, 4, [target])]));
    }

    private static Gen5ShaderProgram Link(Gen5ShaderProgram body)
    {
        var caller = Program(Sop1(0, "SSwappcB64", 14, Gen5Operand.Scalar(14)), EndProgram(4));
        return Gen5ShaderCallLinker.Link(caller, [new(0, 14, 16, 14, 4, [new ShaderCallTarget(0x2000, 0x3000, body)])]);
    }

    private static void AssertPair(ulong expected, ScalarValue handle)
    {
        Assert.All(handle.Operands, value => Assert.True(value.IsConstant));
        Assert.Equal((uint)expected, handle.Operands[0].ConstantU32);
        Assert.Equal((uint)(expected >> 32), handle.Operands[1].ConstantU32);
    }
}
