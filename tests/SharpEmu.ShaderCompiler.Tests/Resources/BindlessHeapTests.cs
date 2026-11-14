// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

public sealed class BindlessHeapTests
{
    // With the heap set (bindlessHeap), an image read from a heap V# at a lane-chosen offset is a heap record: its reads
    // stay on the host, and the shader takes the record its key picks from descriptor set 1 and marks it for the host. A
    // record past the heap's range (the call records' elements) reads null.
    [Fact]
    public void AnImageReadAtALaneChosenOffsetIsAHeapRecord()
    {
        var program = ResourceTrackerTests.LaneIndexedImageProgram();

        var plan = Extract(program, stage: ShaderStage.Compute, bindlessHeap: true);

        var record = Assert.Single(plan.HeapRecords);
        Assert.False(record.Sampler);
        var heap = plan.DescriptorSources[(int)record.Source].Heap!;
        Assert.Equal((0u, BindlessHeap.ImageRecordBytes, false), (heap.Immediate, heap.RecordBytes, heap.Cube));
        Assert.Equal(40u, plan.Memory[heap.KeyMemoryIndex].Pc);
        Assert.Equal(new HeapRecordSource(heap.HeapSource, false), Assert.Single(plan.HeapRecordSources));
        for (var component = 0u; component < 8; component++)
        {
            Assert.True(plan.Memory.TryGetIndex(40, component, out var index));
            Assert.True(plan.Memory[index].PlanningOnly);
        }

        Assert.True(plan.Info.UsesBindlessHeap);
        var request = Request(program, bindlessHeap: true);
        Assert.True(request.UsesBindlessHeap);
        Assert.True(request.Bindings.UsesBindlessHeap);
        Assert.Contains(heap.KeyMemoryIndex, request.IndirectOffsetKeyMemoryIndices);

        var module = Compile(request);
        Assert.Equal([0u, BindlessHeap.FeedbackBinding], module.SetOneBindings);
        Assert.Contains(SpirvOp.AtomicOr, module.Opcodes);
        Assert.Contains((uint)SpirvDecoration.NonUniform, module.Decorations);
        Assert.Contains((uint)SpirvCapability.SampledImageArrayNonUniformIndexing, module.Capabilities);
        Assert.Contains(SpirvOp.ULessThan, module.Opcodes);
        Assert.Contains(BindlessHeap.CallRecordFirstIndex, module.Constants);
        Assert.Contains(BindlessHeap.CallRecordNullIndex, module.Constants);
    }

    // A sampler read from a heap V# at a lane-chosen offset indexes the sampler array of set 1; its words still load.
    [Fact]
    public void ASamplerReadAtALaneChosenOffsetIsAHeapRecord()
    {
        var program = ResourceTrackerTests.LaneIndexedSamplerProgram();

        var plan = Extract(program, stage: ShaderStage.Pixel, bindlessHeap: true);

        var record = Assert.Single(plan.HeapRecords);
        Assert.True(record.Sampler);
        var heap = plan.DescriptorSources[(int)record.Source].Heap!;
        Assert.Equal((0u, BindlessHeap.SamplerRecordBytes), (heap.Immediate, heap.RecordBytes));
        Assert.True(plan.Memory.TryGetIndex(24, 0, out var index));
        Assert.False(plan.Memory[index].PlanningOnly);

        var module = Compile(Request(program, ShaderStage.Pixel, bindlessHeap: true));
        Assert.Equal([(uint)BindlessHeap.SamplerSlot, BindlessHeap.FeedbackBinding], module.SetOneBindings);
    }

    // With both opt-ins, a heap record is read from the heap and not as null.
    [Fact]
    public void AHeapRecordIsNotReadAsNull()
    {
        var plan = Extract(ResourceTrackerTests.LaneIndexedImageProgram(), stage: ShaderStage.Compute,
            bindlessNullDescriptors: true, bindlessHeap: true);

        Assert.Single(plan.HeapRecords);
        Assert.Empty(plan.NullBindlessDescriptorPcs);
    }

    // Each draw evaluates the heap V# for the host, and the heap image takes its class from the instruction.
    [Fact]
    public void MaterializingReportsTheHeapAndSpecializesTheImageFromTheInstruction()
    {
        var plan = Extract(ResourceTrackerTests.LaneIndexedImageProgram(), stage: ShaderStage.Compute, bindlessHeap: true);
        var userData = new uint[64];
        userData[0] = 0x0001_0000;
        userData[1] = 32u << 16;
        userData[2] = 4096;
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();

        Assert.True(ResourceMaterializer.Materialize(plan, Inputs(userData), ref snapshot, ref specialization));

        Assert.Equal([new BindlessHeapDescriptor(false, 0x0001_0000, 4096, 32)], snapshot.Heaps);
        var image = Assert.Single(specialization.Images);
        Assert.Equal((ImageNumericClass.Float, ImageDimension.Dim2D, false), (image.NumericClass, image.Dimension, image.Cube));
    }

    // Materialized the way the program cache chooses its path, a heap program still reports its heap: the fast path
    // assembles no heap descriptors, and with an empty heap the shader's records read nothing.
    [Fact]
    public void AHeapProgramKeepsItsHeapOnThePathTheCacheChooses()
    {
        var plan = Extract(ResourceTrackerTests.LaneIndexedImageProgram(), stage: ShaderStage.Compute, bindlessHeap: true);
        var userData = new uint[64];
        userData[0] = 0x0001_0000;
        userData[1] = 32u << 16;
        userData[2] = 4096;
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();

        var materialized = ResourceMaterializer.CanMaterializeFast(plan)
            ? ResourceMaterializer.MaterializeFast(plan, Inputs(userData), ref snapshot, ref specialization, out _)
            : ResourceMaterializer.Materialize(plan, Inputs(userData), ref snapshot, ref specialization);

        Assert.True(materialized);
        Assert.Equal([new BindlessHeapDescriptor(false, 0x0001_0000, 4096, 32)], snapshot.Heaps);
    }

    // A 3D read of a heap record also has the record's 2D-array element: a record that holds a 2D texture has a null 3D
    // element, and the hardware takes the third coordinate as the layer.
    [Fact]
    public void AHeapVolumeReadFallsBackToTheRecordsLayers()
    {
        var module = Compile(Request(ResourceTrackerTests.LaneIndexedImageProgram(dimension: 2), bindlessHeap: true));

        Assert.Equal([(uint)BindlessHeap.VolumeLayerSlot, (uint)BindlessHeap.VolumeSlot, BindlessHeap.FeedbackBinding], module.SetOneBindings);
        Assert.Contains(SpirvOp.ImageQueryLevels, module.Opcodes);
        Assert.Contains((uint)SpirvCapability.ImageQuery, module.Capabilities);
    }

    // Without the heap set nothing changes: compute still rejects the lane-chosen image.
    [Fact]
    public void WithoutTheOptInTheImageIsStillRejected()
    {
        var error = Assert.Throws<ResourcePlanException>(() =>
            Extract(ResourceTrackerTests.LaneIndexedImageProgram(), stage: ShaderStage.Compute));
        Assert.Contains("not a valid runtime value", error.Message);
    }

    private sealed record Module(List<SpirvOp> Opcodes, List<uint> Decorations, List<uint> Capabilities, List<uint> SetOneBindings,
        List<uint> Constants);

    private static Module Compile(ShaderCompileRequest request)
    {
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        Gen5MipStatisticsTests.ValidateWhenAvailable(shader.Spirv);
        var code = shader.Spirv;
        var opcodes = new List<SpirvOp>();
        var decorations = new List<uint>();
        var capabilities = new List<uint>();
        var constants = new List<uint>();
        var sets = new Dictionary<uint, uint>();
        var bindings = new Dictionary<uint, uint>();
        for (var offset = 20; offset + 4 <= code.Length;)
        {
            var word = BitConverter.ToUInt32(code, offset);
            var count = (int)(word >> 16);
            var opcode = (SpirvOp)(word & 0xFFFF);
            uint Operand(int index) => BitConverter.ToUInt32(code, offset + index * sizeof(uint));
            opcodes.Add(opcode);
            if (opcode == SpirvOp.Capability)
            {
                capabilities.Add(Operand(1));
            }
            else if (opcode == SpirvOp.Constant && count == 4)
            {
                constants.Add(Operand(3));
            }
            else if (opcode == SpirvOp.Decorate)
            {
                decorations.Add(Operand(2));
                if (Operand(2) == (uint)SpirvDecoration.DescriptorSet)
                {
                    sets[Operand(1)] = Operand(3);
                }
                else if (Operand(2) == (uint)SpirvDecoration.Binding)
                {
                    bindings[Operand(1)] = Operand(3);
                }
            }

            offset += count * sizeof(uint);
        }

        var setOne = sets.Where(pair => pair.Value == BindlessHeap.DescriptorSet).Select(pair => bindings[pair.Key]).Order().ToList();
        return new Module(opcodes, decorations, capabilities, setOne, constants);
    }
}
