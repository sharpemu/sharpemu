// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.Host;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Vulkan;
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
    [InlineData(0u, true, 191)]
    [InlineData(1u, true, 64)]
    [InlineData(2u, true, 64)]
    [InlineData(0u, false, 128)]
    [InlineData(1u, false, 0)]
    [InlineData(2u, false, 64)]
    public void InterpolationParameter_DrawReadsSelectedVertex(uint selector, bool custom, int expected)
    {
        VerifyInterpolationDraw(selector, custom, interpolate: false, expected);
    }

    [Fact]
    public void InterpolationPhases_DrawUsesBarycentricRegisters()
    {
        VerifyInterpolationDraw(2, false, interpolate: true, expected: 96);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RectangleList_PreservesInterpolationAndProvokingVertexOutsideTheInputTriangle(bool flat)
    {
        if (!Ready()) return;
        if (!_vulkan.SupportsFillRectangle || !_vulkan.SupportsFragmentShaderBarycentric)
        {
            Assert.False(GatePrerequisites.DeviceRequired, "The required device lacks rectangle fill or fragment barycentrics.");
            return;
        }

        using var presenter = new PresenterUnderTest(_vulkan);
        presenter.LoadRenderingCommands();
        var harness = presenter.Harness;
        var target = harness.MapBacked(0x10000, ReadWrite);
        var vertices = harness.MapBacked(0x10000, ReadWrite);
        var words = RegisterWords.Color(target, Size, Size);
        harness.Write(vertices, Triangle(-1, -1, 1, -1, -1, 1));
        var provider = new FixedProgramProvider((IShaderPipelineHost)presenter.Instance, vertices,
            interpolationShader: CompileInterpolationFragment(2, flat, !flat));
        var banks = Banks(words);
        banks.UserConfig.PrimitiveType = 7;
        banks.Context.ShaderInterface.GeometryOutputPrimitiveType = 3;
        var executor = new RenderExecutor(presenter.RenderHost, provider);
        presenter.Run(() => executor.DrawAuto(1, banks, Draw()));
        presenter.Run(() => presenter.InvokeMethod("FlushBatchedGuestCommands"));
        harness.Finish();
        var pixels = harness.ReadImageBytes(TargetImage(presenter, words));
        // This corner lies outside the input triangle. The smooth value continues its
        // plane across the rectangle; flat input still comes from original vertex zero.
        var actual = Pixel(pixels, Size - 2, Size - 2);
        var expected = flat ? 64 : 126;
        Assert.InRange((int)(actual & 255), expected - 1, expected + 1);
        Assert.Equal(255u, actual >> 24);
        harness.Shutdown();
        _vulkan.AssertNoValidationMessages();
    }

    [Fact]
    public void InterpolationShader_RejectsMissingDeviceFeatureBeforeModuleCreation()
    {
        if (!Ready()) return;
        using var presenter = new PresenterUnderTest(_vulkan);
        presenter.SetField("_supportsFragmentShaderBarycentric", false);
        // A flat P0 move (selector 2) is supplied directly by the fixed-function
        // interface and intentionally does not require fragment barycentrics.
        // P10 (selector 0) still reads per-vertex data and must be feature-gated.
        var shader = new VulkanCompiledGuestShader(CompileInterpolationFragment(0, true, false));
        presenter.Run(() =>
        {
            var error = Assert.Throws<NotSupportedException>(() =>
                ((IShaderPipelineHost)presenter.Instance).CreateShaderModule(shader, ShaderStage.Pixel, 1, 1));
            Assert.Contains("fragmentShaderBarycentric", error.Message);
        });
        presenter.Harness.Shutdown();
    }

    private void VerifyInterpolationDraw(uint selector, bool custom, bool interpolate, int expected)
    {
        if (!Ready()) return;
        if (!_vulkan.SupportsFragmentShaderBarycentric)
        {
            Assert.False(GatePrerequisites.DeviceRequired, "The required device lacks fragmentShaderBarycentric.");
            Console.Error.WriteLine("[TEST][SKIP] The device lacks fragmentShaderBarycentric.");
            return;
        }

        using var presenter = new PresenterUnderTest(_vulkan);
        presenter.LoadRenderingCommands();
        var harness = presenter.Harness;
        var target = harness.MapBacked(0x10000, ReadWrite);
        var vertices = harness.MapBacked(0x10000, ReadWrite);
        var words = RegisterWords.Color(target, Size, Size);
        harness.Write(vertices, Triangle(-1f, -1f, 3f, -1f, -1f, 3f));
        var provider = new FixedProgramProvider((IShaderPipelineHost)presenter.Instance, vertices,
            interpolationShader: CompileInterpolationFragment(selector, custom, interpolate));
        var executor = new RenderExecutor(presenter.RenderHost, provider);
        presenter.Run(() => executor.DrawAuto(1, Banks(words), Draw()));
        presenter.Run(() => presenter.InvokeMethod("FlushBatchedGuestCommands"));
        harness.Finish();
        var pixels = harness.ReadImageBytes(TargetImage(presenter, words));
        var actual = Pixel(pixels, Size / 2, Size / 2);
        Assert.InRange((int)(actual & 255), Math.Max(0, expected - 1), expected + 1);
        Assert.Equal(255u, actual >> 24);
        harness.Shutdown();
        _vulkan.AssertNoValidationMessages();
    }

    private static byte[] CompileInterpolationFragment(uint selector, bool custom, bool interpolate)
    {
        var instructions = new List<Gen5ShaderInstruction>();
        void Parameter(string opcode, uint source) => instructions.Add(new Gen5ShaderInstruction(
            (uint)instructions.Count * 4, Gen5ShaderEncoding.Vintrp, opcode, [source],
            [Gen5Operand.Vector(source)], [Gen5Operand.Vector(4)], new Gen5InterpolationControl(0, 0)));
        Parameter("VInterpMovF32", selector);
        if (interpolate)
        {
            Parameter("VInterpP1F32", 0);
            Parameter("VInterpP2F32", 1);
        }
        instructions.Add(MoveVector((uint)instructions.Count * 4, 5, 0x3F800000));
        instructions.Add(new Gen5ShaderInstruction((uint)instructions.Count * 4, Gen5ShaderEncoding.Exp,
            "Exp", [], [Gen5Operand.Vector(4), Gen5Operand.Vector(4), Gen5Operand.Vector(4), Gen5Operand.Vector(5)],
            [], new Gen5ExportControl(0, 15, false, true, true)));
        instructions.Add(EndProgram((uint)instructions.Count * 4));
        var (plan, resources, layout) = Prepare(Program([.. instructions]), ShaderStage.Pixel, userDataCount: 0);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            PixelInputAddress = 2,
            PixelInputEnable = 2,
            PixelInputCntl = [custom ? 0x400u : 0u],
            PixelCustomInterpolationMask = custom ? 1u : 0u,
            PixelOutputs = [new Gen5PixelOutputBinding(0, 0, Gen5PixelOutputKind.Float)],
            EnableGraphicsSubgroupOperations = false,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        return shader.Spirv;
    }
}
