// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5MeshShaderTests
{
    [Fact]
    public void MergedGeometryProgramLowersToMeshExtModule()
    {
        var request = CreateRequest(CreateMeshProgram(sendMessage: 9));

        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(
                request,
                out var shader,
                out var error),
            error);

        var instructions = Parse(shader.Spirv);
        Assert.Contains(
            instructions,
            instruction =>
                instruction.Opcode == SpirvOp.Capability &&
                instruction.Operands[0] ==
                    (uint)SpirvCapability.MeshShadingEXT);
        Assert.Contains(
            instructions,
            instruction =>
                instruction.Opcode == SpirvOp.EntryPoint &&
                instruction.Operands[0] ==
                    (uint)SpirvExecutionModel.MeshEXT);
        Assert.Contains(
            instructions,
            instruction =>
                instruction.Opcode == SpirvOp.ExecutionMode &&
                instruction.Operands[1] ==
                    (uint)SpirvExecutionMode.LocalSize &&
                instruction.Operands[2..].SequenceEqual([32u, 1u, 1u]));
        Assert.Contains(
            instructions,
            instruction =>
                instruction.Opcode == SpirvOp.ExecutionMode &&
                instruction.Operands[1] ==
                    (uint)SpirvExecutionMode.OutputVertices &&
                instruction.Operands[2] == 64);
        Assert.Contains(
            instructions,
            instruction =>
                instruction.Opcode == SpirvOp.ExecutionMode &&
                instruction.Operands[1] ==
                    (uint)SpirvExecutionMode.OutputPrimitivesEXT &&
                instruction.Operands[2] == 32);
        Assert.Contains(
            instructions,
            instruction =>
                instruction.Opcode == SpirvOp.ExecutionMode &&
                instruction.Operands[1] ==
                    (uint)SpirvExecutionMode.OutputTrianglesEXT);
        Assert.Single(
            instructions,
            static instruction =>
                instruction.Opcode == SpirvOp.SetMeshOutputsEXT);
        Assert.Contains(
            instructions,
            instruction =>
                instruction.Opcode == SpirvOp.Decorate &&
                instruction.Operands.Contains(
                    (uint)SpirvBuiltIn.PrimitiveTriangleIndicesEXT));
        Assert.Contains(
            instructions,
            instruction =>
                instruction.Opcode == SpirvOp.Decorate &&
                instruction.Operands.Contains(
                    (uint)SpirvBuiltIn.CullPrimitiveEXT));
        Assert.Contains(
            instructions,
            instruction =>
                instruction.Opcode == SpirvOp.Decorate &&
                instruction.Operands.Contains(
                    (uint)SpirvDecoration.PerPrimitiveEXT));
        Assert.Contains(
            instructions,
            static instruction =>
                instruction.Opcode == SpirvOp.ConvertUToPtr);
        Assert.True(
            instructions.Count(static instruction =>
                instruction.Opcode == SpirvOp.ControlBarrier) >= 1);
        Assert.Equal(1u, shader.AttributeCount);
    }

    [Fact]
    public void MeshOutputProbeUsesSharedPostEpilogueSamples()
    {
        var baselineRequest = CreateRequest(CreateMeshProgram(sendMessage: 9));
        var tracedRequest = CreateRequest(
            CreateMeshProgram(sendMessage: 9),
            traceMeshOutputs: true);

        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(
                baselineRequest,
                out var baseline,
                out var baselineError),
            baselineError);
        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(
                tracedRequest,
                out var traced,
                out var tracedError),
            tracedError);

        var baselineInstructions = Parse(baseline.Spirv);
        var tracedInstructions = Parse(traced.Spirv);
        static int WorkgroupVariableCount(IReadOnlyList<ParsedInstruction> instructions) =>
            instructions.Count(instruction =>
                instruction.Opcode == SpirvOp.Variable &&
                instruction.Operands[2] == (uint)SpirvStorageClass.Workgroup);

        Assert.Equal(
            WorkgroupVariableCount(baselineInstructions) + 2,
            WorkgroupVariableCount(tracedInstructions));
        Assert.True(
            tracedInstructions.Count(static instruction =>
                instruction.Opcode == SpirvOp.ControlBarrier) >=
            baselineInstructions.Count(static instruction =>
                instruction.Opcode == SpirvOp.ControlBarrier) + 2);
    }

    [Fact]
    public void MeshLayerExportUsesSharedVertexStagingAndProvokingVertex()
    {
        var baselineRequest = CreateRequest(CreateMeshProgram(sendMessage: 9));
        var request = CreateRequest(
            CreateMeshProgram(sendMessage: 9, exportLayer: true),
            positionExportControl: 0x0124_0000,
            provokingVertex: 2);

        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(
                baselineRequest,
                out var baseline,
                out var baselineError),
            baselineError);
        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(
                request,
                out var shader,
                out var error),
            error);

        var baselineInstructions = Parse(baseline.Spirv);
        var instructions = Parse(shader.Spirv);
        Assert.Contains(
            instructions,
            instruction =>
                instruction.Opcode == SpirvOp.Capability &&
                instruction.Operands[0] ==
                    (uint)SpirvCapability.ShaderLayer);

        var layerOutput = Assert.Single(
            instructions,
            instruction =>
                instruction.Opcode == SpirvOp.Decorate &&
                instruction.Operands.Length == 3 &&
                instruction.Operands[1] == (uint)SpirvDecoration.BuiltIn &&
                instruction.Operands[2] == (uint)SpirvBuiltIn.Layer)
            .Operands[0];
        Assert.Contains(
            instructions,
            instruction =>
                instruction.Opcode == SpirvOp.Decorate &&
                instruction.Operands.Length == 2 &&
                instruction.Operands[0] == layerOutput &&
                instruction.Operands[1] ==
                    (uint)SpirvDecoration.PerPrimitiveEXT);
        Assert.Contains(
            instructions,
            instruction =>
                instruction.Opcode == SpirvOp.Variable &&
                instruction.Operands[1] == layerOutput &&
                instruction.Operands[2] == (uint)SpirvStorageClass.Output);

        static int WorkgroupVariableCount(
            IReadOnlyList<ParsedInstruction> module) =>
            module.Count(instruction =>
                instruction.Opcode == SpirvOp.Variable &&
                instruction.Operands[2] ==
                    (uint)SpirvStorageClass.Workgroup);
        Assert.Equal(
            WorkgroupVariableCount(baselineInstructions) + 1,
            WorkgroupVariableCount(instructions));
        var workgroupVariables = instructions
            .Where(instruction =>
                instruction.Opcode == SpirvOp.Variable &&
                instruction.Operands[2] ==
                    (uint)SpirvStorageClass.Workgroup)
            .Select(static instruction => instruction.Operands[1])
            .ToHashSet();

        var constants = instructions
            .Where(static instruction => instruction.Opcode == SpirvOp.Constant)
            .ToDictionary(
                static instruction => instruction.Operands[1],
                static instruction => instruction.Operands[2]);
        bool IsConstant(uint id, uint value) =>
            constants.TryGetValue(id, out var actual) && actual == value;
        var provokingVertexValues = instructions
            .Where(instruction =>
                instruction.Opcode == SpirvOp.BitFieldUExtract &&
                IsConstant(instruction.Operands[3], 20) &&
                IsConstant(instruction.Operands[4], 10))
            .Select(static instruction => instruction.Operands[1])
            .ToHashSet();
        Assert.NotEmpty(provokingVertexValues);
        Assert.Contains(
            instructions,
            instruction =>
                instruction.Opcode == SpirvOp.AccessChain &&
                workgroupVariables.Contains(instruction.Operands[2]) &&
                provokingVertexValues.Contains(instruction.Operands[^1]));
        Assert.Contains(
            instructions,
            instruction =>
                instruction.Opcode == SpirvOp.BitwiseAnd &&
                (IsConstant(instruction.Operands[2], 0x7ff) ||
                 IsConstant(instruction.Operands[3], 0x7ff)));

        var layerOutputPointers = instructions
            .Where(instruction =>
                instruction.Opcode == SpirvOp.AccessChain &&
                instruction.Operands[2] == layerOutput)
            .Select(static instruction => instruction.Operands[1])
            .ToHashSet();
        Assert.NotEmpty(layerOutputPointers);
        Assert.Contains(
            instructions,
            instruction =>
                instruction.Opcode == SpirvOp.Store &&
                layerOutputPointers.Contains(instruction.Operands[0]));
    }

    [Fact]
    public void MeshPositionAppliesClipSpaceTransform()
    {
        var baselineRequest = CreateRequest(CreateMeshProgram(sendMessage: 9));
        var transformedRequest = CreateRequest(
            CreateMeshProgram(sendMessage: 9),
            clipSpace: new ShaderClipSpaceTransform(
                true,
                3,
                4,
                5,
                6,
                8192,
                8192));

        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(
                baselineRequest,
                out var baseline,
                out var baselineError),
            baselineError);
        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(
                transformedRequest,
                out var transformed,
                out var transformedError),
            transformedError);

        var baselineInstructions = Parse(baseline.Spirv);
        var transformedInstructions = Parse(transformed.Spirv);
        foreach (var opcode in new[]
                 {
                     SpirvOp.FMul,
                     SpirvOp.FAdd,
                     SpirvOp.FDiv,
                     SpirvOp.FSub,
                 })
        {
            Assert.Equal(0, baselineInstructions.Count(
                instruction => instruction.Opcode == opcode));
            // Wave64 on a subgroup32 host emits one translated instruction
            // stream for each paired half, so both X/Y transforms occur twice.
            Assert.Equal(4, transformedInstructions.Count(
                instruction => instruction.Opcode == opcode));
        }
    }

    [Fact]
    public void MeshSendMessagePreservesAndValidatesImmediate()
    {
        var request = CreateRequest(CreateMeshProgram(sendMessage: 8));

        Assert.False(
            Gen5SpirvTranslator.TryCompileProgram(
                request,
                out _,
                out var error));
        Assert.Contains("MSG_GS_ALLOC_REQ", error, StringComparison.Ordinal);
    }

    [Fact]
    public void PairedWave64MeshSendMessageUsesTheGuestInvocationIndex()
    {
        var request = CreateRequest(CreateMeshProgram(sendMessage: 9));

        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(
                request,
                out var shader,
                out var error),
            error);

        var instructions = Parse(shader.Spirv);
        var localInvocationIndex = Assert.Single(
            instructions,
            instruction =>
                instruction.Opcode == SpirvOp.Decorate &&
                instruction.Operands.Length == 3 &&
                instruction.Operands[1] == (uint)SpirvDecoration.BuiltIn &&
                instruction.Operands[2] ==
                    (uint)SpirvBuiltIn.LocalInvocationIndex)
            .Operands[0];
        Assert.DoesNotContain(
            instructions,
            instruction =>
                instruction.Opcode == SpirvOp.Load &&
                instruction.Operands[2] == localInvocationIndex);
    }

    [Fact]
    public void UnsignedSopkCompareZeroExtendsImmediate()
    {
        // Exact Trek to Yomi instruction: s_cmpk_le_u32 s4, 0xffff.
        // The unsigned SOPK form must compare against 65535, not the
        // sign-extended UINT_MAX used by signed SOPK operations.
        var compare = new Gen5ShaderInstruction(
            0,
            Gen5ShaderEncoding.Sopk,
            "SCmpkLeU32",
            [0xB704FFFFu],
            [new Gen5Operand(Gen5OperandKind.EncodedConstant, 0xFFFFu)],
            [Gen5Operand.Scalar(4)],
            null);
        var baseProgram = CreateMeshProgram(sendMessage: 9);
        var program = new Gen5ShaderProgram(
            0,
            [
                compare,
                .. baseProgram.Instructions.Select(instruction => instruction with
                {
                    Pc = instruction.Pc + sizeof(uint),
                }),
            ]);

        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(
                CreateRequest(program),
                out var shader,
                out var error),
            error);

        var instructions = Parse(shader.Spirv);
        var constants = instructions
            .Where(static instruction =>
                instruction.Opcode == SpirvOp.Constant &&
                instruction.Operands.Length >= 3)
            .ToDictionary(
                static instruction => instruction.Operands[1],
                static instruction => instruction.Operands[2]);
        var compareInstruction = Assert.Single(
            instructions,
            static instruction =>
                instruction.Opcode == SpirvOp.ULessThanEqual);
        Assert.Equal(0xFFFFu, constants[compareInstruction.Operands[3]]);
    }

    [Fact]
    public void MeshShaderDataMustFollowDrawParameters()
    {
        var program = CreateMeshProgram(sendMessage: 9);
        var request = CreateRequest(program, pushDataStartDword: 0);

        Assert.False(
            Gen5SpirvTranslator.TryCompileProgram(
                request,
                out _,
                out var error));
        Assert.Contains("overlaps", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("dpp16")]
    [InlineData("dpp8")]
    [InlineData("permlane16")]
    [InlineData("permlanex16")]
    [InlineData("readfirstlane")]
    public void PairableWave64OperationsCompileForMultiWaveMesh(
        string operationName)
    {
        var operation = operationName switch
        {
            "dpp16" => new Gen5ShaderInstruction(
                0,
                Gen5ShaderEncoding.Vop1,
                "VMovB32",
                [0u],
                [Gen5Operand.Vector(0)],
                [Gen5Operand.Vector(1)],
                new Gen5DppControl(0x101, false, true, 0, 0, 0xF, 0xF)),
            "dpp8" => new Gen5ShaderInstruction(
                0,
                Gen5ShaderEncoding.Vop1,
                "VMovB32",
                [0u],
                [Gen5Operand.Vector(0)],
                [Gen5Operand.Vector(1)],
                new Gen5Dpp8Control(0xFAC688u, false)),
            "permlane16" or "permlanex16" => new Gen5ShaderInstruction(
                0,
                Gen5ShaderEncoding.Vop3,
                operationName == "permlane16"
                    ? "VPermlane16B32"
                    : "VPermlanex16B32",
                [0u, 0u],
                [Gen5Operand.Vector(0), Gen5Operand.Scalar(2), Gen5Operand.Scalar(3)],
                [Gen5Operand.Vector(1)],
                new Gen5Vop3Control(0, 0, 0, false, 0, null)),
            "readfirstlane" => ResourceTestProgram.ReadFirstLane(
                0,
                scalarDestination: 10,
                vectorSource: 0),
            _ => throw new ArgumentOutOfRangeException(nameof(operationName)),
        };
        var bodyBytes = checked((uint)(operation.Words.Count * sizeof(uint)));
        var baseProgram = CreateMeshProgram(sendMessage: 9);
        var program = new Gen5ShaderProgram(
            0,
            [
                operation,
                .. baseProgram.Instructions.Select(instruction => instruction with
                {
                    Pc = instruction.Pc + bodyBytes,
                }),
            ]);
        var request = CreateRequest(program, localSizeX: 128);

        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(
                request,
                out var shader,
                out var error),
            error);

        Assert.Contains(
            Parse(shader.Spirv),
            instruction =>
                instruction.Opcode == SpirvOp.ExecutionMode &&
                instruction.Operands[1] == (uint)SpirvExecutionMode.LocalSize &&
                instruction.Operands[2..].SequenceEqual([64u, 1u, 1u]));
    }

    [Theory]
    [InlineData(0x130u)]
    [InlineData(0x134u)]
    [InlineData(0x138u)]
    [InlineData(0x13Cu)]
    [InlineData(0x142u)]
    [InlineData(0x143u)]
    public void WaveCrossingDppIsRejectedForPairedWave64Mesh(uint control)
    {
        var operation = new Gen5ShaderInstruction(
            0,
            Gen5ShaderEncoding.Vop1,
            "VMovB32",
            [0u],
            [Gen5Operand.Vector(0)],
            [Gen5Operand.Vector(1)],
            new Gen5DppControl(control, false, true, 0, 0, 0xF, 0xF));
        var bodyBytes = checked((uint)(operation.Words.Count * sizeof(uint)));
        var baseProgram = CreateMeshProgram(sendMessage: 9);
        var program = new Gen5ShaderProgram(
            0,
            [
                operation,
                .. baseProgram.Instructions.Select(instruction => instruction with
                {
                    Pc = instruction.Pc + bodyBytes,
                }),
            ]);

        Assert.False(
            Gen5SpirvTranslator.TryCompileProgram(
                CreateRequest(program, localSizeX: 128),
                out _,
                out var error));
        Assert.Contains(
            $"unsupported DPP16 control 0x{control:X3}",
            error,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PairedWave64MeshUsesSubgroupIdentityForGuestWaveBase()
    {
        var request = CreateRequest(
            CreateMeshProgram(sendMessage: 9),
            localSizeX: 128);

        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(
                request,
                out var shader,
                out var error),
            error);

        var instructions = Parse(shader.Spirv);
        var constants = instructions
            .Where(static instruction => instruction.Opcode == SpirvOp.Constant)
            .ToDictionary(
                static instruction => instruction.Operands[1],
                static instruction => instruction.Operands[2]);
        bool IsConstant(uint id, uint value) =>
            constants.TryGetValue(id, out var actual) && actual == value;
        var subgroupId = Assert.Single(
            instructions,
            instruction =>
                instruction.Opcode == SpirvOp.Decorate &&
                instruction.Operands.Length == 3 &&
                instruction.Operands[1] == (uint)SpirvDecoration.BuiltIn &&
                instruction.Operands[2] == (uint)SpirvBuiltIn.SubgroupId)
            .Operands[0];
        var subgroupLoads = instructions
            .Where(instruction =>
                instruction.Opcode == SpirvOp.Load &&
                instruction.Operands[2] == subgroupId)
            .Select(static instruction => instruction.Operands[1])
            .ToHashSet();
        Assert.NotEmpty(subgroupLoads);
        var waveBases = instructions
            .Where(instruction =>
                instruction.Opcode == SpirvOp.IMul &&
                ((subgroupLoads.Contains(instruction.Operands[2]) &&
                  IsConstant(instruction.Operands[3], 64)) ||
                 (subgroupLoads.Contains(instruction.Operands[3]) &&
                  IsConstant(instruction.Operands[2], 64))))
            .Select(static instruction => instruction.Operands[1])
            .ToHashSet();
        Assert.NotEmpty(waveBases);

        var subgroupLocalId = Assert.Single(
            instructions,
            instruction =>
                instruction.Opcode == SpirvOp.Decorate &&
                instruction.Operands.Length == 3 &&
                instruction.Operands[1] == (uint)SpirvDecoration.BuiltIn &&
                instruction.Operands[2] ==
                    (uint)SpirvBuiltIn.SubgroupLocalInvocationId)
            .Operands[0];
        var subgroupLocalLoads = instructions
            .Where(instruction =>
                instruction.Opcode == SpirvOp.Load &&
                instruction.Operands[2] == subgroupLocalId)
            .Select(static instruction => instruction.Operands[1])
            .ToHashSet();
        var laneBases = instructions
            .Where(instruction =>
                instruction.Opcode == SpirvOp.IAdd &&
                ((waveBases.Contains(instruction.Operands[2]) &&
                  subgroupLocalLoads.Contains(instruction.Operands[3])) ||
                 (waveBases.Contains(instruction.Operands[3]) &&
                  subgroupLocalLoads.Contains(instruction.Operands[2]))))
            .Select(static instruction => instruction.Operands[1])
            .ToHashSet();
        Assert.NotEmpty(laneBases);
        Assert.Contains(
            instructions,
            instruction =>
                instruction.Opcode == SpirvOp.IAdd &&
                ((laneBases.Contains(instruction.Operands[2]) &&
                  IsConstant(instruction.Operands[3], 0)) ||
                 (laneBases.Contains(instruction.Operands[3]) &&
                  IsConstant(instruction.Operands[2], 0))));
        Assert.Contains(
            instructions,
            instruction =>
                instruction.Opcode == SpirvOp.IAdd &&
                ((laneBases.Contains(instruction.Operands[2]) &&
                  IsConstant(instruction.Operands[3], 32)) ||
                 (laneBases.Contains(instruction.Operands[3]) &&
                  IsConstant(instruction.Operands[2], 32))));
        Assert.Contains(
            instructions,
            instruction =>
                instruction.Opcode == SpirvOp.ExecutionMode &&
                instruction.Operands[1] == (uint)SpirvExecutionMode.LocalSize &&
                instruction.Operands[2..].SequenceEqual([64u, 1u, 1u]));
    }

    [Fact]
    public void PairedWave64MeshRejectsPartialFinalGuestWave()
    {
        var request = CreateRequest(
            CreateMeshProgram(sendMessage: 9),
            localSizeX: 96);

        Assert.False(
            Gen5SpirvTranslator.TryCompileProgram(
                request,
                out _,
                out var error));
        Assert.Contains("multiple of 64", error, StringComparison.Ordinal);
        Assert.Contains("96", error, StringComparison.Ordinal);
    }

    [Fact]
    public void NonPairableWave64DiagnosticNamesEveryBlockingOperation()
    {
        Gen5ShaderInstruction[] blockers =
        [
            ResourceTestProgram.DataShare(
                0,
                "DsAppend",
                gds: false,
                [Gen5Operand.Scalar(124)],
                [1]),
            ResourceTestProgram.DataShare(
                8,
                "DsSwizzleB32",
                gds: false,
                [Gen5Operand.Vector(0)],
                [2]),
        ];
        var baseProgram = CreateMeshProgram(sendMessage: 9);
        var program = new Gen5ShaderProgram(
            0,
            [
                .. blockers,
                .. baseProgram.Instructions.Select(instruction => instruction with
                {
                    Pc = instruction.Pc + 16,
                }),
            ]);
        var request = CreateRequest(program, localSizeX: 128);

        Assert.False(
            Gen5SpirvTranslator.TryCompileProgram(
                request,
                out _,
                out var error));
        Assert.Contains("blockers=[", error, StringComparison.Ordinal);
        Assert.Contains("DsAppend@0x0", error, StringComparison.Ordinal);
        Assert.DoesNotContain("DsSwizzleB32@0x8", error, StringComparison.Ordinal);
    }

    private static ShaderCompileRequest CreateRequest(
        Gen5ShaderProgram program,
        uint pushDataStartDword = ShaderMeshInfo.DrawDwordCount,
        uint localSizeX = 64,
        uint positionExportControl = 0,
        uint provokingVertex = 0,
        ShaderClipSpaceTransform clipSpace = default,
        bool traceMeshOutputs = false)
    {
        var plan = ShaderResourcePlan.Extract(
            program,
            ShaderStage.Mesh,
            ResourceTestProgram.Hash,
            userDataBase: 0,
            userDataCount: 9,
            waveSize: 64);
        // Mesh entry setup reads an optional 8/16/32-bit index from the guest
        // address passed in draw dwords 4:5.
        plan.Info.UsesDeviceAddresses = true;
        var resources = ResourceMaterializer.ApplyTo(
            plan,
            ResourceSpecialization.Default(plan.Info));
        var layout = BindingLayout.Allocate(
            resources.Info,
            BindingLayout.CollectUserDataRegisters(
                program,
                userDataBase: 0,
                userDataCount: 9,
                waveSize: 64),
            usesGlobalDataShare: false,
            usesFlattenedTable: false,
            usesShaderBase: false,
            pushDataStartDword);
        return new ShaderCompileRequest(plan, resources, layout)
        {
            Mesh = new ShaderMeshInfo(
                InputPrimitive: 4,
                PrimitivesPerGroup: 16,
                VerticesPerGroup: 48,
                MaxVertices: 64,
                MaxPrimitives: 32,
                OutputPrimitive: 2,
                ProvokingVertex: provokingVertex),
            WaveSize = 64,
            HostSubgroupSize = 32,
            LocalDataShareDwords = 256,
            LocalSizeX = localSizeX,
            LocalSizeY = 1,
            LocalSizeZ = 1,
            RequiredVertexOutputCount = 1,
            PositionExportControl = positionExportControl,
            ClipSpace = clipSpace,
            TraceMeshOutputs = traceMeshOutputs,
        };
    }

    private static Gen5ShaderProgram CreateMeshProgram(
        uint sendMessage,
        bool exportLayer = false)
    {
        var positionExport = new Gen5ShaderInstruction(
            12,
            Gen5ShaderEncoding.Exp,
            "Exp",
            [0u, 0u],
            [
                Gen5Operand.Vector(0),
                Gen5Operand.Vector(1),
                Gen5Operand.Vector(2),
                Gen5Operand.Vector(3),
            ],
            [],
            new Gen5ExportControl(12, 0xF, false, true, false));
        var layerExport = positionExport with
        {
            Pc = 20,
            Control = new Gen5ExportControl(13, 0x4, false, true, false),
        };
        var parameterExport = positionExport with
        {
            Pc = exportLayer ? 28u : 20u,
            Control = new Gen5ExportControl(32, 0xF, false, false, false),
        };
        var primitiveExport = positionExport with
        {
            Pc = exportLayer ? 36u : 28u,
            Control = new Gen5ExportControl(0x14, 1, false, false, false),
        };
        var instructions = new List<Gen5ShaderInstruction>
        {
            ResourceTestProgram.MoveScalar(
                0,
                destination: 124,
                value: 3u | (1u << 12)),
            new Gen5ShaderInstruction(
                4,
                Gen5ShaderEncoding.Sopp,
                "SSendmsg",
                [sendMessage],
                [],
                [],
                null),
            ResourceTestProgram.MoveVectorFromScalar(
                8,
                destination: 10,
                scalarRegister: 8),
            positionExport,
        };
        if (exportLayer)
        {
            instructions.Add(layerExport);
        }
        instructions.Add(parameterExport);
        instructions.Add(primitiveExport);
        instructions.Add(ResourceTestProgram.EndProgram(
            exportLayer ? 44u : 36u));
        return new Gen5ShaderProgram(0, instructions);
    }

    private static IReadOnlyList<ParsedInstruction> Parse(byte[] spirv)
    {
        var result = new List<ParsedInstruction>();
        for (var offset = 20; offset < spirv.Length;)
        {
            var first = BinaryPrimitives.ReadUInt32LittleEndian(
                spirv.AsSpan(offset));
            var wordCount = checked((int)(first >> 16));
            var operands = new uint[wordCount - 1];
            for (var index = 0; index < operands.Length; index++)
            {
                operands[index] = BinaryPrimitives.ReadUInt32LittleEndian(
                    spirv.AsSpan(offset + (index + 1) * sizeof(uint)));
            }

            result.Add(new ParsedInstruction(
                (SpirvOp)(first & 0xFFFF),
                operands));
            offset += wordCount * sizeof(uint);
        }

        return result;
    }

    private readonly record struct ParsedInstruction(
        SpirvOp Opcode,
        uint[] Operands);
}
