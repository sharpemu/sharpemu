// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Metal;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

public sealed class EmbeddedVertexFetchDetectorTests
{
    private const int AttributeTable = 4;
    private const int BufferTable = 6;

    private static Gen5ShaderInstruction FetchLoad(uint pc, uint resourceRegister, uint dwords) =>
        BufferAccess(pc, "BufferLoadFormat" + dwords switch { 1 => "X", 2 => "Xy", 3 => "Xyz", _ => "Xyzw" }, resourceRegister,
            dwords: dwords, vectorData: 4, indexEnabled: true, vectorAddress: 1);

    private static Gen5ShaderInstruction IndexSelect(uint pc) =>
        Vop2(pc, "VCndmaskB32", 1, Gen5Operand.Vector(8), Gen5Operand.Vector(5));

    [Theory]
    [InlineData(124u)]
    [InlineData(126u)]
    [InlineData(127u)]
    public void SpecialScalarWritesRemainInTheProgramWithoutEnteringFetchTracking(uint specialRegister)
    {
        var instructions = new List<Gen5ShaderInstruction>
        {
            ScalarLoad(0, AttributeTable, destination: 8, count: 4),
            MoveScalar(8, specialRegister, 16),
            MoveScalarRegister(12, specialRegister, 8),
            new(16, Gen5ShaderEncoding.Sopk, "SMovkI32", [0u],
                [new(Gen5OperandKind.LiteralConstant, 16)], [Gen5Operand.Scalar(specialRegister)], null),
        };
        uint instructionAddress = 20;
        foreach (var opcode in new[] { "SAndB32", "SLshlB32", "SBfeU32", "SAddI32", "SAddU32" })
        {
            instructions.Add(Sop2(instructionAddress, opcode, specialRegister, Gen5Operand.Scalar(8), Operand(1)));
            instructions.Add(Sop2(instructionAddress + 4, opcode, specialRegister, Operand(16), Operand(1)));
            instructionAddress += 8;
        }

        instructions.Add(ScalarLoad(instructionAddress, BufferTable, destination: 12, count: 4));
        instructions.Add(IndexSelect(instructionAddress + 8));
        instructions.Add(FetchLoad(instructionAddress + 12, 12, 4));
        instructions.Add(EndProgram(instructionAddress + 20));
        var program = Program(instructions.ToArray());
        var plan = EmbeddedVertexFetchDetector.Detect(program, AttributeTable, BufferTable, 0, 8, 32);

        var load = Assert.Single(plan.Loads);
        Assert.Equal(0, load.AttributeId);
        Assert.Equal([instructionAddress], load.PrologLoads);
        var prepared = plan.RemoveReplacedTableLoads(program);
        Assert.Equal(instructions.Take(instructions.Count - 4), prepared.Instructions.Take(instructions.Count - 4));
    }

    [Theory]
    [InlineData(105u)]
    [InlineData(106u)]
    [InlineData(107u)]
    public void UpperGeneralAndConditionRegistersStillCarryFetchOffsets(uint scalarRegister)
    {
        var program = Program(
            MoveScalar(0, scalarRegister, 16),
            ScalarLoad(4, BufferTable, destination: 12, count: 4, dynamicOffsetRegister: scalarRegister),
            IndexSelect(12),
            FetchLoad(16, 12, 4),
            EndProgram(24));

        var plan = EmbeddedVertexFetchDetector.Detect(program, AttributeTable, BufferTable, 0, 8, 32);
        Assert.Equal(1, Assert.Single(plan.Loads).AttributeId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReplacedFetchTablesDoNotRequireDeviceAddressesButOtherReadsStillDo(bool additionalRead)
    {
        var program = Program(
            ScalarLoad(0, AttributeTable, destination: 8, count: 4, immediateOffset: 8),
            Sop2(8, "SLshlB32", 9, Gen5Operand.Scalar(10), Operand(4)),
            ScalarLoad(12, BufferTable, destination: 12, count: 4, dynamicOffsetRegister: 9),
            IndexSelect(20),
            FetchLoad(24, 12, 4),
            additionalRead ? ScalarLoad(32, BufferTable, destination: 20, count: 1, dynamicOffsetRegister: 2) : EndProgram(32),
            EndProgram(40));
        var fetch = EmbeddedVertexFetchDetector.Detect(program, AttributeTable, BufferTable, 0, 8, 32);
        Assert.Single(fetch.Loads);
        Assert.True(Extract(program, userDataCount: 8, stage: ShaderStage.Vertex).Info.UsesDeviceAddresses);

        var prepared = fetch.RemoveReplacedTableLoads(program);
        var plan = ShaderResourcePlan.Extract(prepared, ShaderStage.Vertex, Hash, 0, 8,
            fetch.Loads.Select(load => load.Pc).ToHashSet());
        Assert.Equal(additionalRead, plan.Info.UsesDeviceAddresses);
        Assert.Empty(plan.Info.Buffers);
        Assert.Empty(plan.TableReads);
        Assert.Equal("SNop", prepared.Instructions[0].Opcode);
        Assert.Equal("SNop", prepared.Instructions[2].Opcode);
        Assert.Null(plan.Memory.Find(24));
        Assert.Equal(program.Instructions.Select(instruction => instruction.Pc), prepared.Instructions.Select(instruction => instruction.Pc));

        var resources = ResourceMaterializer.ApplyTo(plan, ResourceSpecialization.Default(plan.Info));
        var layout = BindingLayout.Allocate(resources.Info, BindingLayout.CollectUserDataRegisters(prepared, 0, 8), false, false, false, 0);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            VertexInputs = [new ShaderVertexInput(24, 0, 4, 7, false, [])],
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var spirv, out var spirvError), spirvError);
        Assert.Equal(additionalRead ? 5348u : 0u, new SpirvModuleInspector(spirv.Spirv).AddressingModel);
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out var metal, out var metalError), metalError);
        Assert.DoesNotContain("s[12] = sharpemu_load_device_dword", metal.Source);
    }

    [Fact]
    public void BufferTableWalk_YieldsOneLoadPerAttributeBuffer()
    {
        var program = Program(
            Vop2(0, "VAddI32", 0, Gen5Operand.Scalar(16), Gen5Operand.Vector(0)),
            Vop2(4, "VAddI32", 3, Gen5Operand.Scalar(17), Gen5Operand.Vector(3)),
            ScalarLoad(8, BufferTable, destination: 12, count: 4, immediateOffset: 16),
            IndexSelect(16),
            FetchLoad(20, 12, 3),
            EndProgram(28));

        var plan = EmbeddedVertexFetchDetector.Detect(program, AttributeTable, BufferTable, userDataBase: 0, userDataCount: 20, waveSize: 32);

        var load = Assert.Single(plan.Loads);
        Assert.Equal(20u, load.Pc);
        Assert.Equal(1, load.AttributeId);
        Assert.Equal(3u, load.Components);
        Assert.Equal([8u], load.PrologLoads);
        Assert.Equal(16, plan.VertexOffsetScalarRegister);
        Assert.Equal(17, plan.InstanceOffsetScalarRegister);
    }

    [Fact]
    public void AttributeTableWalk_CarriesTheAttributeIdThroughTheBufferLoad()
    {
        var program = Program(
            ScalarLoad(0, AttributeTable, destination: 8, count: 4, immediateOffset: 8),
            Sop2(8, "SLshlB32", 9, Gen5Operand.Scalar(10), Operand(4)),
            ScalarLoad(12, BufferTable, destination: 12, count: 4, dynamicOffsetRegister: 9),
            IndexSelect(20),
            FetchLoad(24, 12, 4),
            EndProgram(32));

        var plan = EmbeddedVertexFetchDetector.Detect(program, AttributeTable, BufferTable, userDataBase: 0, userDataCount: 8, waveSize: 32);

        var load = Assert.Single(plan.Loads);
        Assert.Equal(4, load.AttributeId);
        Assert.Equal([0u, 12u], load.PrologLoads);
        Assert.Equal(ShaderResourceInfo.NoScalarRegister, plan.VertexOffsetScalarRegister);
    }

    [Fact]
    public void ConflictingOffsetRegisters_LeaveTheOffsetUnresolved()
    {
        var program = Program(
            Vop2(0, "VAddI32", 0, Gen5Operand.Scalar(16), Gen5Operand.Vector(0)),
            Vop2(4, "VAddI32", 0, Gen5Operand.Scalar(17), Gen5Operand.Vector(0)),
            ScalarLoad(8, BufferTable, destination: 12, count: 4),
            IndexSelect(16),
            FetchLoad(20, 12, 1),
            EndProgram(28));

        var plan = EmbeddedVertexFetchDetector.Detect(program, AttributeTable, BufferTable, userDataBase: 0, userDataCount: 20, waveSize: 32);

        Assert.Single(plan.Loads);
        Assert.Equal(ShaderResourceInfo.NoScalarRegister, plan.VertexOffsetScalarRegister);
    }

    [Fact]
    public void ScalarOffsetAccumulatedThroughSad_UsesTheUserDataAtEightLayout()
    {
        var program = Program(
            Vop3(0, "VSadU32", 5, Gen5Operand.Scalar(10), Operand(0), Gen5Operand.Vector(5)),
            ScalarLoad(8, BufferTable, destination: 12, count: 4),
            IndexSelect(16),
            FetchLoad(20, 12, 2),
            EndProgram(28));

        var plan = EmbeddedVertexFetchDetector.Detect(program, AttributeTable, BufferTable, userDataBase: 8, userDataCount: 16, waveSize: 32);

        Assert.Single(plan.Loads);
        Assert.Equal(10, plan.VertexOffsetScalarRegister);
    }

    [Fact]
    public void LaneSavedTableRecord_SurvivesTheRoundTrip()
    {
        var program = Program(
            ScalarLoad(0, BufferTable, destination: 12, count: 4),
            WriteLane(8, vectorRegister: 20, scalarRegister: 12, lane: 3),
            MoveScalar(16, 12, 0),
            ReadLane(20, scalarRegister: 12, vectorRegister: 20, lane: 3),
            IndexSelect(28),
            FetchLoad(32, 12, 4),
            EndProgram(40));

        var plan = EmbeddedVertexFetchDetector.Detect(program, AttributeTable, BufferTable, userDataBase: 0, userDataCount: 8, waveSize: 32);

        var load = Assert.Single(plan.Loads);
        Assert.Equal(0, load.AttributeId);
    }

    [Fact]
    public void LoadWithoutTheIndexSelect_IsNotAFetch()
    {
        var program = Program(
            ScalarLoad(0, BufferTable, destination: 12, count: 4),
            FetchLoad(8, 12, 4),
            EndProgram(16));

        var plan = EmbeddedVertexFetchDetector.Detect(program, AttributeTable, BufferTable, userDataBase: 0, userDataCount: 8, waveSize: 32);

        Assert.Empty(plan.Loads);
    }
}
