// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Rendering;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Rendering;

// A color slot counts as written when the export that SPI_SHADER_COL_FORMAT and CB_SHADER_MASK route to it is written.
public sealed class UnwrittenColorTargetTests
{
    // Two exports (MRT0, MRT1) compacted to targets 0 and 7, as Ghost of Yotei's visibility pass binds its
    // instance ids (R16) and triangle ids (R32).
    private static ContextRegisters CompactedToSlotSeven()
    {
        var context = new ContextRegisters { RenderTargetMask = 0xF000000Fu };
        context.ShaderInterface.TargetOutputModes[0] = 1;
        context.ShaderInterface.TargetOutputModes[1] = 1;
        context.ShaderInterface.ColorShaderMask = 0xF000000Fu;
        return context;
    }

    [Fact]
    public void ASlotFedByACompactedExportIsWritten()
    {
        var context = CompactedToSlotSeven();
        const uint exportsZeroAndOne = 0x11u;

        Assert.False(RenderExecutor.IsUnwrittenColorTarget(context, 0, ColorComponentMap.Identity, exportsZeroAndOne));
        Assert.False(RenderExecutor.IsUnwrittenColorTarget(context, 7, ColorComponentMap.Identity, exportsZeroAndOne));
    }

    [Fact]
    public void ASlotWhoseExportIsNeverWrittenStaysUnwritten()
    {
        var context = CompactedToSlotSeven();
        const uint exportZeroOnly = 0x1u;

        Assert.True(RenderExecutor.IsUnwrittenColorTarget(context, 7, ColorComponentMap.Identity, exportZeroOnly));
    }

    [Fact]
    public void ExportsAndSlotsOfTheSameNumberStillPairUp()
    {
        var context = new ContextRegisters { RenderTargetMask = 0xFFu };
        context.ShaderInterface.TargetOutputModes[0] = 1;
        context.ShaderInterface.TargetOutputModes[1] = 1;
        context.ShaderInterface.ColorShaderMask = 0xFFu;

        Assert.False(RenderExecutor.IsUnwrittenColorTarget(context, 1, ColorComponentMap.Identity, 0x10u));
        Assert.True(RenderExecutor.IsUnwrittenColorTarget(context, 1, ColorComponentMap.Identity, 0x01u));
    }
}
