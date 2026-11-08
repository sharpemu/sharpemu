// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace SharpEmu.ShaderCompiler.Evaluation;

// Vector registers are register-major: the lanes of one register are contiguous, so one ALU
// instruction runs as a few 256-bit operations. Scratch rows after the register file hold
// broadcast and modified sources.
internal sealed partial class Gen5WaveInterpreter
{
    private const int MaxLanes = 64;
    private const int MaxVectorRegisters = 256;
    private const int LanesPerVector = 8;
    private const int ScratchRows = 8;
    private const int SourceRow0 = MaxVectorRegisters * MaxLanes;
    private const int ResultRow = SourceRow0 + 3 * MaxLanes;
    private const int LoadRow = SourceRow0 + 4 * MaxLanes;

    private readonly uint[] _scalars = new uint[128];
    private readonly uint[] _vectors = new uint[(MaxVectorRegisters + ScratchRows) * MaxLanes];
    private readonly ulong[] _vectorDefined = new ulong[MaxVectorRegisters];
    private Gen5EvaluableProgram _program = null!;
    private Gen5DispatchRequest _request = null!;
    private Gen5EvaluationReads _reads = null!;
    private int _width;
    private ulong _laneMask;
    private int _vectorRegisters;
    private readonly ulong[] _scalarDefined = new ulong[2];
    private ulong _validLanes;
    private bool _scc;
    private bool _sccDefined;
    private int _wave;
    private Gen5EvaluationWrites _writes = null!;
    private Gen5FloatVariant _variant;
    private Gen5EvaluationRefusal _refusal;

    public long LaneOperations { get; private set; }

    public bool SawInexactApproximation { get; private set; }

    public void Reset(Gen5EvaluableProgram program, Gen5DispatchRequest request, Gen5EvaluationReads reads)
    {
        _program = program;
        _request = request;
        _reads = reads;
        _width = (int)request.WaveSize;
        _laneMask = _width == 64 ? ulong.MaxValue : uint.MaxValue;
        _vectorRegisters = (int)program.VectorRegisters;
        LaneOperations = 0;
        SawInexactApproximation = false;
    }

    public Gen5EvaluationRefusal Run(Gen5EvaluationWrites writes, Gen5FloatVariant variant)
    {
        _writes = writes;
        _variant = variant;
        _refusal = Gen5EvaluationRefusal.None;
        _wave = 0;
        var request = _request;
        var groupThreads = (int)(request.ThreadsX * request.ThreadsY * request.ThreadsZ);
        var wavesPerGroup = (groupThreads + _width - 1) / _width;
        for (var groupZ = 0u; groupZ < request.GroupsZ; groupZ++)
        {
            for (var groupY = 0u; groupY < request.GroupsY; groupY++)
            {
                for (var groupX = 0u; groupX < request.GroupsX; groupX++)
                {
                    for (var wave = 0; wave < wavesPerGroup; wave++, _wave++)
                    {
                        if (!StartWave(groupX, groupY, groupZ, wave, groupThreads))
                        {
                            continue;
                        }

                        if (!RunWave())
                        {
                            return _refusal;
                        }
                    }
                }
            }
        }

        return Gen5EvaluationRefusal.None;
    }

    private bool StartWave(uint groupX, uint groupY, uint groupZ, int wave, int groupThreads)
    {
        var request = _request;
        var width = _width;
        var first = wave * width;
        Array.Clear(_vectorDefined, 0, _vectorRegisters);
        var active = request.ThreadsY == 1 && request.ThreadsZ == 1
            ? StartLinearWave(groupX, groupY, groupZ, first, groupThreads)
            : StartGridWave(groupX, groupY, groupZ, first, groupThreads);
        if (active == 0)
        {
            return false;
        }

        _validLanes = active;
        _vectorDefined[0] = _vectorDefined[1] = _vectorDefined[2] = _laneMask;
        _scalarDefined[0] = _scalarDefined[1] = 0;
        _sccDefined = false;
        var userData = request.UserData;
        for (var register = 0; register < Math.Min(userData.Length, 106); register++)
        {
            SetScalar((uint)register, userData[register]);
        }

        var system = request.SystemRegisters;
        if (system.WorkGroupXRegister is { } groupXRegister) SetScalar(groupXRegister, groupX);
        if (system.WorkGroupYRegister is { } groupYRegister) SetScalar(groupYRegister, groupY);
        if (system.WorkGroupZRegister is { } groupZRegister) SetScalar(groupZRegister, groupZ);
        if (system.ThreadGroupSizeRegister is { } sizeRegister) SetScalar(sizeRegister, (uint)groupThreads);
        SetScalar(Gen5WaveState.Exec, (uint)active);
        if (_width == 64)
        {
            SetScalar(Gen5WaveState.Exec + 1, (uint)(active >> 32));
        }

        return true;
    }

    private ulong StartLinearWave(uint groupX, uint groupY, uint groupZ, int first, int groupThreads)
    {
        var request = _request;
        var width = _width;
        ref var file = ref MemoryMarshal.GetArrayDataReference(_vectors);
        var lanes = Vector256.Create(0u, 1u, 2u, 3u, 4u, 5u, 6u, 7u) + Vector256.Create((uint)first);
        for (var lane = 0; lane < width; lane += LanesPerVector)
        {
            lanes.StoreUnsafe(ref file, (nuint)lane);
            Vector256<uint>.Zero.StoreUnsafe(ref file, (nuint)(MaxLanes + lane));
            Vector256<uint>.Zero.StoreUnsafe(ref file, (nuint)(2 * MaxLanes + lane));
            lanes += Vector256.Create((uint)LanesPerVector);
        }

        var inside = Math.Min(width, groupThreads - first);
        for (var lane = inside; lane < width; lane++)
        {
            var local = (uint)(first + lane);
            _vectors[lane] = local % request.ThreadsX;
            _vectors[2 * MaxLanes + lane] = local / request.ThreadsX;
        }

        if (request.LimitsThreads)
        {
            if (groupY >= request.ThreadLimitY || groupZ >= request.ThreadLimitZ)
            {
                return 0;
            }

            var start = (ulong)groupX * request.ThreadsX + (ulong)first;
            inside = start >= request.ThreadLimitX ? 0 : (int)Math.Min((ulong)inside, request.ThreadLimitX - start);
        }

        return inside >= 64 ? ulong.MaxValue : (1ul << inside) - 1;
    }

    private ulong StartGridWave(uint groupX, uint groupY, uint groupZ, int first, int groupThreads)
    {
        var request = _request;
        var active = 0ul;
        for (var lane = 0; lane < _width; lane++)
        {
            var local = first + lane;
            var x = (uint)local % request.ThreadsX;
            var y = (uint)local / request.ThreadsX % request.ThreadsY;
            var z = (uint)local / (request.ThreadsX * request.ThreadsY);
            _vectors[lane] = x;
            _vectors[MaxLanes + lane] = y;
            _vectors[2 * MaxLanes + lane] = z;
            if (local >= groupThreads)
            {
                continue;
            }

            if (request.LimitsThreads &&
                ((ulong)groupX * request.ThreadsX + x >= request.ThreadLimitX ||
                 (ulong)groupY * request.ThreadsY + y >= request.ThreadLimitY ||
                 (ulong)groupZ * request.ThreadsZ + z >= request.ThreadLimitZ))
            {
                continue;
            }

            active |= 1ul << lane;
        }

        return active;
    }

    private bool RunWave()
    {
        var instructions = _program.Instructions;
        var budget = _request.LaneOperationBudget;
        var index = 0;
        while (index < instructions.Length)
        {
            var instruction = instructions[index];
            LaneOperations += _width;
            if (LaneOperations > budget)
            {
                return Refuse(Gen5EvaluationRefusal.Budget);
            }

            var next = index + 1;
            switch (instruction.Op)
            {
                case Gen5EvalOp.Nop:
                    break;
                case Gen5EvalOp.End:
                    return true;
                case Gen5EvalOp.Branch:
                    next = instruction.Target;
                    break;
                case Gen5EvalOp.BranchScc0:
                case Gen5EvalOp.BranchScc1:
                    if (!_sccDefined)
                    {
                        return Refuse(Gen5EvaluationRefusal.UndefinedRegister);
                    }

                    if (_scc == (instruction.Op == Gen5EvalOp.BranchScc1))
                    {
                        next = instruction.Target;
                    }

                    break;
                case Gen5EvalOp.BranchVccZero:
                case Gen5EvalOp.BranchVccNonZero:
                case Gen5EvalOp.BranchExecZero:
                case Gen5EvalOp.BranchExecNonZero:
                {
                    var register = instruction.Op is Gen5EvalOp.BranchVccZero or Gen5EvalOp.BranchVccNonZero ? Gen5WaveState.Vcc : Gen5WaveState.Exec;
                    if (!TryReadWaveMask(register, out var mask))
                    {
                        return false;
                    }

                    if ((mask == 0) == (instruction.Op is Gen5EvalOp.BranchVccZero or Gen5EvalOp.BranchExecZero))
                    {
                        next = instruction.Target;
                    }

                    break;
                }
                case >= Gen5EvalOp.ScalarMove and <= Gen5EvalOp.ScalarAndSaveExec:
                    if (!ExecuteScalar(instruction))
                    {
                        return false;
                    }

                    break;
                case Gen5EvalOp.ScalarBufferLoad:
                case Gen5EvalOp.ScalarLoad:
                    if (!ExecuteScalarMemory(instruction))
                    {
                        return false;
                    }

                    break;
                case Gen5EvalOp.VectorReadFirstLane:
                    if (!ExecuteLaneRead(instruction))
                    {
                        return false;
                    }

                    break;
                case Gen5EvalOp.VectorCompare:
                    if (!ExecuteCompare(instruction))
                    {
                        return false;
                    }

                    break;
                case Gen5EvalOp.BufferLoad:
                case Gen5EvalOp.BufferStore:
                    if (!ExecuteBuffer(instruction))
                    {
                        return false;
                    }

                    break;
                case >= Gen5EvalOp.VectorConvertU32F32 and <= Gen5EvalOp.VectorReciprocalF32:
                    if (!ExecuteFloat(instruction))
                    {
                        return false;
                    }

                    break;
                default:
                    if (!ExecuteVector(instruction))
                    {
                        return false;
                    }

                    break;
            }

            index = next;
        }

        return true;
    }

    private bool Refuse(Gen5EvaluationRefusal refusal)
    {
        _refusal = refusal;
        return false;
    }

    private bool IsScalarDefined(uint register) => ((_scalarDefined[register >> 6] >> (int)(register & 63)) & 1) != 0;

    private void SetScalar(uint register, uint value)
    {
        if (register == Gen5WaveState.Null || register > 127)
        {
            return;
        }

        _scalars[register] = value;
        _scalarDefined[register >> 6] |= 1ul << (int)(register & 63);
    }

    private void ForgetScalar(uint register)
    {
        if (register <= 127)
        {
            _scalarDefined[register >> 6] &= ~(1ul << (int)(register & 63));
        }
    }

    private bool TryReadScalarRegister(uint register, out uint value)
    {
        value = _scalars[register];
        return IsScalarDefined(register) || Refuse(Gen5EvaluationRefusal.UndefinedRegister);
    }

    private bool TryReadWaveMask(uint register, out ulong mask)
    {
        mask = 0;
        if (!TryReadScalarRegister(register, out var low))
        {
            return false;
        }

        if (_width == 32)
        {
            mask = low;
            return true;
        }

        if ((register & 1) != 0)
        {
            return Refuse(Gen5EvaluationRefusal.MaskRegister);
        }

        if (!TryReadScalarRegister(register + 1, out var high))
        {
            return false;
        }

        mask = low | ((ulong)high << 32);
        return true;
    }

    private bool TryWriteWaveMask(uint register, ulong mask)
    {
        if (_width == 32)
        {
            SetScalar(register, (uint)mask);
            if (register is not Gen5WaveState.Exec)
            {
                ForgetScalar(register + 1);
            }

            return true;
        }

        if ((register & 1) != 0 || register > Gen5WaveState.Exec)
        {
            return Refuse(Gen5EvaluationRefusal.MaskRegister);
        }

        SetScalar(register, (uint)mask);
        SetScalar(register + 1, (uint)(mask >> 32));
        return true;
    }

    private bool TryExecMask(out ulong exec)
    {
        if (!TryReadWaveMask(Gen5WaveState.Exec, out exec))
        {
            return false;
        }

        exec &= _laneMask;
        return (exec & ~_validLanes) == 0 || Refuse(Gen5EvaluationRefusal.MaskRegister);
    }

    private bool TryReadMaskOperand(in Gen5EvalOperand operand, out ulong mask)
    {
        mask = 0;
        switch (operand.Kind)
        {
            case Gen5EvalOperandKind.Scalar:
                return TryReadWaveMask(operand.Register, out mask);
            case Gen5EvalOperandKind.Constant:
                mask = operand.Bits64 & _laneMask;
                return true;
            default:
                return Refuse(Gen5EvaluationRefusal.MaskRegister);
        }
    }

    private bool TryReadScalarOperand(in Gen5EvalOperand operand, out uint value)
    {
        value = 0;
        switch (operand.Kind)
        {
            case Gen5EvalOperandKind.Scalar:
                return TryReadScalarRegister(operand.Register, out value);
            case Gen5EvalOperandKind.Constant:
                value = operand.Bits;
                return true;
            case Gen5EvalOperandKind.Scc:
                value = _scc ? 1u : 0u;
                return _sccDefined || Refuse(Gen5EvaluationRefusal.UndefinedRegister);
            case Gen5EvalOperandKind.VccZero:
            case Gen5EvalOperandKind.ExecZero:
                if (!TryReadWaveMask(operand.Kind == Gen5EvalOperandKind.VccZero ? Gen5WaveState.Vcc : Gen5WaveState.Exec, out var mask))
                {
                    return false;
                }

                value = mask == 0 ? 1u : 0u;
                return true;
            default:
                return Refuse(Gen5EvaluationRefusal.UndefinedRegister);
        }
    }

    // The row holding the operand's lanes: the register itself, or a scratch row with the value broadcast.
    private bool TryReadSource(in Gen5EvalOperand operand, int slot, ulong active, bool floatConstant, out int row)
    {
        if (operand.Kind == Gen5EvalOperandKind.Vector)
        {
            var register = (int)operand.Register;
            row = register * MaxLanes;
            return (active & ~_vectorDefined[register]) == 0 || Refuse(Gen5EvaluationRefusal.UndefinedRegister);
        }

        row = SourceRow0 + slot * MaxLanes;
        uint value;
        if (operand.Kind == Gen5EvalOperandKind.Constant)
        {
            value = floatConstant ? operand.FloatBits : operand.Bits;
        }
        else if (!TryReadScalarOperand(operand, out value))
        {
            return false;
        }

        Fill(row, value);
        return true;
    }

    private void Fill(int row, uint value)
    {
        ref var file = ref MemoryMarshal.GetArrayDataReference(_vectors);
        var broadcast = Vector256.Create(value);
        for (var lane = 0; lane < _width; lane += LanesPerVector)
        {
            broadcast.StoreUnsafe(ref file, (nuint)(row + lane));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<uint> LaneMask(ulong mask, int lane)
    {
        var bits = Vector256.Create(1u, 2u, 4u, 8u, 16u, 32u, 64u, 128u);
        return Vector256.Equals(Vector256.Create((uint)(mask >> lane) & 0xFF) & bits, bits);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store(ref uint file, int at, Vector256<uint> value, ulong mask, int lane, bool full)
    {
        if (!full)
        {
            value = Vector256.ConditionalSelect(LaneMask(mask, lane), value, Vector256.LoadUnsafe(ref file, (nuint)at));
        }

        value.StoreUnsafe(ref file, (nuint)at);
    }

    private void CopyLanes(int source, int destination, ulong mask)
    {
        ref var file = ref MemoryMarshal.GetArrayDataReference(_vectors);
        var full = mask == _laneMask;
        for (var lane = 0; lane < _width; lane += LanesPerVector)
        {
            Store(ref file, destination + lane, Vector256.LoadUnsafe(ref file, (nuint)(source + lane)), mask, lane, full);
        }
    }

    private void NoteWritten(uint register, ulong mask) => _vectorDefined[register] |= mask;

    private void WriteVector(uint register, int source, ulong mask)
    {
        CopyLanes(source, (int)register * MaxLanes, mask);
        NoteWritten(register, mask);
    }
}
