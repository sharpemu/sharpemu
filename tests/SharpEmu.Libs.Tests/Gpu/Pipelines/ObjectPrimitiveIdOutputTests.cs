// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

[Collection(SchedulingStateCollection.Name)]
public sealed class ObjectPrimitiveIdOutputTests
{
    private const ulong VertexAddress = PipelineTestGuest.MemoryBase + 0x1000;
    private const ulong PixelAddress = PipelineTestGuest.MemoryBase + 0x2000;
    private const ulong InstanceTarget = PipelineTestGuest.MemoryBase + 0x100000;
    private const ulong IdTarget = PipelineTestGuest.MemoryBase + 0x200000;

    // An instance-ID pass: EXP MRT0 feeds the 16-bit slot 0, slots 1..7 name one 32-bit surface
    // CB_SHADER_MASK gives no components. The ID mode declares that surface once, as an ID output
    // reading the object ID from shader data; without it no output reaches the surface.
    [Theory]
    [InlineData(0x2u, true)]
    [InlineData(0x0u, false)]
    public void PixelProgram_DeclaresTheIdTargetOnceInObjectPrimitiveIdMode(uint objectPrimitiveIdControl, bool expectIdOutput)
    {
        using var fatal = new FatalScope();
        ObjectPrimitiveIdTarget.Supported = true;
        var guest = new PipelineTestGuest();
        guest.RegisterProgram(VertexAddress, PipelineTestGuest.MemoryBase + 0x8000, PipelineTestGuest.EndProgram,
            userDataAddress: PipelineTestGuest.MemoryBase + 0x9000);
        guest.RegisterProgram(PixelAddress, PipelineTestGuest.MemoryBase + 0x8100, [0xF8001801, 0x00000000, 0xBF810000],
            userDataAddress: PipelineTestGuest.MemoryBase + 0xA000);
        var context = new ContextRegisters();
        context.ColorTargets[0] = RegisterWords.Color(InstanceTarget, 64, 64, layout: ChannelLayout.Bits16, type: ChannelType.UInt);
        for (var slot = 1; slot < ContextRegisters.ColorTargetCount; slot++)
        {
            context.ColorTargets[slot] = RegisterWords.Color(IdTarget, 64, 64, layout: ChannelLayout.Bits32, type: ChannelType.UInt);
        }

        context.RenderTargetMask = 0xFFFFFFFF;
        context.ObjectPrimitiveIdControl = objectPrimitiveIdControl;
        context.ShaderInterface.ColorShaderMask = 0x1;
        context.ShaderInterface.TargetOutputModes[0] = 1;
        var cache = new ShaderPipelineCache(guest.Context, guest.Host, guest.Compiler, guest.Registry);
        var mapping = new ColorComponentMap[ContextRegisters.ColorTargetCount];
        Array.Fill(mapping, ColorComponentMap.Identity);

        Assert.True(cache.GetGraphicsPrograms(
            new VertexStageRegisters { ExportAddress = VertexAddress }, new PixelStageRegisters { Address = PixelAddress },
            context.ShaderInterface, context, mapping, pixelActive: true, depthBound: true).Available);

        var pixel = Assert.Single(guest.Compiler.Requests, request => request.Stage == ShaderStage.Pixel);
        var slots = pixel.PixelOutputs.Select(output => output.GuestSlot).ToArray();
        Assert.Equal(expectIdOutput ? [0u, 1u] : [0u], slots);
        Assert.False(pixel.PixelOutputs[0].ObjectPrimitiveId);
        Assert.Equal(expectIdOutput, pixel.PixelOutputs.Any(output => output.GuestSlot == 1 && output.ObjectPrimitiveId));
        Assert.Equal(expectIdOutput, pixel.Bindings.UsesObjectId);
    }
}
