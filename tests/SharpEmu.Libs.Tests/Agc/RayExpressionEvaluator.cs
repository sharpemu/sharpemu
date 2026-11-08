// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Vulkan;

namespace SharpEmu.Libs.Tests.Agc;

// Evaluate the emitted scalar math, not shader control flow or device memory access.
internal sealed class RayExpressionEvaluator
{
    private readonly Dictionary<string, uint> _names;
    private readonly Dictionary<uint, (SpirvOp Opcode, uint[] Operands)> _expressions = [];
    private readonly Dictionary<uint, uint> _inputs = [];

    public RayExpressionEvaluator(byte[] spirv)
    {
        _names = new SpirvModuleInspector(spirv).Names.ToDictionary(pair => pair.Value, pair => pair.Key);
        var words = new uint[spirv.Length / 4];
        Buffer.BlockCopy(spirv, 0, words, 0, spirv.Length);
        for (var offset = 5; offset < words.Length;)
        {
            var count = checked((int)(words[offset] >> 16));
            var opcode = (SpirvOp)(words[offset] & 0xFFFF);
            if (IsExpression(opcode))
                _expressions.Add(words[offset + 2], (opcode, words[(offset + 3)..(offset + count)]));
            offset += count;
        }
    }

    public void Set(string name, uint value) => _inputs[_names[name]] = value;
    public void SetFloat(string name, float value) => Set(name, Bits(value));
    public uint Read(string name) => Evaluate(_names[name], []);
    public float ReadFloat(string name) => Float(Read(name));

    private uint Evaluate(uint id, Dictionary<uint, uint> cache)
    {
        if (_inputs.TryGetValue(id, out var input)) return input;
        if (cache.TryGetValue(id, out var cached)) return cached;
        if (!_expressions.TryGetValue(id, out var expression))
            throw new InvalidOperationException($"No scalar expression or input for SPIR-V ID {id}.");
        var (opcode, args) = expression;
        uint U(int index) => Evaluate(args[index], cache);
        float F(int index) => Float(U(index));
        uint result = opcode switch
        {
            SpirvOp.Constant => args[0],
            SpirvOp.ConstantTrue => 1,
            SpirvOp.ConstantFalse => 0,
            SpirvOp.Bitcast => U(0),
            SpirvOp.Select => U(0) != 0 ? U(1) : U(2),
            SpirvOp.IAdd => unchecked(U(0) + U(1)),
            SpirvOp.BitwiseAnd => U(0) & U(1),
            SpirvOp.ShiftLeftLogical => U(0) << checked((int)U(1)),
            SpirvOp.ShiftRightLogical => U(0) >> checked((int)U(1)),
            SpirvOp.FAdd => Bits(F(0) + F(1)),
            SpirvOp.FSub => Bits(F(0) - F(1)),
            SpirvOp.FMul => Bits(F(0) * F(1)),
            SpirvOp.FNegate => U(0) ^ 0x8000_0000,
            SpirvOp.IsNan => Boolean(float.IsNaN(F(0))),
            SpirvOp.IEqual => Boolean(U(0) == U(1)),
            SpirvOp.INotEqual => Boolean(U(0) != U(1)),
            SpirvOp.FOrdEqual => Boolean(F(0) == F(1)),
            SpirvOp.FOrdLessThan => Boolean(F(0) < F(1)),
            SpirvOp.FOrdGreaterThan => Boolean(F(0) > F(1)),
            SpirvOp.FOrdLessThanEqual => Boolean(F(0) <= F(1)),
            SpirvOp.FOrdGreaterThanEqual => Boolean(F(0) >= F(1)),
            SpirvOp.LogicalAnd => Boolean(U(0) != 0 && U(1) != 0),
            SpirvOp.LogicalOr => Boolean(U(0) != 0 || U(1) != 0),
            SpirvOp.LogicalNot => Boolean(U(0) == 0),
            SpirvOp.LogicalNotEqual => Boolean((U(0) != 0) != (U(1) != 0)),
            SpirvOp.ExtInst => args[1] switch
            {
                4 => Bits(MathF.Abs(F(2))),
                38 => Math.Min(U(2), U(3)),
                40 => Bits(MathF.Max(F(2), F(3))),
                _ => throw new InvalidOperationException($"Unsupported GLSL instruction {args[1]}."),
            },
            _ => throw new InvalidOperationException($"Unsupported scalar instruction {opcode}."),
        };
        cache.Add(id, result);
        return result;
    }

    private static bool IsExpression(SpirvOp opcode) => opcode is
        SpirvOp.Constant or SpirvOp.ConstantTrue or SpirvOp.ConstantFalse or SpirvOp.Bitcast or
        SpirvOp.Select or SpirvOp.IAdd or SpirvOp.BitwiseAnd or SpirvOp.ShiftLeftLogical or
        SpirvOp.ShiftRightLogical or SpirvOp.FAdd or SpirvOp.FSub or SpirvOp.FMul or SpirvOp.FNegate or
        SpirvOp.IsNan or SpirvOp.IEqual or SpirvOp.INotEqual or SpirvOp.FOrdEqual or
        SpirvOp.FOrdLessThan or SpirvOp.FOrdGreaterThan or SpirvOp.FOrdLessThanEqual or
        SpirvOp.FOrdGreaterThanEqual or SpirvOp.LogicalAnd or SpirvOp.LogicalOr or
        SpirvOp.LogicalNot or SpirvOp.LogicalNotEqual or SpirvOp.ExtInst;

    private static uint Bits(float value) => BitConverter.SingleToUInt32Bits(value);
    private static float Float(uint value) => BitConverter.UInt32BitsToSingle(value);
    private static uint Boolean(bool value) => value ? 1u : 0u;
}
