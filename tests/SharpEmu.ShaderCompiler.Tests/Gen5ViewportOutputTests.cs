// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5ViewportOutputTests
{
    [Theory]
    [InlineData((1u << 19) | (1u << 21), 13u, 1u << 2, true)]
    [InlineData(1u << 21, 13u, 1u << 2, false)]
    [InlineData((1u << 19) | (1u << 21), 13u, 1u << 1, false)]
    [InlineData((1u << 19) | (1u << 21), 14u, 1u << 2, false)]
    public void ProgramReportsOnlyTranslatedViewportIndexExports(
        uint positionExportControl,
        uint target,
        uint enableMask,
        bool expected)
    {
        var export = new Gen5ShaderInstruction(
            0,
            Gen5ShaderEncoding.Exp,
            "Exp",
            [],
            [
                Gen5Operand.Vector(0),
                Gen5Operand.Vector(1),
                Gen5Operand.Vector(2),
                Gen5Operand.Vector(3),
            ],
            [],
            new Gen5ExportControl(target, enableMask, false, true, true));
        var program = new Gen5ShaderProgram(0x1000, [export]);

        Assert.Equal(expected, program.WritesViewportIndex(positionExportControl));
    }
}
