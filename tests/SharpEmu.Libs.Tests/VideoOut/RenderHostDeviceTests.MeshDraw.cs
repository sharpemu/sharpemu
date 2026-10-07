// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Vulkan;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Silk.NET.Vulkan;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Images.ImageCacheTestSupport;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed unsafe partial class RenderHostDeviceTests
{
    [Theory]
    [InlineData(32u, false)]
    [InlineData(64u, false)]
    [InlineData(32u, true)]
    [InlineData(64u, true)]
    public void MeshDrawRendersGuestInputsAndRetainsUploadedIndices(uint waveSize, bool indexed)
        => RenderMeshDraw(waveSize, indexed);

    private void RenderMeshDraw(uint waveSize, bool indexed, ulong? traceShaderAddress = null)
    {
        if (!GatePrerequisites.Ready(_vulkan, shaderInt64: true)) return;
        if (!_vulkan.SupportsMeshShaders || !_vulkan.SupportsDynamicRendering || _vulkan.SubgroupSize != 32)
        {
            Assert.False(GatePrerequisites.DeviceRequired, "This test requires mesh rendering with 32-lane subgroups.");
            return;
        }
        using var presenter = new PresenterUnderTest(_vulkan);
        presenter.LoadRenderingCommands();
        presenter.SetField("_supportsMeshShader", true);
        var command = (IntPtr)(void*)_vulkan.Vk.GetDeviceProcAddr(_vulkan.Device, "vkCmdDrawMeshTasksEXT");
        Assert.NotEqual(IntPtr.Zero, command);
        presenter.SetField("_cmdDrawMeshTasks", command);
        var harness = presenter.Harness;
        var target = harness.MapBacked(0x10000, ReadWrite);
        var words = RegisterWords.Color(target, Size, Size);
        var banks = Banks(words);
        if (traceShaderAddress is { } shaderAddress) banks.Shader.Vertex.ExportAddress = shaderAddress;
        banks.Context.ShaderStages = 0x20;
        var provider = new MeshFrameProgramProvider((IShaderPipelineHost)presenter.Instance, waveSize);
        var executor = new RenderExecutor(presenter.RenderHost, provider);
        if (indexed)
        {
            var indices = harness.MapBacked(0x10000, ReadWrite);
            harness.Write(indices, [1, 0, 2, 0, 3, 0]);
            presenter.Run(() => executor.DrawIndexed(1, banks,
                new DrawIndexedArguments(0, 0, 3, indices, 0, 1, -1, 0, DrawOffsetSource.Packet)));
            Assert.Equal(0UL, presenter.HostBuffers.CachedBytes);
        }
        else
        {
            presenter.Run(() => executor.DrawAuto(1, banks, Draw()));
        }
        presenter.Run(() => presenter.InvokeMethod("FlushBatchedGuestCommands"));
        harness.Finish();
        presenter.Run(() => presenter.InvokeMethod("WaitForAllGuestSubmissions"));
        if (indexed) Assert.Equal(16UL, presenter.HostBuffers.CachedBytes);
        var pixels = harness.ReadImageBytes(TargetImage(presenter, words));
        Assert.All(Enumerable.Range(0, (int)(Size * Size)), index =>
            Assert.Equal(Red, BitConverter.ToUInt32(pixels, index * 4)));
        harness.Shutdown();
    }

    private sealed class MeshFrameProgramProvider(IShaderPipelineHost host, uint waveSize) : IShaderPipelineProvider
    {
        public bool MeshShadersSupported => true;

        public GraphicsPrograms GetMeshGraphicsPrograms(VertexStageRegisters vertex, PixelStageRegisters pixel,
            ShaderInterfaceRegisters shaderInterface, ContextRegisters context, UserConfigRegisters userConfig,
            ReadOnlySpan<ColorComponentMap> targetExportMapping, bool pixelActive, bool depthBound)
        {
            var instructions = new List<Gen5ShaderInstruction>();
            void Add(Gen5ShaderInstruction instruction) => instructions.Add(instruction with { Pc = (uint)instructions.Count * 8 });
            Add(Vopc(0, "VCmpEqU32", Operand(1), 5));
            Add(Vop2(0, "VCndmaskB32", 10, Operand(BitConverter.SingleToUInt32Bits(-1)), Operand(BitConverter.SingleToUInt32Bits(3))));
            Add(Vopc(0, "VCmpEqU32", Operand(2), 5));
            Add(Vop2(0, "VCndmaskB32", 11, Operand(BitConverter.SingleToUInt32Bits(-1)), Operand(BitConverter.SingleToUInt32Bits(3))));
            Add(MoveVector(0, 12, 0));
            Add(MoveVector(0, 13, BitConverter.SingleToUInt32Bits(1)));
            Add(new Gen5ShaderInstruction(0, Gen5ShaderEncoding.Exp, "Exp", [0, 0],
                [Gen5Operand.Vector(10), Gen5Operand.Vector(11), Gen5Operand.Vector(12), Gen5Operand.Vector(13)], [],
                new Gen5ExportControl(12, 15, false, false, false)));
            Add(MoveVector(0, 14, (1u << 10) | (2u << 20)));
            Add(new Gen5ShaderInstruction(0, Gen5ShaderEncoding.Exp, "Exp", [0, 0],
                [Gen5Operand.Vector(14), Gen5Operand.Vector(14), Gen5Operand.Vector(14), Gen5Operand.Vector(14)], [],
                new Gen5ExportControl(20, 1, false, true, false)));
            Add(MoveScalar(0, 124, 3 | (1u << 12)));
            Add(new Gen5ShaderInstruction(0, Gen5ShaderEncoding.Sopp, "SSendmsg", [0xBF900009], [], [], null));
            Add(EndProgram(0));
            var program = Program([.. instructions]);
            var plan = ShaderResourcePlan.Extract(program, ShaderStage.Mesh, 1, 0, 0, waveSize: waveSize);
            var resources = ResourceMaterializer.ApplyTo(plan, ResourceSpecialization.Default(plan.Info));
            var layout = BindingLayout.Allocate(resources.Info, [], false, false, false, 0, usesMeshDrawParameters: true);
            var request = new ShaderCompileRequest(plan, resources, layout)
            {
                WaveSize = waveSize, LocalSizeX = waveSize,
                Mesh = new MeshShaderConfiguration(3, 1, 0, 1, 3, 0, false, 32),
            };
            Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var compiled, out var error), error);
            var meshProgram = new ShaderProgram(1, host.CreateShaderModule(
                new VulkanCompiledGuestShader(compiled.Spirv), ShaderStage.Mesh, 1, 1));
            var pixelProgram = new ShaderProgram(2, host.CreateShaderModule(new VulkanCompiledGuestShader(
                SpirvFixedShaders.CreateSolidFragment(1, 0, 0, 1)), ShaderStage.Pixel, 2, 2));
            var meshInfo = new ShaderProgramInfo
            {
                Stage = ShaderStageKind.Mesh, Hash = 1, Resources = resources, Bindings = layout,
            };
            return new GraphicsPrograms
            {
                Vertex = meshProgram, Pixel = pixelProgram,
                VertexInput = new VertexInputInfo { Stage = new ShaderStageResources(meshInfo, new() { UserData = [] }) },
                PixelInput = new PixelInputInfo
                {
                    Stage = new ShaderStageResources(FixedProgramProvider.EmptyProgram(ShaderStageKind.Pixel, 2), new() { UserData = [] }),
                },
                MeshInput = new MeshDrawConfiguration
                {
                    Geometry = new GuestGeometryConfiguration
                    {
                        WaveSize = waveSize, ThreadsPerGroup = waveSize, InputPrimitiveCountPerWorkgroup = 1,
                    },
                    Execution = new MeshExecutionLimits { MaxGroupCountX = 65535, MaxGroupCountY = 65535, MaxGroupTotalCount = 65535 },
                },
            };
        }

        public GraphicsPrograms GetGraphicsPrograms(VertexStageRegisters vertex, PixelStageRegisters pixel,
            ShaderInterfaceRegisters shaderInterface, ContextRegisters context, ReadOnlySpan<ColorComponentMap> targetExportMapping,
            bool pixelActive, bool depthBound) => throw new InvalidOperationException("The test requires mesh programs.");

        public PipelineHandle CreateGraphicsPipeline(ReadOnlySpan<ColorTargetState> colors, in DepthAttachmentState depth,
            VertexInputInfo vertexInput, PixelInputInfo? pixelInput, ContextRegisters context, in RenderingState rendering,
            PrimitiveTopology topology, bool primitiveRestartEnabled, bool disableBlending, ShaderProgram vertexProgram,
            ShaderProgram pixelProgram) => host.CreateGraphicsPipeline(ShaderPipelineCache.BuildGraphicsDescription(
                colors, in depth, vertexInput, pixelInput, context, in rendering, topology, primitiveRestartEnabled,
                disableBlending, vertexProgram, pixelProgram, host.NoAttachmentSampleCounts));

        public ComputeProgram GetComputeProgram(ComputeStageRegisters compute, ShaderInterfaceRegisters shaderInterface,
            uint dispatchInitiator, uint dimensionX, uint dimensionY, uint dimensionZ) => throw new NotSupportedException();

        public PipelineHandle CreateComputePipeline(ComputeInputInfo input, ShaderProgram program) => throw new NotSupportedException();
    }
}
