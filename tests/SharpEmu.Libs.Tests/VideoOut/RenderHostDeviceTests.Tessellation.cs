// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.Vulkan;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Silk.NET.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;
using static SharpEmu.Libs.Tests.Gpu.Images.ImageCacheTestSupport;
using ResourceSnapshot = SharpEmu.ShaderCompiler.Resources.ResourceSnapshot;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed unsafe partial class RenderHostDeviceTests
{
    [Theory]
    [InlineData(1f, true)]
    [InlineData(0f, false)]
    public void Tessellation_FactorsGenerateOrCullTheDomainPatch(float firstOuter, bool visible) =>
        RenderTessellationPatch(firstOuter, visible, false);

    [Theory]
    [InlineData(1f, true)]
    [InlineData(0f, false)]
    public void Tessellation_MergedHullWritesFactorsConsumedByTheNativeDraw(float firstOuter, bool visible) =>
        RenderTessellationPatch(firstOuter, visible, true);

    [Fact]
    public void Tessellation_SuccessiveBatchesReuseTheFactorRingAfterThePreviousDraw() =>
        RenderTessellationPatch(0, true, true, patchCount: 2);

    // With the guest's off-chip configuration, hull groups that fit run in one dispatch, each
    // writing its factors to its own run of the ring, and one draw consumes them all.
    [Fact]
    public void Tessellation_GroupsInFlightWriteTheirOwnFactorRuns()
    {
        TessellationOffchip.Configure(0xFF);
        try
        {
            RenderTessellationPatch(0, true, true, patchCount: 2, ringPatches: 2);
        }
        finally
        {
            TessellationOffchip.Reset();
        }
    }

    [Fact]
    public void TessellationOffchip_DecodesTheBufferCountAndGranularity()
    {
        Assert.Equal((256u, 32u * 1024), TessellationOffchip.Decode(0xFF));
        Assert.Equal((1u, 64u * 1024), TessellationOffchip.Decode(1u << 10));
        Assert.Equal((1024u, 256u * 1024), TessellationOffchip.Decode(0x3FFu | (3u << 10)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Tessellation_VertexOffsetUsesPacketRegistersOrIndirectArguments(bool indexed) =>
        RenderTessellationPatch(0, false, true, verifyOffset: true, indexed: indexed);

    private void RenderTessellationPatch(float firstOuter, bool visible, bool computeFactors, uint patchCount = 1,
        bool verifyOffset = false, bool indexed = false, uint ringPatches = 1)
    {
        if (!Ready()) return;
        using var presenter = new PresenterUnderTest(_vulkan!);
        _vulkan!.Vk.GetPhysicalDeviceProperties(_vulkan.Physical, out var properties);
        presenter.SetField("_minStorageBufferOffsetAlignment", properties.Limits.MinStorageBufferOffsetAlignment);
        presenter.LoadRenderingCommands();
        var harness = presenter.Harness;
        using var factors = new GpuBuffer(_vulkan!.DeviceInfo, harness.Scheduler, GpuBufferUsage.Upload,
            0, GpuBuffer.AllFlags | BufferUsageFlags.ShaderDeviceAddressBit, 24);
        Span<float> levels = [firstOuter, 1f, 1f, 1f, 1f, 1f];
        factors.Write(0, System.Runtime.InteropServices.MemoryMarshal.AsBytes(levels));
        var target = harness.MapBacked(0x10000, ReadWrite);
        var guestFactors = computeFactors ? harness.MapBacked(0x10000, ReadWrite) : 0;
        var words = RegisterWords.Color(target, Size, Size);
        var executor = new RenderExecutor(presenter.RenderHost,
            new TessellationProgramProvider((IShaderPipelineHost)presenter.Instance, factors.DeviceAddress, guestFactors, firstOuter,
                patchCount > 1 || verifyOffset, verifyOffset ? 2u : 0u, 24 * ringPatches));
        GuestGpuMemoryHook.Attach(harness.Gpu);
        try
        {
            var banks = Banks(words);
            if (computeFactors)
            {
                banks.Context.ShaderStages = 1u | (1u << 2) | (1u << 3) | (1u << 8) | (1u << 13) | (1u << 25);
                banks.Context.ShaderInterface.MinTessellationLevel = BitConverter.SingleToUInt32Bits(1);
                banks.Context.ShaderInterface.MaxTessellationLevel = BitConverter.SingleToUInt32Bits(9);
                // The guest registers match the provider: quads, equal spacing, counter-clockwise
                // triangles, one patch of four input and four output control points per group.
                banks.Context.ShaderInterface.TessellationFactorParameter = 2u | (0u << 2) | (3u << 5);
                banks.Context.ShaderInterface.LocalHullConfiguration = 1u | (4u << 8) | (4u << 14);
            }
            if (indexed)
            {
                banks.UserConfig.IndexOffset = uint.MaxValue;
                var indexAddress = harness.MapBacked(0x10000, ReadWrite);
                harness.Write(indexAddress, Bytes((ushort)0, (ushort)1, (ushort)2, (ushort)3));
                presenter.Run(() => executor.DrawIndexed(1, banks,
                    new(0, 0, 4, indexAddress, (uint)GuestIndexType.Index16, 1, 1, 0, DrawOffsetSource.Packet)));
            }
            else
            {
                if (verifyOffset) banks.UserConfig.IndexOffset = 1;
                presenter.Run(() => executor.DrawAuto(1, banks,
                    new(0, 0, 4 * patchCount, 1, 0, 0,
                        verifyOffset ? DrawOffsetSource.IndirectArguments : DrawOffsetSource.Packet)));
            }
            presenter.Run(() => presenter.InvokeMethod("FlushBatchedGuestCommands"));
            harness.Finish();
            var image = harness.ReadImageBytes(TargetImage(presenter, words));
            Assert.Equal(visible ? Red : 0u, Pixel(image, Size / 2, Size / 2));
            Assert.Equal(visible ? Red : 0u, Pixel(image, 2, 2));
            if (ringPatches > 1)
            {
                // Patch 0 wrote its zero factor at the start of the ring; patch 1 wrote its
                // own factors one patch further instead of over patch 0's.
                var host = (IShaderPipelineHost)presenter.Instance;
                uint Factor(ulong address) => presenter.Run(() => host.TryReadGuestWord(address, out var word) ? word : uint.MaxValue);
                Assert.Equal(0u, Factor(guestFactors));
                Assert.Equal(BitConverter.SingleToUInt32Bits(1), Factor(guestFactors + 24));
            }
        }
        finally { GuestGpuMemoryHook.Attach(null); }
        harness.Shutdown();
    }

    private sealed class TessellationProgramProvider(IShaderPipelineHost host, ulong factorAddress, ulong guestFactors, float firstOuter,
        bool useAbsolutePatchId, uint factorSourceRegister, uint factorRingBytes) : IShaderPipelineProvider
    {
        private ShaderProgram _vertex, _control, _domain, _pixel;
        private ShaderProgramInfo _domainInfo = null!;
        private ComputeProgram? _hull;
        private static readonly ShaderProgramInfo PixelInfo = FixedProgramProvider.EmptyProgram(ShaderStageKind.Pixel, 4);

        public GraphicsPrograms GetGraphicsPrograms(VertexStageRegisters vertex, PixelStageRegisters pixel,
            ShaderInterfaceRegisters shaderInterface, ContextRegisters context, ReadOnlySpan<ColorComponentMap> mappings,
            bool pixelActive, bool depthBound)
        {
            if (!_domain.IsValid)
            {
                var program = Program(
                    Vop2(0, "VMulF32", 10, Operand(BitConverter.SingleToUInt32Bits(2f)), Gen5Operand.Vector(5)),
                    Vop2(4, "VAddF32", 10, Operand(BitConverter.SingleToUInt32Bits(-1f)), Gen5Operand.Vector(10)),
                    Vop2(8, "VMulF32", 11, Operand(BitConverter.SingleToUInt32Bits(2f)), Gen5Operand.Vector(6)),
                    Vop2(12, "VAddF32", 11, Operand(BitConverter.SingleToUInt32Bits(-1f)), Gen5Operand.Vector(11)),
                    Vop1(16, "VMovB32", 12, Operand(0)),
                    Vop1(20, "VMovB32", 13, Operand(BitConverter.SingleToUInt32Bits(1f))),
                    new(24, Gen5ShaderEncoding.Exp, "Exp", [0u, 0u],
                        [Gen5Operand.Vector(10), Gen5Operand.Vector(11), Gen5Operand.Vector(12), Gen5Operand.Vector(13)], [],
                        new Gen5ExportControl(12, 15, false, true, false)), EndProgram(32));
                var plan = ShaderResourcePlan.Extract(program, ShaderStage.TessellationEvaluation, Hash, 8, 0, waveSize: 32);
                var resources = ResourceMaterializer.ApplyTo(plan, ResourceSpecialization.Default(plan.Info));
                var layout = BindingLayout.Allocate(resources.Info, [], false, false, false, usesTessellationData: true);
                var request = new ShaderCompileRequest(plan, resources, layout)
                { Tessellation = new(Gen5TessellationDomain.Quads, Gen5TessellationSpacing.Equal, false, false) };
                Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
                _domainInfo = new() { Stage = ShaderStageKind.TessellationEvaluation, Hash = Hash, Resources = resources, Bindings = layout };
                _vertex = new(1, host.CreateShaderModule(new VulkanCompiledGuestShader(SpirvFixedShaders.CreateFullscreenVertex(0)), ShaderStage.Vertex, 1, 1));
                _control = new(2, host.CreateShaderModule(new VulkanCompiledGuestShader(Gen5TessellationBridge.CompileControl(layout, Gen5TessellationDomain.Quads).Spirv), ShaderStage.TessellationEvaluation, 2, 2));
                _domain = new(3, host.CreateShaderModule(new VulkanCompiledGuestShader(shader.Spirv), ShaderStage.TessellationEvaluation, Hash, 3));
                _pixel = new(4, host.CreateShaderModule(new VulkanCompiledGuestShader(SpirvFixedShaders.CreateSolidFragment(1, 0, 0, 1)), ShaderStage.Pixel, 4, 4));
                if (guestFactors != 0) _hull = CreateHull();
            }
            var data = new uint[Gen5TessellationData.DwordCount];
            data[Gen5TessellationData.PatchCount] = 1;
            data[Gen5TessellationData.FactorAddress] = (uint)factorAddress;
            data[Gen5TessellationData.FactorAddress + 1] = (uint)(factorAddress >> 32);
            data[Gen5TessellationData.FactorBytes] = 24;
            data[Gen5TessellationData.MinimumLevel] = BitConverter.SingleToUInt32Bits(1);
            data[Gen5TessellationData.MaximumLevel] = BitConverter.SingleToUInt32Bits(9);
            return new()
            {
                Vertex = _vertex, Pixel = _pixel,
                Tessellation = _hull is null ? null : new(new(Gen5TessellationDomain.Quads, Gen5TessellationSpacing.Equal, false, false), new(4, 4, 1, 0), _hull),
                VertexInput = new() { Tessellation = new(_control, _domain, 4), Stage = new(_domainInfo, new ResourceSnapshot()) { TessellationData = data } },
                PixelInput = new() { Stage = new(PixelInfo, new ResourceSnapshot()) },
            };
        }

        private ComputeProgram CreateHull()
        {
            var instructions = new List<Gen5ShaderInstruction>
            {
                Vop2(0, "VLshlrevB32", 4, Operand(2), Gen5Operand.Vector(3)),
                Vop1(4, "VMovB32", 10, Gen5Operand.Scalar(20)),
                DataShare(8, "DsWriteB32", false, [Gen5Operand.Vector(4), Gen5Operand.Vector(10)], []),
                new(16, Gen5ShaderEncoding.Sopp, "SBarrier", [0u], [], [], null),
                DataShare(20, "DsReadB32", false, [Gen5Operand.Vector(4)], [10]),
                Vopc(28, "VCmpxEqU32", Operand(0), 3),
            };
            for (uint index = 1; index < 6; index++)
                instructions.Add(Vop1(28 + index * 4, "VMovB32", 10 + index, Operand(BitConverter.SingleToUInt32Bits(1))));
            if (useAbsolutePatchId) instructions.Add(Vop1(52, "VCvtF32U32", 10, Gen5Operand.Vector(factorSourceRegister)));
            var storePc = useAbsolutePatchId ? 56u : 52u;
            var first = BufferAccess(storePc, "BufferStoreDwordx4", 24, dwords: 4, vectorData: 10);
            var last = BufferAccess(storePc + 8, "BufferStoreDwordx2", 24, dwords: 2, vectorData: 14, offset: 16);
            instructions.Add(first with { Sources = [..first.Sources.Take(2), Gen5Operand.Scalar(4)] });
            instructions.Add(last with { Sources = [..last.Sources.Take(2), Gen5Operand.Scalar(4)] });
            instructions.Add(EndProgram(storePc + 16));
            var program = new Gen5ShaderProgram(0x1000, instructions);
            var plan = ShaderResourcePlan.Extract(program, ShaderStage.Compute, Hash, 8, 20, waveSize: 64);
            var resources = ResourceMaterializer.ApplyTo(plan, ResourceSpecialization.Default(plan.Info));
            var layout = BindingLayout.Allocate(resources.Info, BindingLayout.CollectUserDataRegisters(program, 8, 20), false, false, false, usesTessellationData: true);
            var request = new ShaderCompileRequest(plan, resources, layout)
            { LocalSizeX = 64, LocalDataShareDwords = 64, WaveSize = 64, CooperativeWave64Workgroup = true, TessellationHull = new(4, 4, 1, 0) };
            Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
            var info = new ShaderProgramInfo
            {
                Stage = ShaderStageKind.Compute, Hash = Hash, UserDataBase = 8, UserDataCount = 20,
                Bindings = layout, Resources = resources, TessellationFactorBuffer = (int)Gen5TessellationLowering.FindFactorBuffer(plan, 0),
                Buffers = [new(false, true, false, false, false, 24, 0)],
            };
            var data = new uint[20];
            data[12] = BitConverter.SingleToUInt32Bits(firstOuter);
            data[16] = (uint)guestFactors; data[17] = (uint)(guestFactors >> 32); data[18] = factorRingBytes;
            var input = new ComputeInputInfo
            {
                ThreadsX = 64, ThreadsY = 1, ThreadsZ = 1, WaveSize = 64,
                Stage = new(info, new ResourceSnapshot { UserData = data, Buffers = [[(uint)guestFactors, (uint)(guestFactors >> 32), factorRingBytes, 0]] }),
            };
            return new() { Input = input, Program = new(5, host.CreateShaderModule(new VulkanCompiledGuestShader(shader.Spirv), ShaderStage.Compute, Hash, 5)) };
        }

        public PipelineHandle CreateGraphicsPipeline(ReadOnlySpan<ColorTargetState> colors, in DepthAttachmentState depth,
            VertexInputInfo vertexInput, PixelInputInfo? pixelInput, ContextRegisters context, in RenderingState rendering,
            PrimitiveTopology topology, bool restart, bool disableBlending, ShaderProgram vertexProgram, ShaderProgram pixelProgram)
        {
            var basic = ShaderPipelineCache.BuildGraphicsDescription(colors, depth, vertexInput, pixelInput, context, rendering,
                PrimitiveTopology.PatchList, false, disableBlending, vertexProgram, pixelProgram, host.NoAttachmentSampleCounts);
            return host.CreateGraphicsPipeline(new()
            {
                Rendering = basic.Rendering, VertexInput = basic.VertexInput, VertexInfo = basic.VertexInfo,
                VertexProgram = basic.VertexProgram, VertexStage = basic.VertexStage, PixelInfo = basic.PixelInfo,
                PixelProgram = basic.PixelProgram, PixelStage = basic.PixelStage, StaticParameters = basic.StaticParameters,
                Tessellation = new(_control, _domain, 4),
            });
        }
        public ComputeProgram GetComputeProgram(ComputeStageRegisters compute, ShaderInterfaceRegisters shaderInterface,
            uint initiator, uint x, uint y, uint z) => throw new NotSupportedException();
        public PipelineHandle CreateComputePipeline(ComputeInputInfo input, ShaderProgram program) =>
            host.CreateComputePipeline(new() { Input = input, Program = program, Stage = input.Stage.Program! });
    }
}
