// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler.Metal;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5SdwaUniformResourceTests
{
    [Fact]
    public void FullDwordSdwaOrRemainsUniformForDescriptorReadback()
    {
        // Exact Little Nightmares V_OR_B32 shape. All SDWA selectors are DWORD,
        // so this is semantically the ordinary 32-bit OR despite its encoding.
        var vectorOr = Decode(0x380212F9, 0x8686066A) with { Pc = 4 };
        var control = Assert.IsType<Gen5SdwaControl>(vectorOr.Control);
        Assert.Equal(6u, control.DestinationSelect);
        Assert.Equal(6u, control.Source0Select);
        Assert.Equal(6u, control.Source1Select);

        var program = Program(
            MoveScalar(0, 106, 0x0014_0000),
            vectorOr,
            MoveScalarRegister(12, 0, 8),
            ReadFirstLane(16, 1, 1),
            MoveScalar(20, 2, 1),
            MoveScalar(24, 3, 0x0001_6204),
            BufferStore(28, 0),
            EndProgram(36));
        var plan = Extract(program, userDataCount: 14);
        var userData = new uint[14];
        userData[8] = 0xBD66_0090;
        userData[9] = 0x0000_0020;

        Assert.True(
            RuntimeValueEvaluator.EvaluateDescriptorSource(
                plan,
                plan.Info.Buffers[0].Source,
                Inputs(userData),
                out var descriptor));
        Assert.Equal(0xBD66_0090u, descriptor.Dwords[0]);
        Assert.Equal(0x0014_0020u, descriptor.Dwords[1]);
        Assert.Equal(1u, descriptor.Dwords[2]);
        Assert.Equal(0x0001_6204u, descriptor.Dwords[3]);
    }

    [Fact]
    public void PartialSdwaSelectionStillFailsClosedInResourceGraph()
    {
        var vectorOr = Decode(0x380212F9, 0x8685066A) with { Pc = 4 };
        var program = Program(
            MoveScalar(0, 106, 0x0014_0000),
            vectorOr,
            ReadFirstLane(12, 1, 1),
            MoveScalarRegister(16, 0, 8),
            MoveScalar(20, 2, 1),
            MoveScalar(24, 3, 0x0001_6204),
            BufferStore(28, 0),
            EndProgram(36));

        var error = Assert.Throws<ResourcePlanException>(
            () => Extract(program, userDataCount: 14));
        Assert.Contains("dword 1 is not a valid runtime value", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FullDwordSdwaCompareCanSelectAUniformDescriptorWord()
    {
        var compare = new Gen5ShaderInstruction(
            8,
            Gen5ShaderEncoding.Vopc,
            "VCmpGtU32",
            [0u, 0u],
            [Gen5Operand.Scalar(8), Gen5Operand.Vector(4)],
            [Gen5Operand.Scalar(4)],
            new Gen5SdwaControl(6, 0, 6, 6, false, false, 0, 0, 0, false, 4));
        var program = Program(
            MoveVectorFromScalar(0, 4, 9),
            compare,
            Vop3(
                16,
                "VCndmaskB32",
                2,
                Gen5Operand.Scalar(10),
                Gen5Operand.Scalar(11),
                Gen5Operand.Scalar(4)),
            MoveScalarRegister(24, 0, 12),
            ReadFirstLane(28, 1, 2),
            MoveScalar(32, 2, 1),
            MoveScalar(36, 3, 0x0001_6204),
            BufferStore(40, 0),
            EndProgram(48));
        var plan = Extract(program, userDataCount: 14);
        var userData = new uint[14];
        userData[8] = 5;
        userData[9] = 3;
        userData[10] = 0x1111_1111;
        userData[11] = 0x2222_2222;
        userData[12] = 0xBD66_0090;

        Assert.True(
            RuntimeValueEvaluator.EvaluateDescriptorSource(
                plan,
                plan.Info.Buffers[0].Source,
                Inputs(userData),
                out var descriptor));
        Assert.Equal(0x2222_2222u, descriptor.Dwords[1]);
    }

    [Fact]
    public void Wave32SdwaComparePreservesAdjacentResourcePointerAtJoin()
    {
        // RDNA Wave32 compare masks occupy one SGPR. The captured Little
        // Nightmares shader writes its mask to s9 while s10:s11 remains a live
        // descriptor-table pointer on both sides of this branch join.
        var compare = CapturedWave32Compare(8);
        var program = Program(
            Sopc(0, "SCmpEqU32", Gen5Operand.Scalar(8), Operand(1)),
            Branch(4, "SCbranchScc1", 2),
            compare,
            ScalarLoad(16, 10, 12, count: 4, immediateOffset: 224),
            BufferLoad(24, 12),
            EndProgram(32));
        var plan = Extract(program, userDataCount: 12, waveSize: 32);
        var pointer = 0x0000_0020_07A4_813Cul;
        var memory = new TestWordMemory
        {
            Base = pointer + 224,
            Words = [0x3000, 0, 256, 0],
        };
        var userData = new uint[12];
        userData[8] = 1;
        userData[10] = (uint)pointer;
        userData[11] = (uint)(pointer >> 32);
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();

        Assert.True(ResourceMaterializer.Materialize(
            plan,
            Inputs(userData, memory.Read),
            ref snapshot,
            ref specialization,
            out var failure));
        Assert.Equal(ResourceMaterializationFailure.None, failure);
        Assert.Equal(new uint[] { 0x3000, 0, 256, 0 }, Assert.Single(snapshot.Buffers));
    }

    [Theory]
    [InlineData(32u, 1)]
    [InlineData(64u, 2)]
    public void SdwaCompareBackendWritesArchitecturalDestinationWidth(uint waveSize, int expectedS10Assignments)
    {
        var compare = CapturedWave32Compare(4);
        var request = Request(
            Program(
                MoveScalar(0, 10, 0x1234_5678),
                compare,
                EndProgram(12)),
            userDataCount: 12,
            waveSize: waveSize);

        Assert.True(
            Gen5MslTranslator.TryCompileProgram(request, out var metal, out var metalError),
            metalError);
        Assert.Equal(
            expectedS10Assignments,
            metal.Source.Split("s[10] =", StringSplitOptions.None).Length - 1);
        if (waveSize == 64)
        {
            Assert.Contains(
                "threadgroup uint sharpemu_wave_scratch[3];",
                metal.Source,
                StringComparison.Ordinal);
        }

        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(request, out var spirv, out var spirvError),
            spirvError);
        Assert.Equal(expectedS10Assignments, CountSpirvStoresToSgpr(spirv.Spirv, 10));
        Assert.True(CountSpirvOpcode(spirv.Spirv, SpirvOp.GroupNonUniformBallot) > 0);
    }

    [Theory]
    [InlineData(32u, false)]
    [InlineData(64u, true)]
    public void MaskBranchesReadOnlyTheArchitecturalWaveWidth(uint waveSize, bool readsHigh)
    {
        var program = Program(
            Branch(0, "SCbranchVccz", 1),
            Nop(4),
            EndProgram(8));
        var request = Request(program, userDataCount: 0, waveSize: waveSize);

        Assert.True(
            Gen5MslTranslator.TryCompileProgram(request, out var metal, out var error),
            error);
        Assert.Equal(
            readsHigh,
            metal.Source.Contains("s[106] | s[107]", StringComparison.Ordinal));
    }

    [Fact]
    public void CapturedA6feShaderBuildsACompleteResourcePlan()
    {
        var program = DecodeProgram(
            0xBFA00003u, 0xD7460000u, 0x04010C0Eu, 0x8805FF0Du, 0x00040000u, 0xBE84030Cu, 0xBE860381u, 0xBE8703FFu,
            0x00016204u, 0xB06A0100u, 0xE0300000u, 0x80010400u, 0x92049281u, 0x7E0A026Au, 0x996A9480u, 0xB0062000u,
            0xB00A0080u, 0x380212F9u, 0x8686066Au, 0x7E040208u, 0x7E0602FFu, 0x00016204u, 0xBF8C3F70u, 0x7D8808FFu,
            0x00000200u, 0x7D8808F9u, 0x06868404u, 0x7D8808F9u, 0x06868606u, 0x020D80F9u, 0x8686060Au, 0xD5010005u,
            0x00120AFFu, 0x00000200u, 0xD5010006u, 0x001A0D05u, 0x7E0A0D06u, 0x7E0C0506u, 0xD76D0004u, 0x041A08C1u,
            0x7E0A5705u, 0x81EA0680u, 0x7E080504u, 0x100A0AFFu, 0x4F7FFFFEu, 0x7E0A0F05u, 0x7ED60505u, 0x936A6B6Au,
            0x9AEA6B6Au, 0x816A6A6Bu, 0x9A87046Au, 0x936A0607u, 0x4E08086Au, 0x4E0A0806u, 0x7D8608F9u, 0x06868406u,
            0x7D860A06u, 0x87EA046Au, 0x020902F9u, 0x86860680u, 0xD5286A04u, 0x00120807u, 0xBEEA047Eu, 0x7DA40080u,
            0xBF880003u, 0x7E0A0280u, 0xE0700000u, 0x80000500u, 0xBEFE046Au, 0x7E0A0280u, 0x7E000502u, 0x7E020501u,
            0x7E040504u, 0x7E060503u, 0xE0702010u, 0x80000500u, 0xBF810000u);

        var (plan, resources, layout) = Prepare(program, userDataCount: 14);
        Assert.Equal(2, plan.Info.Buffers.Count);
        Assert.Single(plan.TableReads);
        Assert.True(plan.Memory.Find(0x28)!.PlanningOnly);

        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            LocalSizeX = 1,
            ThreadCountX = 1,
        };
        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(request, out _, out var spirvError),
            spirvError);
        Assert.True(
            Gen5MslTranslator.TryCompileProgram(request, out var metal, out var metalError),
            metalError);
        Assert.Contains("flattened_table", metal.Source, StringComparison.Ordinal);

        var userData = new uint[]
        {
            0xBD62_4100, 0x0004_0020, 1, 0x0000_5204,
            0xBD62_2500, 0x0008_0020, 0x90, 0x0000_5204,
            0xBD62_4104, 0x0000_0020, 0xBD66_0090, 0x0000_0020,
            0xBD62_0040, 0x0000_0020,
        };
        var memory = new TestWordMemory
        {
            Base = 0x0020_BD62_0040ul,
            Words = [320u],
        };
        var dynamicBuffer = Assert.Single(
            plan.Info.Buffers,
            buffer => buffer.FirstUsePc == 0x128);
        Assert.True(
            RuntimeValueEvaluator.EvaluateDescriptorSource(
                plan,
                dynamicBuffer.Source,
                Inputs(userData, memory.Read),
                out var descriptor));
        Assert.Equal(0xBD62_4104u, descriptor.Dwords[0]);
        Assert.Equal(0x0014_0020u, descriptor.Dwords[1]);
        Assert.Equal(5u, descriptor.Dwords[2]);
        Assert.Equal(0x0001_6204u, descriptor.Dwords[3]);
    }

    private static Gen5ShaderInstruction Decode(params uint[] words)
    {
        var complete = new uint[words.Length + 1];
        words.CopyTo(complete, 0);
        complete[^1] = 0xBF810000;
        return DecodeProgram(complete).Instructions[0];
    }

    private static Gen5ShaderInstruction CapturedWave32Compare(uint pc)
    {
        var compare = Decode(0x7D8A9AF9u, 0x06868987u) with { Pc = pc };
        Assert.Equal(Gen5ShaderEncoding.Vopc, compare.Encoding);
        Assert.Equal("VCmpNeU32", compare.Opcode);
        Assert.Equal(Gen5Operand.Scalar(9), Assert.Single(compare.Destinations));
        Assert.Equal(9u, Assert.IsType<Gen5SdwaControl>(compare.Control).ScalarDestination);
        return compare;
    }

    private static int CountSpirvStoresToSgpr(byte[] spirv, uint register)
    {
        var instructions = new List<(ushort Opcode, uint[] Words)>();
        for (var offset = 5 * sizeof(uint); offset + sizeof(uint) <= spirv.Length;)
        {
            var header = BinaryPrimitives.ReadUInt32LittleEndian(spirv.AsSpan(offset, sizeof(uint)));
            var wordCount = checked((int)(header >> 16));
            if (wordCount <= 0 || offset + wordCount * sizeof(uint) > spirv.Length)
            {
                break;
            }

            var words = new uint[wordCount];
            for (var index = 0; index < wordCount; index++)
            {
                words[index] = BinaryPrimitives.ReadUInt32LittleEndian(
                    spirv.AsSpan(offset + index * sizeof(uint), sizeof(uint)));
            }

            instructions.Add(((ushort)header, words));
            offset += wordCount * sizeof(uint);
        }

        uint sgpr = 0;
        foreach (var (opcode, words) in instructions)
        {
            if (opcode != 5 || words.Length < 3) continue; // OpName
            var nameBytes = new byte[(words.Length - 2) * sizeof(uint)];
            for (var index = 2; index < words.Length; index++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(
                    nameBytes.AsSpan((index - 2) * sizeof(uint), sizeof(uint)),
                    words[index]);
            }

            var zero = Array.IndexOf(nameBytes, (byte)0);
            var name = System.Text.Encoding.UTF8.GetString(nameBytes, 0, zero < 0 ? nameBytes.Length : zero);
            if (name == "sgpr") sgpr = words[1];
        }

        Assert.NotEqual(0u, sgpr);
        var registerConstants = instructions
            .Where(instruction => instruction.Opcode == 43 && instruction.Words.Length >= 4 && instruction.Words[3] == register) // OpConstant
            .Select(instruction => instruction.Words[2])
            .ToHashSet();
        var pointers = instructions
            .Where(instruction => instruction.Opcode == 65 && instruction.Words.Length >= 5 && // OpAccessChain
                instruction.Words[3] == sgpr && registerConstants.Contains(instruction.Words[^1]))
            .Select(instruction => instruction.Words[2])
            .ToHashSet();
        return instructions.Count(instruction =>
            instruction.Opcode == 62 && instruction.Words.Length >= 3 && pointers.Contains(instruction.Words[1])); // OpStore
    }

    private static int CountSpirvOpcode(byte[] spirv, SpirvOp opcode)
    {
        var count = 0;
        for (var offset = 5 * sizeof(uint); offset + sizeof(uint) <= spirv.Length;)
        {
            var header = BinaryPrimitives.ReadUInt32LittleEndian(spirv.AsSpan(offset, sizeof(uint)));
            var wordCount = checked((int)(header >> 16));
            if (wordCount <= 0 || offset + wordCount * sizeof(uint) > spirv.Length)
            {
                break;
            }

            if ((ushort)header == (ushort)opcode) count++;
            offset += wordCount * sizeof(uint);
        }

        return count;
    }

    private static Gen5ShaderProgram DecodeProgram(params uint[] words)
    {
        var bytes = new byte[words.Length * sizeof(uint)];
        for (var index = 0; index < words.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(index * sizeof(uint)),
                words[index]);
        }

        var context = new CpuContext(new InstructionMemory(bytes), Generation.Gen5);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(context, 0x1000, out var program, out var error),
            error);
        return program;
    }

    private sealed class InstructionMemory(byte[] bytes) : ICpuMemory
    {
        public bool TryRead(ulong address, Span<byte> destination)
        {
            if (address < 0x1000 || destination.Length > bytes.Length ||
                address - 0x1000 > (ulong)(bytes.Length - destination.Length))
            {
                return false;
            }

            bytes.AsSpan((int)(address - 0x1000), destination.Length).CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => false;
    }
}
