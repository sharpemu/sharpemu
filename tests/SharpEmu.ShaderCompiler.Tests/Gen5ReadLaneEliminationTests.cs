// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5ReadLaneEliminationTests
{
    [Fact]
    public void FixedLaneShadowIsDeclaredInEntryPointInterfaceAndValidates()
    {
        var program = ResourceTestProgram.Program(
            ResourceTestProgram.MoveScalar(0, destination: 4, value: 11),
            ResourceTestProgram.WriteLane(4, vectorRegister: 5, scalarRegister: 4, lane: 1),
            ResourceTestProgram.ReadLane(12, scalarRegister: 6, vectorRegister: 5, lane: 1),
            ResourceTestProgram.EndProgram(20));

        var spirv = CompilePixelSpirv(PreparePixel(program));
        var instructions = ReadInstructions(spirv);
        var fixedLaneShadow = Assert.Single(
            instructions,
            instruction =>
                instruction.Opcode == SpirvOp.Name &&
                DecodeString(instruction.Operands, 1) == "fixedLane_v5_1").Operands[0];
        var entryPoint = Assert.Single(
            instructions,
            instruction => instruction.Opcode == SpirvOp.EntryPoint);

        Assert.Contains(fixedLaneShadow, entryPoint.Operands);
        ValidateWhenAvailable(spirv);
    }

    [Fact]
    public void FixedWriteReadLaneForwardsValueCapturedBeforeScalarOverwrite()
    {
        var program = ResourceTestProgram.Program(
            ResourceTestProgram.MoveScalar(0, destination: 4, value: 11),
            ResourceTestProgram.WriteLane(4, vectorRegister: 5, scalarRegister: 4, lane: 1),
            ResourceTestProgram.MoveScalar(12, destination: 4, value: 22),
            ResourceTestProgram.ReadLane(16, scalarRegister: 6, vectorRegister: 5, lane: 1),
            ResourceTestProgram.EndProgram(24));

        var instructions = CompilePixel(program);

        Assert.DoesNotContain(
            instructions,
            instruction => instruction.Opcode == SpirvOp.GroupNonUniformBroadcast);

        var constants = instructions
            .Where(instruction => instruction.Opcode == SpirvOp.Constant)
            .ToDictionary(
                instruction => instruction.Operands[1],
                instruction => instruction.Operands[2]);
        var scalarPointers = RegisterPointers(
            instructions,
            constants,
            "s",
            "sgpr");

        var sourceOverwrite = Assert.Single(
            instructions.Select((instruction, index) => (instruction, index)),
            item =>
                item.instruction.Opcode == SpirvOp.Store &&
                scalarPointers.TryGetValue(item.instruction.Operands[0], out var register) &&
                register == 4 &&
                constants.TryGetValue(item.instruction.Operands[1], out var value) &&
                value == 22);
        var fixedLaneShadow = Assert.Single(
            instructions,
            instruction =>
                instruction.Opcode == SpirvOp.Name &&
                DecodeString(instruction.Operands, 1) == "fixedLane_v5_1").Operands[0];
        var shadowCapture = Assert.Single(
            instructions.Select((instruction, index) => (instruction, index)),
            item =>
                item.instruction.Opcode == SpirvOp.Store &&
                item.instruction.Operands[0] == fixedLaneShadow);
        var capturedLoad = Assert.Single(
            instructions.Select((instruction, index) => (instruction, index)),
            item =>
                item.instruction.Opcode == SpirvOp.Load &&
                item.instruction.Operands[1] == shadowCapture.instruction.Operands[1] &&
                scalarPointers.TryGetValue(item.instruction.Operands[2], out var register) &&
                register == 4);
        var forwardedStore = Assert.Single(
            instructions.Select((instruction, index) => (instruction, index)),
            item =>
                item.instruction.Opcode == SpirvOp.Store &&
                scalarPointers.TryGetValue(item.instruction.Operands[0], out var register) &&
                register == 6);
        var shadowRead = Assert.Single(
            instructions.Select((instruction, index) => (instruction, index)),
            item =>
                item.instruction.Opcode == SpirvOp.Load &&
                item.instruction.Operands[1] == forwardedStore.instruction.Operands[1] &&
                item.instruction.Operands[2] == fixedLaneShadow);

        Assert.True(capturedLoad.index < shadowCapture.index);
        Assert.True(shadowCapture.index < sourceOverwrite.index);
        Assert.True(sourceOverwrite.index < shadowRead.index);
        Assert.True(shadowRead.index < forwardedStore.index);
    }

    [Fact]
    public void FixedLaneDefinitionDoesNotRequireSymbolicScalarValue()
    {
        var program = ResourceTestProgram.Program(
            ResourceTestProgram.MoveScalar(0, destination: 2, value: 1),
            ResourceTestProgram.MoveScalar(4, destination: 3, value: 0),
            ResourceTestProgram.Sop1(
                8,
                "SFlbitI32B64",
                destination: 4,
                source: Gen5Operand.Scalar(2)),
            ResourceTestProgram.WriteLane(12, vectorRegister: 5, scalarRegister: 4, lane: 1),
            ResourceTestProgram.ReadLane(20, scalarRegister: 6, vectorRegister: 5, lane: 1),
            ResourceTestProgram.EndProgram(28));

        var request = PreparePixel(program);
        Assert.Equal(
            new FixedLaneReadBinding(5, 1),
            request.FixedLaneReads[20]);
        Assert.DoesNotContain(
            CompilePixel(request),
            instruction => instruction.Opcode == SpirvOp.GroupNonUniformBroadcast);
    }

    [Fact]
    public void InterveningVectorWriteInvalidatesProofAndRejectsStaleLaneSpill()
    {
        var program = ResourceTestProgram.Program(
            ResourceTestProgram.MoveScalar(0, destination: 4, value: 11),
            ResourceTestProgram.WriteLane(4, vectorRegister: 5, scalarRegister: 4, lane: 1),
            ResourceTestProgram.MoveVector(12, destination: 5, value: 33),
            ResourceTestProgram.ReadLane(16, scalarRegister: 6, vectorRegister: 5, lane: 1),
            ResourceTestProgram.EndProgram(24));

        var request = PreparePixel(program);
        Assert.DoesNotContain(16u, request.FixedLaneReads.Keys);
        AssertReadLaneUsesCurrentVectorValue(
            CompilePixel(request),
            vectorRegister: 5,
            scalarRegister: 6);
    }

    [Fact]
    public void ImplicitHighDwordVectorWriteInvalidatesProofAndRejectsStaleLaneSpill()
    {
        var program = ResourceTestProgram.Program(
            ResourceTestProgram.MoveScalar(0, destination: 4, value: 11),
            ResourceTestProgram.WriteLane(4, vectorRegister: 6, scalarRegister: 4, lane: 1),
            ResourceTestProgram.Vop2(
                12,
                "VLshlrevB64",
                destination: 5,
                source0: ResourceTestProgram.Operand(1),
                source1: Gen5Operand.Vector(0)),
            ResourceTestProgram.ReadLane(16, scalarRegister: 6, vectorRegister: 6, lane: 1),
            ResourceTestProgram.EndProgram(24));

        var request = PreparePixel(program);
        Assert.DoesNotContain(16u, request.FixedLaneReads.Keys);
        AssertReadLaneUsesCurrentVectorValue(
            CompilePixel(request),
            vectorRegister: 6,
            scalarRegister: 6);
    }

    [Fact]
    public void SecondaryImageLoadDestinationInvalidatesProofAndRejectsStaleLaneSpill()
    {
        var instructions = new List<Gen5ShaderInstruction>();
        var descriptor = new uint[]
        {
            0x1000,
            22u << 20,
            3u | (3u << 14),
            0xFACu | (9u << 28),
            0,
            0,
            0,
            0,
        };
        for (var index = 0; index < descriptor.Length; index++)
        {
            instructions.Add(
                ResourceTestProgram.MoveScalar(
                    checked((uint)index * 4),
                    destination: 16u + (uint)index,
                    value: descriptor[index]));
        }

        instructions.AddRange(
        [
            ResourceTestProgram.MoveScalar(32, destination: 4, value: 11),
            ResourceTestProgram.WriteLane(36, vectorRegister: 5, scalarRegister: 4, lane: 1),
            ResourceTestProgram.Image(44, "ImageLoad", resourceRegister: 16, dmask: 0x3),
            ResourceTestProgram.ReadLane(52, scalarRegister: 6, vectorRegister: 5, lane: 1),
            ResourceTestProgram.EndProgram(60),
        ]);

        var request = PreparePixel(ResourceTestProgram.Program([.. instructions]));
        Assert.DoesNotContain(52u, request.FixedLaneReads.Keys);
        AssertReadLaneUsesCurrentVectorValue(
            CompilePixel(request),
            vectorRegister: 5,
            scalarRegister: 6);
    }

    [Fact]
    public void ScalarRegisterLaneSelectorUsesGraphProvenShadow()
    {
        var program = ResourceTestProgram.Program(
            ResourceTestProgram.MoveScalar(0, destination: 4, value: 11),
            ResourceTestProgram.MoveScalar(4, destination: 7, value: 50),
            WriteLaneFromScalarSelector(
                8,
                vectorRegister: 5,
                scalarRegister: 4,
                selectorRegister: 7),
            ReadLaneFromScalarSelector(
                16,
                scalarRegister: 6,
                vectorRegister: 5,
                selectorRegister: 7),
            ResourceTestProgram.EndProgram(24));

        var request = PreparePixel(program, waveSize: 32);
        Assert.Equal(
            new FixedLaneWriteBinding(5, 18),
            request.FixedLaneWrites[8]);
        Assert.Equal(
            new FixedLaneReadBinding(5, 18),
            request.FixedLaneReads[16]);
        Assert.DoesNotContain(
            CompilePixel(request),
            instruction => instruction.Opcode == SpirvOp.GroupNonUniformBroadcast);
    }

    [Fact]
    public void IndirectControlFlowDisablesAllFixedLaneProofs()
    {
        var program = ResourceTestProgram.Program(
            ResourceTestProgram.MoveScalar(0, destination: 4, value: 11),
            ResourceTestProgram.WriteLane(4, vectorRegister: 5, scalarRegister: 4, lane: 1),
            SetProgramCounter(12, scalarRegister: 8),
            ResourceTestProgram.ReadLane(16, scalarRegister: 6, vectorRegister: 5, lane: 1),
            ResourceTestProgram.EndProgram(24));

        var plan = ResourceTestProgram.Extract(
            program,
            userDataCount: 0,
            stage: ShaderStage.Pixel,
            waveSize: 32);

        Assert.Empty(plan.Graph.FixedLaneWrites);
        Assert.Empty(plan.Graph.FixedLaneReads);
    }

    [Fact]
    public void FixedLaneDefinedByEveryBranchUsesControlFlowShadow()
    {
        var program = ResourceTestProgram.Program(
            ResourceTestProgram.MoveScalar(0, destination: 4, value: 11),
            ResourceTestProgram.Branch(4, "SCbranchScc0", 3),
            ResourceTestProgram.WriteLane(8, vectorRegister: 5, scalarRegister: 4, lane: 1),
            ResourceTestProgram.Branch(16, "SBranch", 3),
            ResourceTestProgram.MoveScalar(20, destination: 4, value: 22),
            ResourceTestProgram.WriteLane(24, vectorRegister: 5, scalarRegister: 4, lane: 1),
            ResourceTestProgram.ReadLane(32, scalarRegister: 6, vectorRegister: 5, lane: 1),
            ResourceTestProgram.EndProgram(40));

        var request = PreparePixel(program);
        Assert.Equal(
            new FixedLaneReadBinding(5, 1),
            request.FixedLaneReads[32]);
        Assert.DoesNotContain(
            CompilePixel(request),
            instruction => instruction.Opcode == SpirvOp.GroupNonUniformBroadcast);
    }

    [Fact]
    public void FixedLaneMissingOnOneBranchRejectsUnprovenLaneSpill()
    {
        var program = ResourceTestProgram.Program(
            ResourceTestProgram.MoveScalar(0, destination: 4, value: 11),
            ResourceTestProgram.Branch(4, "SCbranchScc0", 3),
            ResourceTestProgram.WriteLane(8, vectorRegister: 5, scalarRegister: 4, lane: 1),
            ResourceTestProgram.Branch(16, "SBranch", 1),
            ResourceTestProgram.Nop(20),
            ResourceTestProgram.ReadLane(24, scalarRegister: 6, vectorRegister: 5, lane: 1),
            ResourceTestProgram.EndProgram(32));

        var request = PreparePixel(program);
        Assert.DoesNotContain(24u, request.FixedLaneReads.Keys);
        AssertReadLaneUsesCurrentVectorValue(
            CompilePixel(request),
            vectorRegister: 5,
            scalarRegister: 6);
    }

    [Fact]
    public void Wave32NormalizesFixedLaneSelectorsAcrossBlocks()
    {
        var program = ResourceTestProgram.Program(
            ResourceTestProgram.MoveScalar(0, destination: 4, value: 11),
            ResourceTestProgram.WriteLane(4, vectorRegister: 5, scalarRegister: 4, lane: 50),
            ResourceTestProgram.Branch(12, "SBranch", 1),
            ResourceTestProgram.Nop(16),
            ResourceTestProgram.ReadLane(20, scalarRegister: 6, vectorRegister: 5, lane: 18),
            ResourceTestProgram.EndProgram(28));

        var request = PreparePixel(program, waveSize: 32);
        Assert.Equal(
            new FixedLaneReadBinding(5, 18),
            request.FixedLaneReads[20]);
        Assert.DoesNotContain(
            CompilePixel(request),
            instruction => instruction.Opcode == SpirvOp.GroupNonUniformBroadcast);
    }

    [Fact]
    public void PlanAndRuntimeWaveSizeMismatchIsRejected()
    {
        var program = ResourceTestProgram.Program(
            ResourceTestProgram.MoveScalar(0, destination: 4, value: 11),
            ResourceTestProgram.WriteLane(4, vectorRegister: 5, scalarRegister: 4, lane: 50),
            ResourceTestProgram.ReadLane(12, scalarRegister: 6, vectorRegister: 5, lane: 18),
            ResourceTestProgram.EndProgram(20));
        var (plan, resources, layout) = ResourceTestProgram.Prepare(
            program,
            ShaderStage.Pixel,
            userDataCount: 0,
            waveSize: 32);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            WaveSize = 64,
            PixelOutputs =
            [
                new Gen5PixelOutputBinding(0, 0, Gen5PixelOutputKind.Float),
            ],
        };

        Assert.Equal(
            new FixedLaneReadBinding(5, 18),
            request.FixedLaneReads[12]);
        Assert.False(
            Gen5SpirvTranslator.TryCompileProgram(
                request,
                out _,
                out var error));
        Assert.Contains("fixed-lane analysis used wave32", error, StringComparison.Ordinal);
        Assert.Contains("compile request uses wave64", error, StringComparison.Ordinal);
    }

    [Fact]
    public void FixedLaneShadowSurvivesLoopBackedge()
    {
        var program = ResourceTestProgram.Program(
            ResourceTestProgram.MoveScalar(0, destination: 4, value: 11),
            ResourceTestProgram.WriteLane(4, vectorRegister: 5, scalarRegister: 4, lane: 1),
            ResourceTestProgram.Branch(12, "SBranch", 1),
            ResourceTestProgram.Nop(16),
            ResourceTestProgram.Branch(20, "SCbranchScc1", -1),
            ResourceTestProgram.ReadLane(24, scalarRegister: 6, vectorRegister: 5, lane: 1),
            ResourceTestProgram.EndProgram(32));

        var request = PreparePixel(program);
        Assert.Equal(
            new FixedLaneReadBinding(5, 1),
            request.FixedLaneReads[24]);
        Assert.DoesNotContain(
            CompilePixel(request),
            instruction => instruction.Opcode == SpirvOp.GroupNonUniformBroadcast);
    }

    [Fact]
    public void LoopBackedgeVectorWriteInvalidatesFixedLaneProof()
    {
        var program = ResourceTestProgram.Program(
            ResourceTestProgram.MoveScalar(0, destination: 4, value: 11),
            ResourceTestProgram.WriteLane(4, vectorRegister: 5, scalarRegister: 4, lane: 1),
            ResourceTestProgram.Branch(12, "SBranch", 1),
            ResourceTestProgram.Nop(16),
            ResourceTestProgram.Branch(20, "SCbranchScc1", 2),
            ResourceTestProgram.MoveVector(24, destination: 5, value: 33),
            ResourceTestProgram.Branch(28, "SBranch", -3),
            ResourceTestProgram.ReadLane(32, scalarRegister: 6, vectorRegister: 5, lane: 1),
            ResourceTestProgram.EndProgram(40));

        var request = PreparePixel(program);
        Assert.DoesNotContain(32u, request.FixedLaneReads.Keys);
        AssertReadLaneUsesCurrentVectorValue(
            CompilePixel(request),
            vectorRegister: 5,
            scalarRegister: 6);
    }

    private static ShaderCompileRequest PreparePixel(
        Gen5ShaderProgram program,
        uint waveSize = 64) =>
        ResourceTestProgram.Request(
            program,
            ShaderStage.Pixel,
            userDataCount: 0,
            waveSize: waveSize);

    private static Gen5ShaderInstruction WriteLaneFromScalarSelector(
        uint pc,
        uint vectorRegister,
        uint scalarRegister,
        uint selectorRegister) =>
        new(
            pc,
            Gen5ShaderEncoding.Vop3,
            "VWritelaneB32",
            [0u, 0u],
            [
                Gen5Operand.Scalar(scalarRegister),
                Gen5Operand.Scalar(selectorRegister),
                Gen5Operand.Scalar(0),
            ],
            [Gen5Operand.Vector(vectorRegister)],
            null);

    private static Gen5ShaderInstruction ReadLaneFromScalarSelector(
        uint pc,
        uint scalarRegister,
        uint vectorRegister,
        uint selectorRegister) =>
        new(
            pc,
            Gen5ShaderEncoding.Vop3,
            "VReadlaneB32",
            [0u, 0u],
            [
                Gen5Operand.Vector(vectorRegister),
                Gen5Operand.Scalar(selectorRegister),
                Gen5Operand.Scalar(0),
            ],
            [Gen5Operand.Scalar(scalarRegister)],
            null);

    private static Gen5ShaderInstruction SetProgramCounter(
        uint pc,
        uint scalarRegister) =>
        new(
            pc,
            Gen5ShaderEncoding.Sop1,
            "SSetpcB64",
            [0u],
            [Gen5Operand.Scalar(scalarRegister)],
            [],
            null);

    private static void AssertReadLaneUsesCurrentVectorValue(
        IReadOnlyList<Instruction> instructions,
        uint vectorRegister,
        uint scalarRegister)
    {
        Assert.DoesNotContain(
            instructions,
            instruction => instruction.Opcode == SpirvOp.GroupNonUniformBroadcast);
        Assert.DoesNotContain(
            instructions,
            instruction =>
                instruction.Opcode == SpirvOp.Name &&
                DecodeString(instruction.Operands, 1).StartsWith(
                    "fixedLane_",
                    StringComparison.Ordinal));

        var spillPointers = instructions
            .Where(instruction =>
                instruction.Opcode == SpirvOp.Name &&
                DecodeString(instruction.Operands, 1).StartsWith(
                    $"v{vectorRegister}_lane",
                    StringComparison.Ordinal))
            .Select(instruction => instruction.Operands[0])
            .ToHashSet();
        Assert.NotEmpty(spillPointers);
        Assert.DoesNotContain(
            instructions,
            instruction =>
                instruction.Opcode == SpirvOp.Load &&
                instruction.Operands.Length >= 3 &&
                spillPointers.Contains(instruction.Operands[2]));

        var constants = instructions
            .Where(instruction => instruction.Opcode == SpirvOp.Constant)
            .ToDictionary(
                instruction => instruction.Operands[1],
                instruction => instruction.Operands[2]);
        var vectorPointers = RegisterPointers(
            instructions,
            constants,
            "v",
            "vgpr");
        var scalarPointers = RegisterPointers(
            instructions,
            constants,
            "s",
            "sgpr");
        var vectorLoads = instructions
            .Where(instruction =>
                instruction.Opcode == SpirvOp.Load &&
                instruction.Operands.Length >= 3 &&
                vectorPointers.TryGetValue(
                    instruction.Operands[2],
                    out var register) &&
                register == vectorRegister)
            .Select(instruction => instruction.Operands[1])
            .ToHashSet();

        Assert.Contains(
            instructions,
            instruction =>
                instruction.Opcode == SpirvOp.Store &&
                scalarPointers.TryGetValue(
                    instruction.Operands[0],
                    out var register) &&
                register == scalarRegister &&
                vectorLoads.Contains(instruction.Operands[1]));
    }

    private static Dictionary<uint, uint> RegisterPointers(
        IReadOnlyList<Instruction> instructions,
        IReadOnlyDictionary<uint, uint> constants,
        string registerPrefix,
        string registerFileName)
    {
        var pointers = new Dictionary<uint, uint>();
        uint registerFile = 0;
        foreach (var instruction in instructions.Where(
                     instruction => instruction.Opcode == SpirvOp.Name))
        {
            var name = DecodeString(instruction.Operands, 1);
            if (name == registerFileName)
            {
                registerFile = instruction.Operands[0];
            }
            else if (name.StartsWith(registerPrefix, StringComparison.Ordinal) &&
                     uint.TryParse(name.AsSpan(registerPrefix.Length), out var register))
            {
                pointers[instruction.Operands[0]] = register;
            }
        }

        if (registerFile == 0)
        {
            return pointers;
        }

        foreach (var instruction in instructions.Where(instruction =>
                     instruction.Opcode == SpirvOp.AccessChain &&
                     instruction.Operands.Length >= 4 &&
                     instruction.Operands[2] == registerFile &&
                     constants.ContainsKey(instruction.Operands[3])))
        {
            pointers[instruction.Operands[1]] = constants[instruction.Operands[3]];
        }

        return pointers;
    }

    private static IReadOnlyList<Instruction> CompilePixel(Gen5ShaderProgram program) =>
        CompilePixel(PreparePixel(program));

    private static IReadOnlyList<Instruction> CompilePixel(ShaderCompileRequest request)
    {
        return ReadInstructions(CompilePixelSpirv(request));
    }

    private static byte[] CompilePixelSpirv(ShaderCompileRequest request)
    {
        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(
                request,
                out var shader,
                out var error),
            error);
        return shader.Spirv;
    }

    private static void ValidateWhenAvailable(byte[] spirv)
    {
        var sdk = Environment.GetEnvironmentVariable("VULKAN_SDK");
        if (string.IsNullOrWhiteSpace(sdk))
        {
            return;
        }

        var executable = Path.Combine(
            sdk,
            OperatingSystem.IsWindows() ? "Bin/spirv-val.exe" : "bin/spirv-val");
        if (!File.Exists(executable))
        {
            return;
        }

        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, spirv);
            var start = new System.Diagnostics.ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("--target-env");
            start.ArgumentList.Add("vulkan1.3");
            start.ArgumentList.Add(path);
            using var process = System.Diagnostics.Process.Start(start)!;
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, error);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static IReadOnlyList<Instruction> ReadInstructions(byte[] spirv)
    {
        var words = new uint[spirv.Length / sizeof(uint)];
        Buffer.BlockCopy(spirv, 0, words, 0, spirv.Length);
        var result = new List<Instruction>();
        for (var index = 5; index < words.Length;)
        {
            var count = checked((int)(words[index] >> 16));
            result.Add(
                new Instruction(
                    (SpirvOp)(words[index] & 0xffff),
                    words[(index + 1)..(index + count)]));
            index += count;
        }
        return result;
    }

    private static string DecodeString(uint[] operands, int firstWord)
    {
        var bytes = new List<byte>();
        for (var index = firstWord; index < operands.Length; index++)
        {
            var word = operands[index];
            for (var shift = 0; shift < 32; shift += 8)
            {
                var value = (byte)(word >> shift);
                if (value == 0)
                {
                    return System.Text.Encoding.UTF8.GetString(bytes.ToArray());
                }
                bytes.Add(value);
            }
        }
        return System.Text.Encoding.UTF8.GetString(bytes.ToArray());
    }

    private sealed record Instruction(SpirvOp Opcode, uint[] Operands);
}
