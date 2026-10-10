// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using Xunit.Abstractions;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests;

// Replays the branch skeleton of every dumped program (`*.input.ir.txt`) in the directory named
// by SHARPEMU_CFG_SKELETON_DIR through the structured planner. Every other instruction becomes a
// no-op, so only the control flow is tested. Without the variable the test does nothing.
public sealed class Gen5DumpedSkeletonTests(ITestOutputHelper output)
{
    [Fact]
    public void DumpedSkeletonsAreStructured()
    {
        var directory = Environment.GetEnvironmentVariable("SHARPEMU_CFG_SKELETON_DIR");
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        var failures = new List<string>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.input.ir.txt"))
        {
            var program = Program([.. ReadSkeleton(file)]);
            var code = Gen5StructuredControlFlowTests.Compile(program, forceDispatcher: false);
            if (!Gen5LargeDispatcherValidationTests.ReadSpirvOpcodes(code).Contains(SpirvOp.FunctionCall))
            {
                failures.Add(Path.GetFileName(file));
                continue;
            }

            Gen5LargeDispatcherValidationTests.ValidateStructuredControlFlow(code);
            Gen5LargeDispatcherValidationTests.ValidateWithSpirvToolsWhenAvailable(code);
        }

        foreach (var failure in failures)
        {
            output.WriteLine(failure);
        }

        Assert.Empty(failures);
    }

    private static IEnumerable<Gen5ShaderInstruction> ReadSkeleton(string file)
    {
        foreach (var line in File.ReadLines(file))
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 3 || !fields[0].StartsWith("0x", StringComparison.Ordinal))
            {
                continue;
            }

            var pc = uint.Parse(fields[0].AsSpan(2), NumberStyles.HexNumber);
            var word = uint.Parse(fields[1].Split('_')[0], NumberStyles.HexNumber);
            var opcode = fields[2];
            if (opcode == "SEndpgm")
            {
                yield return EndProgram(pc);
            }
            else if (opcode == "SBranch" || opcode.StartsWith("SCbranch", StringComparison.Ordinal))
            {
                yield return Branch(pc, opcode, unchecked((short)(word & 0xFFFF)));
            }
            else
            {
                yield return Nop(pc);
            }
        }
    }
}
