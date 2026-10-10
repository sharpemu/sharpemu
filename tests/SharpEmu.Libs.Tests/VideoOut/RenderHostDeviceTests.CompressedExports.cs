// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

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
    [Theory]
    [InlineData(Gen5PixelExportFormat.Float16, 0x3A003400u, 0x3C003800u, 64, 191, 128)]
    [InlineData(Gen5PixelExportFormat.Unorm16, 0xBFFF4000u, 0xFFFF8000u, 64, 191, 128)]
    [InlineData(Gen5PixelExportFormat.Snorm16, 0x5FFF2000u, 0x7FFF4000u, 64, 191, 128)]
    [InlineData(Gen5PixelExportFormat.Uint16, 0x00000001u, 0x00010001u, 255, 0, 255)]
    [InlineData(Gen5PixelExportFormat.Sint16, 0xFFFF0001u, 0x00010001u, 255, 0, 255)]
    public void CompressedExport_DrawDecodesTheShaderColorFormat(
        Gen5PixelExportFormat format, uint low, uint high, int red, int green, int blue)
    {
        if (!Ready()) return;
        using var presenter = new PresenterUnderTest(_vulkan);
        presenter.LoadRenderingCommands();
        var harness = presenter.Harness;
        var target = harness.MapBacked(0x10000, ReadWrite);
        var vertices = harness.MapBacked(0x10000, ReadWrite);
        var words = RegisterWords.Color(target, Size, Size);
        harness.Write(vertices, Triangle(-1f, -1f, 3f, -1f, -1f, 3f));
        var export = new Gen5ShaderInstruction(8, Gen5ShaderEncoding.Exp, "Exp", [],
            [Gen5Operand.Vector(0), Gen5Operand.Vector(1), Gen5Operand.Vector(0), Gen5Operand.Vector(0)],
            [], new Gen5ExportControl(0, 15, true, true, true));
        var (plan, resources, layout) = Prepare(Program(
            MoveVector(0, 0, low), MoveVector(4, 1, high), export, EndProgram(16)), ShaderStage.Pixel, userDataCount: 0);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            PixelOutputs = [new Gen5PixelOutputBinding(0, 0, Gen5PixelOutputKind.Float) { ExportFormat = format }],
            EnableGraphicsSubgroupOperations = false,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        var provider = new FixedProgramProvider((IShaderPipelineHost)presenter.Instance, vertices,
            interpolationShader: shader.Spirv);
        var executor = new RenderExecutor(presenter.RenderHost, provider);
        presenter.Run(() => executor.DrawAuto(1, Banks(words), Draw()));
        presenter.Run(() => presenter.InvokeMethod("FlushBatchedGuestCommands"));
        harness.Finish();
        var actual = Pixel(harness.ReadImageBytes(TargetImage(presenter, words)), Size / 2, Size / 2);
        Assert.InRange((int)(actual & 255), red - 1, red + 1);
        Assert.InRange((int)((actual >> 8) & 255), green - 1, green + 1);
        Assert.InRange((int)((actual >> 16) & 255), blue - 1, blue + 1);
        Assert.Equal(255u, actual >> 24);
        harness.Shutdown();
        _vulkan.AssertNoValidationMessages();
    }
}
