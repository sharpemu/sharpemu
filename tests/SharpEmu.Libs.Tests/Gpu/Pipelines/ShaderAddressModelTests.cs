// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

// One code image at two addresses compiles once; each draw reads the data next to its own address, host-side and in-shader.
[Collection(SchedulingStateCollection.Name)]
public sealed class ShaderAddressModelTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    private const ulong PageSize = Gen5SpirvTranslator.DeviceAddressPageSize;
    private const ulong CodeA = PipelineTestGuest.MemoryBase + 0x1_0000;
    private const ulong CodeB = PipelineTestGuest.MemoryBase + 0x2_0000;
    private const ulong HeaderA = PipelineTestGuest.MemoryBase + 0x8000;
    private const ulong HeaderB = PipelineTestGuest.MemoryBase + 0x8100;
    private const ulong ScalarConstantOffset = 0x40;
    private const ulong FlatConstantOffset = 0x44;
    private const uint ResultRegister = 4;
    private const uint ResultBytes = 256;
    private const ulong TableEntries = (CodeB >> Gen5SpirvTranslator.DeviceAddressPageBits) + 16;

    // s_getpc_b64 s[0:1]; s8 = [pc+0x3C] (host-side); v1 = [pc+0x40] (in-shader); both stored to the result buffer.
    private static readonly uint[] Program =
    [
        0xBE801F00,             // s_getpc_b64 s[0:1]
        0xF4000200, 0xFA00003C, // s_load_dword s8, s[0:1], 0x3C
        0x7E040200,             // v_mov_b32 v2, s0
        0x7E060201,             // v_mov_b32 v3, s1
        0xDC300040, 0x01000002, // flat_load_dword v1, v[2:3] offset:0x40
        0xBF8C0000,             // s_waitcnt 0
        0x7E000208,             // v_mov_b32 v0, s8
        0xE0700000, 0x80010000, // buffer_store_dword v0, off, s[4:7], 0
        0xE0700004, 0x80010100, // buffer_store_dword v1, off, s[4:7], 0 offset:4
        0xBF810000,             // s_endpgm
    ];

    private sealed class Run : IDisposable
    {
        private readonly ImageTestHarness _harness;
        private readonly LayoutComputeRunner _runner;
        private readonly GpuBuffer _result;
        private readonly GpuBuffer _fault;
        private readonly GpuBuffer _page;
        private readonly GpuBuffer _pageTable;

        public Run(HeadlessVulkan vulkan, PipelineTestGuest guest, FakeCompiledShader shader, ulong codeAddress)
        {
            _harness = new ImageTestHarness(vulkan);
            _runner = new LayoutComputeRunner(_harness, shader.Request, shader.Spirv);
            _result = _runner.CreateBuffer(ResultBytes);
            _fault = _runner.CreateBuffer(TableEntries / 8);
            var page = new byte[PageSize];
            Assert.True(guest.Memory.TryRead(codeAddress, page));
            _page = _runner.CreateBuffer(page, PageSize);
            _pageTable = _runner.CreatePageTable(TableEntries, [(codeAddress, _page, 0ul)]);
        }

        public (uint Scalar, uint Flat) Dispatch(ShaderStageResources stage, ulong codeAddress)
        {
            var registers = new uint[256];
            registers[ResultRegister + 2] = ResultBytes;
            var bound = new Dictionary<DescriptorBindingKind, GpuBuffer[]>
            {
                [DescriptorBindingKind.Buffers] = [_result],
                [DescriptorBindingKind.DeviceAddressPageTable] = [_pageTable],
                [DescriptorBindingKind.FaultBuffer] = [_fault],
            };
            var table = stage.Resources.FlattenedResourceTable.Length == 0 ? null : stage.Resources.FlattenedResourceTable;
            _harness.Run(() => _runner.Dispatch(registers, bound, 1, flattenedTable: table, shaderBase: codeAddress));
            _harness.Finish();
            var bytes = _runner.ReadBack(_result, 0, 8);
            var faults = _runner.ReadBack(_fault, 0, _fault.Size);
            Assert.All(faults, value => Assert.Equal(0, value));
            return (BinaryPrimitives.ReadUInt32LittleEndian(bytes), BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)));
        }

        public void Dispose()
        {
            _runner.Dispose();
            _harness.Dispose();
        }
    }

    private static uint[] UserData() =>
    [
        0, 0, 0, 0,
        0, 0, ResultBytes, 0,
    ];

    private static byte[] Compile(ShaderCompileRequest request)
    {
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        return shader.Spirv;
    }

    [Fact]
    public void SameCodeAtTwoAddresses_ReadsEachAddressesAdjacentData()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true))
        {
            return;
        }

        using var fatal = new FatalScope();
        var guest = new PipelineTestGuest(Compile);
        guest.RegisterProgram(CodeA, HeaderA, Program);
        guest.RegisterProgram(CodeB, HeaderB, Program);
        guest.WriteWords(CodeA + ScalarConstantOffset, 0xA0A0_0001, 0xA0A0_0002);
        guest.WriteWords(CodeB + ScalarConstantOffset, 0xB0B0_0001, 0xB0B0_0002);

        var cursor = 0u;
        var first = guest.Programs.GetOrCompile(guest.Source(CodeA, ShaderStage.Compute, UserData()), PipelineTestGuest.ComputeOptions(threadsX: 1), ref cursor, out var stageA);
        cursor = 0;
        var second = guest.Programs.GetOrCompile(guest.Source(CodeB, ShaderStage.Compute, UserData()), PipelineTestGuest.ComputeOptions(threadsX: 1), ref cursor, out var stageB);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, guest.Compiler.Compilations);
        Assert.Single(guest.Host.Modules);
        var shader = Assert.Single(guest.Compiler.Shaders);
        Assert.True(shader.Request.Resources.Info.UsesDeviceAddresses);
        Assert.True(shader.Request.Bindings.UsesShaderBase);
        Assert.Equal(CodeA, stageA.ShaderBase);
        Assert.Equal(CodeB, stageB.ShaderBase);

        using (var run = new Run(vulkan, guest, shader, CodeA))
        {
            Assert.Equal((0xA0A0_0001u, 0xA0A0_0002u), run.Dispatch(stageA, CodeA));
        }

        using (var run = new Run(vulkan, guest, shader, CodeB))
        {
            Assert.Equal((0xB0B0_0001u, 0xB0B0_0002u), run.Dispatch(stageB, CodeB));
        }

        vulkan.AssertNoValidationMessages();
    }
}
