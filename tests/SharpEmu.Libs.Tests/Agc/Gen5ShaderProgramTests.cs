// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class Gen5ShaderProgramTests
{
    [Fact]
    public void PixelColorExportMasksPackAllColorTargets()
    {
        var program = new Gen5ShaderProgram(
            0,
            [
                Export(0, 0x1),
                Export(0, 0x4),
                Export(1, 0x2),
                Export(2, 0x3),
                Export(3, 0x4),
                Export(4, 0x5),
                Export(5, 0x6),
                Export(6, 0x7),
                Export(7, 0x8),
                Export(8, 0xF),
            ]);

        Assert.Equal(0x8765_4325u, program.PixelColorExportMasks);
        Assert.Equal(0x8765_4325u, program.PixelColorExportMasks);
    }

    private static Gen5ShaderInstruction Export(uint target, uint enableMask) => new(
        0,
        Gen5ShaderEncoding.Exp,
        "exp",
        [],
        [],
        [],
        new Gen5ExportControl(target, enableMask, false, false, false));
}
