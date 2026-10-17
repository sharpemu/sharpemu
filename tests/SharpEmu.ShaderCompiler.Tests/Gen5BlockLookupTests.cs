// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections;
using System.Reflection;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5BlockLookupTests
{
    private static readonly Type Context = typeof(Gen5SpirvTranslator).GetNestedType("CompilationContext", BindingFlags.NonPublic)!;
    private static readonly MethodInfo Build = Context.GetMethod("BuildBasicBlocks", BindingFlags.Static | BindingFlags.NonPublic)!;
    private static readonly MethodInfo Find = Context.GetMethod("TryFindBlock", BindingFlags.Static | BindingFlags.NonPublic)!;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IndexedBlocks_MatchLinearReference(bool splitBarriers)
    {
        var random = new Random(531);
        for (var sample = 0; sample < 200; sample++)
        {
            var instructions = Enumerable.Range(0, sample).Select(index => new Gen5ShaderInstruction(
                sample % 2 == 0 ? (uint)index * 4 : (uint)random.Next(100) * 4,
                Gen5ShaderEncoding.Sopp,
                new[] { "VMovB32", "SBranch", "SCbranchExecz", "SEndpgm", "SBarrier" }[random.Next(5)],
                [(uint)random.Next(65536)], [], [], null)).ToArray();
            Verify(instructions, splitBarriers);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(128)]
    public void Lookup_PreservesHitsAndMissingTargets(int count)
    {
        var instructions = Enumerable.Range(0, count).Select(index => new Gen5ShaderInstruction(
            (uint)index * 4, Gen5ShaderEncoding.Sopp, "SEndpgm", [0], [], [], null)).ToArray();
        Verify(instructions, false);
    }

    private static void Verify(Gen5ShaderInstruction[] instructions, bool splitBarriers)
    {
        var leaders = new SortedSet<uint>();
        if (instructions.Length != 0) leaders.Add(instructions[0].Pc);
        for (var index = 0; index < instructions.Length; index++)
        {
            var instruction = instructions[index];
            var branch = instruction.Opcode == "SBranch" || instruction.Opcode.StartsWith("SCbranch", StringComparison.Ordinal);
            if (branch)
            {
                var target = (long)instruction.Pc + instruction.Words.Count * 4 + unchecked((short)instruction.Words[0]) * 4;
                if (target >= 0 && target <= uint.MaxValue) leaders.Add((uint)target);
            }
            if ((branch || instruction.Opcode == "SEndpgm" || splitBarriers && instruction.Opcode == "SBarrier") && index + 1 < instructions.Length)
                leaders.Add(instructions[index + 1].Pc);
        }
        var starts = leaders.Where(pc => instructions.Any(instruction => instruction.Pc == pc)).ToArray();
        var expected = new List<(uint Pc, int Start, int End)>();
        for (var index = 0; index < starts.Length; index++)
        {
            var start = Array.FindIndex(instructions, instruction => instruction.Pc == starts[index]);
            var end = index + 1 < starts.Length
                ? Array.FindIndex(instructions, instruction => instruction.Pc == starts[index + 1]) : instructions.Length;
            if (start >= 0 && end > start) expected.Add((starts[index], start, end));
        }

        var blocks = Build.Invoke(null, [instructions, splitBarriers])!;
        var actual = ((IEnumerable)blocks).Cast<object>().Select(block =>
        {
            var type = block.GetType();
            return ((uint)type.GetProperty("StartPc")!.GetValue(block)!,
                (int)type.GetProperty("StartIndex")!.GetValue(block)!,
                (int)type.GetProperty("EndIndex")!.GetValue(block)!);
        }).ToArray();
        Assert.Equal(expected, actual);
        foreach (var pc in instructions.Select(instruction => instruction.Pc).Append(uint.MaxValue).Append(1u))
        {
            object?[] arguments = [blocks, pc, -1];
            var found = (bool)Find.Invoke(null, arguments)!;
            var index = expected.FindIndex(block => block.Pc == pc);
            Assert.Equal(index >= 0, found);
            Assert.Equal(index, (int)arguments[2]!);
        }
    }
}
