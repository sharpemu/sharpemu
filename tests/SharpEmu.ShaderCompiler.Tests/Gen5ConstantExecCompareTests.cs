// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5ConstantExecCompareTests
{
    // The 32-bit forms of V_CMPX_F and V_CMPX_T were the only constant compares the translators rejected.
    [Theory]
    [InlineData("VCmpxFI32")]
    [InlineData("VCmpxFU32")]
    [InlineData("VCmpxTI32")]
    [InlineData("VCmpxTU32")]
    public void Compile(string opcode)
    {
        var (plan, resources, layout) = Prepare(Program(Vopc(0, opcode, Operand(0), 0), EndProgram(8)));
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 1, ThreadCountX = 1 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out _, out var spirvError), spirvError);
    }
}
