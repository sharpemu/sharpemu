// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Evaluation;

public enum Gen5EvalOp : byte
{
    Nop,
    End,
    Branch,
    BranchScc0,
    BranchScc1,
    BranchVccZero,
    BranchVccNonZero,
    BranchExecZero,
    BranchExecNonZero,

    ScalarMove,
    ScalarMove64,
    ScalarMoveK,
    ScalarNot64,
    ScalarAddI,
    ScalarSubI,
    ScalarSubU,
    ScalarMul,
    ScalarMulHiU,
    ScalarSelect64,
    ScalarAnd64,
    ScalarAndNot64,
    ScalarShiftLeft,
    ScalarShiftLeftAdd,
    ScalarBitfieldMask,
    ScalarCompare,
    ScalarCompareK,
    ScalarAndSaveExec,
    ScalarBufferLoad,
    ScalarLoad,

    VectorMove,
    VectorSelect,
    VectorAdd,
    VectorSub,
    VectorSubReverse,
    VectorAddCarry,
    VectorMulLo,
    VectorMulHiU,
    VectorMulU24,
    VectorMadU24,
    VectorMadU64U32,
    VectorAnd,
    VectorShiftLeftReverse,
    VectorShiftRightReverse,
    VectorShiftLeftAdd,
    VectorAndOr,
    VectorAdd3,
    VectorBitfieldInsert,
    VectorMinU,
    VectorBitCount,
    VectorConvertF32U32,
    VectorReadFirstLane,
    VectorCompare,

    VectorConvertU32F32,
    VectorMulF32,
    VectorReciprocalF32,

    BufferLoad,
    BufferStore,
}

public enum Gen5EvalCompare : byte
{
    False,
    Less,
    Equal,
    LessEqual,
    Greater,
    NotEqual,
    GreaterEqual,
    True,
}

public enum Gen5EvalType : byte
{
    U32,
    I32,
}

public enum Gen5EvalOperandKind : byte
{
    None,
    Scalar,
    Vector,
    Constant,
    Scc,
    VccZero,
    ExecZero,
}

public readonly record struct Gen5EvalOperand(Gen5EvalOperandKind Kind, uint Register, uint Bits, uint FloatBits, ulong Bits64)
{
    public static Gen5EvalOperand None => default;
}

public sealed class Gen5EvalInstruction
{
    public Gen5EvalOp Op { get; init; }
    public uint Pc { get; init; }
    public Gen5EvalCompare Compare { get; init; }
    public Gen5EvalType Type { get; init; }
    public bool WritesExec { get; init; }
    public uint Destination { get; init; }
    public uint MaskDestination { get; init; } = Gen5WaveState.Vcc;
    public bool HasCarryInOperand { get; init; }
    public Gen5EvalOperand Source0 { get; init; }
    public Gen5EvalOperand Source1 { get; init; }
    public Gen5EvalOperand Source2 { get; init; }
    public int Target { get; init; } = -1;
    public int Parameter { get; init; }
    public uint VectorAddress { get; init; }
    public uint VectorData { get; init; }
    public uint ScalarResource { get; init; }
    public int OffsetBytes { get; init; }
    public bool IndexEnabled { get; init; }
    public bool OffsetEnabled { get; init; }
    public uint DwordCount { get; init; }
    public bool Formatted { get; init; }
    public uint? DynamicOffsetRegister { get; init; }
}
