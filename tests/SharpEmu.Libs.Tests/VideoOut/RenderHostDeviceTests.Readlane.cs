// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.Host;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Vulkan;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Images.ImageCacheTestSupport;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed unsafe partial class RenderHostDeviceTests
{
    // v_readlane_b32 returns one lane to the whole wave. Each fragment reads lane 31 of its
    // own lane index (lane 63 in a wave32 names the same lane) and lights up when that
    // value is at least 24. A per-lane read would light up only the fragments in lanes 24..31.
    [Fact]
    public void FragmentReadlane_BroadcastsOneLaneToTheWholeSubgroup()
    {
        if (!Ready()) return;
        using var presenter = new PresenterUnderTest(_vulkan);
        presenter.LoadRenderingCommands();
        var harness = presenter.Harness;
        var target = harness.MapBacked(0x10000, ReadWrite);
        var vertices = harness.MapBacked(0x10000, ReadWrite);
        var words = RegisterWords.Color(target, Size, Size);
        harness.Write(vertices, Triangle(-1f, -1f, 3f, -1f, -1f, 3f));
        var program = Program(
            Vop3(0, "VMbcntLoU32B32", 1, Operand(uint.MaxValue), Operand(0)),
            MoveVector(8, 5, 24),
            ReadLane(12, scalarRegister: 10, vectorRegister: 1, lane: 63),
            Vopc(20, "VCmpGeU32", Gen5Operand.Scalar(10), 5),
            Vop2(24, "VCndmaskB32", 2, Operand(0), Operand(0x3F800000)),
            MoveVector(28, 3, 0x3F800000),
            new Gen5ShaderInstruction(32, Gen5ShaderEncoding.Exp, "Exp", [],
                [Gen5Operand.Vector(2), Gen5Operand.Vector(2), Gen5Operand.Vector(2), Gen5Operand.Vector(3)],
                [], new Gen5ExportControl(0, 15, false, true, true)),
            EndProgram(36));
        var (plan, resources, layout) = Prepare(program, ShaderStage.Pixel, userDataCount: 0);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            WaveSize = 32,
            PixelOutputs = [new Gen5PixelOutputBinding(0, 0, Gen5PixelOutputKind.Float)],
            EnableGraphicsSubgroupOperations = true,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        var provider = new FixedProgramProvider((IShaderPipelineHost)presenter.Instance, vertices,
            interpolationShader: shader.Spirv);
        var banks = Banks(words);
        var executor = new RenderExecutor(presenter.RenderHost, provider);
        presenter.Run(() => executor.DrawAuto(1, banks, Draw()));
        presenter.Run(() => presenter.InvokeMethod("FlushBatchedGuestCommands"));
        harness.Finish();
        var pixels = harness.ReadImageBytes(TargetImage(presenter, words));
        var lit = 0;
        for (var y = 0u; y < Size; y++)
        {
            for (var x = 0u; x < Size; x++)
            {
                if (Pixel(pixels, x, y) == uint.MaxValue)
                {
                    lit++;
                }
            }
        }

        harness.Shutdown();
        Assert.Equal(uint.MaxValue, Pixel(pixels, Size / 2, Size / 2));
        // Absent lanes of a partial subgroup can lower the value at the very edge of the draw.
        Assert.True(lit >= Size * Size * 95 / 100, $"only {lit} of {Size * Size} fragments read the broadcast lane");
        _vulkan.AssertNoValidationMessages();
    }

    // v_permlanex16_b32 of a constant must return that constant wherever the partner lane exists,
    // including pixels at the edge of the draw whose partner quad was never launched.
    [Fact]
    public void FragmentPermlanex16_FetchesAPartnerRowLaneOnAPartialSubgroup()
    {
        if (!Ready()) return;
        using var presenter = new PresenterUnderTest(_vulkan);
        presenter.LoadRenderingCommands();
        var harness = presenter.Harness;
        var target = harness.MapBacked(0x10000, ReadWrite);
        var vertices = harness.MapBacked(0x10000, ReadWrite);
        var words = RegisterWords.Color(target, Size, Size);
        harness.Write(vertices, Triangle(-1f, -1f, 0.3f, -1f, -1f, 0.3f));
        var program = Program(
            Vop3(0, "VMbcntLoU32B32", 1, Operand(uint.MaxValue), Operand(0)),
            MoveVector(8, 5, 7),
            Vop3(12, "VPermlanex16B32", 6, Gen5Operand.Vector(5), Operand(uint.MaxValue), Operand(uint.MaxValue)),
            Vopc(20, "VCmpEqU32", Operand(7), 6),
            Vop2(24, "VCndmaskB32", 2, Operand(0), Operand(0x3F800000)),
            MoveVector(28, 3, 0x3F800000),
            new Gen5ShaderInstruction(32, Gen5ShaderEncoding.Exp, "Exp", [],
                [Gen5Operand.Vector(2), Gen5Operand.Vector(2), Gen5Operand.Vector(2), Gen5Operand.Vector(3)],
                [], new Gen5ExportControl(0, 15, false, true, true)),
            EndProgram(36));
        var (plan, resources, layout) = Prepare(program, ShaderStage.Pixel, userDataCount: 0);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            WaveSize = 32,
            PixelOutputs = [new Gen5PixelOutputBinding(0, 0, Gen5PixelOutputKind.Float)],
            EnableGraphicsSubgroupOperations = true,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        var provider = new FixedProgramProvider((IShaderPipelineHost)presenter.Instance, vertices,
            interpolationShader: shader.Spirv);
        var banks = Banks(words);
        var executor = new RenderExecutor(presenter.RenderHost, provider);
        presenter.Run(() => executor.DrawAuto(1, banks, Draw()));
        presenter.Run(() => presenter.InvokeMethod("FlushBatchedGuestCommands"));
        harness.Finish();
        var pixels = harness.ReadImageBytes(TargetImage(presenter, words));
        var lit = 0;
        var covered = 0;
        for (var y = 0u; y < Size; y++)
        {
            for (var x = 0u; x < Size; x++)
            {
                var pixel = Pixel(pixels, x, y);
                if (pixel != 0)
                {
                    covered++;
                }

                if (pixel == uint.MaxValue)
                {
                    lit++;
                }
            }
        }

        harness.Shutdown();
        Assert.True(covered > 0, "the triangle covered no pixel");
        Assert.True(lit >= covered * 90 / 100, $"only {lit} of {covered} covered fragments fetched the partner lane");
        _vulkan.AssertNoValidationMessages();
    }
}
