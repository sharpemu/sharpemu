// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

public sealed class ScalarValueEquivalenceTests
{
    [Fact]
    public void UndefinedValueDoesNotEqualItself()
    {
        var value = ScalarValue.Undefined(ScalarValueType.U32);
        Assert.False(ScalarValueEquivalence.Equivalent(new(), value, value));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedSubgraphsDoNotHideDifferentOperands(bool different)
    {
        ScalarValue Build(uint leaf)
        {
            var shared = ScalarValue.UserData(0);
            for (var depth = 0; depth < 24; depth++)
                shared = ScalarValue.MakeOperation(ScalarOperation.IAdd32, ScalarValueType.U32, shared, shared);
            return ScalarValue.MakeOperation(ScalarOperation.IAdd32, ScalarValueType.U32, shared, ScalarValue.ConstantOf(leaf));
        }
        Assert.Equal(!different, ScalarValueEquivalence.Equivalent(new(), Build(1), Build(different ? 2u : 1u)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CyclicPhisPreservePredecessorChecks(bool differentPredecessor)
    {
        var left = ScalarValue.Phi(3, ScalarValueType.U32);
        var right = ScalarValue.Phi(3, ScalarValueType.U32);
        left.SetPhiOperands([0, 1, 3], [ScalarValue.ConstantOf(1u), ScalarValue.ConstantOf(2u), left]);
        right.SetPhiOperands([0, differentPredecessor ? 2 : 1, 3],
            [ScalarValue.ConstantOf(1u), ScalarValue.ConstantOf(2u), right]);
        Assert.Equal(!differentPredecessor, ScalarValueEquivalence.Equivalent(new(), left, right));
    }

    [Fact]
    public void InvariantPhiStillEqualsItsInput()
    {
        var phi = ScalarValue.Phi(1, ScalarValueType.U32);
        phi.SetPhiOperands([0, 1], [ScalarValue.UserData(4), phi]);
        Assert.True(ScalarValueEquivalence.Equivalent(new(), phi, ScalarValue.UserData(4)));
    }

    [Theory]
    [InlineData(0u, true)]
    [InlineData(4u, false)]
    public void MemoryReadsCompareAccessMetadata(uint offset, bool equivalent)
    {
        var memory = new MemoryAccessTable();
        var first = memory.Add(new() { Pc = 0, Kind = MemoryResourceKind.ScalarAddress, Access = MemoryAccess.Read });
        var second = memory.Add(new() { Pc = 4, Kind = MemoryResourceKind.ScalarAddress, Access = MemoryAccess.Read, Offset = offset });
        ScalarValue Read(int index) => ScalarValue.MemoryRead(ScalarValueKind.ScalarAddressWord,
            ScalarValue.UserData(0), ScalarValue.ConstantOf(0u), index);
        Assert.Equal(equivalent, ScalarValueEquivalence.Equivalent(memory, Read(first), Read(second)));
    }
}
