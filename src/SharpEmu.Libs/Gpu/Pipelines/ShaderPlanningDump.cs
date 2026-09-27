// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.Libs.Gpu.Pipelines;

// Save the planning inputs before validation can stop compilation. Graph output is
// bounded and cycle-safe; the instruction listing retains every decoded word.
internal static class ShaderPlanningDump
{
    private const int MaximumGraphNodes = 8192;

    public static void WriteInput(ShaderSource source, Gen5ShaderProgram program) => Write(source, "input.ir.txt", writer =>
    {
        writer.WriteLine($"stage={source.Stage} address=0x{source.Address:X16} hash=0x{source.Hash:X16}");
        writer.WriteLine($"user_data_base={source.UserDataBase} user_data_count={source.UserData.Length} code_bytes={source.CodeSize}");
        writer.WriteLine($"user_data={string.Join(',', source.UserData.Select(word => $"0x{word:X8}"))}");
        writer.WriteLine("pc words opcode destinations <- sources control");
        foreach (var instruction in program.Instructions)
        {
            writer.WriteLine($"0x{instruction.Pc:X4} {string.Join('_', instruction.Words.Select(word => $"{word:X8}"))} {instruction.Opcode} " +
                $"{string.Join(',', instruction.Destinations)} <- {string.Join(',', instruction.Sources)} {instruction.Control}");
        }
    });

    public static void WriteGraph(ShaderSource source, ShaderResourcePlan plan) => Write(source, "resource-graph.txt", writer =>
    {
        var pending = new Queue<ScalarValue>();
        var visited = new HashSet<ScalarValue>();
        void AddRoot(string name, ScalarValue? value)
        {
            if (value is null) return;
            writer.WriteLine($"root={name} node={value.Id}");
            pending.Enqueue(value);
        }

        for (var index = 0; index < plan.Memory.Count; index++)
        {
            var memory = plan.Memory[index];
            writer.WriteLine($"memory={index} pc=0x{memory.Pc:X8} opcode={memory.Opcode} kind={memory.Kind} access={memory.Access} " +
                $"offset={memory.Offset} words={memory.DataDwords} component={memory.ComponentIndex} planning_only={memory.PlanningOnly}");
            var access = plan.Accesses[index];
            AddRoot($"memory[{index}].handle", access?.Handle);
            AddRoot($"memory[{index}].sampler", access?.SamplerHandle);
            AddRoot($"memory[{index}].read", access?.Read);
            AddRoot($"memory[{index}].offset", access?.Offset);
        }

        foreach (var read in plan.TableReads) AddRoot($"flat[{read.FlatOffset}]", read.Value);
        foreach (var read in plan.DynamicReads) AddRoot("dynamic", read);
        while (pending.Count != 0 && visited.Count < MaximumGraphNodes)
        {
            var value = pending.Dequeue();
            if (!visited.Add(value)) continue;
            writer.WriteLine($"node={value.Id} kind={value.Kind} type={value.Type} operation={value.Operation} payload=0x{value.Payload:X16} " +
                $"operands={string.Join(',', value.Operands.Select(operand => operand.Id))} predecessors={string.Join(',', value.PhiPredecessors)}");
            foreach (var operand in value.Operands) pending.Enqueue(operand);
        }

        writer.WriteLine($"nodes={visited.Count} pending={pending.Count} limit={MaximumGraphNodes}");
    });

    public static void WriteFailure(ShaderSource source, string message) =>
        Write(source, "failure.txt", writer => writer.WriteLine(message));

    private static void Write(ShaderSource source, string suffix, Action<TextWriter> write)
    {
        if (!CompiledShaderDump.ShouldWrite(source.Address, source.Hash)) return;
        try
        {
            var path = $"{CompiledShaderDump.GetBasePath(source.Label, source.Address, source.Hash)}.{suffix}";
            using var writer = new StreamWriter(path);
            write(writer);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Console.Error.WriteLine($"[SHADER][WARN] Cannot write the planning dump: {exception.Message}");
        }
    }
}
