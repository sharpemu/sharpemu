// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5TessellationTests
{
    [Theory]
    [InlineData(6u)]
    [InlineData(32u)]
    public void MergedHull_UsesSeparateBackUserDataAfterLocalRegistersAreReused(uint frontCount)
    {
        // LS receives an inline descriptor and its own table. HS receives a
        // separate table containing both that LS table and the ring table.
        ulong frontTable = 0x20_0010_0000;
        ulong backTable = frontTable + 0x1000;
        ulong rings = frontTable + 0x2000;
        var memory = new TestWordMemory { Base = frontTable, Words = new uint[4096] };
        memory.At(frontTable) = 0x6000;
        memory.At(frontTable + 4) = 0;
        memory.At(frontTable + 8) = 128;
        memory.At(frontTable + 12) = 0;
        memory.At(backTable) = (uint)frontTable;
        memory.At(backTable + 4) = (uint)(frontTable >> 32);
        memory.At(backTable + 8) = (uint)rings;
        memory.At(backTable + 12) = (uint)(rings >> 32);
        memory.At(rings + 48) = 0x7000;
        memory.At(rings + 52) = 0;
        memory.At(rings + 56) = 256;
        memory.At(rings + 60) = 0;
        var program = Program(
            ScalarLoad(0, 8, 16, 4), BufferLoad(8, 16),
            MoveScalar(16, 8, 0xBAD), MoveScalar(20, 9, 0xBAD),
            ScalarLoad(256, 0, 8, 4), ScalarLoad(264, 10, 36, 4, 48),
            BufferStore(272, 36), EndProgram(280)) with { FusedContinuationPc = 256 };
        var lowered = Gen5TessellationLowering.PrepareMergedHull(program, 8 + frontCount);
        var data = new uint[frontCount + 2];
        data[0] = (uint)frontTable;
        data[1] = (uint)(frontTable >> 32);
        data[frontCount] = (uint)backTable;
        data[frontCount + 1] = (uint)(backTable >> 32);
        var plan = ShaderResourcePlan.Extract(lowered, ShaderStage.Compute, Hash, 8, (uint)data.Length, waveSize: 64);
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs(data, memory.Read), ref snapshot, ref specialization));
        Assert.Equal(0x6000u, snapshot.Buffers[0][0]);
        Assert.Equal(0x7000u, snapshot.Buffers[1][0]);
    }

    [Fact]
    public void FactorBuffer_ResolvesHardwareOffsetStoresAndRejectsAmbiguousOrReusedOffsets()
    {
        Gen5ShaderInstruction Store(uint pc, uint descriptor)
        {
            var instruction = BufferAccess(pc, "BufferStoreDword", descriptor);
            return instruction with { Sources = [..instruction.Sources.Take(2), Gen5Operand.Scalar(4)] };
        }
        ShaderResourcePlan Plan(params Gen5ShaderInstruction[] instructions) =>
            ShaderResourcePlan.Extract(Program(instructions), ShaderStage.Compute, Hash, 8, 24, waveSize: 64);
        var plan = Plan(Store(0, 12), Store(8, 12), EndProgram(16));
        Assert.Equal(plan.Memory.Find(0)!.Resource, Gen5TessellationLowering.FindFactorBuffer(plan, 0));
        Assert.Throws<NotSupportedException>(() => Gen5TessellationLowering.FindFactorBuffer(
            Plan(Store(0, 12), Store(8, 16), EndProgram(16)), 0));
        Assert.Throws<NotSupportedException>(() => Gen5TessellationLowering.FindFactorBuffer(
            Plan(MoveScalar(0, 4, 0), Store(4, 12), EndProgram(12)), 0));
        Assert.Throws<NotSupportedException>(() => Gen5TessellationLowering.FindFactorBuffer(Plan(EndProgram(0)), 0));
    }

    [Theory]
    [InlineData(Gen5TessellationDomain.Quads, 6)]
    [InlineData(Gen5TessellationDomain.Triangles, 4)]
    [InlineData(Gen5TessellationDomain.Isolines, 2)]
    public void ControlBridge_UsesGuestFactorBufferAndNativePatchOutputs(Gen5TessellationDomain domain, int factorCount)
    {
        var (plan, resources, _) = Prepare(Program(EndProgram(0)), ShaderStage.TessellationEvaluation);
        var layout = BindingLayout.Allocate(resources.Info, [], false, false, false, usesTessellationData: true);
        Assert.NotNull(layout.Find(DescriptorBindingKind.ShaderData));
        var shader = Gen5TessellationBridge.CompileControl(layout, domain);
        Gen5LargeDispatcherValidationTests.ValidateWithSpirvToolsWhenAvailable(shader.Spirv);
        var instructions = Instructions(shader.Spirv).ToArray();
        Assert.Contains(instructions, i => i.Op == SpirvOp.EntryPoint && i.Args[0] == 1);
        Assert.Contains(instructions, i => i.Op == SpirvOp.ExecutionMode && i.Args[1] == 26 && i.Args[2] == 1);
        Assert.Equal(factorCount, instructions.Count(i => i.Op == SpirvOp.ConvertUToPtr));
        Assert.Equal(2, instructions.Count(i => i.Op == SpirvOp.Decorate && i.Args[1] == (uint)SpirvDecoration.Patch));
    }

    [Fact]
    public void MergedHullLowering_PublishesLocalOutputsAndPreservesOriginalAddresses()
    {
        var front = Program(Nop(0), Nop(4), Nop(8), EndProgram(256)) with { FusedContinuationPc = 256 };
        var lowered = Gen5TessellationLowering.PrepareMergedHull(front, 14);
        Assert.Equal("SMovB64", lowered.Instructions[0].Opcode);
        Assert.Equal(Gen5Operand.Scalar(14), lowered.Instructions[0].Sources[0]);
        Assert.Equal(Gen5Operand.Scalar(0), lowered.Instructions[0].Destinations[0]);
        Assert.Contains(lowered.Instructions, i => i.Pc == 260 && i.Opcode == "SBarrier");
        Assert.Equal(264u, lowered.FusedContinuationPc);
        Assert.Equal(256ul, lowered.InstructionAddressOffset(264));
        Assert.Equal(0ul, lowered.InstructionAddressOffset(4));
        Assert.Throws<ArgumentException>(() => Gen5TessellationLowering.PrepareMergedHull(Program(EndProgram(0)), 14));
    }

    [Theory]
    [InlineData(32u, 128u)]
    [InlineData(64u, 63u)]
    [InlineData(64u, 320u)]
    public void CooperativeWaves_RejectIncompatibleExecutionShapes(uint waveSize, uint threads)
    {
        var (plan, resources, layout) = Prepare(Program(EndProgram(0)));
        Assert.False(Gen5SpirvTranslator.TryCompileProgram(new(plan, resources, layout)
        { CooperativeWave64Workgroup = true, WaveSize = waveSize, LocalSizeX = threads }, out _, out var error));
        Assert.Contains("Cooperative wave64", error);
    }

    [Fact]
    public void HullConfiguration_UsesDirectCountsAndSeparateInputAndOutputSizes()
    {
        Assert.True(Gen5TessellationHullInfo.TryDecode(63 | (4u << 8) | (4u << 14), 1024, out var info, out var error), error);
        Assert.Equal(new Gen5TessellationHullInfo(4, 4, 63, 1024), info);
        Assert.True(Gen5TessellationHullInfo.TryDecode(8 | (6u << 8) | (12u << 14), 256, out info, out error), error);
        Assert.Equal(new Gen5TessellationHullInfo(6, 12, 8, 256), info);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u | (33u << 8) | (4u << 14))]
    [InlineData(65u | (4u << 8) | (4u << 14))]
    public void HullConfiguration_RejectsUnrepresentableWorkgroups(uint configuration) =>
        Assert.False(Gen5TessellationHullInfo.TryDecode(configuration, 256, out _, out _));

    [Theory]
    [InlineData(2u | (2u << 2) | (3u << 5), Gen5TessellationDomain.Quads, Gen5TessellationSpacing.FractionalOdd, false, false)]
    [InlineData(1u | (3u << 2) | (2u << 5), Gen5TessellationDomain.Triangles, Gen5TessellationSpacing.FractionalEven, false, true)]
    [InlineData(1u << 5, Gen5TessellationDomain.Isolines, Gen5TessellationSpacing.Equal, false, false)]
    [InlineData(2u, Gen5TessellationDomain.Quads, Gen5TessellationSpacing.Equal, true, false)]
    public void Parameters_DecodeIndependentDomainSpacingAndTopology(uint parameter,
        Gen5TessellationDomain domain, Gen5TessellationSpacing spacing, bool point, bool clockwise)
    {
        Assert.True(Gen5TessellationInfo.TryDecode(parameter | (2u << 17), out var info, out var error), error);
        Assert.Equal(new Gen5TessellationInfo(domain, spacing, point, clockwise), info);
    }

    [Theory]
    [InlineData(3u)]
    [InlineData(4u << 2)]
    [InlineData(4u << 5)]
    [InlineData(2u | (1u << 5))]
    [InlineData(2u << 5)]
    public void Parameters_RejectReservedOrIncompatibleFields(uint parameter)
    {
        Assert.False(Gen5TessellationInfo.TryDecode(parameter, out _, out var error));
        Assert.Contains("Invalid tessellator", error);
    }

    [Theory]
    [InlineData(Gen5TessellationDomain.Quads, Gen5TessellationSpacing.FractionalOdd, false, false)]
    [InlineData(Gen5TessellationDomain.Triangles, Gen5TessellationSpacing.FractionalEven, false, true)]
    [InlineData(Gen5TessellationDomain.Isolines, Gen5TessellationSpacing.Equal, false, false)]
    [InlineData(Gen5TessellationDomain.Quads, Gen5TessellationSpacing.Equal, true, false)]
    public void Evaluation_UsesNativeTessellatorInputsAndModes(Gen5TessellationDomain domain,
        Gen5TessellationSpacing spacing, bool point, bool clockwise)
    {
        var program = Program(new Gen5ShaderInstruction(0, Gen5ShaderEncoding.Exp, "Exp", [0u, 0u],
            [Gen5Operand.Vector(5), Gen5Operand.Vector(6), Gen5Operand.Vector(7), Gen5Operand.Source(242)], [],
            new Gen5ExportControl(12, 15, false, true, false)), EndProgram(8));
        var (plan, resources, layout) = Prepare(program, ShaderStage.TessellationEvaluation);
        var request = new ShaderCompileRequest(plan, resources, layout)
        { Tessellation = new(domain, spacing, point, clockwise) };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        Gen5LargeDispatcherValidationTests.ValidateWithSpirvToolsWhenAvailable(shader.Spirv);
        var instructions = Instructions(shader.Spirv).ToArray();
        Assert.Contains(instructions, i => i.Op == SpirvOp.EntryPoint && i.Args[0] == 2);
        Assert.Contains(instructions, i => i.Op == SpirvOp.Decorate && i.Args[1] == (uint)SpirvDecoration.BuiltIn && i.Args[2] == 13);
        Assert.Contains(instructions, i => i.Op == SpirvOp.Decorate && i.Args[1] == (uint)SpirvDecoration.BuiltIn && i.Args[2] == 7);
        var mode = domain switch { Gen5TessellationDomain.Quads => 24u, Gen5TessellationDomain.Triangles => 22u, _ => 25u };
        Assert.Contains(instructions, i => i.Op == SpirvOp.ExecutionMode && i.Args[1] == mode);
        Assert.DoesNotContain(instructions, i => i.Op == SpirvOp.ExecutionMode && i.Args[1] == 17);
    }

    // The ES ABI gives the domain shader u, v, its relative patch and its patch ID in v5..v8.
    [Fact]
    public void Evaluation_FillsBothThePatchWithinItsGroupAndThePatchId()
    {
        var program = Program(new Gen5ShaderInstruction(0, Gen5ShaderEncoding.Exp, "Exp", [0u, 0u],
            [Gen5Operand.Vector(7), Gen5Operand.Vector(8), Gen5Operand.Vector(5), Gen5Operand.Vector(6)], [],
            new Gen5ExportControl(12, 15, false, true, false)), EndProgram(8));
        var (plan, resources, layout) = Prepare(program, ShaderStage.TessellationEvaluation);
        var request = new ShaderCompileRequest(plan, resources, layout)
        { Tessellation = new(Gen5TessellationDomain.Quads, Gen5TessellationSpacing.Equal, false, false) };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        Gen5LargeDispatcherValidationTests.ValidateWithSpirvToolsWhenAvailable(shader.Spirv);
        var instructions = Instructions(shader.Spirv).ToArray();
        var primitiveId = Assert.Single(instructions,
            i => i.Op == SpirvOp.Decorate && i.Args[1] == (uint)SpirvDecoration.BuiltIn && i.Args[2] == 7).Args[0];
        var derived = instructions.Where(i => i.Op == SpirvOp.Load && i.Args[2] == primitiveId).Select(i => i.Args[1]).ToHashSet();
        foreach (var add in instructions.Where(i => i.Op == SpirvOp.IAdd && (derived.Contains(i.Args[2]) || derived.Contains(i.Args[3]))))
            derived.Add(add.Args[1]);
        var stored = instructions.Where(i => i.Op == SpirvOp.Store && derived.Contains(i.Args[1])).Select(i => i.Args[0]).ToHashSet();
        // Two distinct registers receive the native primitive ID: v7 and v8.
        Assert.Equal(2, stored.Count);
    }

    [Fact]
    public void Evaluation_MissingConfigurationIsAnExplicitFailure()
    {
        var (plan, resources, layout) = Prepare(Program(EndProgram(0)), ShaderStage.TessellationEvaluation);
        Assert.False(Gen5SpirvTranslator.TryCompileProgram(new(plan, resources, layout), out _, out var error));
        Assert.Contains("tessellation", error, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<(SpirvOp Op, uint[] Args)> Instructions(byte[] bytes)
    {
        for (var offset = 20; offset < bytes.Length;)
        {
            var header = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
            var count = (int)(header >> 16);
            var args = new uint[count - 1];
            for (var i = 0; i < args.Length; i++) args[i] = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 4 * (i + 1)));
            yield return ((SpirvOp)(header & 0xFFFF), args);
            offset += count * 4;
        }
    }
}
