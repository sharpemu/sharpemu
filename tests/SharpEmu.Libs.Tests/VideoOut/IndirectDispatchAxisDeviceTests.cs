// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Vulkan;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Silk.NET.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.Libs.Tests.VideoOut;

[Collection(SchedulingStateCollection.Name)]
public sealed unsafe class IndirectDispatchAxisDeviceTests(HeadlessVulkanFixture fixture)
    : IClassFixture<HeadlessVulkanFixture>
{
    private sealed class Provider(IShaderPipelineHost host) : IShaderPipelineProvider
    {
        private PipelineHandle? _pipeline;
        public required ComputeProgram Compute { get; init; }
        public GraphicsPrograms GetGraphicsPrograms(VertexStageRegisters vertex, PixelStageRegisters pixel,
            ShaderInterfaceRegisters shaderInterface, ContextRegisters context, ReadOnlySpan<ColorComponentMap> mapping,
            bool pixelActive, bool depthBound) => throw new NotSupportedException();
        public PipelineHandle CreateGraphicsPipeline(ReadOnlySpan<ColorTargetState> colors, in DepthAttachmentState depth,
            VertexInputInfo vertexInput, PixelInputInfo? pixelInput, ContextRegisters context, in RenderingState rendering,
            PrimitiveTopology topology, bool restart, bool disableBlending, ShaderProgram vertex, ShaderProgram pixel) =>
            throw new NotSupportedException();
        public ComputeProgram GetComputeProgram(ComputeStageRegisters compute, ShaderInterfaceRegisters shaderInterface,
            uint initiator, uint x, uint y, uint z) => Compute;
        public PipelineHandle CreateComputePipeline(ComputeInputInfo input, ShaderProgram program) =>
            _pipeline ??= host.CreateComputePipeline(new ComputePipelineDescription
                { Input = input, Program = program, Stage = input.Stage.Program! });
    }

    [Theory]
    [InlineData(false, 128u)]
    [InlineData(true, 128u)]
    [InlineData(false, 32u)]
    [InlineData(true, 32u)]
    public void IndirectGridPreservesLogicalGroupIdsAndGpuProducedArguments(bool gpuProduced, uint localZ)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        using var presenter = new PresenterUnderTest(vulkan!);
        presenter.LoadRenderingCommands();
        presenter.SetField("_minStorageBufferOffsetAlignment", 256UL);
        var harness = presenter.Harness;
        var protection = GuestPageProtection.Read | GuestPageProtection.Write;
        var resultAddress = harness.MapBacked(0x10000, protection);
        var argumentsAddress = harness.MapBacked(0x10000, protection);
        harness.Write(resultAddress, new byte[sizeof(uint)]);
        uint[] oldArguments = gpuProduced ? [1u, 1u, 1u] : [2u, 3u, 5u];
        harness.Write(argumentsAddress, MemoryMarshal.AsBytes<uint>(oldArguments).ToArray());
        var decoded = Program(Vop1(0, "VMovB32", 4, Gen5Operand.Scalar(6)), BufferAtomicAdd(8, 0), EndProgram(16));
        var plan = Extract(decoded, userDataCount: 4);
        uint[] userData = [(uint)resultAddress, (uint)(resultAddress >> 32), sizeof(uint), 0];
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs(userData), ref snapshot, ref specialization));
        var resources = ResourceMaterializer.ApplyTo(plan, specialization);
        var layout = BindingLayout.Allocate(resources.Info, [0, 1, 2, 3], false, false, false, 0);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            LocalSizeX = 1, LocalSizeY = 1, LocalSizeZ = localZ, WaveSize = 32,
            ComputeSystemRegisters = new(4, 5, 6, null),
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        var info = new ShaderProgramInfo
        {
            Stage = ShaderStageKind.Compute, Hash = Hash, UserDataCount = 4, Resources = resources, Bindings = layout,
            Buffers = [new BufferResourceInfo(true, true, false, false, false, 4, 0)],
        };
        presenter.Run(() =>
        {
            if (gpuProduced)
            {
                var target = harness.Cache.ObtainBuffer(argumentsAddress, 12, isWritten: true);
                uint* counts = stackalloc uint[3] { 2, 3, 5 };
                vulkan!.Vk.CmdUpdateBuffer(presenter.Command, target.Buffer.Handle, target.Offset, 12, counts);
                Span<byte> stale = stackalloc byte[12];
                Assert.True(harness.Memory.TryRead(argumentsAddress, stale));
                Assert.Equal(1u, BitConverter.ToUInt32(stale));
                Assert.True(harness.Cache.MayHaveGpuDirtyPages(argumentsAddress, 12));
            }
            var host = (IShaderPipelineHost)presenter.Instance;
            var module = host.CreateShaderModule(new VulkanCompiledGuestShader(shader.Spirv), ShaderStage.Compute, Hash, 1);
            var provider = new Provider(host)
            {
                Compute = new ComputeProgram
                {
                    Program = new ShaderProgram(1, module),
                    Input = new ComputeInputInfo
                    {
                        ThreadsX = 1, ThreadsY = 1, ThreadsZ = localZ, WaveSize = 32,
                        Stage = new ShaderStageResources(info, snapshot),
                    },
                },
            };
            new RenderExecutor(presenter.RenderHost, provider).Dispatch(1, RenderExecutorFixtures.Banks(), 1, 1, 1, 0x41, argumentsAddress);
            presenter.InvokeMethod("FlushBatchedGuestCommands");
        });
        presenter.Run(() => harness.Scheduler.Finish());
        var result = presenter.Run(() => harness.Cache.GetBuffer(harness.Cache.FindBuffer(resultAddress, sizeof(uint))));
        var actual = BitConverter.ToUInt32(harness.ReadBack(result, result.Offset(resultAddress), sizeof(uint)));
        // Invocations/group * X=2 * Y=3 * sum(Z=0..4).
        Assert.Equal(localZ * 60u, actual);
        harness.Shutdown();
        vulkan!.AssertNoValidationMessages();
    }
}
