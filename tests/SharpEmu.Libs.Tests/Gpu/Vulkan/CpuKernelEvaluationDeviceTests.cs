// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Evaluation;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Tests.Evaluation;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Evaluation.EvaluationTestSupport;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.Libs.Tests.Gpu.Vulkan;

// The CPU evaluation of a dispatch must equal what the translated program writes on the device, dword for dword.
public sealed class CpuKernelEvaluationDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    private const uint SourceRecords = 256;
    private const uint DestinationRecords = 512;
    private const int SeedsPerProgram = 48;

    public static TheoryData<string, uint, bool> Programs()
    {
        var data = new TheoryData<string, uint, bool>();
        foreach (var kernel in new[] { "104", "107", "masked" })
        {
            foreach (var wave in new[] { 32u, 64u })
            {
                foreach (var subgroup32 in new[] { false, true })
                {
                    data.Add(kernel, wave, subgroup32);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Programs))]
    public void TheCopyKernels_PredictWhatTheDeviceWrites(string kernel, uint waveSize, bool subgroup32)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan)) return;
        var masked = kernel == "masked";
        var words = kernel switch { "104" => ModuloKernel104, "107" => ModuloKernel107, _ => MaskedKernel };
        var program = Decode(words);
        var evaluable = Gen5EvaluableProgram.Create(program);
        Assert.True(evaluable.IsEvaluable, evaluable.Refusal);
        uint[] userData = [.. CopyUserData(SourceRecords, DestinationRecords, masked ? DwordBuffer(ControlAddress, 2) : null)];
        var random = new System.Random(kernel.GetHashCode(StringComparison.Ordinal) ^ (int)waveSize ^ (subgroup32 ? 0x55 : 0));
        var setup = new EvaluationMemory();
        setup.WriteWords(ControlAddress, 1, 1, 0, 0);
        var (request, plan) = Compile(program, userData, setup, waveSize, subgroup32);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var predicted = 0;
        for (var seed = 0; seed < SeedsPerProgram; seed++)
        {
            var count = (uint)random.Next(0, 700);
            var parameter = masked
                ? (uint)(random.Next(4) switch { 0 => 0, 1 => (1 << random.Next(0, 9)) - 1, 2 => random.Next(0, 256), _ => random.Next() })
                : (uint)(random.Next(5) switch { 0 => 0, 1 => 1, 2 => random.Next(2, 300), 3 => random.Next(300, 70000), _ => random.Next() });
            var groups = (uint)random.Next(1, 9);
            var limit = random.Next(2) == 0 ? groups * 64 : (uint)random.Next((int)(groups - 1) * 64 + 1, (int)groups * 64 + 1);
            var source = Enumerable.Range(0, (int)SourceRecords).Select(_ => (uint)random.Next() ^ ((uint)random.Next() << 16)).ToArray();
            var initial = Enumerable.Range(0, (int)DestinationRecords).Select(_ => (uint)random.Next()).ToArray();
            var memory = CopyMemory(count, parameter, source);
            memory.WriteWords(DestinationAddress, initial);
            var outcome = Gen5DispatchEvaluator.Evaluate(evaluable, new Gen5DispatchRequest
            {
                UserData = userData,
                SystemRegisters = new Gen5ComputeSystemRegisters(12, null, null, null),
                ThreadsX = 64,
                GroupsX = groups,
                LimitsThreads = true,
                ThreadLimitX = limit,
                WaveSize = waveSize,
            }, memory);
            if (outcome.Prediction is not { } prediction)
            {
                continue;
            }

            predicted++;
            var device = RunOnDevice(harness, runner, request, plan, userData, memory, source, initial, groups, limit);
            for (var index = 0; index < DestinationRecords; index++)
            {
                var address = DestinationAddress + (ulong)index * sizeof(uint);
                var expected = address >= prediction.Address && address < prediction.Address + (ulong)prediction.Count * sizeof(uint)
                    ? prediction.Words[(int)((address - prediction.Address) / sizeof(uint))]
                    : initial[index];
                Assert.True(expected == device[index],
                    $"seed={seed} count={count} parameter={parameter} groups={groups} limit={limit} index={index} expected=0x{expected:X8} device=0x{device[index]:X8}");
            }
        }

        Assert.True(predicted >= SeedsPerProgram / 3, $"only {predicted} of {SeedsPerProgram} dispatches were predicted");
        harness.AssertNoValidationMessages();
    }

    private static readonly string[] BinaryVectorOps =
    [
        "VAddI32", "VSubI32", "VSubrevI32", "VMulLoU32", "VMulHiU32", "VMulU32U24", "VAndB32", "VLshlrevB32", "VLshrrevB32", "VMinU32", "VBcntU32B32",
    ];

    private static readonly string[] TernaryVectorOps = ["VLshlAddU32", "VAndOrB32", "VAdd3U32", "VBfiB32", "VMadU32U24"];

    private static readonly string[] ScalarOps = ["SAddI32", "SSubI32", "SSubU32", "SMulI32", "SMulHiU32", "SLshlB32", "SLshl2AddU32", "SBfmB32"];

    public static TheoryData<uint, bool> Waves() => new() { { 32u, false }, { 32u, true }, { 64u, false }, { 64u, true } };

    [Theory]
    [MemberData(nameof(Waves))]
    public void RandomIntegerPrograms_PredictWhatTheDeviceWrites(uint waveSize, bool subgroup32)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan)) return;
        var random = new System.Random(0x5EED ^ (int)waveSize ^ (subgroup32 ? 0x100 : 0));
        using var harness = new ImageTestHarness(vulkan);
        uint[] userData = [0, 0, 0, 0, .. DwordBuffer(DestinationAddress, DestinationRecords)];
        var compared = 0;
        for (var seed = 0; seed < 24; seed++)
        {
            var program = Program([.. RandomProgram(random)]);
            var evaluable = Gen5EvaluableProgram.Create(program);
            Assert.True(evaluable.IsEvaluable, evaluable.Refusal);
            var outcome = Gen5DispatchEvaluator.Evaluate(evaluable, new Gen5DispatchRequest
            {
                UserData = userData,
                ThreadsX = 64,
                WaveSize = waveSize,
                LimitsThreads = true,
                ThreadLimitX = 64,
            }, new EvaluationMemory());
            if (outcome.Prediction is not { } prediction)
            {
                Assert.Equal(Gen5EvaluationRefusal.UndefinedBehaviour, outcome.Refusal);
                continue;
            }

            var (request, _) = Compile(program, userData, new EvaluationMemory(), waveSize, subgroup32);
            Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
            using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
            var destination = runner.CreateBuffer(DestinationRecords * sizeof(uint));
            var registers = new uint[256];
            userData.CopyTo(registers, 0);
            harness.Run(() => runner.Dispatch(registers, new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [destination] },
                1, dispatchThreadLimits: [64, 1, 1], bufferRanges: [DestinationRecords * sizeof(uint)]));
            var device = MemoryMarshal.Cast<byte, uint>(runner.ReadBack(destination, 0, DestinationRecords * sizeof(uint))).ToArray();
            Assert.Equal(DestinationAddress, prediction.Address);
            Assert.Equal((int)DestinationRecords, prediction.Count);
            for (var index = 0; index < DestinationRecords; index++)
            {
                Assert.True(prediction.Words[index] == device[index],
                    $"seed={seed} index={index} cpu=0x{prediction.Words[index]:X8} device=0x{device[index]:X8}\n{string.Join("\n", program.Instructions)}");
            }

            compared++;
        }

        Assert.True(compared >= 12, $"only {compared} programs were compared");
        harness.AssertNoValidationMessages();
    }

    private static List<Gen5ShaderInstruction> RandomProgram(System.Random random)
    {
        var instructions = new List<Gen5ShaderInstruction>();
        var pc = 0u;
        void Add(Gen5ShaderInstruction instruction)
        {
            instructions.Add(instruction with { Pc = pc });
            pc += 8;
        }

        uint Literal() => random.Next(3) switch
        {
            0 => (uint)random.Next(0, 65),
            1 => (uint)random.Next() ^ ((uint)random.Next() << 16),
            _ => (uint)random.Next(-64, 64),
        };

        Gen5Operand Constant()
        {
            var value = Literal();
            return value <= 64 ? Gen5Operand.Source(128 + value) : new Gen5Operand(Gen5OperandKind.LiteralConstant, value);
        }

        Gen5Operand Vector() => Gen5Operand.Vector((uint)random.Next(1, 9));
        Gen5Operand Scalar() => Gen5Operand.Scalar((uint)random.Next(20, 24));
        Gen5Operand Any() => random.Next(4) switch { 0 => Constant(), 1 => Scalar(), _ => Vector() };
        var noModifiers = new Gen5Vop3Control(0, 0, 0, false, 0, null);

        Add(Vop1(0, "VMovB32", 1, Gen5Operand.Vector(0)));
        for (var register = 2u; register <= 8; register++)
        {
            Add(Vop3(0, "VMadU32U24", register, Gen5Operand.Vector(0), Constant(), Constant()));
        }

        for (var register = 20u; register < 24; register++)
        {
            Add(Sop1(0, "SMovB32", register, Constant()));
        }

        for (var step = 0; step < 28; step++)
        {
            var destination = (uint)random.Next(1, 9);
            switch (random.Next(11))
            {
                case 0:
                case 1:
                case 2:
                    Add(Vop2(0, BinaryVectorOps[random.Next(BinaryVectorOps.Length)], destination, Any(), Vector()));
                    break;
                case 3:
                    Add(new Gen5ShaderInstruction(0, Gen5ShaderEncoding.Vop3, BinaryVectorOps[random.Next(BinaryVectorOps.Length)], [0u, 0u],
                        [Any(), Any()], [Gen5Operand.Vector(destination)], noModifiers));
                    break;
                case 4:
                case 5:
                    Add(Vop3(0, TernaryVectorOps[random.Next(TernaryVectorOps.Length)], destination, Any(), Any(), Any()));
                    break;
                case 6:
                    Add(Sop2(0, ScalarOps[random.Next(ScalarOps.Length)], (uint)random.Next(20, 24), random.Next(2) == 0 ? Scalar() : Constant(), Scalar()));
                    break;
                case 7:
                    Add(Vopc(0, random.Next(2) == 0 ? "VCmpGtU32" : "VCmpLtI32", Any(), (uint)random.Next(1, 9)));
                    Add(Vop2(0, "VCndmaskB32", destination, Any(), Vector()));
                    break;
                case 8:
                    Add(Vopc(0, "VCmpGtU32", Any(), (uint)random.Next(1, 9)));
                    Add(Vop2(0, "VAddcU32", destination, Any(), Vector()));
                    break;
                case 9:
                    Add(new Gen5ShaderInstruction(0, Gen5ShaderEncoding.Vop3, "VMadU64U32", [0u, 0u], [Vector(), Vector(), Gen5Operand.Vector((uint)random.Next(1, 8))],
                        [Gen5Operand.Vector((uint)random.Next(1, 8))], new Gen5Vop3Control(0, 0, 0, false, 0, Vcc)));
                    break;
                default:
                    Add(Vop1(0, random.Next(2) == 0 ? "VMovB32" : "VCvtF32U32", destination, Any()));
                    break;
            }
        }

        for (var register = 1u; register <= 8; register++)
        {
            Add(Vop2(0, "VLshlrevB32", 9, Gen5Operand.Source(128 + 5), Gen5Operand.Vector(0)));
            Add(BufferAccess(0, "BufferStoreDword", 4, offset: (int)((register - 1) * sizeof(uint)), vectorData: register, offsetEnabled: true, vectorAddress: 9));
        }

        Add(EndProgram(0));
        return instructions;
    }

    private const uint Vcc = 106;

    private static (ShaderCompileRequest Request, ShaderResourcePlan Plan) Compile(Gen5ShaderProgram program, uint[] userData, EvaluationMemory memory,
        uint waveSize, bool subgroup32)
    {
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Compute, SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram.Hash, 0, (uint)userData.Length);
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        GuestWordReader read = (ulong address, out uint word) => TryReadWord(memory, address, out word);
        var inputs = SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram.Inputs(userData, read, read);
        Assert.True(ResourceMaterializer.Materialize(plan, inputs, ref snapshot, ref specialization, out var failure), failure.ToString());
        var resources = ResourceMaterializer.ApplyTo(plan, specialization);
        var layout = BindingLayout.Allocate(resources.Info,
            BindingLayout.CollectUserDataRegisters(program, 0, (uint)userData.Length, plan.Graph.ExcludedUserDataRegisters),
            BindingLayout.UsesGlobalDataShare(program), ShaderCompileRequest.RequiresFlattenedTable(plan, resources),
            BindingLayout.ReadsShaderBase(program), 0, usesDispatchThreadLimits: true);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            LocalSizeX = 64,
            WaveSize = waveSize,
            HostComputeSubgroup32 = subgroup32,
            ComputeSystemRegisters = new Gen5ComputeSystemRegisters(12, null, null, null),
        };
        return (request, plan);
    }

    private static bool TryReadWord(EvaluationMemory memory, ulong address, out uint word)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        word = 0;
        if (!memory.TryRead(address, bytes))
        {
            return false;
        }

        word = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        return true;
    }

    private static uint[] RunOnDevice(ImageTestHarness harness, LayoutComputeRunner runner, ShaderCompileRequest request, ShaderResourcePlan plan, uint[] userData,
        EvaluationMemory memory,
        uint[] source, uint[] initial, uint groups, uint limit)
    {
        var control = new uint[4];
        for (var index = 0; index < control.Length; index++)
        {
            control[index] = memory.ReadWord(ControlAddress + (ulong)index * sizeof(uint));
        }

        var sourceBuffer = runner.CreateBuffer(MemoryMarshal.AsBytes<uint>(source));
        var destinationBuffer = runner.CreateBuffer(MemoryMarshal.AsBytes<uint>(initial));
        var controlBuffer = runner.CreateBuffer(MemoryMarshal.AsBytes<uint>(control));
        var bound = new List<GpuBuffer>();
        var ranges = new List<ulong>();
        foreach (var buffer in request.Resources.Info.Buffers)
        {
            var register = BufferRegister(plan.Graph.Program, buffer.FirstUsePc);
            var (gpu, range) = register switch
            {
                0 => (sourceBuffer, (ulong)SourceRecords * sizeof(uint)),
                4 => (destinationBuffer, (ulong)DestinationRecords * sizeof(uint)),
                _ => (controlBuffer, (ulong)(userData[8 + 2] * ((userData[8 + 1] >> 16) & 0x3FFF))),
            };
            bound.Add(gpu);
            ranges.Add(range);
        }

        var registers = new uint[256];
        userData.CopyTo(registers, 0);
        harness.Run(() => runner.Dispatch(registers, new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [.. bound] },
            groups, dispatchThreadLimits: [limit, 1, 1], bufferRanges: [.. ranges]));
        var bytes = runner.ReadBack(destinationBuffer, 0, DestinationRecords * sizeof(uint));
        return MemoryMarshal.Cast<byte, uint>(bytes).ToArray();
    }

    private static uint BufferRegister(Gen5ShaderProgram program, uint pc)
    {
        var instruction = program.Instructions.First(candidate => candidate.Pc == pc);
        return instruction.Control switch
        {
            Gen5BufferMemoryControl buffer => buffer.ScalarResource,
            _ => instruction.Sources[0].Value,
        };
    }
}
