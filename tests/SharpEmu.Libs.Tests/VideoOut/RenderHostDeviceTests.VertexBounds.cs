// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Images.ImageCacheTestSupport;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed unsafe partial class RenderHostDeviceTests
{
    [Fact]
    public void FullscreenStrip_DoesNotRasterizeTheVertexBeyondTheGuestDescriptor()
    {
        if (!Ready()) return;
        using var presenter = new PresenterUnderTest(_vulkan);
        presenter.LoadRenderingCommands();
        var harness = presenter.Harness;
        var target = harness.MapBacked(0x10000, ReadWrite);
        var vertices = harness.MapBacked(0x10000, ReadWrite);
        var words = RegisterWords.Color(target, Size, Size);
        // PLAYROOM draws four vertices from a three-record, 24-byte-stride descriptor.
        // The following record belongs to other guest data, not the fullscreen triangle.
        harness.Write(vertices, Bytes(
            -1f, -1f, 0f, 1f, 0f, 0f,
             3f, -1f, 0f, 1f, 2f, 0f,
            -1f,  3f, 0f, 1f, 0f, 2f,
             0f,  0f, BitConverter.UInt32BitsToSingle(9952), 0f, 0f, 0f));

        var fetch = new Gen5ShaderInstruction(0, Gen5ShaderEncoding.Mubuf, "BufferLoadFormatXyzw", [], [], [],
            new Gen5BufferMemoryControl(4, 0, 0, 0, 0, true, false, false, false));
        var export = new Gen5ShaderInstruction(8, Gen5ShaderEncoding.Exp, "Exp", [],
            [Gen5Operand.Vector(0), Gen5Operand.Vector(1), Gen5Operand.Vector(2), Gen5Operand.Vector(3)], [],
            new Gen5ExportControl(12, 15, false, true, true));
        var parameter = new Gen5ShaderInstruction(16, Gen5ShaderEncoding.Exp, "Exp", [],
            [Gen5Operand.Vector(3), Gen5Operand.Vector(3), Gen5Operand.Vector(3), Gen5Operand.Vector(3)], [],
            new Gen5ExportControl(32, 15, false, true, true));
        var end = new Gen5ShaderInstruction(24, Gen5ShaderEncoding.Sopp, "SEndpgm", [], [], [], null);
        var plan = ShaderResourcePlan.Extract(new Gen5ShaderProgram(0, [fetch, export, parameter, end]), ShaderStage.Vertex, 1, 0, 0, new HashSet<uint> { 0 });
        var resources = ResourceMaterializer.ApplyTo(plan, ResourceSpecialization.Default(plan.Info));
        var request = new ShaderCompileRequest(plan, resources, BindingLayout.Allocate(resources.Info, [], false, false, false, 0))
        {
            EnableGraphicsSubgroupOperations = false,
            VertexInputs = [new ShaderVertexInput(0, 0, 4, 7, false, [])],
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);

        // Valid vertices interpolate white; an unculled out-of-bounds vertex can overwrite it.
        var fragment = new SpirvModuleBuilder();
        fragment.AddCapability(SpirvCapability.Shader);
        var voidType = fragment.TypeVoid();
        var vectorType = fragment.TypeVector(fragment.TypeFloat(32), 4);
        var input = fragment.AddGlobalVariable(fragment.TypePointer(SpirvStorageClass.Input, vectorType), SpirvStorageClass.Input);
        var output = fragment.AddGlobalVariable(fragment.TypePointer(SpirvStorageClass.Output, vectorType), SpirvStorageClass.Output);
        fragment.AddDecoration(input, SpirvDecoration.Location, 0);
        fragment.AddDecoration(output, SpirvDecoration.Location, 0);
        var main = fragment.BeginFunction(voidType, fragment.TypeFunction(voidType));
        fragment.AddLabel();
        fragment.AddStatement(SpirvOp.Store, output, fragment.AddInstruction(SpirvOp.Load, vectorType, input));
        fragment.AddStatement(SpirvOp.Return);
        fragment.EndFunction();
        fragment.AddEntryPoint(SpirvExecutionModel.Fragment, main, "main", [input, output]);
        fragment.AddExecutionMode(main, SpirvExecutionMode.OriginUpperLeft);
        var executor = new RenderExecutor(presenter.RenderHost,
            new FixedProgramProvider((IShaderPipelineHost)presenter.Instance, vertices,
                interpolationShader: fragment.Build(), vertexShader: shader.Spirv, vertexComponents: 4, vertexStride: 24));
        var banks = Banks(words);
        banks.UserConfig.PrimitiveType = 5;
        presenter.Run(() => executor.DrawAuto(1, banks,
            new DrawAutoArguments(0, 0, 4, 1, 0, 0, DrawOffsetSource.Packet)));
        presenter.Run(() => presenter.InvokeMethod("FlushBatchedGuestCommands"));
        harness.Finish();
        var pixels = harness.ReadImageBytes(TargetImage(presenter, words));
        Assert.All(Enumerable.Range(0, (int)(Size * Size)),
            index => Assert.Equal(uint.MaxValue, BitConverter.ToUInt32(pixels, index * 4)));
        harness.Shutdown();
    }
}
