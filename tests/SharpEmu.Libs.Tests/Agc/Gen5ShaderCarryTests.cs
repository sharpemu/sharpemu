// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

// Covers the GFX10 VOP3B carry family, whose third source is a Wave32 lane mask and
// whose scalar destination is a single 32-bit mask rather than an SGPR pair.
public sealed class Gen5ShaderCarryTests
{
    private const ulong ShaderAddress = 0x1_0000_0000;

    [Fact]
    public void RealVop3AddcWord_DecodesSourcesAndScalarDestination()
    {
        // V_ADDC_CO_U32 v0, s106, s19, v0, s16
        var instruction = DecodeSingle(0xD5286A00, 0x00420013);

        Assert.Equal(Gen5ShaderEncoding.Vop3, instruction.Encoding);
        Assert.Equal("VAddcU32", instruction.Opcode);
        Assert.Equal(
            new[]
            {
                Gen5Operand.Scalar(19),
                Gen5Operand.Vector(0),
                Gen5Operand.Scalar(16),
            },
            instruction.Sources);
        Assert.Equal(new[] { Gen5Operand.Vector(0) }, instruction.Destinations);
        var control = Assert.IsType<Gen5Vop3Control>(instruction.Control);
        Assert.Equal(0u, control.AbsoluteMask);
        Assert.Equal(106u, control.ScalarDestination);
    }

    [Theory]
    [InlineData(0xD5281400u, "VAddcU32")]
    [InlineData(0xD5291400u, "VSubbU32")]
    [InlineData(0xD52A1400u, "VSubbrevU32")]
    public void Vop3CarryFamily_DecodesAsVop3b(uint word, string opcode)
    {
        var instruction = DecodeSingle(word, 0x00420013);

        Assert.Equal(opcode, instruction.Opcode);
        Assert.Equal(3, instruction.Sources.Count);
        var control = Assert.IsType<Gen5Vop3Control>(instruction.Control);
        Assert.Equal(20u, control.ScalarDestination);
    }

    [Theory]
    [InlineData(0xD5281400u, (ushort)SpirvOp.IAdd)]
    [InlineData(0xD5291400u, (ushort)SpirvOp.ISub)]
    [InlineData(0xD52A1400u, (ushort)SpirvOp.ISub)]
    public void Vop3CarryFamily_Translates(uint word, ushort arithmeticOpcode)
    {
        var spirv = CompileCompute(word, 0x00420013);
        var module = ParseModule(spirv);

        Assert.Contains(module, instruction => instruction.Opcode == (SpirvOp)arithmeticOpcode);
    }

    [Fact]
    public void Vop3AddcTranslation_ReadsExplicitMaskAndPreservesAdjacentSgpr()
    {
        var spirv = CompileCompute(0xD5281400, 0x00420013);
        var module = ParseModule(spirv);
        var accesses = AnalyzeScalarAccesses(module);

        Assert.Contains(16u, accesses.LoadedRegisters);
        Assert.Equal(1, accesses.StoredRegisters.GetValueOrDefault(20u));
        Assert.False(accesses.StoredRegisters.ContainsKey(21u));
        Assert.Contains(module, instruction => instruction.Opcode == SpirvOp.Not);
    }

    [Fact]
    public void Vop2AddcTranslation_KeepsImplicitVccInput()
    {
        // V_ADDC_U32 v0, s19, v0. Unlike VOP3B, the carry-in is implicit VCC.
        var spirv = CompileCompute(0x50000013);
        var accesses = AnalyzeScalarAccesses(ParseModule(spirv));

        Assert.DoesNotContain(16u, accesses.LoadedRegisters);
    }

    [Fact]
    public void RealVop3AddcTranslation_UpdatesVccLowWithoutExtraHighWrite()
    {
        var spirv = CompileCompute(0xD5286A00, 0x00420013);
        var accesses = AnalyzeScalarAccesses(ParseModule(spirv));

        // Initial wave-state setup writes each half once. The instruction adds one
        // write to VCC_LO only; VCC_HI is not an implicit sdst+1 destination.
        Assert.Equal(2, accesses.StoredRegisters.GetValueOrDefault(106u));
        Assert.Equal(1, accesses.StoredRegisters.GetValueOrDefault(107u));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void CarryTranslation_UsesLaneIdentityInEveryShaderStage(
        int stageValue)
    {
        var stage = (Gen5SpirvStage)stageValue;
        var shader = Compile(stage, 0xD5286A00, 0x00420013);
        var module = ParseModule(shader.Spirv);

        Assert.Equal(
            Gen5ShaderSubgroupFeatures.LaneIdentity,
            shader.SubgroupRequirements.Features);
        Assert.Contains(
            module,
            instruction =>
                instruction.Opcode == SpirvOp.Capability &&
                instruction.Operands[0] == (uint)SpirvCapability.GroupNonUniform);
        Assert.DoesNotContain(
            module,
            instruction =>
                instruction.Opcode == SpirvOp.Capability &&
                instruction.Operands[0] == (uint)SpirvCapability.GroupNonUniformVote);

        var subgroupInvocationId = module.Single(instruction =>
            instruction.Opcode == SpirvOp.Decorate &&
            instruction.Operands.Length >= 3 &&
            instruction.Operands[1] == (uint)SpirvDecoration.BuiltIn &&
            instruction.Operands[2] == (uint)SpirvBuiltIn.SubgroupLocalInvocationId).Operands[0];
        Assert.Contains(
            module,
            instruction =>
                instruction.Opcode == SpirvOp.Load &&
                instruction.Operands.Length >= 3 &&
                instruction.Operands[2] == subgroupInvocationId);
    }

    [Fact]
    public void Vop3CarryNullDestination_DiscardsMaskWrite()
    {
        var instruction = DecodeSingle(0xD5287D00, 0x00420013);
        Assert.Equal(125u, Assert.IsType<Gen5Vop3Control>(instruction.Control).ScalarDestination);

        var accesses = AnalyzeScalarAccesses(
            ParseModule(CompileCompute(0xD5287D00, 0x00420013)));
        Assert.DoesNotContain(125u, accesses.LoadedRegisters);
        Assert.False(accesses.StoredRegisters.ContainsKey(125u));
    }

    [Fact]
    public void Vop3CarryNullSource_ReadsZeroWithoutScalarAccess()
    {
        var instruction = DecodeSingle(0xD5281400, 0x01F60013);
        Assert.Equal(
            new Gen5Operand(Gen5OperandKind.EncodedConstant, 125),
            instruction.Sources[2]);

        var accesses = AnalyzeScalarAccesses(
            ParseModule(CompileCompute(0xD5281400, 0x01F60013)));
        Assert.DoesNotContain(125u, accesses.LoadedRegisters);
    }

    private static Gen5ShaderInstruction DecodeSingle(params uint[] words)
    {
        var memory = new FakeCpuMemory(ShaderAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        Gen5ShaderAtomicDecodeTests.WriteProgram(memory, ShaderAddress, words);
        Assert.True(
            Gen5ShaderTranslator.TryCreateState(
                ctx,
                ShaderAddress,
                0,
                new Dictionary<uint, uint> { [0x213] = 0 },
                0x240,
                out var state,
                out var error),
            error);
        return state.Program.Instructions[0];
    }

    private static byte[] CompileCompute(params uint[] words)
        => Compile(Gen5SpirvStage.Compute, words).Spirv;

    private static Gen5SpirvShader Compile(
        Gen5SpirvStage stage,
        params uint[] words)
    {
        var memory = new FakeCpuMemory(ShaderAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        Gen5ShaderAtomicDecodeTests.WriteProgram(memory, ShaderAddress, words);
        Assert.True(
            Gen5ShaderTranslator.TryCreateState(
                ctx,
                ShaderAddress,
                0,
                new Dictionary<uint, uint> { [0x213] = 0 },
                0x240,
                out var state,
                out var error),
            error);
        Assert.True(
            Gen5ShaderScalarEvaluator.TryEvaluate(ctx, state, out var evaluation, out error),
            error);
        var compiled = stage switch
        {
            Gen5SpirvStage.Vertex => Gen5SpirvTranslator.TryCompileVertexShader(
                state,
                evaluation,
                out var vertexShader,
                out error)
                ? vertexShader
                : throw new Xunit.Sdk.XunitException(error),
            Gen5SpirvStage.Pixel => Gen5SpirvTranslator.TryCompilePixelShader(
                state,
                evaluation,
                Gen5PixelOutputKind.Float,
                out var pixelShader,
                out error)
                ? pixelShader
                : throw new Xunit.Sdk.XunitException(error),
            _ => Gen5SpirvTranslator.TryCompileComputeShader(
                state,
                evaluation,
                32,
                1,
                1,
                out var computeShader,
                out error)
                ? computeShader
                : throw new Xunit.Sdk.XunitException(error),
        };
        return compiled;
    }

    private static IReadOnlyList<ParsedInstruction> ParseModule(byte[] spirv)
    {
        var instructions = new List<ParsedInstruction>();
        for (var offset = 5 * sizeof(uint); offset + sizeof(uint) <= spirv.Length;)
        {
            var header = BinaryPrimitives.ReadUInt32LittleEndian(
                spirv.AsSpan(offset, sizeof(uint)));
            var wordCount = Math.Max((int)(header >> 16), 1);
            var operands = new uint[wordCount - 1];
            for (var index = 0; index < operands.Length; index++)
            {
                operands[index] = BinaryPrimitives.ReadUInt32LittleEndian(
                    spirv.AsSpan(offset + ((index + 1) * sizeof(uint)), sizeof(uint)));
            }

            instructions.Add(new ParsedInstruction((SpirvOp)(ushort)header, operands));
            offset += wordCount * sizeof(uint);
        }

        return instructions;
    }

    private static ScalarAccesses AnalyzeScalarAccesses(
        IReadOnlyList<ParsedInstruction> module)
    {
        var scalarRegistersId = module
            .Where(instruction => instruction.Opcode == SpirvOp.Name)
            .Single(instruction => DecodeString(instruction.Operands, 1) == "sgpr")
            .Operands[0];
        var constants = module
            .Where(instruction =>
                instruction.Opcode == SpirvOp.Constant &&
                instruction.Operands.Length == 3)
            .ToDictionary(instruction => instruction.Operands[1], instruction => instruction.Operands[2]);
        var pointers = new Dictionary<uint, uint>();
        foreach (var instruction in module.Where(instruction =>
                     instruction.Opcode == SpirvOp.AccessChain &&
                     instruction.Operands.Length == 4 &&
                     instruction.Operands[2] == scalarRegistersId))
        {
            if (constants.TryGetValue(instruction.Operands[3], out var register))
            {
                pointers[instruction.Operands[1]] = register;
            }
        }

        var loaded = new HashSet<uint>();
        var stored = new Dictionary<uint, int>();
        foreach (var instruction in module)
        {
            if (instruction.Opcode == SpirvOp.Load &&
                pointers.TryGetValue(instruction.Operands[2], out var loadedRegister))
            {
                loaded.Add(loadedRegister);
            }
            else if (instruction.Opcode == SpirvOp.Store &&
                     pointers.TryGetValue(instruction.Operands[0], out var storedRegister))
            {
                stored[storedRegister] = stored.GetValueOrDefault(storedRegister) + 1;
            }
        }

        return new ScalarAccesses(loaded, stored);
    }

    private static string DecodeString(uint[] operands, int startIndex)
    {
        var bytes = new List<byte>();
        foreach (var word in operands.Skip(startIndex))
        {
            for (var shift = 0; shift < 32; shift += 8)
            {
                var value = (byte)(word >> shift);
                if (value == 0)
                {
                    return Encoding.UTF8.GetString(bytes.ToArray());
                }

                bytes.Add(value);
            }
        }

        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private sealed record ParsedInstruction(SpirvOp Opcode, uint[] Operands);

    private sealed record ScalarAccesses(
        IReadOnlySet<uint> LoadedRegisters,
        IReadOnlyDictionary<uint, int> StoredRegisters);
}
