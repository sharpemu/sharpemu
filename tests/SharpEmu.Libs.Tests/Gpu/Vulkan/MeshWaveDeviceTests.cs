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

public sealed class MeshWaveDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    [Theory]
    [InlineData(77u, 0xFACu)]
    [InlineData(13u, 0xFACu)]
    [InlineData(5u, 0xFACu)]
    [InlineData(6u, 0xFACu)]
    [InlineData(56u, 0xFACu)]
    [InlineData(57u, 0xFACu)]
    [InlineData(32u, 0xFACu)]
    [InlineData(77u, 0x247u)]
    [InlineData(77u, 0xFACu, 8)]
    [InlineData(77u, 0x247u, 8)]
    [InlineData(77u, 0xFACu, 16, 1)]
    [InlineData(77u, 0xFACu, 16, 2)]
    [InlineData(77u, 0xFACu, 16, 3)]
    [InlineData(56u, 0xFACu, 16, 3)]
    [InlineData(13u, 0xFACu, 16, 3)]
    [InlineData(48u, 0xFACu, 16, 3)]
    [InlineData(71u, 0xFACu, 16, 1)]
    [InlineData(77u, 0x247u, 8, 3)]
    [InlineData(56u, 0xFACu, 16, -1)]
    [InlineData(77u, 0xFACu, 32, 1)]
    [InlineData(77u, 0xFACu, 32, 2)]
    [InlineData(77u, 0xFACu, 32, 3)]
    public void SpecializedMeshFormatsMatchGenericBufferLoads(uint format, uint swizzle, int inputBytes = 16, int byteOffset = 0)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        if (!vulkan.SupportsMeshShaders || vulkan.SubgroupSize != 32)
        {
            Assert.False(GatePrerequisites.DeviceRequired, "This test requires mesh shaders with 32-lane subgroups.");
            return;
        }
        byte[]? reference = null;
        foreach (var mesh in new[] { false, true })
        {
            var program = Program(MoveScalar(0, 126, 1), MoveScalar(4, 127, 0),
                MoveVector(8, 0, unchecked((uint)byteOffset)),
                BufferAccess(12, "BufferLoadFormatXyzw", 8, dwords: 4, vectorData: 4, offsetEnabled: true),
                BufferAccess(20, "BufferStoreDwordx4", 12, dwords: 4, vectorData: 4), EndProgram(28));
            var (plan, resources, layout) = Prepare(program, mesh ? ShaderStage.Mesh : ShaderStage.Compute);
            resources.Info.Buffers[0].DescriptorFormat = format;
            resources.Info.Buffers[0].DescriptorSwizzle = swizzle;
            var request = new ShaderCompileRequest(plan, resources, layout)
            {
                WaveSize = 64, LocalSizeX = 64,
                Mesh = mesh ? new MeshShaderConfiguration(64, 64, 0, 21, 63, 0, false, 32) : null,
            };
            Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
            using var harness = new ImageTestHarness(vulkan);
            using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
            uint[] words = [0xBF803C00, 0x3F801234, 0xFFFFFFFF, 0x3F000000,
                0x01234567, 0x89ABCDEF, 0xFEDCBA98, 0x76543210];
            var input = runner.CreateBuffer(System.Runtime.InteropServices.MemoryMarshal.AsBytes(words.AsSpan())[..inputBytes]);
            var output = runner.CreateBuffer(16);
            var registers = new uint[256];
            registers[10] = (uint)inputBytes;
            registers[14] = 16;
            harness.Run(() => runner.Dispatch(registers,
                new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [input, output] },
                1, meshDrawParameters: mesh ? [3, 0, 0, 0, 0, 0] : null));
            var actual = runner.ReadBack(output, 0, 16);
            if (inputBytes == 16 && byteOffset == 0) Assert.Contains(actual, value => value != 0);
            if (reference is null) reference = actual;
            else Assert.Equal(reference, actual);
            harness.AssertNoValidationMessages();
        }
    }

    [Theory]
    [InlineData(9, false)]
    [InlineData(12, false)]
    [InlineData(60, true)]
    [InlineData(63, true)]
    public void IndexedMeshPrimitiveInputsReadVertexRecordsThroughSharedMemory(int indexCount, bool primitiveGeneration)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        if (!vulkan.SupportsMeshShaders || vulkan.SubgroupSize != 32)
        {
            Assert.False(GatePrerequisites.DeviceRequired, "This test requires mesh shaders with 32-lane subgroups.");
            return;
        }
        var instructions = new List<Gen5ShaderInstruction>();
        void Add(Gen5ShaderInstruction instruction) => instructions.Add(instruction with { Pc = (uint)instructions.Count * 8 });
        Add(Vop3(0, "VMbcntLoU32B32", 2, Operand(uint.MaxValue), Operand(0)));
        Add(Vop3(0, "VMbcntHiU32B32", 2, Operand(uint.MaxValue), Gen5Operand.Vector(2)));
        Add(Sop2(0, "SAndB32", 20, Gen5Operand.Scalar(3), Operand(255)));
        Add(Vopc(0, "VCmpxGtU32", Gen5Operand.Scalar(20), 2));
        Add(Vop2(0, "VLshlrevB32", 4, Operand(2), Gen5Operand.Vector(2)));
        Add(DataShare(0, "DsWriteB32", false, [Gen5Operand.Vector(4), Gen5Operand.Vector(5)], []));
        Add(MoveScalar(0, 126, uint.MaxValue));
        Add(MoveScalar(0, 127, uint.MaxValue));
        Add(new Gen5ShaderInstruction(0, Gen5ShaderEncoding.Sopp, "SBarrier", [0], [], [], null));
        Add(Sop2(0, "SLshrB32", 20, Gen5Operand.Scalar(3), Operand(8)));
        Add(Sop2(0, "SAndB32", 20, Gen5Operand.Scalar(20), Operand(255)));
        Add(Vopc(0, "VCmpxGtU32", Gen5Operand.Scalar(20), 2));
        Add(Vop2(0, "VAndB32", 4, Operand(65535), Gen5Operand.Vector(0)));
        Add(DataShare(0, "DsReadB32", false, [Gen5Operand.Vector(4)], [10]));
        Add(Vop2(0, "VLshrrevB32", 4, Operand(16), Gen5Operand.Vector(0)));
        Add(DataShare(0, "DsReadB32", false, [Gen5Operand.Vector(4)], [11]));
        Add(DataShare(0, "DsReadB32", false, [Gen5Operand.Vector(1)], [12]));
        Add(MoveVectorFromScalar(0, 13, 20));
        Add(Vop2(0, "VLshlrevB32", 7, Operand(4), Gen5Operand.Vector(2)));
        Add(BufferAccess(0, "BufferStoreDwordx4", 8, dwords: 4,
            vectorData: 10, offsetEnabled: true, vectorAddress: 7));
        Add(EndProgram(0));
        var (plan, resources, layout) = Prepare(Program([.. instructions]), ShaderStage.Mesh);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            WaveSize = 64, LocalSizeX = primitiveGeneration ? 64u : 128u,
            Mesh = primitiveGeneration
                ? new MeshShaderConfiguration(64, 64, 0, 21, 63, 2304, false, 32)
                : new MeshShaderConfiguration(96, 88, 0, 4, 12, 3968, false, 32),
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        uint[] values = Enumerable.Range(0, indexCount).Select(index => (uint)(100 + index * 7 % 11)).ToArray();
        var indices = runner.CreateBuffer(System.Runtime.InteropServices.MemoryMarshal.AsBytes(values.AsSpan(0, indexCount)));
        var primitiveCapacity = primitiveGeneration ? 21 : 4;
        var outputSize = primitiveCapacity * 16;
        var output = runner.CreateBuffer(new byte[outputSize]);
        var registers = new uint[256];
        registers[10] = (uint)outputSize;
        harness.Run(() => runner.Dispatch(registers,
            new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [output] },
            1, meshDrawParameters: [(uint)indexCount, 0, 0, 4, (uint)indices.DeviceAddress, (uint)(indices.DeviceAddress >> 32)]));
        var bytes = runner.ReadBack(output, 0, (ulong)outputSize);
        for (var primitive = 0; primitive < primitiveCapacity; primitive++)
        for (var component = 0; component < 4; component++)
        {
            var expected = primitive >= indexCount / 3 ? 0u
                : component == 3 ? (uint)indexCount / 3 : values[primitive * 3 + component];
            Assert.Equal(expected, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(primitive * 16 + component * 4)));
        }
        harness.AssertNoValidationMessages();
    }

    [Fact]
    public void IndexedMeshReadsDrawIndicesAcrossPartialGroups()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        if (!vulkan.SupportsMeshShaders || vulkan.SubgroupSize != 32)
        {
            Assert.False(GatePrerequisites.DeviceRequired, "This test requires mesh shaders with 32-lane subgroups.");
            return;
        }
        var program = Program(
            Vop3(0, "VMbcntLoU32B32", 2, Operand(uint.MaxValue), Operand(0)),
            Vop3(8, "VMbcntHiU32B32", 2, Operand(uint.MaxValue), Gen5Operand.Vector(2)),
            Sop2(16, "SAndB32", 20, Gen5Operand.Scalar(3), Operand(255)),
            Vopc(24, "VCmpxGtU32", Gen5Operand.Scalar(20), 2),
            Vop2(32, "VSubI32", 7, Gen5Operand.Vector(5), Operand(100)),
            Vop2(40, "VLshlrevB32", 7, Operand(2), Gen5Operand.Vector(7)),
            BufferAccess(48, "BufferStoreDword", 8,
                vectorData: 5, offsetEnabled: true, vectorAddress: 7),
            EndProgram(56));
        var (plan, resources, layout) = Prepare(program, ShaderStage.Mesh);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            WaveSize = 64, LocalSizeX = 64,
            Mesh = new MeshShaderConfiguration(64, 2, 0, 2, 6, 0, false, 32),
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        uint[] values = [105, 100, 108, 101, 107, 102, 106, 104, 103];
        var indices = runner.CreateBuffer(System.Runtime.InteropServices.MemoryMarshal.AsBytes(values.AsSpan()));
        var output = runner.CreateBuffer(36);
        var registers = new uint[256];
        registers[10] = 36;
        harness.Run(() => runner.Dispatch(registers,
            new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [output] },
            2, meshDrawParameters: [9, 0, 0, 4, (uint)indices.DeviceAddress, (uint)(indices.DeviceAddress >> 32)]));
        var bytes = runner.ReadBack(output, 0, 36);
        for (var index = 0; index < 9; index++)
            Assert.Equal((uint)(100 + index), BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(index * 4)));
        harness.AssertNoValidationMessages();
    }

    [Theory]
    [InlineData("inputs")]
    [InlineData("masks")]
    [InlineData("mask-word-writes")]
    [InlineData("shared-memory")]
    [InlineData("wave-local-memory")]
    [InlineData("masked-writes")]
    [InlineData("barrier-branches")]
    [InlineData("loop-barrier")]
    [InlineData("loop-only")]
    [InlineData("loop-prefix-barrier")]
    [InlineData("loop-workgroup-barrier")]
    [InlineData("loop-shared-memory")]
    [InlineData("empty-mask")]
    public void ThreeWavesPreserveStripInputsAndCrossHalfMasks(string operation)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        if (!vulkan.SupportsMeshShaders || vulkan.SubgroupSize != 32)
        {
            Assert.False(GatePrerequisites.DeviceRequired, "This test requires mesh shaders with 32-lane subgroups.");
            return;
        }
        var instructions = new List<Gen5ShaderInstruction>();
        uint programCounter = 0;
        void Add(Gen5ShaderInstruction instruction)
        {
            instructions.Add(instruction with { Pc = programCounter });
            programCounter += 8;
        }
        Add(Vop3(0, "VMbcntLoU32B32", 2, Operand(uint.MaxValue), Operand(0)));
        Add(Vop3(0, "VMbcntHiU32B32", 2, Operand(uint.MaxValue), Gen5Operand.Vector(2)));
        Add(Sop2(0, "SLshrB32", 20, Gen5Operand.Scalar(3), Operand(24)));
        Add(Sop2(0, "SAndB32", 20, Gen5Operand.Scalar(20), Operand(15)));
        Add(Sop2(0, "SLshlB32", 20, Gen5Operand.Scalar(20), Operand(6)));
        Add(Vop2(0, "VAddI32", 6, Gen5Operand.Scalar(20), Gen5Operand.Vector(2)));
        Add(Vop2(0, "VSubI32", 7, Gen5Operand.Vector(5), Gen5Operand.Vector(6)));
        Add(Vop2(0, "VSubI32", 7, Gen5Operand.Vector(7), Operand(100)));
        Add(Vop3(0, "VMulLoU32", 7, Gen5Operand.Vector(7), Operand(64)));
        Add(Vop2(0, "VAddI32", 7, Gen5Operand.Vector(7), Gen5Operand.Vector(6)));
        Add(Vop2(0, "VLshlrevB32", 7, Operand(4), Gen5Operand.Vector(7)));
        if (operation is "loop-barrier" or "loop-only" or "loop-prefix-barrier" or "loop-workgroup-barrier" or "loop-shared-memory")
        {
            if (operation == "loop-prefix-barrier")
                Add(new Gen5ShaderInstruction(0, Gen5ShaderEncoding.Sopp, "SBarrier", [0], [], [], null));
            Add(Sop2(0, "SLshrB32", 21, Gen5Operand.Scalar(20), Operand(6)));
            Add(Sop2(0, "SAddU32", 21, Gen5Operand.Scalar(21), Operand(1)));
            Add(MoveScalar(0, 22, 0));
            var loopStart = programCounter;
            Add(Sop2(0, "SAddU32", 22, Gen5Operand.Scalar(22), Operand(1)));
            Add(Sopc(0, "SCmpLtU32", Gen5Operand.Scalar(22), Gen5Operand.Scalar(21)));
            Add(Branch(programCounter, "SCbranchScc1", checked((short)(((long)loopStart - programCounter - 4) / 4))));
            if (operation is "loop-only" or "loop-prefix-barrier" or "loop-workgroup-barrier")
            {
                if (operation == "loop-workgroup-barrier")
                    Add(new Gen5ShaderInstruction(0, Gen5ShaderEncoding.Sopp, "SBarrier", [0], [], [], null));
                Add(MoveVectorFromScalar(0, 10, 22));
            }
            else
            {
                Add(MoveVectorFromScalar(0, 14, 22));
                Add(Vop2(0, "VLshlrevB32", 4, Operand(2), Gen5Operand.Vector(6)));
                Add(DataShare(0, "DsWriteB32", false, [Gen5Operand.Vector(4), Gen5Operand.Vector(14)], []));
                if (operation == "loop-barrier")
                    Add(new Gen5ShaderInstruction(0, Gen5ShaderEncoding.Sopp, "SBarrier", [0], [], [], null));
                Add(operation == "loop-barrier"
                    ? Vop2(0, "VSubI32", 4, Operand(191), Gen5Operand.Vector(6))
                    : Vop2(0, "VXorB32", 4, Gen5Operand.Vector(6), Operand(33)));
                Add(Vop2(0, "VLshlrevB32", 4, Operand(2), Gen5Operand.Vector(4)));
                Add(DataShare(0, "DsReadB32", false, [Gen5Operand.Vector(4)], [10]));
                Add(DataShare(0, "DsReadB32", false, [Gen5Operand.Vector(4)], [11]));
                Add(DataShare(0, "DsReadB32", false, [Gen5Operand.Vector(4)], [12]));
                Add(Vop2(0, "VAddI32", 10, Gen5Operand.Vector(10), Gen5Operand.Vector(11)));
                Add(Vop2(0, "VSubI32", 10, Gen5Operand.Vector(10), Gen5Operand.Vector(12)));
            }
            Add(MoveVectorFromScalar(0, 11, 20));
            Add(MoveVectorFromScalar(0, 12, 21));
            Add(MoveVectorFromScalar(0, 13, 22));
        }
        else if (operation == "masked-writes")
        {
            Add(Vop2(0, "VLshlrevB32", 4, Operand(2), Gen5Operand.Vector(6)));
            Add(DataShare(0, "DsWriteB32", false, [Gen5Operand.Vector(4), Gen5Operand.Vector(6)], []));
            Add(new Gen5ShaderInstruction(0, Gen5ShaderEncoding.Sopp, "SBarrier", [0], [], [], null));
            Add(Sopc(0, "SCmpEqU32", Gen5Operand.Scalar(20), Operand(0)));
            var skipIndex = instructions.Count;
            Add(Branch(0, "SCbranchScc0", 0));
            Add(MoveScalar(0, 126, 3));
            Add(MoveScalar(0, 127, 0));
            Add(MoveVector(0, 14, 1000));
            Add(DataShare(0, "DsWriteB32", false, [Gen5Operand.Vector(4), Gen5Operand.Vector(14)], []));
            Add(MoveScalar(0, 126, uint.MaxValue));
            Add(MoveScalar(0, 127, uint.MaxValue));
            var barrierAddress = programCounter;
            instructions[skipIndex] = Branch(instructions[skipIndex].Pc, "SCbranchScc0",
                checked((short)((barrierAddress - instructions[skipIndex].Pc - 4) / 4)));
            Add(new Gen5ShaderInstruction(0, Gen5ShaderEncoding.Sopp, "SBarrier", [0], [], [], null));
            Add(Vop2(0, "VXorB32", 4, Gen5Operand.Vector(6), Operand(33)));
            Add(Vop2(0, "VLshlrevB32", 4, Operand(2), Gen5Operand.Vector(4)));
            Add(DataShare(0, "DsReadB32", false, [Gen5Operand.Vector(4)], [10]));
            Add(MoveVector(0, 11, 0));
            Add(MoveVector(0, 12, 0));
            Add(MoveVector(0, 13, 0));
        }
        else if (operation == "barrier-branches")
        {
            Add(Vop2(0, "VLshlrevB32", 4, Operand(2), Gen5Operand.Vector(6)));
            Add(Sopc(0, "SCmpEqU32", Gen5Operand.Scalar(20), Operand(0)));
            var skipIndex = instructions.Count;
            Add(Branch(0, "SCbranchScc0", 0));
            Add(DataShare(0, "DsWriteB32", false, [Gen5Operand.Vector(4), Gen5Operand.Vector(6)], []));
            var barrierAddress = programCounter;
            instructions[skipIndex] = Branch(instructions[skipIndex].Pc, "SCbranchScc0",
                checked((short)((barrierAddress - instructions[skipIndex].Pc - 4) / 4)));
            Add(new Gen5ShaderInstruction(0, Gen5ShaderEncoding.Sopp, "SBarrier", [0], [], [], null));
            Add(Vop2(0, "VLshlrevB32", 4, Operand(2), Gen5Operand.Vector(2)));
            Add(DataShare(0, "DsReadB32", false, [Gen5Operand.Vector(4)], [10]));
            Add(MoveVector(0, 11, 0));
            Add(MoveVector(0, 12, 0));
            Add(MoveVector(0, 13, 0));
        }
        else if (operation == "empty-mask")
        {
            Add(MoveScalar(0, 126, 0));
            Add(MoveScalar(0, 127, 0));
            Add(ReadFirstLane(0, 21, 6));
            Add(MoveScalar(0, 126, uint.MaxValue));
            Add(MoveScalar(0, 127, uint.MaxValue));
            Add(MoveVectorFromScalar(0, 10, 21));
            Add(MoveVector(0, 11, 0));
            Add(MoveVector(0, 12, 0));
            Add(MoveVector(0, 13, 0));
        }
        else if (operation is "shared-memory" or "wave-local-memory")
        {
            Add(Vop2(0, "VLshlrevB32", 4, Operand(2), Gen5Operand.Vector(6)));
            Add(DataShare(0, "DsWriteB32", false, [Gen5Operand.Vector(4), Gen5Operand.Vector(6)], []));
            if (operation == "shared-memory")
                Add(new Gen5ShaderInstruction(0, Gen5ShaderEncoding.Sopp, "SBarrier", [0], [], [], null));
            Add(Vop2(0, "VXorB32", 4, Gen5Operand.Vector(6), Operand(33)));
            Add(Vop2(0, "VLshlrevB32", 4, Operand(2), Gen5Operand.Vector(4)));
            Add(DataShare(0, "DsReadB32", false, [Gen5Operand.Vector(4)], [10]));
            Add(Vop2(0, "VAddI32", 6, Gen5Operand.Vector(6), Operand(0)) with
            {
                Control = new Gen5DppControl(0x140, true, true, 0, 0, 15, 15),
            });
            Add(Vop1(0, "VMovB32", 11, Gen5Operand.Vector(6)));
            Add(ReadLane(0, 21, 6, 63));
            Add(MoveVectorFromScalar(0, 12, 21));
            Add(MoveVector(0, 13, 0));
        }
        else if (operation == "mask-word-writes")
        {
            Add(MoveScalar(0, 126, 0));
            Add(MoveVector(0, 10, 10));
            Add(MoveScalar(0, 127, 0));
            Add(MoveScalar(0, 126, uint.MaxValue));
            Add(MoveVector(0, 10, 20));
            Add(MoveScalar(0, 127, uint.MaxValue));
            Add(MoveScalar(0, 106, 0));
            Add(MoveScalar(0, 107, uint.MaxValue));
            Add(Vop2(0, "VCndmaskB32", 11, Operand(30), Operand(40)));
            Add(MoveScalar(0, 106, uint.MaxValue));
            Add(MoveScalar(0, 107, 0));
            Add(Vop2(0, "VCndmaskB32", 12, Operand(30), Operand(40)));
            Add(MoveVector(0, 13, 0));
        }
        else if (operation == "masks")
        {
            Add(ReadLane(0, 21, 6, 63));
            Add(MoveVectorFromScalar(0, 10, 21));
            Add(Vopc(0, "VCmpEqU32", Operand(40), 2));
            Add(MoveVectorFromScalar(0, 11, 106));
            Add(MoveVectorFromScalar(0, 12, 107));
            Add(MoveScalarRegister(0, 126, 106));
            Add(MoveScalarRegister(0, 127, 107));
            Add(ReadFirstLane(0, 22, 6));
            Add(MoveScalar(0, 126, uint.MaxValue));
            Add(MoveScalar(0, 127, uint.MaxValue));
            Add(MoveVectorFromScalar(0, 13, 22));
        }
        else
        {
            Add(Vop1(0, "VMovB32", 10, Gen5Operand.Vector(5)));
            Add(Vop1(0, "VMovB32", 11, Gen5Operand.Vector(0)));
            Add(Vop1(0, "VMovB32", 12, Gen5Operand.Vector(1)));
            Add(MoveVectorFromScalar(0, 13, 3));
        }
        Add(BufferAccess(0, "BufferStoreDwordx4", 8, dwords: 4,
            vectorData: 10, offsetEnabled: true, vectorAddress: 7));
        Add(EndProgram(0));
        var (plan, resources, layout) = Prepare(Program([.. instructions]), ShaderStage.Mesh);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            WaveSize = 64, LocalSizeX = 192,
            Mesh = new MeshShaderConfiguration(192, 64, 0, 3, 5, 192, true, 32),
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        const uint byteCount = 192 * 2 * 16;
        var output = runner.CreateBuffer(byteCount);
        var registers = new uint[256];
        registers[10] = byteCount;
        harness.Run(() => runner.Dispatch(registers,
            new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [output] },
            2, meshDrawParameters: [8, 100, 0, 0, 0, 0]));
        var bytes = runner.ReadBack(output, 0, byteCount);
        for (uint group = 0; group < 2; group++)
        for (uint lane = 0; lane < 192; lane++)
        {
            var waveBase = lane & ~63u;
            var primitive = group * 3 + lane;
            var parity = primitive & 1;
            uint[] expected = operation is "loop-barrier" or "loop-only" or "loop-prefix-barrier" or "loop-workgroup-barrier" or "loop-shared-memory"
                ? [operation == "loop-barrier" ? 3 - lane / 64 : lane / 64 + 1,
                    waveBase, lane / 64 + 1, lane / 64 + 1]
                : operation == "barrier-branches"
                ? [lane & 63, 0, 0, 0]
                : operation == "empty-mask"
                ? [waveBase, 0, 0, 0]
                : operation == "masked-writes"
                ? [(lane ^ 33) < 2 ? 1000u : lane ^ 33, 0, 0, 0]
                : operation is "shared-memory" or "wave-local-memory"
                ? [lane ^ 33, (lane & ~15u) + 15 - (lane & 15), waveBase + 48, 0]
                : operation == "mask-word-writes"
                ? [(lane & 63) < 32 ? 20u : 10u, (lane & 63) < 32 ? 30u : 40u,
                    (lane & 63) < 32 ? 40u : 30u, 0]
                : operation == "masks"
                ? [waveBase + 63, 0, 256, waveBase + 40]
                : [100 + primitive, ((lane + parity) << 2) | ((lane + 1 - parity) << 18),
                    (lane + 2) << 2, 3u << 28 | (lane / 64) << 24 | (lane < 64 ? 5u | (3u << 8) : 0)];
            for (var component = 0; component < 4; component++)
            {
                var offset = checked((int)((group * 192 + lane) * 16) + component * 4);
                var actual = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
                Assert.True(expected[component] == actual,
                    $"operation={operation} group={group} lane={lane} component={component} expected={expected[component]} actual={actual} values={BitConverter.ToString(bytes, checked((int)((group * 192 + lane) * 16)), 16)}");
            }
        }
        harness.AssertNoValidationMessages();
    }
}
