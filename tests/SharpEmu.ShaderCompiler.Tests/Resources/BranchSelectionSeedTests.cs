// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;
using Mode = SharpEmu.ShaderCompiler.Resources.RuntimeValueEvaluator.CompiledBranchMode;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

// The branch walk successfully reads a guest word that a later CLEAN table slot also needs. The compiled path may
// not read it again.
public sealed class BranchSelectionSeedTests
{
    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    public void SuccessfulCleanPredicateSeedsTheLaterCleanTable(uint condition)
    {
        var plan = Extract(Program(ScalarLoad(0, 12, 8),
            Sopc(8, "SCmpEqU32", Gen5Operand.Scalar(8), Operand(1)),
            Branch(12, "SCbranchScc1", 3),
            BufferLoad(16, 0), Branch(24, "SBranch", 2),
            BufferLoad(28, 4), EndProgram(36)), userDataCount: 14);
        Assert.NotEmpty(plan.ResourceBranches);
        Assert.NotEmpty(plan.TableReads);
        Array.Fill((byte[])plan.CleanFlatSlots, (byte)1);
        plan.CompileEvaluatorNow();
        Assert.IsType<CompiledBranchTransaction>(plan.CompiledBranches);

        uint[] userData = [0x1000, 0, 256, 0, 0x2000, 0, 256, 0, 0, 0, 0, 0, 0x3000, 0];
        uint[]? expectedTable = null;
        foreach (var mode in new[] { Mode.Off, Mode.On })
        {
            var cleanReads = 0;
            bool ReadClean(ulong address, out uint word)
            {
                Assert.Equal(0x3000ul, address);
                Assert.Equal(1, ++cleanReads);
                word = condition;
                return true;
            }
            bool ReadGeneral(ulong address, out uint word)
            {
                word = 0;
                throw new System.InvalidOperationException("The predicate and table use the clean reader in this fixture.");
            }
            var snapshot = new ResourceSnapshot();
            var specialization = new ResourceSpecialization();
            var served = RuntimeValueEvaluator.BranchesServedOnThread;
            var previousMode = RuntimeValueEvaluator.BranchModeOverride;
            RuntimeValueEvaluator.BranchModeOverride = mode;
            try
            {
                Assert.True(ResourceMaterializer.Materialize(plan, Inputs(userData, ReadGeneral, ReadClean),
                    ref snapshot, ref specialization));
            }
            finally
            {
                RuntimeValueEvaluator.BranchModeOverride = previousMode;
            }
            Assert.Equal(1, cleanReads);
            Assert.Equal(condition == 0 ? 0x1000u : 0u, snapshot.Buffers[0][0]);
            Assert.Equal(condition == 1 ? 0x2000u : 0u, snapshot.Buffers[1][0]);
            if (expectedTable is null) expectedTable = [.. snapshot.FlattenedResourceTable];
            else Assert.Equal(expectedTable, snapshot.FlattenedResourceTable);
            if (mode == Mode.On) Assert.Equal(served + 1, RuntimeValueEvaluator.BranchesServedOnThread);
        }
    }
}
