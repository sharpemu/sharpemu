// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Ir;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5ShaderCallLinkerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LinkedFunctionBuffers_KeepDescriptorsOnTheDevice(bool scalar)
    {
        var load = scalar ? ScalarBufferLoad(0, 0, 8) : BufferLoad(0, 0);
        var linked = Link(Program(load, Sop1(8, "SSetpcB64", 0, Gen5Operand.Scalar(14))));
        var plan = ShaderResourcePlan.Extract(linked, ShaderStage.Compute, Hash, 0, 4);
        Assert.Single(plan.Memory.Entries);
        Assert.All(plan.Memory.Entries, memory => Assert.True(memory.DeviceDescriptor));
        Assert.Empty(plan.Info.Buffers);
        Assert.True(plan.Info.UsesDeviceAddresses);
    }

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
        // The plan reads each record's argument from the user-data registers the dispatch fills with the tables' current
        // arguments, so a table rebuilt elsewhere keeps the plan; the return address stays a constant.
        Assert.Equal(targets.Select(target => target.Argument), Gen5ShaderCallLinker.CallEntryArguments(linked));
        Assert.Equal(4u, graph.CallArgumentRegisters);
        var argument = graph.Accesses[argumentIndex]!.Handle!;
        Assert.All(argument.Operands, value => Assert.Equal(ScalarValueKind.UserData, value.Kind));
        Assert.Equal([ScalarValueGraph.CallArgumentRegisterBase, ScalarValueGraph.CallArgumentRegisterBase + 1],
            argument.Operands.Select(value => value.UserDataRegister));
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
    [InlineData("SRfeB64")]
    [InlineData("SMovreldB32")]
    public void Link_RejectsUnsupportedFunctionControl(string opcode)
    {
        var body = Program(Sop1(0, opcode, 0, Gen5Operand.Scalar(0)), Sop1(4, "SSetpcB64", 0, Gen5Operand.Scalar(14)));
        Assert.Throws<InvalidOperationException>(() => Link(body));
    }

    // A function that reads its program address gets its own: the dispatch's shader base is the caller's address, and
    // the copied S_GETPC_B64 keeps the function's distance from it, also for a function placed below the caller.
    [Theory]
    [InlineData(0x8000ul)]
    [InlineData(0x20000ul)]
    public void Link_KeepsTheFunctionAddressForProgramCounterReads(ulong function)
    {
        const ulong callerAddress = 0x10000;
        var caller = Program(Sop1(0, "SSwappcB64", 14, Gen5Operand.Scalar(14)), EndProgram(4)) with { Address = callerAddress };
        var body = Program(Nop(0), Sop1(4, "SGetpcB64", 0, Gen5Operand.Scalar(0)), Sop1(8, "SSetpcB64", 0, Gen5Operand.Scalar(14)));
        var linked = Gen5ShaderCallLinker.Link(caller, [new(0, 14, 16, 14, callerAddress + 4, [new ShaderCallTarget(function, 0x3000, body)])]);
        var read = Assert.Single(linked.Instructions, instruction => instruction.Opcode == "SGetpcB64");
        Assert.Equal(function + 4, unchecked(callerAddress + read.ProgramOffset));
        Assert.Equal(new ShaderFunctionPcControl(function, 4), read.Control);
        Assert.True(BindingLayout.ReadsShaderBase(linked));
    }

    // The host pushes the callees in the order the module tests them: each once, by site and then by record.
    [Fact]
    public void CallTargets_FollowTheSitesAndRecords()
    {
        var caller = Program(Sop1(0, "SSwappcB64", 14, Gen5Operand.Scalar(14)), Sop1(4, "SSwappcB64", 14, Gen5Operand.Scalar(14)),
            EndProgram(8));
        var body = Program(Sop1(0, "SSetpcB64", 0, Gen5Operand.Scalar(14)));
        ShaderCallSite[] calls =
        [
            new(4, 14, 16, 14, 8, [new(0x5000, 1, body), new(0x2000, 2, body)]),
            new(0, 14, 16, 14, 4, [new(0x3000, 1, body), new(0x2000, 3, body), new(0x3000, 4, body)]),
        ];
        var linked = Gen5ShaderCallLinker.Link(caller, calls);
        Assert.Equal([0x3000ul, 0x2000, 0x5000], Gen5ShaderCallLinker.CallTargets(linked));
        Assert.Equal(Gen5ShaderCallLinker.CallTargets(linked), Gen5ShaderCallLinker.CallTargets(calls));
        Assert.Equal(3u, BindingLayout.CountCallTargets(linked));
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

    // A site whose table holds only null records has no target: the call links to its fault.
    [Fact]
    public void Link_SendsACallWithoutTargetsToItsFault()
    {
        var caller = Program(Sop1(0, "SSwappcB64", 14, Gen5Operand.Scalar(14)), EndProgram(4));
        var linked = Gen5ShaderCallLinker.Link(caller, [new(0, 14, 16, 14, 4, [])]);
        Assert.DoesNotContain(linked.Instructions, instruction => instruction.Opcode == "SSwappcB64");
        Assert.Equal(0u, Assert.Single(linked.Instructions, instruction => instruction.Control is ShaderCallFaultControl).Pc);
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
