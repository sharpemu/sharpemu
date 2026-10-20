// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Images.ImageCacheTestSupport;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed unsafe partial class RenderHostDeviceTests
{
    // An instance-ID style pass: MRT0 feeds slot 0, and slot 1 is a 32-bit target CB_SHADER_MASK
    // gives no components. With the object/primitive ID mode slot 1 holds VGT_OBJECT_ID plus the
    // primitive ID, which restarts at zero for each instance: the second instance overwrites the
    // first with the same values.
    [Fact]
    public void ObjectPrimitiveIdTarget_DrawWritesTheObjectIdPlusThePerInstancePrimitiveId()
    {
        if (!Ready()) return;
        const uint ObjectId = 0x0096_0000;
        using var presenter = new PresenterUnderTest(_vulkan);
        presenter.LoadRenderingCommands();
        Assert.True(ObjectPrimitiveIdTarget.Supported);
        var harness = presenter.Harness;
        var colorTarget = harness.MapBacked(0x10000, ReadWrite);
        var idTarget = harness.MapBacked(0x10000, ReadWrite);
        var vertices = harness.MapBacked(0x10000, ReadWrite);
        var colorWords = RegisterWords.Color(colorTarget, Size, Size, layout: ChannelLayout.Bits16, type: ChannelType.UInt);
        var idWords = RegisterWords.Color(idTarget, Size, Size, layout: ChannelLayout.Bits32, type: ChannelType.UInt);
        // Primitive 0 covers the left edge, primitive 1 the right edge; the middle stays clear.
        harness.Write(vertices, [.. Triangle(-1f, -1f, 0f, -1f, -1f, 1f), .. Triangle(1f, -1f, 1f, 1f, 0f, 1f)]);
        var export = new Gen5ShaderInstruction(8, Gen5ShaderEncoding.Exp, "Exp", [],
            [Gen5Operand.Vector(0), Gen5Operand.Vector(0), Gen5Operand.Vector(0), Gen5Operand.Vector(0)],
            [], new Gen5ExportControl(0, 1, false, true, true));
        var (plan, resources, _) = Prepare(Program(MoveVector(0, 0, 7), export, EndProgram(16)), ShaderStage.Pixel, userDataCount: 0);
        var layout = BindingLayout.Allocate(resources.Info, [], usesGlobalDataShare: false, usesFlattenedTable: false,
            usesShaderBase: false, usesObjectId: true);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            PixelOutputs =
            [
                new Gen5PixelOutputBinding(0, 0, Gen5PixelOutputKind.Uint),
                new Gen5PixelOutputBinding(1, 1, Gen5PixelOutputKind.Uint) { ExportTarget = 9, ObjectPrimitiveId = true },
            ],
            EnableGraphicsSubgroupOperations = false,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        var provider = new FixedProgramProvider((IShaderPipelineHost)presenter.Instance, vertices,
            interpolationShader: shader.Spirv, vertexCount: 6, pixelBindings: layout);
        var executor = new RenderExecutor(presenter.RenderHost, provider);
        var banks = Banks(colorWords);
        banks.Context.ColorTargets[1] = idWords;
        banks.Context.RenderTargetMask = 0xFF;
        banks.Context.ShaderInterface.ColorShaderMask = 0x1;
        banks.Context.ShaderInterface.TargetOutputModes[0] = 1;
        banks.Context.ObjectPrimitiveIdControl = 0x2;
        banks.UserConfig.ObjectId = ObjectId;
        presenter.Run(() => executor.DrawAuto(1, banks, new DrawAutoArguments(0, 0, 6, 2, 0, 0, DrawOffsetSource.Packet)));
        presenter.Run(() => presenter.InvokeMethod("FlushBatchedGuestCommands"));
        harness.Finish();
        var pixels = harness.ReadImageBytes(TargetImage(presenter, idWords));
        Assert.Equal(ObjectId, Pixel(pixels, 4, 32));
        Assert.Equal(ObjectId + 1, Pixel(pixels, 60, 32));
        Assert.Equal(0u, Pixel(pixels, 40, 8));
        harness.Shutdown();
        _vulkan.AssertNoValidationMessages();
    }
}
