// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.Libs.Tests.Gpu.Vulkan;

public sealed class WaveLaneTransferDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    [Theory]
    [InlineData(32u, 31u, false)]
    [InlineData(32u, 63u, true)]
    [InlineData(64u, 31u, false)]
    [InlineData(64u, 63u, false)]
    [InlineData(64u, 95u, true)]
    [InlineData(64u, 127u, true)]
    public void ReadLane_UsesGuestWaveIndexAndIgnoresExecutionMask(uint waveSize, uint selector, bool disableExecution)
    {
        Run(waveSize, selector, disableExecution, writeLane: false);
    }

    [Theory]
    [InlineData(32u, 63u)]
    [InlineData(64u, 31u)]
    [InlineData(64u, 63u)]
    [InlineData(64u, 127u)]
    public void WriteLane_ChangesOnlySelectedGuestLaneWithExecutionDisabled(uint waveSize, uint selector)
    {
        Run(waveSize, selector, disableExecution: true, writeLane: true);
    }

    private void Run(uint waveSize, uint selector, bool disableExecution, bool writeLane)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        var instructions = new List<Gen5ShaderInstruction>();
        uint programCounter = 0;
        void Add(Gen5ShaderInstruction instruction)
        {
            instructions.Add(instruction with { Pc = programCounter });
            programCounter += 8;
        }
        Add(Vop2(0, "VLshlrevB32", 2, Operand(3), Gen5Operand.Vector(1)));
        Add(Vop2(0, "VAddI32", 2, Gen5Operand.Vector(0), Gen5Operand.Vector(2)));
        Add(Vop2(0, "VAddI32", 3, Operand(1000), Gen5Operand.Vector(2)));
        Add(MoveScalar(0, 20, selector));
        Add(MoveScalar(0, 21, 0x12345678));
        if (disableExecution)
        {
            Add(MoveScalar(0, 126, 0));
            Add(MoveScalar(0, 127, 0));
        }
        if (writeLane)
            Add(WriteLane(0, 3, 21, 0) with { Sources = [Gen5Operand.Scalar(21), Gen5Operand.Scalar(20)] });
        else
            Add(ReadLane(0, 22, 3, 0) with { Sources = [Gen5Operand.Vector(3), Gen5Operand.Scalar(20)] });
        Add(MoveScalar(0, 126, uint.MaxValue));
        Add(MoveScalar(0, 127, uint.MaxValue));
        if (!writeLane) Add(MoveVectorFromScalar(0, 3, 22));
        Add(Vop2(0, "VLshlrevB32", 5, Operand(2), Gen5Operand.Vector(2)));
        Add(BufferAccess(0, "BufferStoreDword", 4, vectorData: 3, offsetEnabled: true, vectorAddress: 5));
        Add(EndProgram(0));
        var (plan, resources, layout) = Prepare(Program([.. instructions]));
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            LocalSizeX = 8, LocalSizeY = waveSize / 8, ThreadCountX = 8, ThreadCountY = waveSize / 8,
            WaveSize = waveSize,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var output = runner.CreateBuffer(256);
        var registers = new uint[256];
        registers[6] = 256;
        harness.Run(() => runner.Dispatch(registers,
            new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [output] }, 1));
        var bytes = runner.ReadBack(output, 0, 256);
        var selectedLane = selector % waveSize;
        for (uint lane = 0; lane < waveSize; lane++)
        {
            var expected = writeLane ? (lane == selectedLane ? 0x12345678u : 1000 + lane) : 1000 + selectedLane;
            Assert.Equal(expected, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)lane * 4)));
        }
        harness.AssertNoValidationMessages();
    }
}
