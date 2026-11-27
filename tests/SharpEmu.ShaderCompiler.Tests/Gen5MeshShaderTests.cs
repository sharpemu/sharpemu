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
    public void IndirectMeshPreparationValidates()
        => ValidateWithInstalledSdk(SpirvMeshIndirect.Create());

    [Fact]
    public void TaskStageValidates()
        => ValidateWithInstalledSdk(SpirvMeshTask.Create());

    // The task stage's host interface: one TaskEXT invocation, eight push words after the shared push
    // data, five specialization constants with their defaults, and one emit that passes the payload.
    [Fact]
    public void TaskStageDeclaresItsHostInterface()
    {
        var spirv = SpirvMeshTask.Create();
        var module = ReadInstructions(spirv);
        var inspector = new SpirvModuleInspector(spirv);
        Assert.Equal(new[] { SpirvCapability.Shader, SpirvCapability.Int64, SpirvCapability.PhysicalStorageBufferAddresses,
            SpirvCapability.MeshShadingExt }.Select(capability => (uint)capability).Order(), inspector.Capabilities.Order());
        Assert.Equal(5348u, inspector.AddressingModel);
        var entryPoint = Assert.Single(module, instruction => instruction.Op == SpirvOp.EntryPoint);
        Assert.Equal((uint)SpirvExecutionModel.TaskExt, entryPoint.Operands[0]);
        Assert.Equal(new uint[] { (uint)SpirvExecutionMode.LocalSize, 1, 1, 1 },
            Assert.Single(module, instruction => instruction.Op == SpirvOp.ExecutionMode).Operands[1..]);

        Assert.Equal(128u, SpirvMeshTask.PushOffsetBytes);
        var push = Assert.Single(module, instruction => instruction.Op == SpirvOp.Variable &&
            instruction.Operands[2] == (uint)SpirvStorageClass.PushConstant);
        Assert.Contains(push.Operands[1], EntryPointInterface(entryPoint));
        var block = PointeeType(module, push.Operands[0]);
        Assert.Equal(new[] { block.Operands[0], 0u, (uint)SpirvDecoration.Offset, SpirvMeshTask.PushOffsetBytes },
            Assert.Single(module, instruction => instruction.Op == MemberDecorate && instruction.Operands[0] == block.Operands[0]).Operands);
        var constants = Constants(module);
        var words = Assert.Single(module, instruction => instruction.Op == SpirvOp.TypeArray && instruction.Operands[0] == block.Operands[1]);
        Assert.Equal(SpirvMeshTask.PushWordCount, constants[words.Operands[2]]);
        Assert.Equal(Enumerable.Range(0, (int)SpirvMeshTask.PushWordCount).Select(word => (ulong)word),
            module.Where(instruction => instruction.Op == SpirvOp.AccessChain && instruction.Operands[2] == push.Operands[1])
                .Select(instruction => constants[instruction.Operands[^1]]).Order());

        var specIds = module.Where(instruction => instruction.Op == SpirvOp.Decorate && instruction.Operands[1] == (uint)SpirvDecoration.SpecId)
            .ToDictionary(instruction => instruction.Operands[0], instruction => instruction.Operands[2]);
        Assert.Equal(new[]
            {
                (SpirvMeshTask.SpecPrimitivesPerGroup, 1u), (SpirvMeshTask.SpecPointList, 0u), (SpirvMeshTask.SpecMaxGroupCountX, 1u),
                (SpirvMeshTask.SpecMaxGroupCountY, 1u), (SpirvMeshTask.SpecMaxGroupTotalCount, 1u),
            },
            module.Where(instruction => instruction.Op == SpirvOp.SpecConstant)
                .Select(instruction => (specIds[instruction.Operands[1]], instruction.Operands[2])).Order());

        var payload = Assert.Single(module, instruction => instruction.Op == SpirvOp.Variable &&
            instruction.Operands[2] == (uint)SpirvStorageClass.TaskPayloadWorkgroupExt);
        var emit = Assert.Single(module, instruction => instruction.Op == SpirvOp.EmitMeshTasksExt);
        Assert.Equal(4, emit.Operands.Length);
        Assert.Equal(payload.Operands[1], emit.Operands[3]);
        Assert.Equal(Enumerable.Repeat((32u, 0u), (int)SpirvMeshTask.PayloadWordCount), TaskPayloadMembers(module));
    }

    // Each argument merges a device load, which only a nonzero address reaches, with push word 4 to 7,
    // so the arguments of a draw without an address read no memory.
    [Fact]
    public void TaskStageReadsPushedArgumentsWithoutAnAddress()
    {
        var module = ReadInstructions(SpirvMeshTask.Create());
        var constants = Constants(module);
        var values = module.Where(instruction => instruction.Op is SpirvOp.Load or SpirvOp.AccessChain or SpirvOp.ConvertUToPtr or
            SpirvOp.INotEqual or SpirvOp.IAdd).ToDictionary(instruction => instruction.Operands[1]);
        var push = Assert.Single(module, instruction => instruction.Op == SpirvOp.Variable &&
            instruction.Operands[2] == (uint)SpirvStorageClass.PushConstant).Operands[1];
        bool IsDeviceLoad(SpirvInstruction instruction) => instruction.Op == SpirvOp.Load &&
            values.TryGetValue(instruction.Operands[2], out var pointer) && pointer.Op == SpirvOp.ConvertUToPtr;

        var entry = module.First(instruction => instruction.Op == SpirvOp.Label).Operands[0];
        var branch = Assert.Single(module, instruction => instruction.Op == SpirvOp.BranchConditional && instruction.Block == entry);
        var (condition, read, merge) = (values[branch.Operands[0]], branch.Operands[1], branch.Operands[2]);
        Assert.Equal(SpirvOp.INotEqual, condition.Op);
        Assert.Equal(0ul, constants[condition.Operands[3]]);
        Assert.Equal(4, module.Count(IsDeviceLoad));
        Assert.All(module.Where(IsDeviceLoad), load => Assert.Equal(read, load.Block));
        var phis = module.Where(instruction => instruction.Op == SpirvOp.Phi).ToList();
        Assert.Equal(4, phis.Count);
        for (var index = 0; index < phis.Count; index++)
        {
            Assert.Equal(merge, phis[index].Block);
            var incoming = phis[index].Operands[2..].Chunk(2).ToDictionary(pair => pair[1], pair => values[pair[0]]);
            Assert.Equal(2, incoming.Count);
            var loaded = incoming[read];
            Assert.True(IsDeviceLoad(loaded));
            Assert.Equal(new uint[] { 2, 4 }, loaded.Operands[3..]);
            // The first argument is read at the address itself: its "+ 0" folds away.
            var target = values[loaded.Operands[2]].Operands[2];
            if (index == 0)
            {
                Assert.Equal(condition.Operands[2], target);
            }
            else
            {
                var address = values[target];
                Assert.Equal(condition.Operands[2], address.Operands[2]);
                Assert.Equal((ulong)index * 4, constants[address.Operands[3]]);
            }
            var pushed = values[incoming[entry].Operands[2]];
            Assert.Equal(push, pushed.Operands[2]);
            Assert.Equal((ulong)index + 4, constants[pushed.Operands[^1]]);
        }
    }

    // A task-launched mesh shader takes its draw parameters from the payload the task stage declares and
    // reads none of them from the shader data, in each wave arrangement. Left off, the module is unchanged.
    [Theory]
    [InlineData(32u, 32u, 0u)]
    [InlineData(32u, 32u, 32u)]
    [InlineData(64u, 192u, 32u)]
    [InlineData(64u, 64u, 64u)]
    public void TaskLaunchedMeshReadsItsDrawParametersFromThePayload(uint waveSize, uint localSize, uint hostSubgroupSize)
    {
        var (plan, resources, layout) = ResourceTestProgram.Prepare(AllocatingMeshProgram(), ShaderStage.Mesh, userDataCount: 0);
        var mesh = new MeshShaderConfiguration(localSize, 16, 0, 8, 24, 0, DeviceSubgroupLaneCount: hostSubgroupSize);
        byte[] Compile(MeshShaderConfiguration configuration)
        {
            var request = new ShaderCompileRequest(plan, resources, layout)
            {
                WaveSize = waveSize, LocalSizeX = localSize, Mesh = configuration,
            };
            Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
            return shader.Spirv;
        }
        var shaderData = Compile(mesh);
        Assert.Equal(shaderData, Compile(mesh with { TaskLaunched = false }));
        var task = Compile(mesh with { TaskLaunched = true });

        Assert.True(layout.UsesPushData);
        var parameters = Enumerable.Range(0, 6)
            .Select(index => (ulong)(layout.PushDataStartDword + layout.MeshDrawParametersDword) + (ulong)index).ToHashSet();
        Assert.True(PushDataReads(shaderData, parameters) >= parameters.Count);
        Assert.Equal(0, PushDataReads(task, parameters));
        Assert.False(new SpirvModuleInspector(shaderData).HasVariableInStorageClass(SpirvStorageClass.TaskPayloadWorkgroupExt));
        // The validator checks each stage alone, so the payload the two stages share is compared here.
        Assert.Equal(TaskPayloadMembers(ReadInstructions(SpirvMeshTask.Create())), TaskPayloadMembers(ReadInstructions(task)));
        ValidateWithInstalledSdk(task);
    }

    // An allocating mesh program with a position and a primitive export.
    private static Gen5ShaderProgram AllocatingMeshProgram() => new(0,
    [
        new Gen5ShaderInstruction(0, Gen5ShaderEncoding.Sop1, "SMovB32", [0],
            [Gen5Operand.Source(131)], [Gen5Operand.Scalar(124)], null),
        new Gen5ShaderInstruction(4, Gen5ShaderEncoding.Sopp, "SSendmsg", [0xBF900009], [], [], null),
        new Gen5ShaderInstruction(8, Gen5ShaderEncoding.Exp, "Exp", [0, 0],
            [Gen5Operand.Vector(0), Gen5Operand.Vector(1), Gen5Operand.Vector(2), Gen5Operand.Vector(3)], [],
            new Gen5ExportControl(12, 15, false, false, false)),
        new Gen5ShaderInstruction(16, Gen5ShaderEncoding.Exp, "Exp", [0, 0],
            [Gen5Operand.Vector(4), Gen5Operand.Vector(4), Gen5Operand.Vector(4), Gen5Operand.Vector(4)], [],
            new Gen5ExportControl(20, 1, false, true, false)),
        new Gen5ShaderInstruction(24, Gen5ShaderEncoding.Sopp, "SEndpgm", [0xBF810000], [], [], null),
    ]);

    private const SpirvOp MemberDecorate = (SpirvOp)72;

    // One instruction of a module: its opcode, its operand words and the label of the block it is in.
    private readonly record struct SpirvInstruction(SpirvOp Op, uint[] Operands, uint Block);

    private static List<SpirvInstruction> ReadInstructions(byte[] spirv)
    {
        var words = new uint[spirv.Length / sizeof(uint)];
        Buffer.BlockCopy(spirv, 0, words, 0, spirv.Length);
        var instructions = new List<SpirvInstruction>();
        var block = 0u;
        for (var offset = 5; offset < words.Length; offset += (int)(words[offset] >> 16))
        {
            var op = (SpirvOp)(words[offset] & 0xffff);
            var operands = words[(offset + 1)..(offset + (int)(words[offset] >> 16))];
            if (op == SpirvOp.Label) block = operands[0];
            instructions.Add(new SpirvInstruction(op, operands, block));
        }
        return instructions;
    }

    // The integer constants of a module by id, 64-bit ones included.
    private static Dictionary<uint, ulong> Constants(List<SpirvInstruction> module) =>
        module.Where(instruction => instruction.Op == SpirvOp.Constant).ToDictionary(instruction => instruction.Operands[1],
            instruction => instruction.Operands.Length > 3 ? instruction.Operands[2] | (ulong)instruction.Operands[3] << 32 : instruction.Operands[2]);

    private static SpirvInstruction PointeeType(List<SpirvInstruction> module, uint pointerType)
    {
        var pointer = Assert.Single(module, instruction => instruction.Op == SpirvOp.TypePointer && instruction.Operands[0] == pointerType);
        return Assert.Single(module, instruction => instruction.Op is >= SpirvOp.TypeVoid and <= SpirvOp.TypeFunction &&
            instruction.Operands[0] == pointer.Operands[2]);
    }

    // The interface of an entry point: the ids after its nul-terminated name.
    private static uint[] EntryPointInterface(SpirvInstruction entryPoint)
    {
        var name = 2;
        while (entryPoint.Operands[name] >> 24 != 0) name++;
        return entryPoint.Operands[(name + 1)..];
    }

    // The members of a module's task payload as (width, signedness). Its variable is in the entry point's
    // interface, and the payload type has no decorations.
    private static (uint Width, uint Signedness)[] TaskPayloadMembers(List<SpirvInstruction> module)
    {
        var payload = Assert.Single(module, instruction => instruction.Op == SpirvOp.Variable &&
            instruction.Operands[2] == (uint)SpirvStorageClass.TaskPayloadWorkgroupExt);
        Assert.Contains(payload.Operands[1], EntryPointInterface(Assert.Single(module, instruction => instruction.Op == SpirvOp.EntryPoint)));
        var structure = PointeeType(module, payload.Operands[0]);
        Assert.Equal(SpirvOp.TypeStruct, structure.Op);
        Assert.DoesNotContain(module, instruction => instruction.Op is SpirvOp.Decorate or MemberDecorate &&
            instruction.Operands[0] == structure.Operands[0]);
        return [.. structure.Operands[1..].Select(member => Assert.Single(module, instruction => instruction.Op == SpirvOp.TypeInt &&
            instruction.Operands[0] == member)).Select(type => (type.Operands[1], type.Operands[2]))];
    }

    // The number of reads of the given push data dwords.
    private static int PushDataReads(byte[] spirv, IReadOnlySet<ulong> dwords)
    {
        var module = ReadInstructions(spirv);
        var constants = Constants(module);
        var pushData = new SpirvModuleInspector(spirv).Names.Single(name => name.Value == "pushData").Key;
        return module.Count(instruction => instruction.Op == SpirvOp.AccessChain && instruction.Operands[2] == pushData &&
            constants.TryGetValue(instruction.Operands[^1], out var dword) && dwords.Contains(dword));
    }

    [Theory]
    [InlineData(null, 11, 41984ul)]
    [InlineData(1501u, 8, 32768ul)]
    [InlineData(0u, 0, 8192ul)]
    public void MeshOutputDeclarationsMatchTheConsumerMask(uint? mask, int count, ulong bytes)
    {
        var instructions = new List<Gen5ShaderInstruction>();
        for (uint location = 0; location < 11; location++)
            instructions.Add(new Gen5ShaderInstruction(location * 8, Gen5ShaderEncoding.Exp, "Exp", [0, 0],
                [Gen5Operand.Vector(0), Gen5Operand.Vector(0), Gen5Operand.Vector(0), Gen5Operand.Vector(0)], [],
                new Gen5ExportControl(32 + location, 15, false, false, false)));
        instructions.Add(ResourceTestProgram.EndProgram(88));
        var (plan, resources, layout) = ResourceTestProgram.Prepare(ResourceTestProgram.Program([.. instructions]), ShaderStage.Mesh);
        var mesh = new MeshShaderConfiguration(192, 160, 0, 3, 9, 0, false, 32);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            WaveSize = 64, LocalSizeX = 192, Mesh = mesh, MeshOutputLocationMask = mask,
        };
        var locations = MeshShaderConfiguration.ParameterLocations(request);
        Assert.Equal(count, locations.Length);
        Assert.Equal(bytes, mesh.OutputMemoryBytes(locations.Length, 32, 32));
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        var words = new uint[shader.Spirv.Length / 4];
        Buffer.BlockCopy(shader.Spirv, 0, words, 0, shader.Spirv.Length);
        var declared = new List<uint>();
        for (var offset = 5; offset < words.Length; offset += (int)(words[offset] >> 16))
            if ((words[offset] & 65535) == (uint)SpirvOp.Decorate && words[offset + 2] == 30)
                declared.Add(words[offset + 3]);
        Assert.Equal(locations, declared.Order().ToArray());
        ValidateWithInstalledSdk(shader.Spirv);
    }

    [Fact]
    public void PairedMeshSaveexecDoesNotTruncateTheUpperMask()
    {
        var program = ResourceTestProgram.Program(
            ResourceTestProgram.Sop1(0, "SAndSaveexecB64", 4, Gen5Operand.Scalar(0)),
            ResourceTestProgram.EndProgram(4));
        var (plan, resources, layout) = ResourceTestProgram.Prepare(program, ShaderStage.Mesh);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            WaveSize = 64, LocalSizeX = 64,
            Mesh = new MeshShaderConfiguration(64, 16, 0, 8, 24, 0, true, 32),
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        var words = new uint[shader.Spirv.Length / sizeof(uint)];
        Buffer.BlockCopy(shader.Spirv, 0, words, 0, shader.Spirv.Length);
        var lowMasks = new HashSet<uint>();
        for (var offset = 5; offset < words.Length; offset += (int)(words[offset] >> 16))
        {
            var operation = (SpirvOp)(words[offset] & 0xffff);
            if (operation == SpirvOp.Constant && words[offset] >> 16 == 5 &&
                words[offset + 3] == uint.MaxValue && words[offset + 4] == 0)
                lowMasks.Add(words[offset + 2]);
            if (operation == SpirvOp.BitwiseAnd)
                Assert.DoesNotContain(words[offset + 4], lowMasks);
        }
        ValidateWithInstalledSdk(shader.Spirv);
    }

    [Theory]
    [InlineData(false, 32u)]
    [InlineData(false, 64u)]
    [InlineData(true, 32u)]
    [InlineData(true, 64u)]
    public void MeshUserPointerAndWaveInputUseSeparateInitialization(bool pointList, uint waveSize)
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
            WaveSize = waveSize, LocalSizeX = waveSize,
            Mesh = new MeshShaderConfiguration(32, 16, 0, 8, pointList ? 8u : 24u, 0, !pointList, 32, pointList),
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
    public void Wave32SaveexecClearsTheUpperMaskWords(string opcode)
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
            var zeroes = new HashSet<uint>();
            var stores = new Dictionary<uint, uint>();
            for (var offset = 5; offset < words.Length; offset += (int)(words[offset] >> 16))
            {
                var operation = (SpirvOp)(words[offset] & 0xffff);
                if (operation == SpirvOp.Constant && words[offset] >> 16 == 4 && words[offset + 3] == 0)
                    zeroes.Add(words[offset + 2]);
                if (operation == SpirvOp.ConstantNull)
                    zeroes.Add(words[offset + 2]);
                if (operation == SpirvOp.Store)
                    stores[words[offset + 1]] = words[offset + 2];
            }
            var names = new SpirvModuleInspector(shader.Spirv).Names;
            foreach (var name in new[] { "s5", "s127" })
            {
                var register = Assert.Single(names, item => item.Value == name).Key;
                Assert.Contains(stores[register], zeroes);
            }
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
    [InlineData((1u << 21) | (1u << 16))]
    [InlineData((1u << 22) | (1u << 8) | 1u)]
    public void MeshAuxiliaryExportsFollowPositionControl(uint positionControl)
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
        var elementBases = new Dictionary<uint, uint>();
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
                elementBases[words[offset + 2]] = words[offset + 3];
            }
            if (opcode is SpirvOp.Load or SpirvOp.Store)
            {
                var pointer = words[offset + (opcode == SpirvOp.Load ? 3 : 1)];
                if (elementBases.TryGetValue(pointer, out var variable))
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
        // A wave32 DPP16 source reads the guest EXEC of its lane from a ballot, not from a second shuffle.
        Assert.True(shuffleCount >= (operation == "dpp" && waveSize == 32 ? 1 : 2));
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

    // A primitive export packs three 9-bit vertex indices at bits 0, 10 and 20; bit 9 of each field is the vertex's
    // edge flag, so a set edge flag must not move the index past 511.
    [Fact]
    public void PrimitiveExportIndicesLeaveOutTheEdgeFlags()
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

        var words = new uint[shader.Spirv.Length / 4];
        Buffer.BlockCopy(shader.Spirv, 0, words, 0, shader.Spirv.Length);
        var constants = new Dictionary<uint, uint>();
        var extracts = new List<(uint Offset, uint Count)>();
        for (var index = 5; index < words.Length; index += (int)(words[index] >> 16))
        {
            var opcode = (SpirvOp)(words[index] & 0xFFFF);
            if (opcode == SpirvOp.Constant && words[index] >> 16 == 4)
                constants[words[index + 2]] = words[index + 3];
            else if (opcode == SpirvOp.BitFieldUExtract)
                extracts.Add((words[index + 4], words[index + 5]));
        }

        var fields = extracts.Select(extract =>
            (Offset: constants.GetValueOrDefault(extract.Offset, uint.MaxValue),
             Count: constants.GetValueOrDefault(extract.Count, uint.MaxValue))).ToList();
        // (0, 10) also decodes the vertex count of the GS allocation request, so only fields 1 and 2 tell the old width.
        foreach (var offset in new[] { 0u, 10u, 20u })
        {
            Assert.Contains((offset, 9u), fields);
        }

        Assert.DoesNotContain((10u, 10u), fields);
        Assert.DoesNotContain((20u, 10u), fields);
    }

    private sealed class MeshFixtureTheoryAttribute : TheoryAttribute
    {
        public MeshFixtureTheoryAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SHARPEMU_MESH_FIXTURE_DIR")))
                Skip = "Set SHARPEMU_MESH_FIXTURE_DIR to run the captured-program integration test.";
        }
    }

    // A wave reduction in a mesh program ends with V_READLANE_B32 of the row results (lanes 15 and 31 of a wave32).
    // The read takes the selected lane of the host subgroup that runs the wave, not the invocation's own value.
    [Theory]
    [InlineData(32u)]
    [InlineData(64u)]
    public void MeshReadlaneReadsTheSelectedLane(uint waveSize)
    {
        var program = ResourceTestProgram.Program(ResourceTestProgram.ReadLane(0, 4, 1, 15),
            ResourceTestProgram.EndProgram(8));
        var (plan, resources, layout) = ResourceTestProgram.Prepare(program, ShaderStage.Mesh);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            WaveSize = waveSize, LocalSizeX = waveSize,
            Mesh = new MeshShaderConfiguration(32, 30, 0, 1, 1, 0, false, waveSize, InputPointList: true),
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        var words = new uint[shader.Spirv.Length / 4];
        Buffer.BlockCopy(shader.Spirv, 0, words, 0, shader.Spirv.Length);
        var laneReads = 0;
        for (var offset = 5; offset < words.Length; offset += (int)(words[offset] >> 16))
        {
            if ((SpirvOp)(words[offset] & 0xffff) is SpirvOp.GroupNonUniformBroadcast or SpirvOp.GroupNonUniformShuffle)
                laneReads++;
        }
        Assert.True(laneReads > 0);
        ValidateWithInstalledSdk(shader.Spirv);
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
