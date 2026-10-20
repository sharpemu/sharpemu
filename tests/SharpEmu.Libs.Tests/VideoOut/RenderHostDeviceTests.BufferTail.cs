// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Vulkan;
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
    // The hardware range-checks a buffer access by its first byte, so a dword load that starts
    // inside a guest range ending mid-word returns the whole word; the next word reads zero.
    [Theory]
    [InlineData(93)]
    [InlineData(94)]
    [InlineData(95)]
    [InlineData(96)]
    public void DwordBuffer_ReadsAWordThatStartsInsideTheRange(int bytes)
    {
        if (!Ready() || !_vulkan!.ShaderInt64) return;
        using var presenter = new PresenterUnderTest(_vulkan!);
        presenter.LoadRenderingCommands();
        presenter.SetField("_minStorageBufferOffsetAlignment", 256UL);
        var harness = presenter.Harness;
        var input = harness.MapBacked(0x10000, ReadWrite);
        var output = harness.MapBacked(0x10000, ReadWrite);
        var states = Enumerable.Range(1, bytes + 4).Select(value => (byte)value).ToArray();
        harness.Write(input, states);

        var guest = Program(
            Vop2(0, "VLshlrevB32", 2, Operand(2), Gen5Operand.Vector(0)),
            BufferAccess(4, "BufferLoadDword", 0, vectorData: 4, offsetEnabled: true, vectorAddress: 2),
            BufferAccess(12, "BufferStoreDword", 4, vectorData: 4, offsetEnabled: true, vectorAddress: 2),
            EndProgram(20));
        var plan = ShaderResourcePlan.Extract(guest, ShaderStage.Compute, 1, 0, 8);
        var resources = ResourceMaterializer.ApplyTo(plan, ResourceSpecialization.Default(plan.Info));
        var layout = BindingLayout.Allocate(resources.Info, BindingLayout.CollectUserDataRegisters(guest, 0, 8), false, false, false);
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 64 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var compiled, out var error), error);
        var program = new ShaderProgramInfo { Stage = ShaderStageKind.Compute, Hash = 0xB002, UserDataCount = 8, Resources = resources, Bindings = layout };
        var snapshot = new ResourceSnapshot
        {
            UserData = new uint[8],
            Buffers =
            [
                [(uint)input, (uint)(input >> 32), (uint)bytes, (20u << 12) | DescriptorConstants.IdentityDestinationSelect],
                [(uint)output, (uint)(output >> 32), 256, (20u << 12) | DescriptorConstants.IdentityDestinationSelect],
            ],
        };
        presenter.Run(() =>
        {
            var host = (IShaderPipelineHost)presenter.Instance;
            var shader = new ShaderProgram(program.Hash, host.CreateShaderModule(new VulkanCompiledGuestShader(compiled.Spirv), ShaderStage.Compute, program.Hash, program.Hash));
            var stage = new ShaderStageResources(program, snapshot);
            var pipeline = host.CreateComputePipeline(new ComputePipelineDescription
            {
                Input = new ComputeInputInfo { ThreadsX = 64, ThreadsY = 1, ThreadsZ = 1, Stage = stage },
                Program = shader, Stage = program,
            });
            using var preparation = presenter.RenderHost.BeginPreparation();
            _ = harness.Cache.ObtainBuffer(input, (ulong)bytes, false, requiresDeviceAddress: true);
            var prepared = presenter.RenderHost.PrepareBindings(stage);
            presenter.RenderHost.BindResources(prepared);
            presenter.RenderHost.CommitBindings(PipelineBindPoint.Compute, in pipeline, [prepared]);
            presenter.RenderHost.BindPipeline(PipelineBindPoint.Compute, in pipeline);
            presenter.RenderHost.Dispatch(1, 1, 1);
        });
        presenter.Run(() => presenter.InvokeMethod("FlushBatchedGuestCommands"));
        harness.Finish();
        presenter.Run(() =>
        {
            var host = (IShaderPipelineHost)presenter.Instance;
            for (var lane = 0; lane < 64; lane++)
            {
                Assert.True(host.TryReadGuestWord(output + (ulong)lane * 4, out var value));
                var expected = lane * 4 < bytes ? BitConverter.ToUInt32(states, lane * 4) : 0u;
                Assert.True(expected == value, $"bytes={bytes} lane={lane}: expected {expected:X8}, got {value:X8}");
            }
        });
        harness.Shutdown();
    }
}
