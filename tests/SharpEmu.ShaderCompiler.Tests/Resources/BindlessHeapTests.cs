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

    // A heap read takes the element of its record when set 1 holds that record (below CallRecordFirstIndex) and the null
    // element for any other record, however large: the range test reads the record itself, not a value cut to the set's
    // size, so no record reads the element of another record.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ARecordSetOneCannotHoldReadsTheNullElement(bool sampler)
    {
        var program = sampler ? ResourceTrackerTests.LaneIndexedSamplerProgram() : ResourceTrackerTests.LaneIndexedImageProgram();
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(Request(program, ShaderStage.Pixel, bindlessHeap: true), out var shader, out var error), error);
        Gen5MipStatisticsTests.ValidateWhenAvailable(shader.Spirv);
        var module = new Expressions(shader.Spirv);
        var recordBytes = sampler ? BindlessHeap.SamplerRecordBytes : BindlessHeap.ImageRecordBytes;

        var tested = module.ComparedWith(BindlessHeap.CallRecordFirstIndex).ToList();
        Assert.NotEmpty(tested);
        Assert.All(tested, value => Assert.Equal(SpirvOp.ShiftRightLogical, module.Opcode(value)));
        var indexes = module.ElementIndexes(sampler ? (uint)BindlessHeap.SamplerSlot : 0u);
        Assert.NotEmpty(indexes);
        foreach (var index in indexes)
        {
            var offset = module.ShiftedOperand(index, sampler ? 4u : 5u);
            foreach (var record in new[] { 0u, 1234u, BindlessHeap.CallRecordFirstIndex - 1 })
            {
                Assert.Equal(record, module.Evaluate(index, offset, record * recordBytes));
            }

            foreach (var record in new[] { BindlessHeap.CallRecordFirstIndex, BindlessHeap.Capacity, BindlessHeap.Capacity + 1234, 3 * BindlessHeap.Capacity + 7 })
            {
                Assert.Equal(BindlessHeap.CallRecordNullIndex, module.Evaluate(index, offset, record * recordBytes));
            }
        }
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

    // The integer value instructions of a module, enough to evaluate a set-1 element index for a given byte offset.
    private sealed class Expressions
    {
        private readonly Dictionary<uint, (SpirvOp Opcode, uint[] Operands)> _definitions = [];
        private readonly Dictionary<uint, uint> _constants = [];
        private readonly Dictionary<uint, (uint? Set, uint? Binding)> _bindings = [];
        private readonly List<(uint Base, uint Index)> _elementPointers = [];

        public Expressions(byte[] code)
        {
            for (var offset = 20; offset + 4 <= code.Length;)
            {
                var word = BitConverter.ToUInt32(code, offset);
                var count = (int)(word >> 16);
                var opcode = (SpirvOp)(word & 0xFFFF);
                var operands = new uint[count - 1];
                for (var index = 0; index < operands.Length; index++)
                {
                    operands[index] = BitConverter.ToUInt32(code, offset + (index + 1) * sizeof(uint));
                }

                switch (opcode)
                {
                    case SpirvOp.Constant when operands.Length == 3:
                        _constants[operands[1]] = operands[2];
                        break;
                    case SpirvOp.Decorate when operands.Length == 3 && operands[1] == (uint)SpirvDecoration.DescriptorSet:
                        _bindings[operands[0]] = (operands[2], _bindings.GetValueOrDefault(operands[0]).Binding);
                        break;
                    case SpirvOp.Decorate when operands.Length == 3 && operands[1] == (uint)SpirvDecoration.Binding:
                        _bindings[operands[0]] = (_bindings.GetValueOrDefault(operands[0]).Set, operands[2]);
                        break;
                    case SpirvOp.AccessChain when operands.Length >= 4:
                        _elementPointers.Add((operands[2], operands[3]));
                        break;
                    case SpirvOp.IAdd or SpirvOp.ISub or SpirvOp.BitwiseAnd or SpirvOp.BitwiseOr or SpirvOp.ShiftLeftLogical or
                        SpirvOp.ShiftRightLogical or SpirvOp.ULessThan or SpirvOp.Select or SpirvOp.CopyObject or SpirvOp.Bitcast:
                        _definitions[operands[1]] = (opcode, operands[2..]);
                        break;
                }

                offset += count * sizeof(uint);
            }
        }

        // The indexes of the element pointers into the set-1 array at binding slot.
        public List<uint> ElementIndexes(uint slot)
        {
            var array = Assert.Single(_bindings, entry => entry.Value.Set == BindlessHeap.DescriptorSet && entry.Value.Binding == slot).Key;
            return _elementPointers.Where(pointer => pointer.Base == array).Select(pointer => pointer.Index).ToList();
        }

        // What the nearest logical right shift by amount under value shifts: the read's byte offset.
        public uint ShiftedOperand(uint value, uint amount)
        {
            var pending = new Queue<uint>();
            pending.Enqueue(value);
            var seen = new HashSet<uint>();
            while (pending.TryDequeue(out var id))
            {
                if (!seen.Add(id) || !_definitions.TryGetValue(id, out var definition))
                {
                    continue;
                }

                if (definition.Opcode == SpirvOp.ShiftRightLogical &&
                    _constants.TryGetValue(definition.Operands[1], out var shift) && shift == amount)
                {
                    return definition.Operands[0];
                }

                foreach (var operand in definition.Operands)
                {
                    pending.Enqueue(operand);
                }
            }

            throw new InvalidOperationException($"No right shift by {amount} feeds %{value}.");
        }

        // The left operands of the unsigned comparisons with the constant bound.
        public IEnumerable<uint> ComparedWith(uint bound) =>
            _definitions.Values
                .Where(definition => definition.Opcode == SpirvOp.ULessThan &&
                    _constants.TryGetValue(definition.Operands[1], out var value) && value == bound)
                .Select(definition => definition.Operands[0]);

        public SpirvOp Opcode(uint id) => _definitions.TryGetValue(id, out var definition) ? definition.Opcode : SpirvOp.Nop;

        // The value of id when the value named offset holds value.
        public uint Evaluate(uint id, uint offset, uint value)
        {
            if (id == offset)
            {
                return value;
            }

            if (_constants.TryGetValue(id, out var constant))
            {
                return constant;
            }

            var (opcode, operands) = _definitions[id];
            uint Operand(int index) => Evaluate(operands[index], offset, value);
            return opcode switch
            {
                SpirvOp.IAdd => Operand(0) + Operand(1),
                SpirvOp.ISub => Operand(0) - Operand(1),
                SpirvOp.BitwiseAnd => Operand(0) & Operand(1),
                SpirvOp.BitwiseOr => Operand(0) | Operand(1),
                SpirvOp.ShiftLeftLogical => Operand(0) << (int)(Operand(1) & 31),
                SpirvOp.ShiftRightLogical => Operand(0) >> (int)(Operand(1) & 31),
                SpirvOp.ULessThan => Operand(0) < Operand(1) ? 1u : 0u,
                SpirvOp.Select => Operand(0) != 0 ? Operand(1) : Operand(2),
                _ => Operand(0),
            };
        }
    }
}
