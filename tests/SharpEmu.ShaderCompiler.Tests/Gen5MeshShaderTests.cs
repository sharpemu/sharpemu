// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5MeshShaderTests
{
    [Fact]
    public void MeshUserPointerAndWaveInputUseSeparateInitialization()
    {
        var program = ResourceTestProgram.Program(
            ResourceTestProgram.Sop1(0, "SMovB64", 20, Gen5Operand.Scalar(0)),
            ResourceTestProgram.MoveScalarRegister(4, 22, 3),
            ResourceTestProgram.MoveScalarRegister(8, 23, 8),
            ResourceTestProgram.EndProgram(12));
        var (plan, resources, layout) = ResourceTestProgram.Prepare(program, ShaderStage.Mesh, 0, 9);
        Assert.Equal(new uint[] { 0, 1, 8 }, layout.UserDataRegisters);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            WaveSize = 32, LocalSizeX = 32,
            Mesh = new MeshShaderConfiguration(32, 16, 0, 8, 24, 0, true, 32),
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        ValidateWithInstalledSdk(shader.Spirv);
    }

    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public Gen5MeshShaderTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData("SAndSaveexecB64")]
    [InlineData("SOrSaveexecB64")]
    [InlineData("SXorSaveexecB64")]
    [InlineData("SAndn2SaveexecB64")]
    [InlineData("SAndn1SaveexecB64")]
    [InlineData("SOrn1SaveexecB64")]
    [InlineData("SOrn2SaveexecB64")]
    [InlineData("SNandSaveexecB64")]
    [InlineData("SNorSaveexecB64")]
    [InlineData("SXnorSaveexecB64")]
    public void Wave32SaveexecUses64BitMask(string opcode)
    {
        foreach (var stage in new[] { ShaderStage.Compute, ShaderStage.Mesh })
        {
            var program = ResourceTestProgram.Program(
                ResourceTestProgram.Sop1(0, opcode, 4, Gen5Operand.Scalar(0)),
                ResourceTestProgram.EndProgram(4));
            var (plan, resources, layout) = ResourceTestProgram.Prepare(program, stage);
            var request = new ShaderCompileRequest(plan, resources, layout)
            {
                WaveSize = 32, LocalSizeX = 32,
                Mesh = stage == ShaderStage.Mesh
                    ? new MeshShaderConfiguration(32, 16, 0, 8, 24, 0, true, 32)
                    : null,
            };
            Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
            var words = new uint[shader.Spirv.Length / sizeof(uint)];
            Buffer.BlockCopy(shader.Spirv, 0, words, 0, shader.Spirv.Length);
            var integerWidths = new Dictionary<uint, uint>();
            var valueTypes = new Dictionary<uint, uint>();
            var masks = new HashSet<uint>();
            var maskCount = 0;
            for (var offset = 5; offset < words.Length; offset += (int)(words[offset] >> 16))
            {
                var operation = (SpirvOp)(words[offset] & 0xffff);
                if (operation == SpirvOp.TypeInt)
                    integerWidths[words[offset + 1]] = words[offset + 2];
                if (operation is SpirvOp.Constant or SpirvOp.Not or SpirvOp.BitwiseAnd or
                    SpirvOp.BitwiseOr or SpirvOp.BitwiseXor)
                    valueTypes[words[offset + 2]] = words[offset + 1];
                if (operation == SpirvOp.Constant && words[offset] >> 16 == 5 &&
                    words[offset + 3] == uint.MaxValue && words[offset + 4] == 0)
                    masks.Add(words[offset + 2]);
                if (operation != SpirvOp.BitwiseAnd || !masks.Contains(words[offset + 4]))
                    continue;

                var resultType = words[offset + 1];
                Assert.Equal(64u, integerWidths[resultType]);
                Assert.Equal(resultType, valueTypes[words[offset + 3]]);
                Assert.Equal(resultType, valueTypes[words[offset + 4]]);
                maskCount++;
            }
            Assert.True(maskCount > 0);
            ValidateWithInstalledSdk(shader.Spirv);
        }
    }

    [Theory]
    [InlineData("DsAppend", SpirvOp.AtomicIAdd)]
    [InlineData("DsConsume", SpirvOp.AtomicISub)]
    public void MeshWaveCountersUseWorkgroupAtomics(string opcode, SpirvOp atomic)
    {
        var program = ResourceTestProgram.Program(
            ResourceTestProgram.DataShare(0, opcode, false, [Gen5Operand.Scalar(124)], [1]),
            ResourceTestProgram.EndProgram(8));
        var (plan, resources, layout) = ResourceTestProgram.Prepare(program, ShaderStage.Mesh);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            WaveSize = 32, LocalSizeX = 64,
            Mesh = new MeshShaderConfiguration(64, 16, 0, 8, 24, 256, true, 32),
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        Assert.Contains((ushort)atomic, new SpirvModuleInspector(shader.Spirv).Opcodes);
        ValidateWithInstalledSdk(shader.Spirv);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData((1u << 21) | (1u << 18))]
    public void MeshAuxiliaryExportsIgnoreDisabledOutputs(uint positionControl)
    {
        var program = ResourceTestProgram.Program(
            new Gen5ShaderInstruction(0, Gen5ShaderEncoding.Exp, "Exp", [0u, 0u],
                [Gen5Operand.Vector(0), Gen5Operand.Vector(1), Gen5Operand.Vector(2), Gen5Operand.Vector(3)], [],
                new Gen5ExportControl(13, 15, false, false, false)), ResourceTestProgram.EndProgram(8));
        var (plan, resources, layout) = ResourceTestProgram.Prepare(program, ShaderStage.Mesh);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            WaveSize = 32, LocalSizeX = 32, PositionExportControl = positionControl,
            Mesh = new MeshShaderConfiguration(32, 16, 0, 8, 24, 0, true, 32),
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        ValidateWithInstalledSdk(shader.Spirv);
    }

    [Fact]
    public void PairedMeshStorageKeepsTwoPersistentPrivateBanks()
    {
        var instructions = new List<Gen5ShaderInstruction>();
        for (uint index = 0; index < 3; index++)
        {
            instructions.Add(ResourceTestProgram.Vop2(index * 16, "VCvtPkrtzF16F32", 1,
                Gen5Operand.Vector(0), Gen5Operand.Vector(1)));
            instructions.Add(new Gen5ShaderInstruction(index * 16 + 8, Gen5ShaderEncoding.Flat,
                "ScratchStoreDword", [0, 0], [], [],
                new Gen5GlobalMemoryControl(1, 0, 1, 0, 125, 0, false, false)));
        }
        instructions.Add(ResourceTestProgram.EndProgram(48));
        var program = ResourceTestProgram.Program(instructions.ToArray());
        var (plan, resources, layout) = ResourceTestProgram.Prepare(program, ShaderStage.Mesh);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            WaveSize = 64, LocalSizeX = 192, ScratchDwords = 17,
            Mesh = new MeshShaderConfiguration(192, 64, 0, 62, 64, 0, true, 32),
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        var words = new uint[shader.Spirv.Length / sizeof(uint)];
        Buffer.BlockCopy(shader.Spirv, 0, words, 0, shader.Spirv.Length);
        var constants = new Dictionary<uint, uint>();
        var scratchArrays = new HashSet<uint>();
        var scratchPointers = new HashSet<uint>();
        var scratchVariables = new HashSet<uint>();
        var accessCounts = new Dictionary<uint, int>();
        var packedVariables = new HashSet<uint>();
        for (var offset = 5; offset < words.Length; offset += (int)(words[offset] >> 16))
        {
            var opcode = (SpirvOp)(words[offset] & 0xffff);
            if (opcode == SpirvOp.Name)
            {
                var name = System.Text.Encoding.UTF8.GetString(shader.Spirv,
                    (offset + 2) * 4, ((int)(words[offset] >> 16) - 2) * 4).TrimEnd('\0');
                if (name == "vgprPackedHalf") packedVariables.Add(words[offset + 1]);
            }
            if (opcode == SpirvOp.Constant && words[offset] >> 16 == 4)
                constants[words[offset + 2]] = words[offset + 3];
            if (opcode == SpirvOp.TypeArray && constants.TryGetValue(words[offset + 3], out var length) && length == 17)
                scratchArrays.Add(words[offset + 1]);
            if (opcode == SpirvOp.TypePointer && scratchArrays.Contains(words[offset + 3]))
                scratchPointers.Add(words[offset + 1]);
            if (opcode == SpirvOp.Variable && scratchPointers.Contains(words[offset + 1]))
                scratchVariables.Add(words[offset + 2]);
            if (opcode == SpirvOp.AccessChain)
            {
                var variable = words[offset + 3];
                accessCounts[variable] = accessCounts.GetValueOrDefault(variable) + 1;
            }
        }
        Assert.Equal(2, packedVariables.Count);
        Assert.Equal(2, scratchVariables.Count);
        foreach (var variable in packedVariables.Concat(scratchVariables))
            Assert.True(accessCounts.GetValueOrDefault(variable) >= 3);
        ValidateWithInstalledSdk(shader.Spirv);
    }

    [Theory]
    [InlineData(32u, 32u, 31u, "dpp")]
    [InlineData(64u, 32u, 31u, "dpp")]
    [InlineData(64u, 64u, 63u, "dpp")]
    [InlineData(64u, 64u, 63u, "dpp8")]
    [InlineData(64u, 64u, 63u, "VPermlane16B32")]
    [InlineData(64u, 64u, 63u, "VPermlanex16B32")]
    [InlineData(32u, 0u, 31u, "dpp", ShaderStage.Compute)]
    [InlineData(64u, 0u, 63u, "dpp", ShaderStage.Compute)]
    [InlineData(32u, 0u, 31u, "dpp8", ShaderStage.Compute)]
    [InlineData(64u, 0u, 63u, "dpp8", ShaderStage.Compute)]
    [InlineData(32u, 0u, 31u, "VPermlane16B32", ShaderStage.Compute)]
    [InlineData(64u, 0u, 63u, "VPermlane16B32", ShaderStage.Compute)]
    [InlineData(32u, 0u, 31u, "VPermlanex16B32", ShaderStage.Compute)]
    [InlineData(64u, 0u, 63u, "VPermlanex16B32", ShaderStage.Compute)]
    public void DppShufflePreservesTheNativeSubgroupLaneRange(uint waveSize, uint hostSubgroupSize,
        uint expectedMask, string operation, ShaderStage stage = ShaderStage.Mesh)
    {
        var instruction = ResourceTestProgram.Vop1(0, "VMovB32", 1, Gen5Operand.Vector(0)) with
        {
            Control = new Gen5DppControl(0x140, false, true, 0, 0, 15, 15),
        };
        if (operation == "dpp8")
            instruction = instruction with { Control = new Gen5Dpp8Control(0xFAC688, false) };
        else if (operation.StartsWith("VPerm", StringComparison.Ordinal))
            instruction = instruction with
            {
                Opcode = operation, Encoding = Gen5ShaderEncoding.Vop3,
                Sources = [Gen5Operand.Vector(0), Gen5Operand.Scalar(0), Gen5Operand.Scalar(1)],
                Control = new Gen5Vop3Control(0, 0, 0, false, 0, null),
            };
        var program = ResourceTestProgram.Program(
        [
            instruction,
            ResourceTestProgram.EndProgram(8),
        ]);
        var (plan, resources, layout) = ResourceTestProgram.Prepare(program, stage);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            WaveSize = waveSize, LocalSizeX = 192,
            Mesh = stage == ShaderStage.Mesh
                ? new MeshShaderConfiguration(192, 64, 0, 62, 64, 0, true, hostSubgroupSize) : null,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        var words = new uint[shader.Spirv.Length / sizeof(uint)];
        Buffer.BlockCopy(shader.Spirv, 0, words, 0, shader.Spirv.Length);
        var constants = new Dictionary<uint, uint>();
        var laneMasks = new Dictionary<uint, uint>();
        var shuffleCount = 0;
        for (var offset = 5; offset < words.Length; offset += (int)(words[offset] >> 16))
        {
            var opcode = (SpirvOp)(words[offset] & 0xffff);
            if (opcode == SpirvOp.Constant && words[offset] >> 16 == 4)
                constants[words[offset + 2]] = words[offset + 3];
            if (opcode == SpirvOp.BitwiseAnd && constants.TryGetValue(words[offset + 4], out var mask))
                laneMasks[words[offset + 2]] = mask;
            if (opcode != SpirvOp.GroupNonUniformShuffle) continue;
            Assert.Equal(expectedMask, laneMasks[words[offset + 5]]);
            shuffleCount++;
        }
        Assert.True(shuffleCount >= 2);
        ValidateWithInstalledSdk(shader.Spirv);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    public void PairedSharedMemoryBarriersFollowTheBlockSelectionMerge(int readCount)
    {
        var instructions = new List<Gen5ShaderInstruction>
        {
            ResourceTestProgram.Sopc(0, "SCmpEqU32", Gen5Operand.Scalar(3), ResourceTestProgram.Operand(0)),
            ResourceTestProgram.Branch(4, "SCbranchScc0", checked((short)(2 + readCount * 2))),
            ResourceTestProgram.DataShare(8, "DsWriteB32", false,
                [Gen5Operand.Vector(0), Gen5Operand.Vector(1)], []),
        };
        for (var index = 0; index < readCount; index++)
            instructions.Add(ResourceTestProgram.DataShare((uint)(16 + index * 8), "DsReadB32", false,
                [Gen5Operand.Vector(0)], [2]));
        instructions.Add(new Gen5ShaderInstruction((uint)(16 + readCount * 8),
            Gen5ShaderEncoding.Sopp, "SBarrier", [0], [], [], null));
        instructions.Add(ResourceTestProgram.EndProgram((uint)(20 + readCount * 8)));
        var program = ResourceTestProgram.Program(instructions.ToArray());
        var (plan, resources, layout) = ResourceTestProgram.Prepare(program, ShaderStage.Mesh);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            WaveSize = 64, LocalSizeX = 192,
            Mesh = new MeshShaderConfiguration(192, 64, 0, 62, 64, 192, true, 32),
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        var words = new uint[shader.Spirv.Length / sizeof(uint)];
        Buffer.BlockCopy(shader.Spirv, 0, words, 0, shader.Spirv.Length);
        var constants = new Dictionary<uint, uint>();
        var selections = new Stack<uint>();
        var subgroupBarriers = 0;
        var workgroupBarriers = 0;
        for (var offset = 5; offset < words.Length; offset += (int)(words[offset] >> 16))
        {
            var opcode = (SpirvOp)(words[offset] & 0xffff);
            if (opcode == SpirvOp.Constant && words[offset] >> 16 == 4)
                constants[words[offset + 2]] = words[offset + 3];
            if (opcode == SpirvOp.SelectionMerge) selections.Push(words[offset + 1]);
            if (opcode == SpirvOp.Label && selections.TryPeek(out var merge) && words[offset + 1] == merge)
                selections.Pop();
            if (opcode != SpirvOp.ControlBarrier) continue;
            Assert.Empty(selections);
            Assert.Equal(2u, constants[words[offset + 2]]);
            Assert.Equal(0x108u, constants[words[offset + 3]]);
            if (constants[words[offset + 1]] == 3) subgroupBarriers++;
            else workgroupBarriers++;
        }
        Assert.Equal(readCount == 0 ? 1 : 2, subgroupBarriers);
        Assert.True(workgroupBarriers >= 3);
        ValidateWithInstalledSdk(shader.Spirv);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(-2, true)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    public void MeshBranchesRequireConvergenceForLoopsOrBarrierCrossings(short branchOffset, bool usesDispatcher)
    {
        var program = ResourceTestProgram.Program(
        [
            ResourceTestProgram.Sopc(0, "SCmpEqU32", Gen5Operand.Scalar(20), ResourceTestProgram.Operand(0)),
            ResourceTestProgram.Branch(4, "SCbranchScc1", branchOffset),
            ResourceTestProgram.MoveScalar(8, 21, 1),
            new Gen5ShaderInstruction(12, Gen5ShaderEncoding.Sopp, "SBarrier", [0], [], [], null),
            ResourceTestProgram.MoveScalar(16, 21, 2),
            ResourceTestProgram.EndProgram(20),
        ]);
        var (plan, resources, layout) = ResourceTestProgram.Prepare(program, ShaderStage.Mesh);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            WaveSize = 64, LocalSizeX = 192,
            Mesh = new MeshShaderConfiguration(192, 64, 0, 62, 64, 0, true, 32),
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        var module = new SpirvModuleInspector(shader.Spirv);
        Assert.Equal(usesDispatcher, module.Opcodes.Contains((ushort)SpirvOp.LoopMerge));
        Assert.Equal(usesDispatcher, module.Opcodes.Contains((ushort)SpirvOp.AtomicOr));
        Assert.Equal(usesDispatcher, module.Opcodes.Contains((ushort)SpirvOp.GroupNonUniformElect));
        ValidateWithInstalledSdk(shader.Spirv);
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    public void MeshLoopsUseSeparateRegionsUnlessTheirRangesOverlap(bool overlapping, int expectedLoops)
    {
        var program = ResourceTestProgram.Program(
        [
            ResourceTestProgram.MoveScalar(0, 20, 0),
            ResourceTestProgram.Sopc(4, "SCmpEqU32", Gen5Operand.Scalar(20), ResourceTestProgram.Operand(1)),
            ResourceTestProgram.Branch(8, "SCbranchScc1", -2),
            overlapping
                ? ResourceTestProgram.MoveScalar(12, 21, 0)
                : new Gen5ShaderInstruction(12, Gen5ShaderEncoding.Sopp, "SBarrier", [0], [], [], null),
            ResourceTestProgram.Sopc(16, "SCmpEqU32", Gen5Operand.Scalar(21), ResourceTestProgram.Operand(1)),
            ResourceTestProgram.Branch(20, "SCbranchScc1", overlapping ? (short)-5 : (short)-2),
            ResourceTestProgram.EndProgram(24),
        ]);
        var (plan, resources, layout) = ResourceTestProgram.Prepare(program, ShaderStage.Mesh);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            WaveSize = 64, LocalSizeX = 192,
            Mesh = new MeshShaderConfiguration(192, 64, 0, 62, 64, 0, true, 32),
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        var words = new uint[shader.Spirv.Length / sizeof(uint)];
        Buffer.BlockCopy(shader.Spirv, 0, words, 0, shader.Spirv.Length);
        var loopCount = 0;
        for (var offset = 5; offset < words.Length; offset += (int)(words[offset] >> 16))
            if ((SpirvOp)(words[offset] & 0xffff) == SpirvOp.LoopMerge) loopCount++;
        Assert.Equal(expectedLoops, loopCount);
        ValidateWithInstalledSdk(shader.Spirv);
    }

    [Fact]
    public void PairedMeshHalfSelectionDoesNotEmitMaskWorkForNoOps()
    {
        byte[] Compile(int count)
        {
            var instructions = Enumerable.Range(0, count)
                .Select(index => ResourceTestProgram.Nop((uint)index * 4))
                .Append(ResourceTestProgram.EndProgram((uint)count * 4)).ToArray();
            var (plan, resources, layout) = ResourceTestProgram.Prepare(
                ResourceTestProgram.Program(instructions), ShaderStage.Mesh);
            var request = new ShaderCompileRequest(plan, resources, layout)
            {
                WaveSize = 64, LocalSizeX = 192,
                Mesh = new MeshShaderConfiguration(192, 64, 0, 62, 64, 0, true, 32),
            };
            Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
            ValidateWithInstalledSdk(shader.Spirv);
            return shader.Spirv;
        }
        Assert.Equal(Compile(1), Compile(8));
    }

    [Fact]
    public void FusedRegistrationUsesTheSharedMemoryRoot()
    {
        var memory = new FixtureMemory();
        var producer = new CpuContext(new MemoryWrapper(new MemoryWrapper(memory)), Generation.Gen5);
        var consumer = new CpuContext(new MemoryWrapper(memory), Generation.Gen5);
        Gen5ShaderTranslator.RegisterFusedProgram(producer, 0x1000, 0x3000, 0x2000, 0x4000);
        Assert.True(Gen5ShaderTranslator.TryGetFusedProgramParts(consumer, 0x1000,
            out var continuation, out var header));
        Assert.Equal(0x2000ul, continuation);
        Assert.Equal(0x4000ul, header);
        Assert.False(Gen5ShaderTranslator.TryGetFusedProgramParts(
            new CpuContext(new FixtureMemory(), Generation.Gen5), 0x1000, out _, out _));
    }

    [MeshFixtureTheory]
    [InlineData(32u, 0u)]
    [InlineData(64u, 0u)]
    [InlineData(64u, 32u)]
    public void CapturedFusedProgramCompilesWhenFixtureIsAvailable(uint waveSize, uint hostSubgroupSize)
    {
        var fixture = Environment.GetEnvironmentVariable("SHARPEMU_MESH_FIXTURE_DIR");
        Assert.False(string.IsNullOrWhiteSpace(fixture));

        const string prefix = "0000002009960000-0000000000000000.geometry";
        var memory = new FixtureMemory();
        memory.Add(0x1000, File.ReadAllBytes(Path.Combine(fixture, prefix + ".front.bin")));
        memory.Add(0x2000, File.ReadAllBytes(Path.Combine(fixture, prefix + ".back.bin")));
        memory.Add(0x3000, File.ReadAllBytes(Path.Combine(fixture, prefix + ".front.header.bin")));
        memory.Add(0x4000, File.ReadAllBytes(Path.Combine(fixture, prefix + ".back.header.bin")));
        var context = new CpuContext(memory, Generation.Gen5);
        Gen5ShaderTranslator.RegisterFusedProgram(new CpuContext(new MemoryWrapper(memory), Generation.Gen5),
            0x1000, 0x3000, 0x2000, 0x4000);
        Assert.True(Gen5ShaderTranslator.TryDecodeProgram(context, 0x1000,
            out var program, out var error), error);
        var (plan, resources, layout) = ResourceTestProgram.Prepare(program,
            ShaderStage.Mesh, userDataBase: 8, userDataCount: 32);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            WaveSize = waveSize,
            EnableGraphicsSubgroupOperations = true,
            LocalSizeX = hostSubgroupSize == 32 ? 192u : 64u,
            PositionExportControl = (1u << 21) | (1u << 18),
            Mesh = hostSubgroupSize == 32
                ? new MeshShaderConfiguration(192, 64, 0, 62, 64, 2176, true, hostSubgroupSize)
                : new MeshShaderConfiguration(64, 64, 0, 16, 48, 4096),
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out error), error);
        if (hostSubgroupSize == 32)
        {
            var module = new SpirvModuleInspector(shader.Spirv);
            Assert.DoesNotContain((ushort)SpirvOp.LoopMerge, module.Opcodes);
            Assert.DoesNotContain((ushort)SpirvOp.Switch, module.Opcodes);
        }
        ValidateWithInstalledSdk(shader.Spirv);
    }

    [Fact]
    public void MeshProgramDeclaresOutputsAndAllocation()
    {
        var program = new Gen5ShaderProgram(0,
        [
            new Gen5ShaderInstruction(0, Gen5ShaderEncoding.Sop1, "SMovB32", [0],
                [Gen5Operand.Source(131)], [Gen5Operand.Scalar(124)], null),
            new Gen5ShaderInstruction(4, Gen5ShaderEncoding.Sopp, "SSendmsg", [0xBF900009],
                [], [], null),
            new Gen5ShaderInstruction(8, Gen5ShaderEncoding.Exp, "Exp", [0, 0],
                [Gen5Operand.Vector(0), Gen5Operand.Vector(1),
                    Gen5Operand.Vector(2), Gen5Operand.Vector(3)], [],
                new Gen5ExportControl(12, 15, false, false, false)),
            new Gen5ShaderInstruction(16, Gen5ShaderEncoding.Exp, "Exp", [0, 0],
                [Gen5Operand.Vector(4), Gen5Operand.Vector(4),
                    Gen5Operand.Vector(4), Gen5Operand.Vector(4)], [],
                new Gen5ExportControl(20, 1, false, true, false)),
            new Gen5ShaderInstruction(24, Gen5ShaderEncoding.Sopp, "SEndpgm", [0xBF810000],
                [], [], null),
        ]);
        var (plan, resources, layout) = ResourceTestProgram.Prepare(
            program, ShaderStage.Mesh, userDataCount: 0);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            WaveSize = 32,
            LocalSizeX = 32,
            Mesh = new MeshShaderConfiguration(32, 16, 0, 8, 24, 0),
        };

        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        var module = new SpirvModuleInspector(shader.Spirv);
        Assert.Contains((uint)SpirvCapability.MeshShadingExt, module.Capabilities);
        Assert.Contains((ushort)SpirvOp.SetMeshOutputsExt, module.Opcodes);

        ValidateWithInstalledSdk(shader.Spirv);
    }

    private sealed class MeshFixtureTheoryAttribute : TheoryAttribute
    {
        public MeshFixtureTheoryAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SHARPEMU_MESH_FIXTURE_DIR")))
                Skip = "Set SHARPEMU_MESH_FIXTURE_DIR to run the captured-program integration test.";
        }
    }

    [Fact]
    public void NativeWave64ReadFirstLaneIncludesTheUpperBallotWord()
    {
        var program = ResourceTestProgram.Program(ResourceTestProgram.ReadFirstLane(0, 4, 1),
            ResourceTestProgram.EndProgram(4));
        var (plan, resources, layout) = ResourceTestProgram.Prepare(program, ShaderStage.Mesh);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            WaveSize = 64, LocalSizeX = 64,
            Mesh = new MeshShaderConfiguration(64, 16, 0, 8, 24, 0, true, 64),
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        var words = new uint[shader.Spirv.Length / 4];
        Buffer.BlockCopy(shader.Spirv, 0, words, 0, shader.Spirv.Length);
        var ballots = new HashSet<uint>();
        var constants = new Dictionary<uint, uint>();
        var subgroupInputs = new HashSet<uint>();
        var subgroupLoads = new HashSet<uint>();
        var halfIndices = new HashSet<uint>();
        var upperWords = 0;
        for (var offset = 5; offset < words.Length; offset += (int)(words[offset] >> 16))
        {
            var opcode = (SpirvOp)(words[offset] & 0xffff);
            if (opcode == SpirvOp.Constant && (words[offset] >> 16) == 4)
                constants[words[offset + 2]] = words[offset + 3];
            if (opcode == SpirvOp.BitwiseAnd &&
                constants.TryGetValue(words[offset + 3], out var left) &&
                constants.TryGetValue(words[offset + 4], out var right))
                constants[words[offset + 2]] = left & right;
            if (opcode == SpirvOp.Decorate && words[offset + 2] == 11 && words[offset + 3] == 41)
                subgroupInputs.Add(words[offset + 1]);
            if (opcode == SpirvOp.Load && subgroupInputs.Contains(words[offset + 3]))
                subgroupLoads.Add(words[offset + 2]);
            if (opcode == SpirvOp.ShiftRightLogical && subgroupLoads.Contains(words[offset + 3]) &&
                constants.TryGetValue(words[offset + 4], out var shift) && shift == 5)
                halfIndices.Add(words[offset + 2]);
            if (opcode == SpirvOp.GroupNonUniformBallot) ballots.Add(words[offset + 2]);
            if (opcode == SpirvOp.CompositeExtract && ballots.Contains(words[offset + 3]) && words[offset + 4] == 1)
                upperWords++;
            if (opcode == SpirvOp.VectorExtractDynamic && ballots.Contains(words[offset + 3]) &&
                halfIndices.Contains(words[offset + 4]))
                upperWords++;
        }
        Assert.True(upperWords > 0);
        ValidateWithInstalledSdk(shader.Spirv);
    }

    private void ValidateWithInstalledSdk(byte[] spirv)
    {
        var sdk = Environment.GetEnvironmentVariable("VULKAN_SDK");
        var executable = OperatingSystem.IsWindows() ? "spirv-val.exe" : "spirv-val";
        var validator = sdk is null ? null : Path.Combine(sdk, OperatingSystem.IsWindows() ? "Bin" : "bin", executable);
        if (validator is null || !File.Exists(validator))
            validator = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
                .Where(directory => !string.IsNullOrWhiteSpace(directory))
                .Select(directory => Path.Combine(directory, executable)).FirstOrDefault(File.Exists);
        if (validator is null || !File.Exists(validator))
        {
            _output.WriteLine("SPIR-V validation was not run. Install spirv-val or set VULKAN_SDK. Compiler assertions still run.");
            return;
        }

        var path = Path.Combine(Path.GetTempPath(), $"sharpemu-mesh-{Guid.NewGuid():N}.spv");
        try
        {
            File.WriteAllBytes(path, spirv);
            using var process = Process.Start(new ProcessStartInfo(validator)
            {
                ArgumentList = { "--target-env", "vulkan1.3", path },
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            })!;
            var output = process.StandardError.ReadToEnd() + process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, output);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class FixtureMemory : ICpuMemory
    {
        private readonly List<(ulong Address, byte[] Data)> _regions = [];

        public void Add(ulong address, byte[] data) => _regions.Add((address, data));

        public bool TryRead(ulong address, Span<byte> destination)
        {
            foreach (var (start, data) in _regions)
            {
                if (address >= start && address - start <= (ulong)data.Length &&
                    (ulong)destination.Length <= (ulong)data.Length - (address - start))
                {
                    data.AsSpan((int)(address - start), destination.Length).CopyTo(destination);
                    return true;
                }
            }
            return false;
        }

        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => false;
    }

    private sealed class MemoryWrapper(ICpuMemory inner) : ICpuMemory, ICpuMemoryWrapper
    {
        public ICpuMemory Inner => inner;
        public bool TryRead(ulong address, Span<byte> destination) => inner.TryRead(address, destination);
        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => inner.TryWrite(address, source);
    }
}
