// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.GpuCommands;
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
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Images.ImageCacheTestSupport;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed unsafe partial class RenderHostDeviceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IndirectDispatch_ArgumentOwnerGrowthPreservesTheShaderOutput(bool registerArgumentsEarly)
    {
        if (!GatePrerequisites.Ready(_vulkan, shaderInt64: true)) return;
        using var presenter = new PresenterUnderTest(_vulkan!);
        using var fatal = new FatalScope();
        presenter.SetField("_minStorageBufferOffsetAlignment", 256UL);
        presenter.LoadRenderingCommands();
        Assert.True(((ICommandStreamHost)presenter.Instance).ResolvesIndirectDispatchOnGpu);
        var harness = presenter.Harness;
        var page = GuestBufferCache.CachingPageSize;
        var address = harness.MapBacked(4 * page, ReadWrite);
        // Arguments start in the last DWORD of the output's first cache page.
        // Their later 12-byte acquisition must grow its owner across the boundary.
        var arguments = address + page - sizeof(uint);
        harness.Write(address, new byte[(int)page]);
        harness.Write(address + sizeof(uint), BitConverter.GetBytes(0xABCD1234u));
        uint[] counts = [1, 1, 1];
        uint[] initialCounts = [0, 1, 1];
        harness.Write(arguments, MemoryMarshal.AsBytes(initialCounts.AsSpan()).ToArray());

        var decoded = Program(MoveVector(0, 4, 1), BufferAtomicAdd(8, 0), EndProgram(16));
        var plan = Extract(decoded, userDataCount: 4);
        uint[] userData = [(uint)address, (uint)(address >> 32), sizeof(uint), 0];
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs(userData), ref snapshot, ref specialization));
        var resources = ResourceMaterializer.ApplyTo(plan, specialization);
        var layout = BindingLayout.Allocate(resources.Info, [0, 1, 2, 3], false, false, false, 32);
        Assert.False(layout.UsesPushData);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            LocalSizeX = 1, LocalSizeY = 1, LocalSizeZ = 1,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        var stageInfo = new ShaderProgramInfo
        {
            Stage = ShaderStageKind.Compute, Hash = Hash, UserDataCount = 4,
            Resources = resources, Bindings = layout,
            Buffers = resources.Info.Buffers.Select(buffer => new BufferResourceInfo(
                buffer.Read, buffer.Written, buffer.Atomic, buffer.Formatted, buffer.Scalar,
                buffer.MaxByteExtent)).ToArray(),
        };
        GpuBuffer? original = null;
        var retained = false;
        try
        {
            var current = presenter.Run(() =>
            {
                // Establish and upload a persistent owner; the readonly stream path would
                // otherwise hide the ownership change this regression is exercising.
                original = harness.Cache.GetBuffer(harness.Cache.FindBuffer(address, page));
                Assert.Equal(address, original.CpuAddress);
                Assert.Equal(page, original.Size);
                harness.Cache.SynchronizeBuffersInRange(address, page);
                original.RetainForeignRead();
                retained = true;
                // A real GPU writer makes the argument DWORD dirty, independently of
                // tracker page size, and prevents the readonly CPU stream shortcut.
                var (producer, producerOffset) = harness.Cache.ObtainBuffer(arguments, sizeof(uint), isWritten: true);
                Assert.Same(original, producer);
                producer.Fill(producerOffset, sizeof(uint), 1);
                if (registerArgumentsEarly) _ = harness.Cache.FindBuffer(arguments, 12);

                var pipelineHost = (IShaderPipelineHost)presenter.Instance;
                var program = new ShaderProgram(1, pipelineHost.CreateShaderModule(
                    new VulkanCompiledGuestShader(shader.Spirv), ShaderStage.Compute, Hash, 1));
                var provider = new FixedProgramProvider(pipelineHost, 0)
                {
                    Compute = new ComputeProgram
                    {
                        Program = program,
                        Input = new ComputeInputInfo
                        {
                            ThreadsX = 1, ThreadsY = 1, ThreadsZ = 1,
                            Stage = new ShaderStageResources(stageInfo, snapshot),
                        },
                    },
                };
                var banks = RenderExecutorFixtures.Banks();
                banks.Shader.Compute.ThreadsX = 1;
                banks.Shader.Compute.ThreadsY = 1;
                banks.Shader.Compute.ThreadsZ = 1;
                var executor = new RenderExecutor(presenter.RenderHost, provider);
                // Exercise the production executor, including its argument acquisition,
                // descriptor commit and final indirect command. No copied call sequence.
                executor.Dispatch(1, banks, 1, 1, 1, 0x41u, arguments);
                var owner = harness.Cache.GetBuffer(harness.Cache.FindBuffer(address, sizeof(uint)));
                Assert.NotSame(original, owner);
                return owner;
            });
            var output = harness.ReadBack(current, current.Offset(address), 8);
            var oldOutput = harness.ReadBack(original!, original!.Offset(address), 4);
            var argumentBytes = harness.ReadBack(current, current.Offset(arguments), 12);
            Assert.Equal(counts, MemoryMarshal.Cast<byte, uint>(argumentBytes.AsSpan()).ToArray());
            Assert.Equal(0xABCD1234u, BitConverter.ToUInt32(output, 4));
            Assert.True(BitConverter.ToUInt32(output) == 1,
                $"The current owner lost the dispatch write: current={BitConverter.ToUInt32(output)}, old={BitConverter.ToUInt32(oldOutput)}.");
        }
        finally
        {
            presenter.Run(() =>
            {
                harness.Scheduler.Finish();
                if (retained) original!.ReleaseForeignRead();
            });
        }
        harness.Shutdown();
    }
}
