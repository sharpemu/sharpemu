// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

public sealed class DynamicUserDataPlanTests
{
    [Fact]
    public void AStartInstanceRegisterIsNotAHostKnownInput()
    {
        var program = ResourceTestProgram.Program(
            ResourceTestProgram.Sop2(0, "SLshlB32", 12, Gen5Operand.Scalar(10), ResourceTestProgram.Operand(6)),
            ResourceTestProgram.Sop2(4, "SAddU32", 8, Gen5Operand.Scalar(8), Gen5Operand.Scalar(12)),
            ResourceTestProgram.Sop2(8, "SAddcU32", 9, Gen5Operand.Scalar(9), ResourceTestProgram.Operand(0)),
            ResourceTestProgram.ScalarLoad(12, baseRegister: 8, destination: 16, count: 4),
            ResourceTestProgram.MoveVectorFromScalar(20, 0, 16),
            ResourceTestProgram.EndProgram(24));

        var known = ShaderResourcePlan.Extract(program, ShaderStage.Vertex, ResourceTestProgram.Hash, 8, 4, waveSize: 32);
        var dynamic = ShaderResourcePlan.Extract(program, ShaderStage.Vertex, ResourceTestProgram.Hash, 8, 4, waveSize: 32, dynamicUserData: 2);

        Assert.Equal(4, known.TableReads.Count);
        Assert.Contains(known.Graph.Values, value => value.Kind == ScalarValueKind.UserData && value.UserDataRegister == 10);

        Assert.Equal(2, dynamic.Graph.DynamicUserData);
        Assert.Empty(dynamic.TableReads);
        Assert.False(Assert.Single(dynamic.DeviceAddressRanges).Plannable);
        Assert.DoesNotContain(dynamic.Graph.Values, value => value.Kind == ScalarValueKind.UserData && value.UserDataRegister == 10);
    }
}
