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
    [InlineData(false)]
    [InlineData(true)]
    public void LocalComparisonPreservesBothWaveHalvesAndScalarMaskReads(bool readMask)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        var instructions = new List<Gen5ShaderInstruction>
        {
            Vopc(0, "VCmpLtU32", Operand(31), 0),
            Vop2(4, "VCndmaskB32", 3, Operand(10), Operand(20)),
        };
        if (readMask) instructions.Add(MoveVectorFromScalar(8, 6, 107));
        instructions.Add(Vopc(12, "VCmpEqU32", Operand(0), 0));
        instructions.Add(Vop2(16, "VLshlrevB32", 5, Operand(2), Gen5Operand.Vector(0)));
        instructions.Add(BufferAccess(24, "BufferStoreDword", 8, vectorData: 3, offsetEnabled: true, vectorAddress: 5));
        if (readMask)
            instructions.Add(BufferAccess(32, "BufferStoreDword", 8, offset: 256, vectorData: 6,
                offsetEnabled: true, vectorAddress: 5));
        instructions.Add(EndProgram(40));
        var (plan, resources, layout) = Prepare(Program([.. instructions]));
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            LocalSizeX = 64, ThreadCountX = 64, WaveSize = 64,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        var barriers = 0;
        for (var offset = 20; offset < shader.Spirv.Length;)
        {
            var word = BinaryPrimitives.ReadUInt32LittleEndian(shader.Spirv.AsSpan(offset));
            if ((word & 0xffff) == 224) barriers++;
            offset += checked((int)(word >> 16) * 4);
        }
        Assert.Equal(readMask ? 1 : 0, barriers);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var output = runner.CreateBuffer(512);
        var registers = new uint[256];
        registers[10] = 512;
        harness.Run(() => runner.Dispatch(registers,
            new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [output] }, 1));
        var bytes = runner.ReadBack(output, 0, 512);
        for (var lane = 0; lane < 64; lane++)
        {
            Assert.Equal(lane > 31 ? 20u : 10u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(lane * 4)));
            if (readMask)
                Assert.Equal(uint.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(256 + lane * 4)));
        }
        harness.AssertNoValidationMessages();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LocalComparisonPreservesModifiedSelectionsAndPartialMaskWrites(bool partialWrite)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        var selectionControl = new Gen5SdwaControl(6, 0, 6, 6, false, false, 0, 0, 0, false, null);
        var instructions = new List<Gen5ShaderInstruction>
        {
            Vop2(0, "VLshlrevB32", 9, Operand(2), Gen5Operand.Vector(0)),
            DataShare(0, "DsWriteB32", false, [Gen5Operand.Vector(9), Gen5Operand.Vector(0)], []),
            Vopc(0, "VCmpLtU32", Operand(31), 0),
            Vopc(4, "VCmpGtU32", Operand(16), 0) with
            {
                Control = selectionControl with { ScalarDestination = 2 },
                Destinations = [Gen5Operand.Scalar(2)],
            },
            Vop3(8, "VAddLshlU32", 7, Gen5Operand.Vector(0), Operand(0), Operand(0)),
            DataShare(0, "DsReadB32", false, [Gen5Operand.Vector(9)], [7]),
            Vop2(16, "VCndmaskB32", 3, Gen5Operand.Vector(7), Operand(20)) with { Control = selectionControl },
            Vop3(24, "VCndmaskB32", 4, Operand(30), Operand(40), Gen5Operand.Scalar(2)),
        };
        if (partialWrite)
        {
            instructions.Add(MoveScalar(32, 106, 0));
            instructions.Add(MoveVectorFromScalar(36, 6, 107));
        }
        instructions.Add(Sop2(40, "SAndB64", 106, Operand(0), Operand(0)));
        instructions.Add(Vop2(44, "VLshlrevB32", 5, Operand(2), Gen5Operand.Vector(0)));
        instructions.Add(BufferAccess(48, "BufferStoreDword", 8, vectorData: 3, offsetEnabled: true, vectorAddress: 5));
        instructions.Add(BufferAccess(56, "BufferStoreDword", 8, offset: 256, vectorData: 4,
            offsetEnabled: true, vectorAddress: 5));
        if (partialWrite)
            instructions.Add(BufferAccess(64, "BufferStoreDword", 8, offset: 512, vectorData: 6,
                offsetEnabled: true, vectorAddress: 5));
        instructions.Add(EndProgram(72));
        var (plan, resources, layout) = Prepare(Program([.. instructions.Select((instruction, index) =>
            instruction with { Pc = (uint)index * 8 })]));
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            LocalSizeX = 64, ThreadCountX = 64, WaveSize = 64,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        var barriers = 0;
        for (var offset = 20; offset < shader.Spirv.Length;)
        {
            var word = BinaryPrimitives.ReadUInt32LittleEndian(shader.Spirv.AsSpan(offset));
            if ((word & 0xffff) == 224) barriers++;
            offset += checked((int)(word >> 16) * 4);
        }
        Assert.Equal(partialWrite ? 2 : 1, barriers);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var output = runner.CreateBuffer(768);
        var registers = new uint[256];
        registers[10] = 768;
        harness.Run(() => runner.Dispatch(registers,
            new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [output] }, 1));
        var bytes = runner.ReadBack(output, 0, 768);
        for (var lane = 0; lane < 64; lane++)
        {
            Assert.Equal(lane > 31 ? 20u : (uint)lane, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(lane * 4)));
            Assert.Equal(lane < 16 ? 40u : 30u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(256 + lane * 4)));
            if (partialWrite)
                Assert.Equal(uint.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(512 + lane * 4)));
        }
        harness.AssertNoValidationMessages();
    }

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

    [Theory]
    [InlineData(32u, 31u, false)]
    [InlineData(64u, 63u, false)]
    [InlineData(64u, 127u, false)]
    [InlineData(64u, 63u, true)]
    public void SavedLane_PreservesWrittenValueAndInvalidatesVectorWrites(uint waveSize, uint selector, bool overwriteVector)
    {
        Run(waveSize, selector, disableExecution: true, writeLane: false, savedLane: true, overwriteVector: overwriteVector);
    }

    [Theory]
    [InlineData("unchanged", false)]
    [InlineData("other-lane", false)]
    [InlineData("vector-write", true)]
    [InlineData("dynamic-lane", true)]
    [InlineData("block-boundary", true)]
    public void SavedLane_EliminatesBarriersOnlyForProvenValues(string operation, bool needsBarrier)
    {
        var instructions = new List<Gen5ShaderInstruction>
        {
            MoveScalar(0, 20, 42),
            WriteLane(4, 3, 20, 31),
        };
        instructions.Add(operation switch
        {
            "other-lane" => WriteLane(12, 3, 20, 30),
            "vector-write" => MoveVectorFromScalar(12, 3, 20),
            "dynamic-lane" => WriteLane(12, 3, 20, 0) with
            {
                Sources = [Gen5Operand.Scalar(20), Gen5Operand.Scalar(21)],
            },
            "block-boundary" => Branch(12, "SBranch", 1),
            _ => Nop(12),
        });
        instructions.Add(ReadLane(20, 22, 3, 31));
        instructions.Add(EndProgram(28));
        var (plan, resources, layout) = Prepare(Program([.. instructions]));
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            LocalSizeX = 64, ThreadCountX = 64, WaveSize = 64,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        var barriers = 0;
        for (var offset = 20; offset < shader.Spirv.Length;)
        {
            var instruction = BinaryPrimitives.ReadUInt32LittleEndian(shader.Spirv.AsSpan(offset));
            if ((instruction & 0xffff) == 224) barriers++;
            offset += checked((int)(instruction >> 16) * 4);
        }
        // Mask setup needs no exchange. An unresolved lane read needs one barrier.
        Assert.Equal(needsBarrier ? 1 : 0, barriers);
    }

    private void Run(uint waveSize, uint selector, bool disableExecution, bool writeLane,
        bool savedLane = false, bool overwriteVector = false)
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
        if (savedLane)
        {
            var lane = new Gen5Operand(Gen5OperandKind.LiteralConstant, selector);
            Add(WriteLane(0, 3, 21, 0) with { Sources = [Gen5Operand.Scalar(21), lane] });
            Add(MoveScalar(0, 21, 0x87654321));
            if (overwriteVector)
            {
                Add(MoveScalar(0, 126, uint.MaxValue));
                Add(MoveScalar(0, 127, uint.MaxValue));
                Add(MoveVectorFromScalar(0, 3, 21));
            }
            Add(ReadLane(0, 22, 3, 0) with { Sources = [Gen5Operand.Vector(3), lane] });
        }
        else if (writeLane)
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
            var expected = savedLane ? (overwriteVector ? 0x87654321u : 0x12345678u)
                : writeLane ? (lane == selectedLane ? 0x12345678u : 1000 + lane) : 1000 + selectedLane;
            Assert.Equal(expected, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)lane * 4)));
        }
        harness.AssertNoValidationMessages();
    }
}
