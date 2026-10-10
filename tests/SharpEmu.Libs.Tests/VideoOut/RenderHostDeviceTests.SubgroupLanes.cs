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
    // This is the wave-minimum idiom at 0x80003C5100/0x1CC, isolated from the
    // texture loop. A tiny primitive leaves lane 31 absent from its fragment
    // subgroup. Red exports every live lane's input, green exports the reduced
    // result, so the CPU can compare it with the true minimum of the live lanes.
    [Fact]
    public void PixelWaveMinimum_UsesItsOwnValueForAbsentSubgroupLanes()
    {
        if (!Ready()) return;
        using var presenter = new PresenterUnderTest(_vulkan);
        presenter.LoadRenderingCommands();
        var harness = presenter.Harness;
        var target = harness.MapBacked(0x10000, ReadWrite);
        var vertices = harness.MapBacked(0x10000, ReadWrite);
        var words = RegisterWords.Color(target, Size, Size);
        // Four pixels at most: deliberately much smaller than an NVIDIA wave32.
        harness.Write(vertices, Triangle(-1f, -1f, -0.875f, -1f, -1f, -0.875f));
        var provider = new FixedProgramProvider((IShaderPipelineHost)presenter.Instance, vertices,
            interpolationShader: CompilePixelWaveMinimum());
        var executor = new RenderExecutor(presenter.RenderHost, provider);
        presenter.Run(() => executor.DrawAuto(1, Banks(words), Draw()));
        presenter.Run(() => presenter.InvokeMethod("FlushBatchedGuestCommands"));
        harness.Finish();

        var pixels = harness.ReadImageBytes(TargetImage(presenter, words));
        var covered = Enumerable.Range(0, checked((int)(Size * Size)))
            .Select(index => BitConverter.ToUInt32(pixels, index * sizeof(uint)))
            .Where(value => value >> 24 == byte.MaxValue)
            .ToArray();
        Assert.InRange(covered.Length, 1, 31);
        var trueMinimum = covered.Min(value => value & byte.MaxValue);
        Assert.All(covered, value =>
        {
            Assert.Equal(trueMinimum, (value >> 8) & byte.MaxValue);
            Assert.Equal(byte.MaxValue, value >> 24);
        });

        harness.Shutdown();
        _vulkan.AssertNoValidationMessages();
    }

    private static byte[] CompilePixelWaveMinimum()
    {
        const uint One = 0x3F80_0000;
        const uint OneOver32 = 0x3D00_0000;
        var instructions = new List<Gen5ShaderInstruction>();
        void Add(Gen5ShaderInstruction instruction) => instructions.Add(instruction with { Pc = (uint)instructions.Count * 8 });

        // v14 = subgroup invocation id + 1. It makes every present lane distinct.
        Add(Vop2(0, "VMbcntLoU32B32", 14, Operand(uint.MaxValue), Operand(1)));
        Add(Vopc(0, "VCmpEqU32", Gen5Operand.Vector(14), 14));
        Add(Sop2(0, "SAndB64", 6, Gen5Operand.Scalar(126), Gen5Operand.Scalar(106)));
        // 0xBEEA287E: save EXEC and force it, exactly like the game's reduction.
        Add(Sop1(0, "SOrn2SaveexecB64", 106, Gen5Operand.Scalar(126)));
        Add(new Gen5ShaderInstruction(0, Gen5ShaderEncoding.Vop3, "VCndmaskB32", [0, 0],
            [Operand(uint.MaxValue), Gen5Operand.Vector(14), Gen5Operand.Scalar(6)], [Gen5Operand.Vector(13)],
            new Gen5Vop3Control(0, 0, 0, false, 0, null)));
        foreach (var shift in new uint[] { 1, 2, 4, 8 })
        {
            // 0x261A1AFA_FF011?0D: v_min_u32 v13, v13, v13 row_shr:shift.
            Add(Vop2(0, "VMinU32", 13, Gen5Operand.Vector(13), Gen5Operand.Vector(13)) with
            {
                Control = new Gen5DppControl(0x110 + shift, false, false, 0, 0, 15, 15),
            });
        }

        // 0xD778100C_0305830D: exchange the two rows, then reduce them.
        Add(Vop3(0, "VPermlanex16B32", 12, Gen5Operand.Vector(13), Operand(uint.MaxValue), Operand(uint.MaxValue)) with
        {
            Control = new Gen5Vop3Control(0, 0, 0, false, 2, null),
        });
        Add(Vop2(0, "VMinU32", 13, Gen5Operand.Vector(13), Gen5Operand.Vector(12)));
        Add(Sop1(0, "SMovB64", 126, Gen5Operand.Scalar(106)));
        Add(ReadLane(0, 6, 13, 31));
        Add(ReadLane(0, 7, 13, 63));
        Add(Sop2(0, "SMinU32", 8, Gen5Operand.Scalar(6), Gen5Operand.Scalar(7)));
        // Red is the input, green is the shared reduction, both normalized by 32.
        Add(Vop1(0, "VCvtF32U32", 0, Gen5Operand.Vector(14)));
        Add(MoveVectorFromScalar(0, 1, 8));
        Add(Vop1(0, "VCvtF32U32", 1, Gen5Operand.Vector(1)));
        Add(Vop2(0, "VMulF32", 0, Gen5Operand.Vector(0), Operand(OneOver32)));
        Add(Vop2(0, "VMulF32", 1, Gen5Operand.Vector(1), Operand(OneOver32)));
        Add(MoveVector(0, 2, One));
        Add(MoveVector(0, 3, One));
        Add(new Gen5ShaderInstruction(0, Gen5ShaderEncoding.Exp, "Exp", [],
            [Gen5Operand.Vector(0), Gen5Operand.Vector(1), Gen5Operand.Vector(2), Gen5Operand.Vector(3)],
            [], new Gen5ExportControl(0, 15, false, true, true)));
        Add(EndProgram(0));

        var (plan, resources, layout) = Prepare(Program([.. instructions]), ShaderStage.Pixel, userDataCount: 0);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            PixelOutputs = [new Gen5PixelOutputBinding(0, 0, Gen5PixelOutputKind.Float)],
            EnableGraphicsSubgroupOperations = true,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        return shader.Spirv;
    }
}
