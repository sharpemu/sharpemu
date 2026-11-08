// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;

namespace SharpEmu.ShaderCompiler.Evaluation;

public sealed class Gen5EvaluableProgram
{
    public const int MaxInstructions = 256;

    private static readonly ConditionalWeakTable<Gen5ShaderProgram, Gen5EvaluableProgram> _programs = new();

    private Gen5EvaluableProgram(Gen5EvalInstruction[] instructions, uint vectorRegisters, string refusal)
    {
        Instructions = instructions;
        VectorRegisters = vectorRegisters;
        Refusal = refusal;
    }

    public Gen5EvalInstruction[] Instructions { get; }

    public uint VectorRegisters { get; }

    public string Refusal { get; }

    public bool IsEvaluable => Refusal.Length == 0;

    public static Gen5EvaluableProgram Get(Gen5ShaderProgram program) =>
        _programs.GetValue(program, static decoded => Create(decoded));

    public static Gen5EvaluableProgram Create(Gen5ShaderProgram program)
    {
        var instructions = program.Instructions;
        if (instructions.Count == 0 || instructions.Count > MaxInstructions)
        {
            return Refused("instruction count");
        }

        var indexOfPc = new Dictionary<uint, int>(instructions.Count);
        for (var index = 0; index < instructions.Count; index++)
        {
            indexOfPc.TryAdd(instructions[index].Pc, index);
        }

        var lowered = new Gen5EvalInstruction[instructions.Count];
        var vectorRegisters = 3u;
        var stores = 0;
        for (var index = 0; index < instructions.Count; index++)
        {
            if (!Gen5EvaluableLowering.TryLower(instructions[index], indexOfPc, out var instruction, out var reason))
            {
                return Refused(reason);
            }

            lowered[index] = instruction;
            vectorRegisters = Math.Max(vectorRegisters, Gen5EvaluableLowering.VectorRegistersUsed(instruction));
            stores += instruction.Op == Gen5EvalOp.BufferStore ? 1 : 0;
        }

        if (stores == 0)
        {
            return Refused("no buffer store");
        }

        if (vectorRegisters > 256)
        {
            return Refused("vector registers");
        }

        return new Gen5EvaluableProgram(lowered, vectorRegisters, string.Empty);
    }

    private static Gen5EvaluableProgram Refused(string reason) => new([], 0, reason);
}
