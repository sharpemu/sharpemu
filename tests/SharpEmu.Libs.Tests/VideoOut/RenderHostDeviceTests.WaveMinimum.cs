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

// G-buffer pixel shaders scalarize a per-pixel
// byte ID with a wave64 minimum: whole-wave EXEC (S_ORN2_SAVEEXEC of EXEC), identity 0xFFFFFFFF for the lanes the
// wave did not start with, V_MIN_U32 DPP row_shr:1/2/4/8 with BOUND_CTRL clear, V_PERMLANEX16 with BOUND_CTRL set,
// V_READLANE 31 and 63, S_MIN_U32. A PS5 wave always has its dead lanes, which hold the identity. A host fragment
// subgroup of a small primitive does not: the lanes past its quads were never launched. Reading such a lane must not
// produce a value below every live ID, or the ID loop that follows never finishes.
public sealed unsafe partial class RenderHostDeviceTests
{
    [Theory]
    [InlineData(false, 32u)]
    [InlineData(true, 32u)]
    [InlineData(false, 64u)]
    [InlineData(true, 64u)]
    public void WaveMinimum_PartialFragmentSubgroupFindsTheLiveMinimum(bool onePixel, uint waveSize)
    {
        if (!Ready()) return;
        using var presenter = new PresenterUnderTest(_vulkan!);
        presenter.LoadRenderingCommands();
        var harness = presenter.Harness;
        var target = harness.MapBacked(0x10000, ReadWrite);
        var vertices = harness.MapBacked(0x10000, ReadWrite);
        var words = RegisterWords.Color(target, Size, Size);
        // One pixel: the centre of pixel (32, 32) is at (1/64, 1/64) in clip space.
        harness.Write(vertices, onePixel
            ? Triangle(0f, 0f, 0.06f, 0f, 0f, 0.06f)
            : Triangle(-1f, -1f, 3f, -1f, -1f, 3f));
        const uint id = 7;
        var minusOne = Gen5Operand.Source(193);
        var instructions = new List<Gen5ShaderInstruction>();
        uint pc = 0;
        void Add(Gen5ShaderInstruction instruction)
        {
            instructions.Add(instruction with { Pc = pc });
            pc += 8;
        }

        Gen5ShaderInstruction MinDpp(uint control) =>
            Vop2(0, "VMinU32", 5, Gen5Operand.Vector(5), Gen5Operand.Vector(5)) with
            {
                Control = new Gen5DppControl(control, false, false, 0, 0, 15, 15),
            };

        Add(MoveVector(0, 15, id));
        Add(MoveScalar(0, 26, uint.MaxValue));
        Add(MoveScalar(0, 27, uint.MaxValue));
        Add(Sop2(0, "SAndB64", 32, Gen5Operand.Scalar(126), Gen5Operand.Scalar(26)));
        Add(Sop1(0, "SOrn2SaveexecB64", 106, Gen5Operand.Scalar(126)));
        Add(Vop3(0, "VCndmaskB32", 5, minusOne, Gen5Operand.Vector(15), Gen5Operand.Scalar(32)));
        Add(MinDpp(0x111));
        Add(MinDpp(0x112));
        Add(MinDpp(0x114));
        Add(MinDpp(0x118));
        Add(Vop3(0, "VPermlanex16B32", 2, Gen5Operand.Vector(5), minusOne, minusOne) with
        {
            Control = new Gen5Vop3Control(0, 0, 0, false, 2, null),
        });
        Add(Vop2(0, "VMinU32", 5, Gen5Operand.Vector(5), Gen5Operand.Vector(2)));
        Add(Sop1(0, "SMovB64", 126, Gen5Operand.Scalar(106)));
        Add(ReadLane(0, 32, 5, 31));
        Add(ReadLane(0, 33, 5, 63));
        Add(Sop2(0, "SMinU32", 59, Gen5Operand.Scalar(32), Gen5Operand.Scalar(33)));
        Add(Vopc(0, "VCmpEqU32", Gen5Operand.Scalar(59), 15));
        Add(Vop2(0, "VCndmaskB32", 2, Operand(0), Operand(0x3F800000)));
        Add(MoveVector(0, 3, 0x3F800000));
        Add(new Gen5ShaderInstruction(0, Gen5ShaderEncoding.Exp, "Exp", [],
            [Gen5Operand.Vector(2), Gen5Operand.Vector(2), Gen5Operand.Vector(2), Gen5Operand.Vector(3)],
            [], new Gen5ExportControl(0, 15, false, true, true)));
        Add(EndProgram(0));
        var (plan, resources, layout) = Prepare(Program([.. instructions]), ShaderStage.Pixel, userDataCount: 0);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            WaveSize = waveSize,
            PixelOutputs = [new Gen5PixelOutputBinding(0, 0, Gen5PixelOutputKind.Float)],
            EnableGraphicsSubgroupOperations = true,
            GraphicsPartialSubgroupLanes = true,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        var provider = new FixedProgramProvider((IShaderPipelineHost)presenter.Instance, vertices,
            interpolationShader: shader.Spirv);
        var executor = new RenderExecutor(presenter.RenderHost, provider);
        presenter.Run(() => executor.DrawAuto(1, Banks(words), Draw()));
        presenter.Run(() => presenter.InvokeMethod("FlushBatchedGuestCommands"));
        harness.Finish();
        var bytes = harness.ReadImageBytes(TargetImage(presenter, words));
        var covered = 0;
        var wrong = new List<string>();
        for (uint y = 0; y < Size; y++)
        {
            for (uint x = 0; x < Size; x++)
            {
                var pixel = Pixel(bytes, x, y);
                if (pixel >> 24 == 0) continue;
                covered++;
                if (pixel != uint.MaxValue) wrong.Add($"({x},{y})=0x{pixel:X8}");
            }
        }

        Assert.True(covered > 0, "the draw covered no pixel");
        Assert.True(wrong.Count == 0, $"{wrong.Count} of {covered} pixels saw a wave minimum other than {id}: " +
            string.Join(' ', wrong.Take(8)));
        harness.Shutdown();
        _vulkan!.AssertNoValidationMessages();
    }

    // The same G-buffer shaders take screen-space derivatives with quad DPP (quad_perm [0,0,0,0] and [1,1,1,1],
    // BOUND_CTRL set). A one-pixel primitive's quad is one live lane and three helpers; the helpers are launched
    // host invocations, so their values must still be read.
    [Theory]
    [InlineData(32u)]
    [InlineData(64u)]
    public void QuadDppDerivative_ReadsHelperLanesOfAPartialFragmentSubgroup(uint waveSize)
    {
        if (!Ready()) return;
        using var presenter = new PresenterUnderTest(_vulkan!);
        presenter.LoadRenderingCommands();
        var harness = presenter.Harness;
        var target = harness.MapBacked(0x10000, ReadWrite);
        var vertices = harness.MapBacked(0x10000, ReadWrite);
        var words = RegisterWords.Color(target, Size, Size);
        harness.Write(vertices, Triangle(0f, 0f, 0.06f, 0f, 0f, 0.06f));
        Gen5ShaderInstruction QuadRead(uint pc, uint destination, uint control) =>
            Vop1(pc, "VMovB32", destination, Gen5Operand.Vector(0)) with
            {
                Control = new Gen5DppControl(control, false, true, 0, 0, 15, 15),
            };

        // v0 = POS_X_FLOAT; ddx = x(quad lane 1) - x(quad lane 0) = 1.
        var program = Program(
            QuadRead(0, 3, 0x00),
            QuadRead(8, 4, 0x55),
            Vop2(16, "VSubF32", 2, Gen5Operand.Vector(4), Gen5Operand.Vector(3)),
            MoveVector(24, 5, 0x3F800000),
            new Gen5ShaderInstruction(32, Gen5ShaderEncoding.Exp, "Exp", [],
                [Gen5Operand.Vector(2), Gen5Operand.Vector(2), Gen5Operand.Vector(2), Gen5Operand.Vector(5)],
                [], new Gen5ExportControl(0, 15, false, true, true)),
            EndProgram(40));
        var (plan, resources, layout) = Prepare(program, ShaderStage.Pixel, userDataCount: 0);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            WaveSize = waveSize,
            PixelInputAddress = 1u << 8,
            PixelInputEnable = 1u << 8,
            PixelOutputs = [new Gen5PixelOutputBinding(0, 0, Gen5PixelOutputKind.Float)],
            EnableGraphicsSubgroupOperations = true,
            GraphicsPartialSubgroupLanes = true,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        var provider = new FixedProgramProvider((IShaderPipelineHost)presenter.Instance, vertices,
            interpolationShader: shader.Spirv);
        var executor = new RenderExecutor(presenter.RenderHost, provider);
        presenter.Run(() => executor.DrawAuto(1, Banks(words), Draw()));
        presenter.Run(() => presenter.InvokeMethod("FlushBatchedGuestCommands"));
        harness.Finish();
        var bytes = harness.ReadImageBytes(TargetImage(presenter, words));
        var covered = new List<uint>();
        for (uint y = 0; y < Size; y++)
        {
            for (uint x = 0; x < Size; x++)
            {
                var pixel = Pixel(bytes, x, y);
                if (pixel >> 24 != 0) covered.Add(pixel);
            }
        }

        Assert.NotEmpty(covered);
        Assert.All(covered, pixel => Assert.Equal(uint.MaxValue, pixel));
        harness.Shutdown();
        _vulkan!.AssertNoValidationMessages();
    }
}
