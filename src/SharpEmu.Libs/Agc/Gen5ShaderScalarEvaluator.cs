// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using System.Numerics;

namespace SharpEmu.Libs.Agc;

internal static class Gen5ShaderScalarEvaluator
{
    private const int ScalarRegisterCount = 256;
    private const int ImageDescriptorDwords = 8;
    private const int SamplerDescriptorDwords = 4;
    private const int MaxGlobalMemoryBindingBytes = 16 * 1024 * 1024;
    private const ulong RdnaWaveMask = 0xFFFF_FFFFUL;

    private readonly record struct BufferDescriptor(
        ulong BaseAddress,
        uint Stride,
        uint NumRecords,
        ulong SizeBytes,
        uint NumberFormat,
        uint DataFormat);

    public static bool TryResolveImageBindings(
        CpuContext ctx,
        Gen5ShaderState state,
        out IReadOnlyList<Gen5ImageBinding> bindings,
        out string error)
    {
        if (TryEvaluate(ctx, state, out var evaluation, out error))
        {
            bindings = evaluation.ImageBindings;
            return true;
        }

        bindings = [];
        return false;
    }

    public static bool TryEvaluate(
        CpuContext ctx,
        Gen5ShaderState state,
        out Gen5ShaderEvaluation evaluation,
        out string error,
        bool resolveVertexInputs = false)
    {
        evaluation = default!;
        error = string.Empty;
        var scalarRegisters = new uint[ScalarRegisterCount];
        for (var index = 0;
             index < state.UserData.Count &&
             state.UserDataScalarRegisterBase + (uint)index < scalarRegisters.Length;
             index++)
        {
            scalarRegisters[state.UserDataScalarRegisterBase + (uint)index] =
                state.UserData[index];
        }

        if (state.ComputeSystemRegisters is { } computeSystemRegisters)
        {
            computeSystemRegisters.ClearStaticValues(scalarRegisters);
        }

        var execMask = RdnaWaveMask;
        WriteScalarPair(scalarRegisters, 106, 0, ref execMask);
        WriteScalarPair(scalarRegisters, 126, execMask, ref execMask);
        var initialScalarRegisters = (uint[])scalarRegisters.Clone();

        var resolved = new List<Gen5ImageBinding>();
        var globalMemoryBindings = new List<Gen5GlobalMemoryBinding>();
        var globalMemoryByAddress = new Dictionary<(uint ScalarAddress, ulong BaseAddress), Gen5GlobalMemoryBinding>();
        var vertexInputBindings = new List<Gen5VertexInputBinding>();
        var runtimeScalarRegisters = CollectRuntimeScalarRegisters(state.Program);
        var scalarRegisterSnapshots = new Dictionary<uint, IReadOnlyList<uint>>();
        var scalarConditionCode = false;
        uint? skipUntilPc = null;

        foreach (var instruction in state.Program.Instructions)
        {
            if (skipUntilPc.HasValue)
            {
                if (instruction.Pc < skipUntilPc.Value)
                {
                    continue;
                }

                skipUntilPc = null;
            }

            scalarRegisterSnapshots[instruction.Pc] = (uint[])scalarRegisters.Clone();

            if (instruction.Opcode == "SEndpgm")
            {
                break;
            }

            if (instruction.Opcode == "SBranch" &&
                TryGetSoppBranchTargetPc(instruction, out var targetPc) &&
                targetPc > instruction.Pc)
            {
                skipUntilPc = targetPc;
                continue;
            }

            if (instruction.Encoding == Gen5ShaderEncoding.Sopc)
            {
                if (!TryExecuteScalarCompare(
                        instruction,
                        scalarRegisters,
                        out scalarConditionCode,
                        out error))
                {
                    return false;
                }

                continue;
            }

            if (instruction.Encoding == Gen5ShaderEncoding.Sopk &&
                instruction.Opcode.StartsWith("SCmpk", StringComparison.Ordinal))
            {
                if (!TryExecuteScalarCompareK(
                        instruction,
                        scalarRegisters,
                        out scalarConditionCode,
                        out error))
                {
                    return false;
                }

                continue;
            }

            if (instruction.Encoding is
                Gen5ShaderEncoding.Sop1 or
                Gen5ShaderEncoding.Sop2 or
                Gen5ShaderEncoding.Sopk)
            {
                if (instruction.Opcode is "SSetpcB64" or "SSwappcB64")
                {
                    break;
                }

                if (!TryExecuteScalarAlu(
                        instruction,
                        state.Program.Address,
                        scalarRegisters,
                        ref execMask,
                        ref scalarConditionCode,
                        out error))
                {
                    return false;
                }

                continue;
            }

            if (instruction.Control is Gen5ScalarMemoryControl scalarMemory)
            {
                foreach (var destination in instruction.Destinations)
                {
                    if (destination.Kind == Gen5OperandKind.ScalarRegister && destination.Value < ScalarRegisterCount)
                    {
                        runtimeScalarRegisters.Add(destination.Value);
                    }
                }

                if (!TryExecuteScalarLoad(ctx, state, instruction, scalarMemory, scalarRegisters, globalMemoryBindings, globalMemoryByAddress, runtimeScalarRegisters, out error))
                {
                    return false;
                }
                continue;
            }

            if (instruction.Control is Gen5GlobalMemoryControl globalMemory)
            {
                if (globalMemory.ScalarAddress >= ScalarRegisterCount - 1)
                {
                    error =
                        $"global-address-register-range pc=0x{instruction.Pc:X} " +
                        $"s{globalMemory.ScalarAddress}";
                    return false;
                }

                var baseAddress =
                    scalarRegisters[globalMemory.ScalarAddress] |
                    ((ulong)scalarRegisters[globalMemory.ScalarAddress + 1] << 32);
                if (baseAddress == 0)
                {
                    error = $"global-address-null pc=0x{instruction.Pc:X}";
                    return false;
                }

                var key = (globalMemory.ScalarAddress, baseAddress);
                if (globalMemoryByAddress.TryGetValue(key, out var existingBinding))
                {
                    if (existingBinding.InstructionPcs is List<uint> instructionPcs)
                    {
                        instructionPcs.Add(instruction.Pc);
                    }
                }
                else
                {
                    byte[] data;
                    if (!TryReadGlobalMemory(ctx, baseAddress, out data))
                    {
                        error =
                            $"global-memory-read-failed pc=0x{instruction.Pc:X} " +
                            $"address=0x{baseAddress:X16}";
                        return false;
                    }

                    var binding = new Gen5GlobalMemoryBinding(
                        globalMemory.ScalarAddress,
                        baseAddress,
                        new List<uint> { instruction.Pc },
                        data);
                    globalMemoryByAddress.Add(key, binding);
                    globalMemoryBindings.Add(binding);
                }

                continue;
            }

            if (instruction.Control is Gen5BufferMemoryControl bufferMemory)
            {
                if (bufferMemory.ScalarResource >= ScalarRegisterCount - 3)
                {
                    error =
                        $"buffer-resource-register-range pc=0x{instruction.Pc:X} " +
                        $"s{bufferMemory.ScalarResource}";
                    return false;
                }

                if (!TryDecodeBufferDescriptor(
                        scalarRegisters,
                        bufferMemory.ScalarResource,
                        strictType: true,
                        out var bufferDescriptor))
                {
                    error =
                        $"buffer-descriptor-invalid pc=0x{instruction.Pc:X} " +
                        $"s{bufferMemory.ScalarResource}";
                    return false;
                }

                if (bufferDescriptor.BaseAddress == 0)
                {
                    error = $"buffer-address-null pc=0x{instruction.Pc:X}";
                    return false;
                }

                if (resolveVertexInputs &&
                    IsVertexFetchCandidate(instruction, bufferMemory, bufferDescriptor))
                {
                    if (!TryReadGlobalMemory(
                            ctx,
                            bufferDescriptor.BaseAddress,
                            bufferDescriptor.SizeBytes,
                            out var vertexData))
                    {
                        error =
                            $"vertex-buffer-read-failed pc=0x{instruction.Pc:X} " +
                            $"address=0x{bufferDescriptor.BaseAddress:X16} " +
                            $"bytes={bufferDescriptor.SizeBytes} " +
                            $"stride={bufferDescriptor.Stride} records={bufferDescriptor.NumRecords}";
                        return false;
                    }

                    if (!TryCreateVertexInputBinding(
                            instruction,
                            bufferMemory,
                            bufferDescriptor,
                            vertexData,
                            (uint)vertexInputBindings.Count,
                            scalarRegisters,
                            out var vertexInputBinding))
                    {
                        error =
                            $"vertex-input-binding-failed pc=0x{instruction.Pc:X} " +
                            $"s{bufferMemory.ScalarResource}";
                        return false;
                    }

                    vertexInputBindings.Add(vertexInputBinding);
                    continue;
                }

                var key = (bufferMemory.ScalarResource, bufferDescriptor.BaseAddress);
                if (globalMemoryByAddress.TryGetValue(key, out var existingBinding))
                {
                    if (existingBinding.InstructionPcs is List<uint> instructionPcs)
                    {
                        instructionPcs.Add(instruction.Pc);
                    }
                }
                else
                {
                    if (!TryReadGlobalMemory(
                            ctx,
                            bufferDescriptor.BaseAddress,
                            bufferDescriptor.SizeBytes,
                            out var data))
                    {
                        var descriptorWords = string.Join(
                            ':',
                            Enumerable.Range(0, 4).Select(index =>
                                $"{scalarRegisters[bufferMemory.ScalarResource + (uint)index]:X8}"));
                        error =
                            $"buffer-memory-read-failed pc=0x{instruction.Pc:X} " +
                            $"address=0x{bufferDescriptor.BaseAddress:X16} " +
                            $"bytes={bufferDescriptor.SizeBytes} " +
                            $"stride={bufferDescriptor.Stride} records={bufferDescriptor.NumRecords} " +
                            $"s{bufferMemory.ScalarResource}=[{descriptorWords}]";
                        return false;
                    }

                    var binding = new Gen5GlobalMemoryBinding(
                        bufferMemory.ScalarResource,
                        bufferDescriptor.BaseAddress,
                        new List<uint> { instruction.Pc },
                        data);
                    globalMemoryByAddress.Add(key, binding);
                    globalMemoryBindings.Add(binding);
                }

                continue;
            }

            if (instruction.Control is not Gen5ImageControl image)
            {
                continue;
            }

            if (!TryCopyRegisters(
                    scalarRegisters,
                    image.ScalarResource,
                    ImageDescriptorDwords,
                    out var resourceDescriptor))
            {
                error = $"resource-register-range pc=0x{instruction.Pc:X} s{image.ScalarResource}";
                return false;
            }

            IReadOnlyList<uint> samplerDescriptor = [];
            if (UsesSampler(instruction.Opcode) &&
                !TryCopyRegisters(
                    scalarRegisters,
                    image.ScalarSampler,
                    SamplerDescriptorDwords,
                    out samplerDescriptor))
            {
                error = $"sampler-register-range pc=0x{instruction.Pc:X} s{image.ScalarSampler}";
                return false;
            }

            resolved.Add(new Gen5ImageBinding(
                instruction.Pc,
                instruction.Opcode,
                image,
                resourceDescriptor,
                samplerDescriptor,
                instruction.Opcode is "ImageLoadMip" or "ImageStoreMip" &&
                TryResolveVectorConstantBefore(
                    state.Program,
                    instruction.Pc,
                    image.GetAddressRegister(2),
                    out var mipLevel)
                    ? mipLevel
                    : null));
        }

        evaluation = new Gen5ShaderEvaluation(
            initialScalarRegisters,
            scalarRegisters,
            scalarRegisterSnapshots,
            resolved,
            globalMemoryBindings,
            state.ComputeSystemRegisters,
            runtimeScalarRegisters,
            vertexInputBindings);
        return true;
    }

    private static bool TryCreateVertexInputBinding(
        Gen5ShaderInstruction instruction,
        Gen5BufferMemoryControl control,
        BufferDescriptor descriptor,
        byte[] data,
        uint location,
        uint[] scalarRegisters,
        out Gen5VertexInputBinding binding)
    {
        binding = default!;
        if (!IsVertexFetchCandidate(instruction, control, descriptor) ||
            instruction.Sources.Count <= 2 ||
            !TryEvaluateScalarOperand(instruction.Sources[2], scalarRegisters, out var scalarOffset))
        {
            return false;
        }

        var bindingData = data;
        var bindingStride = descriptor.Stride;
        var bindingOffset = unchecked((uint)control.OffsetBytes + scalarOffset);
        var bindingDataFormat = descriptor.DataFormat;
        var bindingNumberFormat = descriptor.NumberFormat;
        binding = new Gen5VertexInputBinding(
            instruction.Pc,
            location,
            control.DwordCount,
            bindingDataFormat,
            bindingNumberFormat,
            descriptor.BaseAddress,
            bindingStride,
            bindingOffset,
            bindingData);
        return true;
    }

    private static bool IsVertexFetchCandidate(
        Gen5ShaderInstruction instruction,
        Gen5BufferMemoryControl control,
        BufferDescriptor descriptor) =>
        control.IndexEnabled &&
        !control.OffsetEnabled &&
        control.DwordCount is >= 1 and <= 4 &&
        descriptor.BaseAddress != 0 &&
        descriptor.Stride != 0 &&
        (instruction.Opcode.StartsWith("BufferLoadFormat", StringComparison.Ordinal) ||
         instruction.Opcode.StartsWith("TBufferLoadFormat", StringComparison.Ordinal));

    private static HashSet<uint> CollectRuntimeScalarRegisters(Gen5ShaderProgram program)
    {
        var registers = new HashSet<uint>();
        foreach (var instruction in program.Instructions)
        {
            foreach (var operand in instruction.Sources.Concat(instruction.Destinations))
            {
                if (operand.Kind == Gen5OperandKind.ScalarRegister &&
                    operand.Value < ScalarRegisterCount)
                {
                    registers.Add(operand.Value);
                }
            }

            if (instruction.Control is Gen5ScalarMemoryControl
                {
                    DynamicOffsetRegister: { } offsetRegister,
                } &&
                offsetRegister < ScalarRegisterCount)
            {
                registers.Add(offsetRegister);
            }
        }

        return registers;
    }

    private static bool TryGetSoppBranchTargetPc(
        Gen5ShaderInstruction instruction,
        out uint targetPc)
    {
        targetPc = 0;
        if (instruction.Encoding != Gen5ShaderEncoding.Sopp ||
            instruction.Words.Count == 0)
        {
            return false;
        }

        var offset = unchecked((short)(instruction.Words[0] & 0xFFFF));
        var nextPc = (long)instruction.Pc + instruction.Words.Count * sizeof(uint);
        var target = nextPc + offset * sizeof(uint);
        if (target < 0 || target > uint.MaxValue)
        {
            return false;
        }

        targetPc = (uint)target;
        return true;
    }

    private static bool TryResolveVectorConstantBefore(
        Gen5ShaderProgram program,
        uint pc,
        uint vectorRegister,
        out uint value)
    {
        for (var index = program.Instructions.Count - 1; index >= 0; index--)
        {
            var instruction = program.Instructions[index];
            if (instruction.Pc >= pc ||
                instruction.Destinations.Count != 1 ||
                instruction.Destinations[0] is not
                {
                    Kind: Gen5OperandKind.VectorRegister,
                    Value: var destination,
                } ||
                destination != vectorRegister)
            {
                continue;
            }

            if (instruction.Opcode == "VMovB32" &&
                instruction.Sources.Count == 1 &&
                TryResolveConstantOperand(instruction.Sources[0], out value))
            {
                return true;
            }

            break;
        }

        value = 0;
        return false;
    }

    private static bool TryResolveConstantOperand(Gen5Operand operand, out uint value)
    {
        if (operand.Kind == Gen5OperandKind.LiteralConstant)
        {
            value = operand.Value;
            return true;
        }

        if (operand.Kind == Gen5OperandKind.EncodedConstant)
        {
            return TryDecodeInlineConstant(operand.Value, out value);
        }

        value = 0;
        return false;
    }

    private static bool TryReadGlobalMemory(
        CpuContext ctx,
        ulong baseAddress,
        out byte[] data)
    {
        for (var size = MaxGlobalMemoryBindingBytes; size >= 4096; size >>= 1)
        {
            data = GC.AllocateUninitializedArray<byte>(size);
            if (ctx.Memory.TryRead(baseAddress, data))
            {
                return true;
            }
        }

        data = [];
        return false;
    }

    private static bool TryReadGlobalMemory(
        CpuContext ctx,
        ulong baseAddress,
        ulong sizeBytes,
        out byte[] data)
    {
        if (sizeBytes == 0)
        {
            data = [];
            return false;
        }

        var cappedSize = Math.Min(sizeBytes, MaxGlobalMemoryBindingBytes);
        if (cappedSize > int.MaxValue)
        {
            data = [];
            return false;
        }

        var candidateSize = (int)cappedSize;
        while (candidateSize >= sizeof(uint))
        {
            data = GC.AllocateUninitializedArray<byte>(candidateSize);
            if (ctx.Memory.TryRead(baseAddress, data) ||
                KernelMemoryCompatExports.TryReadTrackedLibcHeap(baseAddress, data))
            {
                return true;
            }

            if (candidateSize == sizeof(uint))
            {
                break;
            }

            candidateSize = Math.Max(candidateSize / 2, sizeof(uint));
        }

        data = [];
        return false;
    }

    private static bool TryExecuteScalarAlu(
        Gen5ShaderInstruction instruction,
        ulong programAddress,
        uint[] registers,
        ref ulong execMask,
        ref bool scalarConditionCode,
        out string error)
    {
        error = string.Empty;
        if (instruction.Destinations.Count != 1 ||
            instruction.Destinations[0] is not
            {
                Kind: Gen5OperandKind.ScalarRegister,
                Value: < ScalarRegisterCount,
            } destination)
        {
            error = $"unsupported-scalar-destination pc=0x{instruction.Pc:X} op={instruction.Opcode}";
            return false;
        }

        if (instruction.Opcode == "SMovkI32")
        {
            registers[destination.Value] = unchecked((uint)(short)instruction.Sources[0].Value);
            return true;
        }

        if (instruction.Opcode is "SAddkI32" or "SMulkI32")
        {
            var immediate = unchecked((uint)(short)instruction.Sources[0].Value);
            registers[destination.Value] = instruction.Opcode == "SAddkI32"
                ? registers[destination.Value] + immediate
                : unchecked((uint)((int)registers[destination.Value] * (int)immediate));
            return true;
        }

        if (instruction.Opcode == "SGetpcB64")
        {
            var pc = programAddress + instruction.Pc + (ulong)(instruction.Words.Count * sizeof(uint));
            WriteScalarPair(registers, destination.Value, pc, ref execMask);
            return true;
        }

        if (TryExecuteSaveExecScalarAlu(
                instruction,
                registers,
                ref execMask,
                ref scalarConditionCode,
                out error))
        {
            return true;
        }

        if (instruction.Opcode is "SMovB64" or "SWqmB64" or "SNotB64")
        {
            if (destination.Value >= ScalarRegisterCount - 1 ||
                !TryEvaluateScalarOperand64(
                    instruction.Sources[0],
                    registers,
                    execMask,
                    out var value))
            {
                error = $"scalar-source64 pc=0x{instruction.Pc:X} op={instruction.Opcode}";
                return false;
            }

            if (instruction.Opcode == "SNotB64")
            {
                value = ~value;
                scalarConditionCode = value != 0;
            }

            WriteScalarPair(registers, destination.Value, value, ref execMask);
            return true;
        }

        if (instruction.Opcode is "SLshlB64" or "SLshrB64")
        {
            if (instruction.Sources.Count < 2 ||
                destination.Value >= ScalarRegisterCount - 1 ||
                !TryEvaluateScalarOperand64(
                    instruction.Sources[0],
                    registers,
                    execMask,
                    out var value) ||
                !TryEvaluateScalarOperand(
                    instruction.Sources[1],
                    registers,
                    out var shift))
            {
                error = $"scalar-source64 pc=0x{instruction.Pc:X} op={instruction.Opcode}";
                return false;
            }

            value = instruction.Opcode == "SLshlB64"
                ? value << ((int)shift & 63)
                : value >> ((int)shift & 63);
            WriteScalarPair(registers, destination.Value, value, ref execMask);
            scalarConditionCode = value != 0;
            return true;
        }

        if (instruction.Opcode is "SBfeU64" or "SBfeI64")
        {
            if (instruction.Sources.Count < 2 ||
                destination.Value >= ScalarRegisterCount - 1 ||
                !TryEvaluateScalarOperand64(
                    instruction.Sources[0],
                    registers,
                    execMask,
                    out var source) ||
                !TryEvaluateScalarOperand(
                    instruction.Sources[1],
                    registers,
                    out var control))
            {
                error = $"scalar-source64 pc=0x{instruction.Pc:X} op={instruction.Opcode}";
                return false;
            }

            var offset = (int)control & 63;
            var width = Math.Min(((int)control >> 16) & 0x7F, 64 - offset);
            ulong value;
            if (width == 0)
            {
                value = 0;
            }
            else
            {
                value = source >> offset;
                if (width < 64)
                {
                    value &= ulong.MaxValue >> (64 - width);
                    if (instruction.Opcode == "SBfeI64")
                    {
                        value = unchecked((ulong)((long)(value << (64 - width)) >> (64 - width)));
                    }
                }
            }

            WriteScalarPair(registers, destination.Value, value, ref execMask);
            scalarConditionCode = value != 0;
            return true;
        }

        if (instruction.Opcode is
            "SCselectB64" or
            "SAndB64" or
            "SOrB64" or
            "SXorB64" or
            "SAndn2B64" or
            "SOrn2B64" or
            "SNandB64" or
            "SNorB64" or
            "SXnorB64")
        {
            if (instruction.Sources.Count < 2 ||
                !TryEvaluateScalarOperand64(
                    instruction.Sources[0],
                    registers,
                    execMask,
                    out var maskLeft) ||
                !TryEvaluateScalarOperand64(
                    instruction.Sources[1],
                    registers,
                    execMask,
                    out var maskRight))
            {
                error = $"scalar-source64 pc=0x{instruction.Pc:X} op={instruction.Opcode}";
                return false;
            }

            var value = instruction.Opcode switch
            {
                "SCselectB64" => scalarConditionCode ? maskLeft : maskRight,
                "SAndB64" => maskLeft & maskRight,
                "SOrB64" => maskLeft | maskRight,
                "SXorB64" => maskLeft ^ maskRight,
                "SAndn2B64" => maskLeft & ~maskRight,
                "SOrn2B64" => maskLeft | ~maskRight,
                "SNandB64" => ~(maskLeft & maskRight),
                "SNorB64" => ~(maskLeft | maskRight),
                _ => ~(maskLeft ^ maskRight),
            };
            WriteScalarPair(registers, destination.Value, value, ref execMask);
            scalarConditionCode = value != 0;
            return true;
        }

        if (instruction.Sources.Count == 0 ||
            !TryEvaluateScalarOperand(instruction.Sources[0], registers, out var left))
        {
            var source = instruction.Sources.Count == 0
                ? "<missing>"
                : instruction.Sources[0].ToString();
            error = $"scalar-source0 pc=0x{instruction.Pc:X} op={instruction.Opcode} source={source}";
            return false;
        }

        if (instruction.Opcode == "SMovB32")
        {
            registers[destination.Value] = left;
            return true;
        }

        if (instruction.Opcode is
            "SNotB32" or
            "SBrevB32" or
            "SBcnt1I32B32" or
            "SFF1I32B32" or
            "SBitset1B32")
        {
            registers[destination.Value] = instruction.Opcode switch
            {
                "SNotB32" => ~left,
                "SBrevB32" => ReverseBits(left),
                "SBcnt1I32B32" => (uint)BitOperations.PopCount(left),
                "SFF1I32B32" => left == 0 ? uint.MaxValue : (uint)BitOperations.TrailingZeroCount(left),
                _ => registers[destination.Value] | (1u << ((int)left & 31)),
            };
            if (instruction.Opcode != "SBitset1B32")
            {
                scalarConditionCode = registers[destination.Value] != 0;
            }

            return true;
        }

        if (instruction.Sources.Count < 2 ||
            !TryEvaluateScalarOperand(instruction.Sources[1], registers, out var right))
        {
            var source = instruction.Sources.Count < 2
                ? "<missing>"
                : instruction.Sources[1].ToString();
            error = $"scalar-source1 pc=0x{instruction.Pc:X} op={instruction.Opcode} source={source}";
            return false;
        }

        uint result;
        switch (instruction.Opcode)
        {
            case "SAddU32":
                {
                    var wide = (ulong)left + right;
                    result = (uint)wide;
                    scalarConditionCode = wide > uint.MaxValue;
                    break;
                }
            case "SSubU32":
                result = left - right;
                scalarConditionCode = right > left;
                break;
            case "SAddI32":
                result = unchecked((uint)((int)left + (int)right));
                scalarConditionCode = SignedAddOverflow(left, right, result);
                break;
            case "SSubI32":
                result = unchecked((uint)((int)left - (int)right));
                scalarConditionCode = SignedSubOverflow(left, right, result);
                break;
            case "SAddcU32":
                {
                    var wide = (ulong)left + right + (scalarConditionCode ? 1UL : 0UL);
                    result = (uint)wide;
                    scalarConditionCode = wide > uint.MaxValue;
                    break;
                }
            case "SSubbU32":
                {
                    var borrow = scalarConditionCode ? 1UL : 0UL;
                    var subtrahend = (ulong)right + borrow;
                    result = unchecked(left - (uint)subtrahend);
                    scalarConditionCode = subtrahend > left;
                    break;
                }
            case "SMinI32":
                result = unchecked((uint)Math.Min((int)left, (int)right));
                scalarConditionCode = (int)left < (int)right;
                break;
            case "SMinU32":
                result = Math.Min(left, right);
                scalarConditionCode = left < right;
                break;
            case "SMaxI32":
                result = unchecked((uint)Math.Max((int)left, (int)right));
                scalarConditionCode = (int)left > (int)right;
                break;
            case "SMaxU32":
                result = Math.Max(left, right);
                scalarConditionCode = left > right;
                break;
            case "SCselectB32":
                result = scalarConditionCode ? left : right;
                break;
            case "SAndB32":
                result = left & right;
                scalarConditionCode = result != 0;
                break;
            case "SOrB32":
                result = left | right;
                scalarConditionCode = result != 0;
                break;
            case "SXorB32":
                result = left ^ right;
                scalarConditionCode = result != 0;
                break;
            case "SAndn2B32":
                result = left & ~right;
                scalarConditionCode = result != 0;
                break;
            case "SOrn2B32":
                result = left | ~right;
                scalarConditionCode = result != 0;
                break;
            case "SNandB32":
                result = ~(left & right);
                scalarConditionCode = result != 0;
                break;
            case "SNorB32":
                result = ~(left | right);
                scalarConditionCode = result != 0;
                break;
            case "SXnorB32":
                result = ~(left ^ right);
                scalarConditionCode = result != 0;
                break;
            case "SLshlB32":
                result = left << ((int)right & 31);
                scalarConditionCode = result != 0;
                break;
            case "SLshrB32":
                result = left >> ((int)right & 31);
                scalarConditionCode = result != 0;
                break;
            case "SAshrI32":
                result = unchecked((uint)((int)left >> ((int)right & 31)));
                scalarConditionCode = result != 0;
                break;
            case "SBfmB32":
                {
                    var width = (int)left & 31;
                    var offset = (int)right & 31;
                    result = width == 0 ? 0 : ((1u << width) - 1u) << offset;
                    break;
                }
            case "SMulI32":
                result = unchecked((uint)((int)left * (int)right));
                break;
            case "SBfeU32":
                {
                    var offset = (int)right & 31;
                    var width = Math.Min(((int)right >> 16) & 0x7F, 32 - offset);
                    result = width == 0 ? 0 : left >> offset & (uint.MaxValue >> (32 - width));
                    scalarConditionCode = result != 0;
                    break;
                }
            case "SBfeI32":
                {
                    var offset = (int)right & 31;
                    var width = Math.Min(((int)right >> 16) & 0x7F, 32 - offset);
                    result = width == 0
                        ? 0
                        : unchecked((uint)(((int)(left << (32 - width - offset))) >> (32 - width)));
                    scalarConditionCode = result != 0;
                    break;
                }
            case "SAbsdiffI32":
                result = unchecked((uint)Math.Abs((long)(int)left - (int)right));
                scalarConditionCode = result != 0;
                break;
            case "SLshl1AddU32":
                {
                    var wide = ((ulong)left << 1) + right;
                    result = (uint)wide;
                    scalarConditionCode = wide > uint.MaxValue;
                    break;
                }
            case "SLshl2AddU32":
                {
                    var wide = ((ulong)left << 2) + right;
                    result = (uint)wide;
                    scalarConditionCode = wide > uint.MaxValue;
                    break;
                }
            case "SLshl3AddU32":
                {
                    var wide = ((ulong)left << 3) + right;
                    result = (uint)wide;
                    scalarConditionCode = wide > uint.MaxValue;
                    break;
                }
            case "SLshl4AddU32":
                {
                    var wide = ((ulong)left << 4) + right;
                    result = (uint)wide;
                    scalarConditionCode = wide > uint.MaxValue;
                    break;
                }
            case "SPackLlB32B16":
                result = (left & 0xFFFFu) | (right << 16);
                break;
            case "SPackLhB32B16":
                result = (left & 0xFFFFu) | (right & 0xFFFF0000u);
                break;
            case "SPackHhB32B16":
                result = (left >> 16) | (right & 0xFFFF0000u);
                break;
            case "SMulHiU32":
                result = (uint)(((ulong)left * right) >> 32);
                break;
            case "SMulHiI32":
                result = unchecked((uint)(((long)(int)left * (int)right) >> 32));
                break;
            default:
                error = $"unsupported-scalar-op pc=0x{instruction.Pc:X} op={instruction.Opcode}";
                return false;
        }

        registers[destination.Value] = result;
        return true;
    }

    private static bool TryExecuteSaveExecScalarAlu(
        Gen5ShaderInstruction instruction,
        uint[] registers,
        ref ulong execMask,
        ref bool scalarConditionCode,
        out string error)
    {
        error = string.Empty;
        if (instruction.Opcode is not (
            "SAndSaveexecB64" or
            "SOrSaveexecB64" or
            "SXorSaveexecB64" or
            "SAndn2SaveexecB64" or
            "SOrn2SaveexecB64" or
            "SNandSaveexecB64" or
            "SNorSaveexecB64" or
            "SXnorSaveexecB64" or
            "SAndn1SaveexecB64" or
            "SOrn1SaveexecB64"))
        {
            return false;
        }

        if (instruction.Destinations.Count != 1 ||
            instruction.Destinations[0] is not
            {
                Kind: Gen5OperandKind.ScalarRegister,
                Value: < ScalarRegisterCount - 1,
            } destination ||
            instruction.Sources.Count == 0 ||
            !TryEvaluateScalarOperand64(
                instruction.Sources[0],
                registers,
                execMask,
                out var source))
        {
            error = $"scalar-source64 pc=0x{instruction.Pc:X} op={instruction.Opcode}";
            return false;
        }

        var oldExec = execMask;
        var newExec = instruction.Opcode switch
        {
            "SAndSaveexecB64" => oldExec & source,
            "SOrSaveexecB64" => oldExec | source,
            "SXorSaveexecB64" => oldExec ^ source,
            "SAndn1SaveexecB64" => ~source & oldExec,
            "SAndn2SaveexecB64" => source & ~oldExec,
            "SOrn1SaveexecB64" => ~source | oldExec,
            "SOrn2SaveexecB64" => source | ~oldExec,
            "SNandSaveexecB64" => ~(source & oldExec),
            "SNorSaveexecB64" => ~(source | oldExec),
            _ => ~(oldExec ^ source),
        };

        WriteScalarPair(registers, destination.Value, oldExec, ref execMask);
        execMask = MaskWaveValue(newExec);
        WriteScalarPair(registers, 126, execMask, ref execMask);
        scalarConditionCode = execMask != 0;
        return true;
    }

    private static bool TryEvaluateScalarOperand64(
        Gen5Operand operand,
        uint[] registers,
        ulong execMask,
        out ulong value)
    {
        if (operand.Kind == Gen5OperandKind.ScalarRegister &&
            operand.Value == 126)
        {
            value = execMask;
            return true;
        }

        if (operand.Kind == Gen5OperandKind.ScalarRegister &&
            operand.Value < ScalarRegisterCount - 1)
        {
            value = registers[operand.Value] | ((ulong)registers[operand.Value + 1] << 32);
            return true;
        }

        if (TryEvaluateScalarOperand(operand, registers, out var low))
        {
            value = operand.Kind == Gen5OperandKind.EncodedConstant &&
                    operand.Value is >= 193 and <= 208
                ? ulong.MaxValue << 32 | low
                : low;
            return true;
        }

        value = 0;
        return false;
    }

    private static void WriteScalarPair(
        uint[] registers,
        uint destination,
        ulong value,
        ref ulong execMask)
    {
        if (destination >= ScalarRegisterCount - 1)
        {
            return;
        }

        if (destination == 126)
        {
            value = MaskWaveValue(value);
        }

        registers[destination] = (uint)value;
        registers[destination + 1] = (uint)(value >> 32);
        if (destination == 126)
        {
            execMask = value;
        }
    }

    private static ulong MaskWaveValue(ulong value) => value & RdnaWaveMask;

    private static bool SignedAddOverflow(uint left, uint right, uint result) =>
        ((left ^ result) & (right ^ result) & 0x80000000u) != 0;

    private static bool SignedSubOverflow(uint left, uint right, uint result) =>
        ((left ^ right) & (left ^ result) & 0x80000000u) != 0;

    private static bool TryExecuteScalarCompare(
        Gen5ShaderInstruction instruction,
        uint[] registers,
        out bool scalarConditionCode,
        out string error)
    {
        scalarConditionCode = false;
        error = string.Empty;
        if (instruction.Sources.Count != 2 ||
            !TryEvaluateScalarOperand(instruction.Sources[0], registers, out var left) ||
            !TryEvaluateScalarOperand(instruction.Sources[1], registers, out var right))
        {
            error = $"scalar-compare-source pc=0x{instruction.Pc:X} op={instruction.Opcode}";
            return false;
        }

        if (instruction.Opcode is "SBitcmp0B32" or "SBitcmp1B32")
        {
            var bit = (int)(right & 31u);
            var isSet = ((left >> bit) & 1u) != 0;
            scalarConditionCode = instruction.Opcode == "SBitcmp1B32" ? isSet : !isSet;
            return true;
        }

        if (instruction.Opcode is "SBitcmp0B64" or "SBitcmp1B64")
        {
            if (!TryEvaluateScalarOperand64(instruction.Sources[0], registers, ulong.MaxValue, out var wide))
            {
                error = $"scalar-bitcmp-source64 pc=0x{instruction.Pc:X} op={instruction.Opcode}";
                return false;
            }

            var bit = (int)(right & 63u);
            var isSet = ((wide >> bit) & 1UL) != 0;
            scalarConditionCode = instruction.Opcode == "SBitcmp1B64" ? isSet : !isSet;
            return true;
        }

        scalarConditionCode = instruction.Opcode switch
        {
            "SCmpEqI32" => (int)left == (int)right,
            "SCmpLgI32" => (int)left != (int)right,
            "SCmpGtI32" => (int)left > (int)right,
            "SCmpGeI32" => (int)left >= (int)right,
            "SCmpLtI32" => (int)left < (int)right,
            "SCmpLeI32" => (int)left <= (int)right,
            "SCmpEqU32" => left == right,
            "SCmpLgU32" => left != right,
            "SCmpGtU32" => left > right,
            "SCmpGeU32" => left >= right,
            "SCmpLtU32" => left < right,
            "SCmpLeU32" => left <= right,
            _ => false,
        };
        if (!instruction.Opcode.StartsWith("SCmp", StringComparison.Ordinal))
        {
            error = $"unsupported-scalar-compare pc=0x{instruction.Pc:X} op={instruction.Opcode}";
            return false;
        }

        return true;
    }

    private static bool TryExecuteScalarCompareK(
        Gen5ShaderInstruction instruction,
        uint[] registers,
        out bool scalarConditionCode,
        out string error)
    {
        scalarConditionCode = false;
        error = string.Empty;
        if (instruction.Destinations.Count != 1 ||
            instruction.Destinations[0] is not
            {
                Kind: Gen5OperandKind.ScalarRegister,
                Value: < ScalarRegisterCount,
            } destination)
        {
            error = $"scalar-comparek-destination pc=0x{instruction.Pc:X} op={instruction.Opcode}";
            return false;
        }

        var left = registers[destination.Value];
        var right = unchecked((uint)(short)instruction.Sources[0].Value);
        scalarConditionCode = instruction.Opcode switch
        {
            "SCmpkEqI32" => (int)left == (int)right,
            "SCmpkLgI32" => (int)left != (int)right,
            "SCmpkGtI32" => (int)left > (int)right,
            "SCmpkGeI32" => (int)left >= (int)right,
            "SCmpkLtI32" => (int)left < (int)right,
            "SCmpkLeI32" => (int)left <= (int)right,
            "SCmpkEqU32" => left == right,
            "SCmpkLgU32" => left != right,
            "SCmpkGtU32" => left > right,
            "SCmpkGeU32" => left >= right,
            "SCmpkLtU32" => left < right,
            "SCmpkLeU32" => left <= right,
            _ => false,
        };
        if (!instruction.Opcode.StartsWith("SCmpk", StringComparison.Ordinal))
        {
            error = $"unsupported-scalar-comparek pc=0x{instruction.Pc:X} op={instruction.Opcode}";
            return false;
        }

        return true;
    }

    private static bool TryExecuteScalarLoad(
        CpuContext ctx,
        Gen5ShaderState state,
        Gen5ShaderInstruction instruction,
        Gen5ScalarMemoryControl control,
        uint[] scalarRegisters,
        List<Gen5GlobalMemoryBinding> globalMemoryBindings,
        Dictionary<(uint ScalarAddress, ulong BaseAddress), Gen5GlobalMemoryBinding> globalMemoryByAddress,
        HashSet<uint> runtimeScalarRegisters,
        out string error)
    {
        error = string.Empty;
        if (instruction.Sources.Count == 0 ||
            instruction.Sources[0] is not
            {
                Kind: Gen5OperandKind.ScalarRegister,
                Value: < ScalarRegisterCount - 1,
            } scalarBase)
        {
            error = $"invalid-scalar-base pc=0x{instruction.Pc:X}";
            return false;
        }

        var isBufferLoad =
            instruction.Opcode.StartsWith("SBufferLoad", StringComparison.Ordinal);
        BufferDescriptor bufferDescriptor = default;
        var hasBufferDescriptor =
            isBufferLoad &&
            TryDecodeBufferDescriptor(
                scalarRegisters,
                scalarBase.Value,
                strictType: false,
                out bufferDescriptor);
        var baseAddress = hasBufferDescriptor
            ? bufferDescriptor.BaseAddress
            : scalarRegisters[scalarBase.Value] |
              ((ulong)scalarRegisters[scalarBase.Value + 1] << 32);
        var dynamicOffset = control.DynamicOffsetRegister is { } offsetRegister &&
                            offsetRegister < ScalarRegisterCount
            ? scalarRegisters[offsetRegister]
            : 0;
        var immediateOffset = (ulong)(long)control.ImmediateOffsetBytes;
        var byteOffset = unchecked(immediateOffset + dynamicOffset);
        var address = unchecked(
            baseAddress +
            byteOffset) & ~3UL;
        var bufferUnbound =
            isBufferLoad &&
            (!hasBufferDescriptor ||
             bufferDescriptor.SizeBytes == 0 ||
             (scalarRegisters[scalarBase.Value] == 0 &&
              scalarRegisters[scalarBase.Value + 1] == 0 &&
              scalarBase.Value + 3 < ScalarRegisterCount &&
              scalarRegisters[scalarBase.Value + 2] == 0 &&
              scalarRegisters[scalarBase.Value + 3] == 0));
        var bufferSize = ulong.MaxValue;
        if (isBufferLoad)
        {
            bufferSize = hasBufferDescriptor ? bufferDescriptor.SizeBytes : ulong.MaxValue;

            var key = (scalarBase.Value, bufferDescriptor.BaseAddress);
            if (globalMemoryByAddress.TryGetValue(key, out var existingBinding))
            {
                if (existingBinding.InstructionPcs is List<uint> instructionPcs) instructionPcs.Add(instruction.Pc);
            }
            else
            {
                TryReadGlobalMemory(ctx, bufferDescriptor.BaseAddress, bufferDescriptor.SizeBytes, out var data);
                var binding = new Gen5GlobalMemoryBinding(scalarBase.Value, bufferDescriptor.BaseAddress, new List<uint> { instruction.Pc }, data);
                globalMemoryByAddress.Add(key, binding);
                globalMemoryBindings.Add(binding);
            }
        }
        else if (baseAddress != 0)
        {
            var key = (scalarBase.Value, baseAddress);
            if (globalMemoryByAddress.TryGetValue(key, out var existingBinding))
            {
                if (existingBinding.InstructionPcs is List<uint> instructionPcs)
                {
                    instructionPcs.Add(instruction.Pc);
                }
            }
            else
            {
                var requiredBytes = Math.Max(
                    256UL * 1024UL,
                    checked(
                        byteOffset +
                        (ulong)Math.Max(instruction.Destinations.Count, 1) *
                        sizeof(uint)));
                requiredBytes = Math.Min(
                    (requiredBytes + 4095UL) & ~4095UL,
                    MaxGlobalMemoryBindingBytes);
                if (!TryReadGlobalMemory(
                        ctx,
                        baseAddress,
                        requiredBytes,
                        out var data))
                {
                    data = [];
                }

                var binding = new Gen5GlobalMemoryBinding(
                    scalarBase.Value,
                    baseAddress,
                    new List<uint> { instruction.Pc },
                    data);
                globalMemoryByAddress.Add(key, binding);
                globalMemoryBindings.Add(binding);
            }
        }

        if (!bufferUnbound && address == 0)
        {
            error = FormatScalarLoadError(
                "invalid-load-address",
                instruction,
                scalarBase.Value,
                scalarRegisters,
                control,
                baseAddress,
                dynamicOffset,
                address);
            return false;
        }

        for (var index = 0; index < instruction.Destinations.Count; index++)
        {
            var destination = instruction.Destinations[index];
            if (destination.Kind != Gen5OperandKind.ScalarRegister ||
                destination.Value >= ScalarRegisterCount)
            {
                error = FormatScalarLoadError(
                    "invalid-scalar-destination",
                    instruction,
                    scalarBase.Value,
                    scalarRegisters,
                    control,
                    baseAddress,
                    dynamicOffset,
                    address);
                return false;
            }

            var componentOffset = unchecked(byteOffset + (ulong)(index * sizeof(uint)));
            if (bufferUnbound ||
                isBufferLoad &&
                (componentOffset >= bufferSize ||
                 bufferSize - componentOffset < sizeof(uint)))
            {
                scalarRegisters[destination.Value] = 0;
                continue;
            }

            if (!ctx.TryReadUInt32(
                    address + (ulong)(index * sizeof(uint)),
                    out var value) &&
                !TryReadUserDataScalarLoad(
                    state,
                    instruction,
                    control,
                    byteOffset,
                    index,
                    out value))
            {
                if (isBufferLoad)
                {
                    scalarRegisters[destination.Value] = 0;
                    continue;
                }

                error = FormatScalarLoadError(
                    "scalar-load-failed",
                    instruction,
                    scalarBase.Value,
                    scalarRegisters,
                    control,
                    baseAddress,
                    dynamicOffset,
                    address);
                return false;
            }

            scalarRegisters[destination.Value] = value;
        }

        return true;
    }

    private static bool TryDecodeBufferDescriptor(
        IReadOnlyList<uint> scalarRegisters,
        uint scalarBase,
        bool strictType,
        out BufferDescriptor descriptor)
    {
        descriptor = default;
        if (scalarBase + 3 >= scalarRegisters.Count)
        {
            return false;
        }

        var word0 = scalarRegisters[(int)scalarBase];
        var word1 = scalarRegisters[(int)scalarBase + 1];
        var word2 = scalarRegisters[(int)scalarBase + 2];
        var word3 = scalarRegisters[(int)scalarBase + 3];
        if (word0 == 0 &&
            word1 == 0 &&
            word2 == 0 &&
            word3 == 0)
        {
            descriptor = new BufferDescriptor(0, 0, 0, 0, 0, 0);
            return true;
        }

        var type = word3 >> 30;
        if (type != 0)
        {
            if (strictType)
            {
                return false;
            }

            descriptor = new BufferDescriptor(0, 0, 0, 0, 0, 0);
            return true;
        }

        var baseAddress = word0 | ((ulong)(word1 & 0xFFFFu) << 32);
        var stride = (word1 >> 16) & 0x3FFFu;
        var unifiedFormat = (word3 >> 12) & 0x7Fu;
        var (dataFormat, numberFormat) =
            DecodeGfx10BufferFormat(unifiedFormat);
        var sizeBytes = stride == 0
            ? word2
            : (ulong)stride * word2;
        descriptor = new BufferDescriptor(baseAddress, stride, word2, sizeBytes, numberFormat, dataFormat);
        return true;
    }

    private static (uint DataFormat, uint NumberFormat)
        DecodeGfx10BufferFormat(uint format) =>
        format switch
        {
            0 => (0, 0),
            >= 1 and <= 6 => (1, format - 1),
            >= 7 and <= 13 => (2, DecodeUnifiedNumber(format - 7, 7)),
            >= 14 and <= 19 => (3, format - 14),
            >= 20 and <= 22 => (4, DecodeIntegerOrFloatNumber(format - 20)),
            >= 23 and <= 29 => (5, DecodeUnifiedNumber(format - 23, 7)),
            >= 30 and <= 36 => (6, DecodeUnifiedNumber(format - 30, 7)),
            >= 37 and <= 43 => (7, DecodeUnifiedNumber(format - 37, 7)),
            >= 44 and <= 49 => (8, format - 44),
            >= 50 and <= 55 => (9, format - 50),
            >= 56 and <= 61 => (10, format - 56),
            >= 62 and <= 64 => (11, DecodeIntegerOrFloatNumber(format - 62)),
            >= 65 and <= 71 => (12, DecodeUnifiedNumber(format - 65, 7)),
            >= 72 and <= 74 => (13, DecodeIntegerOrFloatNumber(format - 72)),
            >= 75 and <= 77 => (14, DecodeIntegerOrFloatNumber(format - 75)),
            128 => (1, 9),
            129 => (3, 9),
            130 => (10, 9),
            132 => (34, 7),
            133 => (16, 0),
            134 => (17, 0),
            135 => (18, 0),
            136 => (19, 0),
            140 => (4, 7),
            _ => (0, 0),
        };

    private static uint DecodeUnifiedNumber(uint offset, uint formatCount) =>
        offset == formatCount - 1 ? 7u : offset;

    private static uint DecodeIntegerOrFloatNumber(uint offset) =>
        offset switch
        {
            0 => 4,
            1 => 5,
            _ => 7,
        };

    private static bool TryReadUserDataScalarLoad(
        Gen5ShaderState state,
        Gen5ShaderInstruction instruction,
        Gen5ScalarMemoryControl control,
        ulong byteOffset,
        int componentIndex,
        out uint value)
    {
        value = 0;
        if (!instruction.Opcode.StartsWith("SLoadDword", StringComparison.Ordinal) ||
            state.Metadata is not { } metadata ||
            (byteOffset & 3) != 0)
        {
            return false;
        }

        var baseDwordOffset = byteOffset >> 2;
        if (baseDwordOffset > int.MaxValue)
        {
            return false;
        }

        var dwordOffset = (long)baseDwordOffset + componentIndex;
        if (dwordOffset < 0 ||
            dwordOffset >= state.UserData.Count ||
            !IsShaderUserDataResourceOffset(metadata, (uint)dwordOffset))
        {
            return false;
        }

        value = state.UserData[(int)dwordOffset];
        return true;
    }

    private static bool IsShaderUserDataResourceOffset(
        Gen5ShaderMetadata metadata,
        uint dwordOffset)
    {
        if (dwordOffset < metadata.ShaderResourceTableSizeDwords)
        {
            return true;
        }

        foreach (var resource in metadata.Resources)
        {
            var dwordCount = resource.Kind switch
            {
                Gen5ShaderResourceKind.ReadOnlyTexture or
                    Gen5ShaderResourceKind.ReadWriteTexture => 8u,
                Gen5ShaderResourceKind.Sampler or
                    Gen5ShaderResourceKind.ConstantBuffer => 4u,
                _ => 1u,
            };
            if (dwordOffset >= resource.OffsetDwords &&
                dwordOffset < resource.OffsetDwords + dwordCount)
            {
                return true;
            }
        }

        return metadata.DirectResources.Values.Any(offset => offset == dwordOffset);
    }

    private static string FormatScalarLoadError(
        string reason,
        Gen5ShaderInstruction instruction,
        uint scalarBase,
        uint[] scalarRegisters,
        Gen5ScalarMemoryControl control,
        ulong baseAddress,
        uint dynamicOffset,
        ulong address)
    {
        var high = scalarBase + 1 < scalarRegisters.Length
            ? scalarRegisters[scalarBase + 1]
            : 0;
        var dynamic = control.DynamicOffsetRegister is { } register
            ? $" dyn=s{register}=0x{dynamicOffset:X8}"
            : " dyn=none";
        var descriptor = string.Join(
            ':',
            Enumerable.Range(0, 4).Select(index =>
                scalarBase + index < scalarRegisters.Length
                    ? $"{scalarRegisters[scalarBase + index]:X8}"
                    : "????????"));
        var words = string.Join(',', instruction.Words.Select(word => $"{word:X8}"));
        return
            $"{reason} pc=0x{instruction.Pc:X} op={instruction.Opcode} " +
            $"words=[{words}] base=s{scalarBase}[0x{scalarRegisters[scalarBase]:X8}:0x{high:X8}] " +
            $"desc=[{descriptor}] " +
            $"base_addr=0x{baseAddress:X16} imm={control.ImmediateOffsetBytes}" +
            $"{dynamic} address=0x{address:X16}";
    }

    private static bool TryEvaluateScalarOperand(
        Gen5Operand operand,
        uint[] scalarRegisters,
        out uint value)
    {
        if (operand.Kind == Gen5OperandKind.ScalarRegister &&
            operand.Value < ScalarRegisterCount)
        {
            value = scalarRegisters[operand.Value];
            return true;
        }

        if (operand.Kind == Gen5OperandKind.LiteralConstant)
        {
            value = operand.Value;
            return true;
        }

        if (operand.Kind == Gen5OperandKind.EncodedConstant)
        {
            return TryDecodeInlineConstant(operand.Value, out value);
        }

        value = 0;
        return false;
    }

    private static bool TryDecodeInlineConstant(uint encoded, out uint value)
    {
        if (encoded == 125)
        {
            value = 0;
            return true;
        }

        if (encoded is >= 128 and <= 192)
        {
            value = encoded - 128;
            return true;
        }

        if (encoded is >= 193 and <= 208)
        {
            value = unchecked((uint)-(int)(encoded - 192));
            return true;
        }

        var floatingPoint = encoded switch
        {
            240 => 0.5f,
            241 => -0.5f,
            242 => 1.0f,
            243 => -1.0f,
            244 => 2.0f,
            245 => -2.0f,
            246 => 4.0f,
            247 => -4.0f,
            248 => 1.0f / (2.0f * MathF.PI),
            _ => float.NaN,
        };
        if (float.IsNaN(floatingPoint))
        {
            value = 0;
            return false;
        }

        value = BitConverter.SingleToUInt32Bits(floatingPoint);
        return true;
    }

    private static uint ReverseBits(uint value)
    {
        value = (value >> 1 & 0x55555555u) | ((value & 0x55555555u) << 1);
        value = (value >> 2 & 0x33333333u) | ((value & 0x33333333u) << 2);
        value = (value >> 4 & 0x0F0F0F0Fu) | ((value & 0x0F0F0F0Fu) << 4);
        value = (value >> 8 & 0x00FF00FFu) | ((value & 0x00FF00FFu) << 8);
        return value >> 16 | value << 16;
    }

    private static bool TryCopyRegisters(
        uint[] registers,
        uint start,
        int count,
        out IReadOnlyList<uint> values)
    {
        values = [];
        if (start > (uint)(registers.Length - count))
        {
            return false;
        }

        var copy = new uint[count];
        Array.Copy(registers, (int)start, copy, 0, count);
        values = copy;
        return true;
    }

    private static bool UsesSampler(string opcode) =>
        opcode.StartsWith("ImageSample", StringComparison.Ordinal) ||
        opcode.StartsWith("ImageGather", StringComparison.Ordinal);

}
