// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

public sealed class ScalarGraphProcessingOrderTests
{
    [Theory]
    [InlineData(128)]
    [InlineData(256)]
    public void SharedJoinRetainsAllIncomingValues(int pathCount)
    {
        var instructions = new List<Gen5ShaderInstruction>();
        var joinAddress = checked((uint)pathCount * 8);
        for (var pathIndex = 0; pathIndex < pathCount; pathIndex++)
        {
            var address = checked((uint)pathIndex * 8);
            instructions.Add(MoveScalar(address, 20, checked(1000u + (uint)pathIndex)));
            instructions.Add(Branch(address + 4, "SCbranchScc0", checked((short)((joinAddress - address - 8) / 4))));
        }
        instructions.Add(BufferLoad(joinAddress, 8));
        instructions.Add(EndProgram(joinAddress + 8));

        var plan = Extract(Program(instructions.ToArray()), userDataCount: 16);
        var graph = plan.Graph;
        var joinBlock = graph.ControlFlow.BlockByStartPc[joinAddress];
        Assert.Equal(pathCount, graph.ControlFlow.Predecessors[joinBlock].Count);
        var merged = Assert.Single(graph.Values, value => value.Kind == ScalarValueKind.Phi &&
            value.PhiPredecessors.Length == pathCount && value.Operands.All(operand => operand.IsConstant));
        Assert.Equal(Enumerable.Range(1000, pathCount).Select(value => (ulong)value),
            merged.Operands.Select(value => value.ConstantU64));
        Assert.Single(plan.Info.Buffers);
    }

    [Fact]
    public void BackwardAcyclicEdgeUsesDependencyOrder()
    {
        var program = Program(
            Branch(0, "SBranch", 3),
            MoveScalarRegister(4, 8, 20),
            Branch(8, "SBranch", 3),
            MoveScalar(12, 8, 0xDEAD),
            MoveScalar(16, 20, 0x1000),
            Branch(20, "SBranch", -5),
            MoveScalar(24, 9, 0),
            MoveScalar(28, 10, 16),
            MoveScalar(32, 11, 0),
            BufferLoad(36, 8),
            EndProgram(44));

        var plan = Extract(program, userDataCount: 0);
        Assert.True(RuntimeValueEvaluator.EvaluateDescriptorSource(
            plan, Assert.Single(plan.Info.Buffers).Source, Inputs([]), out var descriptor));
        Assert.Equal(0x1000u, descriptor.Dwords[0]);
    }
}
