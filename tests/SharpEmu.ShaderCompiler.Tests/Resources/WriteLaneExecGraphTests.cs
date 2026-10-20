// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

// An ordinary vector write changes only the lanes EXEC enables; a lane that a known EXEC disables
// keeps the value v_writelane gave it, so v_readlane can still resolve it.
public sealed class WriteLaneExecGraphTests
{
    private const uint Exec = 126;

    private static Gen5ShaderInstruction ReadLane(uint pc, uint scalarDestination, uint vectorSource, uint lane) =>
        new(pc, Gen5ShaderEncoding.Vop3, "VReadlaneB32", [0u, 0u],
            [Gen5Operand.Vector(vectorSource), Operand(lane)], [Gen5Operand.Scalar(scalarDestination)],
            new Gen5Vop3Control(0, 0, 0, false, 0, null));

    private static ShaderResourcePlan Plan(uint execMask, uint readLane, bool dynamicWriteLane = false)
    {
        var writeLane = dynamicWriteLane
            ? Vop3(4, "VWritelaneB32", 0, Gen5Operand.Scalar(8), Gen5Operand.Scalar(3))
            : Vop3(4, "VWritelaneB32", 0, Gen5Operand.Scalar(8), Operand(3));
        return Extract(Program(
            MoveScalar(0, 8, 0xCAFE),
            writeLane,
            MoveScalar(12, Exec, execMask),
            MoveScalar(16, Exec + 1, 0),
            Vop1(20, "VMovB32", 0, Operand(5)),
            ReadLane(24, 20, 0, readLane),
            MoveScalar(32, 21, 0),
            MoveScalar(36, 22, 16),
            MoveScalar(40, 23, 0),
            BufferLoad(44, 20),
            EndProgram(52)));
    }

    private static bool TryEvaluate(ShaderResourcePlan plan, out uint dword0)
    {
        dword0 = 0;
        if (plan.Info.Buffers.Count == 0 ||
            !RuntimeValueEvaluator.EvaluateDescriptorSource(plan, plan.Info.Buffers[0].Source, Inputs(new uint[16]), out var result))
        {
            return false;
        }

        dword0 = result.Dwords[0];
        return true;
    }

    [Fact]
    public void ALaneThatExecDisables_KeepsItsValueAcrossAnOrdinaryWrite()
    {
        // EXEC = lane 0 only: the v_mov never touches lane 3.
        Assert.True(TryEvaluate(Plan(execMask: 1, readLane: 3), out var value));
        Assert.Equal(0xCAFEu, value);
    }

    [Fact]
    public void ALaneThatExecEnables_IsOverwrittenByAnOrdinaryWrite()
    {
        var failure = Assert.Throws<ResourcePlanException>(() => Plan(execMask: 1u << 3, readLane: 3));
        Assert.Contains("VReadlaneB32", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WritelaneWithoutAKnownLane_ForgetsEveryLane()
    {
        // The lane index is a runtime value, so no lane of v0 can be assumed afterwards.
        var failure = Assert.Throws<ResourcePlanException>(() => Plan(execMask: 1, readLane: 3, dynamicWriteLane: true));
        Assert.Contains("VReadlaneB32", failure.Message, StringComparison.Ordinal);
    }
}
