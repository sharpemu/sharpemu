// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcShaderCacheTests
{
    [Fact]
    public void ShaderStructureFingerprint_IncludesSortedReachableProgramCounters()
    {
        var first = CreateEvaluation([8, 0, 4]);
        var reordered = CreateEvaluation([4, 8, 0]);
        var different = CreateEvaluation([0, 4, 12]);

        Assert.Equal(
            AgcExports.ComputeShaderStructuralFingerprint(first),
            AgcExports.ComputeShaderStructuralFingerprint(reordered));
        Assert.NotEqual(
            AgcExports.ComputeShaderStructuralFingerprint(first),
            AgcExports.ComputeShaderStructuralFingerprint(different));
    }

    private static Gen5ShaderEvaluation CreateEvaluation(IEnumerable<uint> reachablePcs)
    {
        var scalarRegistersByPc = new Dictionary<uint, IReadOnlyList<uint>>();
        foreach (var pc in reachablePcs)
        {
            scalarRegistersByPc.Add(pc, Array.Empty<uint>());
        }

        return new Gen5ShaderEvaluation(
            InitialScalarRegisters: Array.Empty<uint>(),
            ScalarRegisters: Array.Empty<uint>(),
            ImageBindings: Array.Empty<Gen5ImageBinding>(),
            GlobalMemoryBindings: Array.Empty<Gen5GlobalMemoryBinding>(),
            ScalarRegistersByPc: scalarRegistersByPc);
    }
}
