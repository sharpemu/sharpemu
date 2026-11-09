// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Diagnostics;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests;

// A loop closed by a backward branch is emitted as a structured SPIR-V loop instead of the PC
// dispatcher, with conditional exits from its body as breaks. Any other backward edge falls back
// to the guarded in-order blocks.
public sealed class Gen5StructuredLoopTests
{
    private const uint MoveFour = 0xBE800384;       // s_mov_b32 s0, 4
    private const uint SubtractOne = 0x80808100;    // s_sub_u32 s0, s0, 1
    private const uint CompareNotZero = 0xBF078000; // s_cmp_lg_u32 s0, 0
    private const uint CompareTwo = 0xBF068200;     // s_cmp_eq_u32 s0, 2
    private const uint CompareZero = 0xBF068000;    // s_cmp_eq_u32 s0, 0
    private const uint EndProgram = 0xBF810000;     // s_endpgm

    [Fact]
    public void BackwardConditionalBranchBecomesAStructuredLoop()
    {
        var spirv = Compile(MoveFour, SubtractOne, CompareNotZero, 0xBF85FFFD /* s_cbranch_scc1 0x04 */, EndProgram);

        var opcodes = Opcodes(spirv);
        Assert.Contains(SpirvOp.LoopMerge, opcodes);
        Assert.DoesNotContain(SpirvOp.Switch, opcodes);
        ValidateWhenAvailable(spirv);
    }

    [Fact]
    public void ConditionalExitFromTheBodyBecomesABreak()
    {
        var spirv = Compile(
            MoveFour, SubtractOne, CompareTwo,
            0xBF850002, // s_cbranch_scc1 0x18, past the loop
            CompareNotZero,
            0xBF85FFFB, // s_cbranch_scc1 0x04
            EndProgram);

        var opcodes = Opcodes(spirv);
        Assert.Contains(SpirvOp.LoopMerge, opcodes);
        Assert.DoesNotContain(SpirvOp.Switch, opcodes);
        ValidateWhenAvailable(spirv);
    }

    [Fact]
    public void UnconditionalBackBranchLoopLeftByABreakIsStructured()
    {
        var spirv = Compile(
            MoveFour, SubtractOne, CompareZero,
            0xBF850001, // s_cbranch_scc1 0x14, past the loop
            0xBF82FFFC, // s_branch 0x04
            EndProgram);

        var opcodes = Opcodes(spirv);
        Assert.Contains(SpirvOp.LoopMerge, opcodes);
        Assert.DoesNotContain(SpirvOp.Switch, opcodes);
        ValidateWhenAvailable(spirv);
    }

    [Fact]
    public void SecondBackEdgeToTheHeaderFallsBackToGuardedBlocks()
    {
        var spirv = Compile(
            MoveFour, SubtractOne, CompareTwo,
            0xBF85FFFD, // s_cbranch_scc1 0x04, a continue from the middle of the body
            CompareNotZero,
            0xBF85FFFB, // s_cbranch_scc1 0x04
            EndProgram);

        // Declined by the structurer; the guarded in-order blocks still emit the loop structured.
        var opcodes = Opcodes(spirv);
        Assert.Contains(SpirvOp.LoopMerge, opcodes);
        Assert.DoesNotContain(SpirvOp.Switch, opcodes);
        ValidateWhenAvailable(spirv);
    }

    private static byte[] Compile(params uint[] words)
    {
        var context = new CpuContext(new InstructionMemory(words), Generation.Gen5);
        Assert.True(Gen5ShaderTranslator.TryDecodeProgram(context, 0x1000, out var program, out var decodeError), decodeError);
        var (plan, resources, layout) = Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 1, ThreadCountX = 1 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        return shader.Spirv;
    }

    private static List<SpirvOp> Opcodes(byte[] code)
    {
        var result = new List<SpirvOp>();
        for (var index = 5 * sizeof(uint); index < code.Length;)
        {
            var word = BinaryPrimitives.ReadUInt32LittleEndian(code.AsSpan(index));
            result.Add((SpirvOp)(word & 0xFFFF));
            index += (int)(word >> 16) * sizeof(uint);
        }

        return result;
    }

    private static void ValidateWhenAvailable(byte[] code)
    {
        var sdk = Environment.GetEnvironmentVariable("VULKAN_SDK");
        var executable = string.IsNullOrWhiteSpace(sdk) ? null
            : Path.Combine(sdk, OperatingSystem.IsWindows() ? "Bin/spirv-val.exe" : "bin/spirv-val");
        if (executable is null || !File.Exists(executable))
        {
            return;
        }

        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, code);
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true };
            start.ArgumentList.Add("--target-env");
            start.ArgumentList.Add("vulkan1.2");
            start.ArgumentList.Add(path);
            using var process = Process.Start(start)!;
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, error);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class InstructionMemory(uint[] words) : ICpuMemory
    {
        public bool TryRead(ulong address, Span<byte> destination)
        {
            var index = (address - 0x1000) / sizeof(uint);
            if (address < 0x1000 || (address - 0x1000) % sizeof(uint) != 0 || destination.Length != sizeof(uint) ||
                index >= (ulong)words.Length)
            {
                return false;
            }

            BinaryPrimitives.WriteUInt32LittleEndian(destination, words[(int)index]);
            return true;
        }

        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => false;
    }
}
