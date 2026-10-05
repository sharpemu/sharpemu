// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Metal;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

public sealed class FlattenedReadReuseTests
{
    // Both loads feed output stores, so removing a destination write changes the result.
    internal static Gen5ShaderProgram RepeatedReadProgram(uint components, bool dynamicOffset = false)
    {
        var instructions = new List<Gen5ShaderInstruction>();
        uint pc = 0;
        for (uint repetition = 0; repetition < 2; repetition++)
        {
            var destination = 16 + repetition * 16;
            instructions.Add(ScalarLoad(pc, 0, destination, components,
                dynamicOffsetRegister: dynamicOffset ? 8u : null));
            pc += 8;
            for (uint component = 0; component < components; component++)
            {
                instructions.Add(Vop1(pc, "VMovB32", 4, Gen5Operand.Scalar(destination + component)));
                pc += 4;
                instructions.Add(BufferStore(pc, 4, (int)((repetition * components + component) * 4)));
                pc += 8;
            }
        }

        instructions.Add(EndProgram(pc));
        return Program([.. instructions]);
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(4u)]
    public void EquivalentReads_ShareHostReadsAndPreserveEveryDestination(uint components)
    {
        var program = RepeatedReadProgram(components);
        var (plan, resources, layout) = Prepare(program, userDataCount: 9);
        var request = new ShaderCompileRequest(plan, resources, layout);
        Assert.Equal((int)components, plan.TableReads.Count);
        Assert.False(plan.Info.UsesDeviceAddresses);
        Assert.Empty(plan.DeviceAddressRanges);
        Assert.Equal((int)(components * 2), request.FlattenedSlotByMemoryIndex.Count);
        var memory = new TestWordMemory { Words = [11, 22, 33, 44] };
        Assert.True(RuntimeValueEvaluator.FlattenResourceTable(plan, Inputs([0x1000, 0], memory.Read), out var table));
        Assert.Equal(memory.Words.Take((int)components), table);
        Assert.Equal(components, memory.Reads);

        foreach (var instruction in program.Instructions.Where(instruction => instruction.Encoding == Gen5ShaderEncoding.Smem))
        {
            for (uint component = 0; component < components; component++)
            {
                Assert.True(plan.Memory.TryGetIndex(instruction.Pc, component, out var memoryIndex));
                Assert.True(plan.Memory[memoryIndex].PlanningOnly);
                Assert.Equal(component, request.FlattenedSlotByMemoryIndex[memoryIndex]);
            }
        }
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(4u)]
    public void EquivalentReads_CompileWithoutDeviceAddressBindings(uint components)
    {
        var request = Request(RepeatedReadProgram(components), userDataCount: 9);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var spirv, out var spirvError), spirvError);
        var module = new SpirvModuleInspector(spirv.Spirv);
        Assert.Equal(0u, module.AddressingModel);
        Assert.DoesNotContain(request.Bindings.Descriptors, descriptor => descriptor.Kind == DescriptorBindingKind.DeviceAddressPageTable);
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out var metal, out var metalError), metalError);
        var kernel = metal.Source[metal.Source.IndexOf("kernel void ", StringComparison.Ordinal)..];
        Assert.DoesNotContain("sharpemu_load_device_dword", kernel);
        for (uint component = 0; component < components; component++)
        {
            Assert.Contains($"s[{16 + component}] =", metal.Source);
            Assert.Contains($"s[{32 + component}] =", metal.Source);
            Assert.Equal(2, metal.Source.Split($".flattened_table[{component}u]").Length - 1);
        }
    }

    [Fact]
    public void DynamicReads_KeepDeviceAddressBindings()
    {
        var request = Request(RepeatedReadProgram(1, dynamicOffset: true), userDataCount: 9);
        Assert.True(request.Resources.Info.UsesDeviceAddresses);
        Assert.Empty(request.FlattenedSlotByMemoryIndex);
        Assert.Contains(request.Bindings.Descriptors, descriptor => descriptor.Kind == DescriptorBindingKind.DeviceAddressPageTable);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out _, out var spirvError), spirvError);
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out var metal, out var metalError), metalError);
        var kernel = metal.Source[metal.Source.IndexOf("kernel void ", StringComparison.Ordinal)..];
        Assert.Contains("sharpemu_load_device_dword", kernel);
    }
}
