// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Generic;

namespace SharpEmu.ShaderCompiler.Resources;

// Accepts a graph value only when every node down to its leaves is one the host can
// evaluate per draw: user data, shader base, reads, invariant phis, uniform operations.
public sealed class RuntimeValueValidator
{
    private readonly ScalarValueGraph _graph;
    private readonly uint _userDataBase;
    private readonly uint _userDataCount;
    private readonly int _tableReadCount;
    private readonly ScalarValue? _activeMask;
    private readonly HashSet<ScalarValue> _visiting = [];

    // Set when a failure traces back to a phi that isn't loop-invariant (e.g. a
    // hash-table/linear-probe bindless lookup): the value genuinely has no single
    // compile-time source, as opposed to using an operation or memory shape we just
    // don't recognize. Callers use this to decide whether degrading to a null
    // descriptor is reasonable instead of failing outright (KytyPS5 does the same
    // for image/sampler handles specifically).
    public bool ControlDependent { get; private set; }

    public RuntimeValueValidator(ScalarValueGraph graph, uint userDataBase, uint userDataCount, int tableReadCount, ScalarValue? activeMask = null)
    {
        _graph = graph;
        _userDataBase = userDataBase;
        _userDataCount = userDataCount;
        _tableReadCount = tableReadCount;
        _activeMask = activeMask;
    }

    public static bool IsUniformOperation(ScalarOperation operation) => operation switch
    {
        ScalarOperation.ConvertU32F32 or ScalarOperation.ConvertF32U32 or
        ScalarOperation.Construct64 or ScalarOperation.Extract64 or
        ScalarOperation.BitFieldInsert or ScalarOperation.BitFieldUExtract or ScalarOperation.BitFieldSExtract or
        ScalarOperation.IAdd32 or ScalarOperation.IAdd64 or ScalarOperation.AddCarry32 or
        ScalarOperation.ISub32 or ScalarOperation.ISub64 or ScalarOperation.IMul32 or ScalarOperation.IMul64 or
        ScalarOperation.UMin32 or ScalarOperation.SMin32 or ScalarOperation.SMax32 or
        ScalarOperation.ShiftLeft32 or ScalarOperation.ShiftLeft64 or
        ScalarOperation.ShiftRightLogical32 or ScalarOperation.ShiftRightLogical64 or
        ScalarOperation.ShiftRightArithmetic32 or ScalarOperation.ShiftRightArithmetic64 or
        ScalarOperation.And32 or ScalarOperation.And64 or ScalarOperation.Or32 or ScalarOperation.Xor32 or ScalarOperation.Not32 or
        ScalarOperation.ULessThan32 or ScalarOperation.IEqual32 or ScalarOperation.UGreaterThan32 or ScalarOperation.INotEqual32 or
        ScalarOperation.LogicalOr or ScalarOperation.LogicalAnd or ScalarOperation.LogicalXor or ScalarOperation.LogicalNot or
        ScalarOperation.FLessThanEqual or ScalarOperation.FGreaterThanEqual or ScalarOperation.FIsNan or
        ScalarOperation.FMul or ScalarOperation.FTrunc => true,
        _ => false,
    };

    public bool Validate(ScalarValue value)
    {
        if (value.IsConstant)
        {
            return true;
        }

        if (!_visiting.Add(value))
        {
            return false;
        }

        try
        {
            var result = ValidateNode(value);
            return result;
        }
        finally
        {
            _visiting.Remove(value);
        }
    }

    private bool ValidateNode(ScalarValue value)
    {
        // Under a first-lane read, a select on that lane's active mask is its active arm.
        if (_activeMask is not null && value.Kind == ScalarValueKind.Select && ReferenceEquals(value.Operands[0], _activeMask))
        {
            return Validate(value.Operands[1]);
        }

        switch (value.Kind)
        {
            case ScalarValueKind.Undefined:
                return false;
            case ScalarValueKind.UserData:
                return value.UserDataRegister >= _userDataBase && value.UserDataRegister - _userDataBase < _userDataCount;
            case ScalarValueKind.ShaderBase:
                return true;
            case ScalarValueKind.Phi:
            {
                var invariant = _graph.ResolveInvariantPhi(value);
                if (invariant is null)
                {
                    ControlDependent = true;
                    return false;
                }

                return Validate(invariant);
            }
            case ScalarValueKind.FirstLane:
            {
                if (value.Operands.Length != 2)
                {
                    return false;
                }

                var nested = new RuntimeValueValidator(_graph, _userDataBase, _userDataCount, _tableReadCount, value.Operands[1]);
                var ok = nested.Validate(value.Operands[0]);
                if (nested.ControlDependent)
                {
                    ControlDependent = true;
                }

                return ok;
            }
            case ScalarValueKind.ResourceTableWord:
                return value.Payload < (ulong)_tableReadCount;
            case ScalarValueKind.ScalarAddressWord:
                if (!IsRawRead(value) || value.Operands[0].Kind != ScalarValueKind.AddressHandle)
                {
                    return false;
                }

                break;
            case ScalarValueKind.ScalarBufferWord:
                if (!IsRawRead(value) || value.Operands[0].Kind != ScalarValueKind.BufferHandle)
                {
                    return false;
                }

                break;
            case ScalarValueKind.BufferHandle:
            case ScalarValueKind.SamplerHandle:
                if (value.Operands.Length != 4)
                {
                    return false;
                }

                break;
            case ScalarValueKind.ImageHandle:
                if (value.Operands.Length != 8)
                {
                    return false;
                }

                break;
            case ScalarValueKind.AddressHandle:
                if (value.Operands.Length != 2)
                {
                    return false;
                }

                break;
            case ScalarValueKind.Select:
                break;
            case ScalarValueKind.Operation:
                if (value.Operation == ScalarOperation.Extract64 &&
                    (!value.Operands[1].IsConstant || value.Operands[1].ConstantU32 >= 2))
                {
                    return false;
                }

                if (!IsUniformOperation(value.Operation))
                {
                    return false;
                }

                break;
            default:
                return false;
        }

        foreach (var operand in value.Operands)
        {
            if (!Validate(operand))
            {
                return false;
            }
        }

        return true;
    }

    // A raw read is a scalar load whose memory record has the matching scalar kind.
    public bool IsRawRead(ScalarValue value)
    {
        if (value.MemoryIndex >= _graph.Memory.Count)
        {
            return false;
        }

        var kind = _graph.Memory[value.MemoryIndex].Kind;
        return (value.Kind == ScalarValueKind.ScalarAddressWord && kind == MemoryResourceKind.ScalarAddress) ||
            (value.Kind == ScalarValueKind.ScalarBufferWord && kind is MemoryResourceKind.ScalarBuffer or MemoryResourceKind.Buffer);
    }
}
