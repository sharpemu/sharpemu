// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

// The ObjPrimID write picks one 32-bit color slot that no export reaches.
public sealed class ObjectIdOutputTests
{
    private const uint Info32Bit = (uint)ChannelLayout.Bits32 << 2;
    private const uint Info16Bit = (uint)ChannelLayout.Bits16 << 2;

    // Ghost of Yotei's visibility pass: MRT0 (R16) exports to slot 0, slots 1..7 bind the R32 id image.
    private static ContextRegisters VisibilityPass()
    {
        var context = new ContextRegisters { RenderTargetMask = 0xFFFFFFFFu };
        for (var slot = 0; slot < ContextRegisters.ColorTargetCount; slot++)
        {
            context.ColorTargets[slot] = new ColorTargetWords(0x1000ul * (ulong)(slot + 1), 0, slot == 0 ? Info16Bit : Info32Bit, 0, 0, 0, 0, 0, 0, 0, 0);
        }

        context.ShaderInterface.TargetOutputModes[0] = 4;
        context.ShaderInterface.ColorShaderMask = 0x1u;
        context.ShaderInterface.DrawPayloadControl = 1;
        return context;
    }

    [Fact]
    public void SlotSevenIsPreferred()
    {
        Assert.Equal(7, ObjectIdOutput.SlotFor(VisibilityPass()));
    }

    [Fact]
    public void WithoutThePayloadBitThereIsNoOutput()
    {
        var context = VisibilityPass();
        context.ShaderInterface.DrawPayloadControl = 0;

        Assert.Equal(ObjectIdOutput.NoSlot, ObjectIdOutput.SlotFor(context));
    }

    [Fact]
    public void AnExportedSlotIsSkipped()
    {
        var context = VisibilityPass();
        context.ShaderInterface.TargetOutputModes[1] = 4;
        context.ShaderInterface.ColorShaderMask = 0xF000_000Fu;

        // MRT0 and MRT1 reach slots 0 and 7; the first free 32-bit slot takes the id.
        Assert.Equal(1, ObjectIdOutput.SlotFor(context));
    }

    [Fact]
    public void ASlotThatIsNotThirtyTwoBitsIsSkipped()
    {
        var context = VisibilityPass();
        context.ColorTargets[7] = context.ColorTargets[7] with { Info = Info16Bit };

        Assert.Equal(1, ObjectIdOutput.SlotFor(context));
    }

    [Fact]
    public void ASlotWithoutAnAddressOrAMaskIsSkipped()
    {
        var context = VisibilityPass();
        context.ColorTargets[7] = context.ColorTargets[7] with { BaseAddress = 0 };
        context.ColorTargets[1] = context.ColorTargets[1] with { BaseAddress = 0 };
        context.RenderTargetMask &= ~(0xFu << 8);

        Assert.Equal(3, ObjectIdOutput.SlotFor(context));
    }

    [Fact]
    public void NoQualifyingSlotMeansNoOutput()
    {
        var context = VisibilityPass();
        context.RenderTargetMask = 0xFu;

        Assert.Equal(ObjectIdOutput.NoSlot, ObjectIdOutput.SlotFor(context));
    }

    [Fact]
    public void ThePrimitiveIndexIsAddedOnlyWhenTheControlAsksForIt()
    {
        var context = VisibilityPass();
        Assert.False(ObjectIdOutput.AddsPrimitiveId(context));

        context.ShaderInterface.ObjectPrimitiveIdControl = 0x2;
        Assert.True(ObjectIdOutput.AddsPrimitiveId(context));
    }

    [Fact]
    public void TheObjectIdSlotIsAWrittenTarget()
    {
        var context = VisibilityPass();

        Assert.False(RenderExecutor.IsUnwrittenColorTarget(context, 7, ColorComponentMap.Identity, 0x1u));
        Assert.True(RenderExecutor.IsUnwrittenColorTarget(context, 6, ColorComponentMap.Identity, 0x1u));
    }

    [Fact]
    public void WithoutTheObjectIdTheSameSlotIsUnwritten()
    {
        var context = VisibilityPass();
        context.ShaderInterface.DrawPayloadControl = 0;

        Assert.True(RenderExecutor.IsUnwrittenColorTarget(context, 7, ColorComponentMap.Identity, 0x1u));
    }
}
