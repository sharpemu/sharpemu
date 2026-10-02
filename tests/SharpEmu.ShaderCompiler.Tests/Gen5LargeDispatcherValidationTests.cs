// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.ShaderCompiler.Vulkan;
using SharpEmu.ShaderCompiler.Tests.Resources;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5LargeDispatcherValidationTests
{
    [Fact]
    public void LargeDispatcherProducesValidStructuredSpirv()
    {
        const int branchPairs = 400;
        var instructions = new List<Gen5ShaderInstruction>(branchPairs * 2 + 1);
        for (var index = 0; index < branchPairs; index++)
        {
            var pc = (uint)index * 8;
            instructions.Add(ResourceTestProgram.Branch(pc, "SCbranchScc0", 1));
            instructions.Add(ResourceTestProgram.MoveScalar(pc + 4, 0, (uint)index));
        }

        instructions.Add(ResourceTestProgram.EndProgram((uint)branchPairs * 8));
        var request = ResourceTestProgram.Request(
            new Gen5ShaderProgram(0, instructions),
            userDataCount: 0);

        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error),
            error);
        Assert.Contains(
            ReadSpirvOpcodes(shader.Spirv),
            opcode => opcode == SpirvOp.SelectionMerge);

        ValidateStructuredControlFlow(shader.Spirv);
        ValidateWithSpirvToolsWhenAvailable(shader.Spirv);
    }

    [Fact]
    public void ScalarBitCount64Uses32BitSpirvOperands()
    {
        var request = ResourceTestProgram.Request(
            ResourceTestProgram.Program(
                ResourceTestProgram.MoveScalar(0, 0, 0x0123_4567),
                ResourceTestProgram.MoveScalar(4, 1, 0x89AB_CDEF),
                ResourceTestProgram.Sop1(
                    8,
                    "SBcnt1I32B64",
                    2,
                    Gen5Operand.Scalar(0)),
                ResourceTestProgram.EndProgram(12)),
            userDataCount: 0);

        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error),
            error);

        ValidateBitCountOperandTypes(shader.Spirv);
        ValidateWithSpirvToolsWhenAvailable(shader.Spirv);
    }

    [Fact]
    public void DataShareWaveCounterUses32BitBitCountOperands()
    {
        var request = ResourceTestProgram.Request(
            ResourceTestProgram.Program(
                ResourceTestProgram.MoveScalar(0, 124, 0x0001_0004),
                ResourceTestProgram.DataShare(
                    4,
                    "DsAppend",
                    gds: false,
                    [Gen5Operand.Scalar(124)],
                    [1]),
                ResourceTestProgram.EndProgram(8)),
            userDataCount: 0);

        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error),
            error);

        ValidateBitCountOperandTypes(shader.Spirv);
        ValidateWithSpirvToolsWhenAvailable(shader.Spirv);
    }

    internal static IReadOnlyList<SpirvOp> ReadSpirvOpcodes(byte[] code)
    {
        var words = new uint[code.Length / sizeof(uint)];
        Buffer.BlockCopy(code, 0, words, 0, code.Length);
        var result = new List<SpirvOp>();
        for (var index = 5; index < words.Length;)
        {
            var instructionWord = words[index];
            var wordCount = (int)(instructionWord >> 16);
            Assert.True(wordCount > 0 && index + wordCount <= words.Length);
            result.Add((SpirvOp)(instructionWord & 0xFFFF));
            index += wordCount;
        }

        return result;
    }

    private static IReadOnlyList<ParsedInstruction> ReadSpirvInstructions(byte[] code)
    {
        var words = new uint[code.Length / sizeof(uint)];
        Buffer.BlockCopy(code, 0, words, 0, code.Length);
        var result = new List<ParsedInstruction>();
        for (var index = 5; index < words.Length;)
        {
            var instructionWord = words[index];
            var wordCount = (int)(instructionWord >> 16);
            Assert.True(wordCount > 0 && index + wordCount <= words.Length);
            result.Add(new ParsedInstruction(
                (SpirvOp)(instructionWord & 0xFFFF),
                words[(index + 1)..(index + wordCount)]));
            index += wordCount;
        }

        return result;
    }

    internal static void ValidateStructuredControlFlow(byte[] code)
    {
        var blockOpen = false;
        var blockTerminated = false;
        var mergeCount = 0;
        foreach (var instruction in ReadSpirvInstructions(code))
        {
            if (instruction.Opcode == SpirvOp.FunctionEnd)
            {
                if (blockOpen)
                {
                    Assert.True(blockTerminated, "A function ended with an unterminated basic block.");
                    blockOpen = false;
                }

                continue;
            }

            if (instruction.Opcode == SpirvOp.Label)
            {
                if (blockOpen)
                {
                    Assert.True(blockTerminated, "An OpLabel followed an unterminated basic block.");
                }

                blockOpen = true;
                blockTerminated = false;
                mergeCount = 0;
                continue;
            }

            if (!blockOpen)
            {
                continue;
            }

            if (instruction.Opcode is SpirvOp.SelectionMerge or SpirvOp.LoopMerge)
            {
                Assert.True(++mergeCount == 1, "A basic block contains more than one merge instruction.");
            }

            if (IsTerminator(instruction.Opcode))
            {
                Assert.False(blockTerminated, "A basic block contains instructions after its terminator.");
                blockTerminated = true;
            }
            else
            {
                Assert.False(blockTerminated, "A basic block contains an instruction after its terminator.");
            }
        }

        Assert.False(blockOpen, "The SPIR-V module ended inside a basic block.");
    }

    private static void ValidateBitCountOperandTypes(byte[] code)
    {
        var instructions = ReadSpirvInstructions(code);
        var integerWidths = instructions
            .Where(instruction => instruction.Opcode == SpirvOp.TypeInt)
            .ToDictionary(instruction => instruction.Operands[0], instruction => instruction.Operands[1]);
        var resultTypes = instructions
            .Where(instruction => HasResultType(instruction.Opcode) && instruction.Operands.Length >= 2)
            .ToDictionary(instruction => instruction.Operands[1], instruction => instruction.Operands[0]);
        var bitCounts = instructions.Where(instruction => instruction.Opcode == SpirvOp.BitCount).ToArray();

        Assert.NotEmpty(bitCounts);
        foreach (var bitCount in bitCounts)
        {
            Assert.True(integerWidths.TryGetValue(bitCount.Operands[0], out var resultWidth));
            Assert.Equal(32u, resultWidth);
            Assert.True(resultTypes.TryGetValue(bitCount.Operands[2], out var operandType));
            Assert.True(integerWidths.TryGetValue(operandType, out var operandWidth));
            Assert.Equal(32u, operandWidth);
        }
    }

    private static bool HasResultType(SpirvOp opcode) => opcode is
        SpirvOp.BitCount or SpirvOp.Bitcast or SpirvOp.IAdd or SpirvOp.ISub or
        SpirvOp.ShiftRightLogical or SpirvOp.UConvert or SpirvOp.Constant;

    private static bool IsTerminator(SpirvOp opcode) => opcode is
        SpirvOp.Branch or SpirvOp.BranchConditional or SpirvOp.Switch or SpirvOp.Kill or
        SpirvOp.Return or SpirvOp.ReturnValue or SpirvOp.Unreachable ||
        (ushort)opcode is 4416 or 4448 or 4449; // OpTerminateInvocation/IgnoreIntersectionKHR/TerminateRayKHR.

    private sealed record ParsedInstruction(SpirvOp Opcode, uint[] Operands);

    internal static void ValidateWithSpirvToolsWhenAvailable(byte[] code)
    {
        var sdk = Environment.GetEnvironmentVariable("VULKAN_SDK");
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(sdk))
        {
            candidates.Add(Path.Combine(sdk, OperatingSystem.IsWindows() ? "Bin/spirv-val.exe" : "bin/spirv-val"));
        }

        candidates.Add(@"C:\VulkanSDK\1.4.357.0\Bin\spirv-val.exe");
        var executable = candidates.FirstOrDefault(File.Exists);
        if (executable is null)
        {
            return;
        }

        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, code);
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("--target-env");
            start.ArgumentList.Add("vulkan1.2");
            start.ArgumentList.Add(path);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, $"spirv-val failed: {output}{error}");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
