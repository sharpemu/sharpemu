// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

public sealed class VectorCompareResourceTests
{
    [Fact]
    public void ExecutionComparePreservesResourcePointerWithUnknownVectorInputs()
    {
        var program = Program(
            ScalarLoad(0, 0, 106, count: 2),
            Vopc(8, "VCmpxLtU32", Gen5Operand.Vector(0), 1),
            Branch(12, "SCbranchExecz", 4),
            ScalarLoad(16, 106, 8, count: 4),
            BufferLoad(24, 8),
            EndProgram(32));
        var plan = Extract(program, userDataCount: 2);
        var memory = new TestWordMemory
        {
            Base = 0x2_00001000,
            Words = [0x1010, 2, 0, 0, 0x3000, 0, 256, 0],
        };
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();

        Assert.True(ResourceMaterializer.Materialize(plan, Inputs([0x1000, 2], memory.Read),
            ref snapshot, ref specialization, out var failure));
        Assert.Equal(ResourceMaterializationFailure.None, failure);
        Assert.Equal(new uint[] { 0x3000, 0, 256, 0 }, Assert.Single(snapshot.Buffers));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CompareUpdatesOnlyItsDestinationMask(bool updatesExecutionMask, bool comparisonPasses)
    {
        var program = Program(
            MoveScalar(0, 106, 0x12345678),
            MoveScalar(4, 107, 0x87654321),
            MoveVector(8, 0, comparisonPasses ? 2u : 0u),
            Vopc(12, updatesExecutionMask ? "VCmpxLtU32" : "VCmpLtU32", Operand(1), 0),
            MoveScalarRegister(16, 8, 106),
            MoveScalarRegister(20, 9, 107),
            MoveScalarRegister(24, 10, 126),
            MoveScalarRegister(28, 11, 127),
            BufferLoad(32, 8),
            EndProgram(40));
        var plan = Extract(program, userDataCount: 0);

        Assert.True(RuntimeValueEvaluator.EvaluateDescriptorSource(plan,
            Assert.Single(plan.Info.Buffers).Source, Inputs([]), out var descriptor));
        var comparisonMask = comparisonPasses ? 1u : 0u;
        Assert.Equal(new uint[]
        {
            updatesExecutionMask ? 0x12345678u : comparisonMask,
            updatesExecutionMask ? 0x87654321u : 0u,
            updatesExecutionMask ? comparisonMask : uint.MaxValue,
            0,
        }, descriptor.Dwords);
    }
}
