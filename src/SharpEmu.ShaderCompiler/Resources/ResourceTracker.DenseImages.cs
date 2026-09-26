// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Resources;

public sealed partial class ResourceTracker
{
    private const uint DenseIndirectImageShift = 5;
    private const uint MaxDenseIndirectImageEntries = 4096;

    private bool TryMakeDenseIndirectImage(ScalarValue handle, uint pc, out IndirectImagePlan plan)
    {
        plan = null!;
        if (handle.Kind != ScalarValueKind.ImageHandle || handle.Operands.Length != 8)
            return false;

        var reads = new ScalarValue[8];
        var memoryIndices = new int[8];
        ScalarValue? heapHandle = null;
        ScalarValue? based = null;
        uint tableImmediate = 0;
        var canSuppressMemoryReads = true;

        for (var dword = 0; dword < 8; dword++)
        {
            var read = handle.Operands[dword];
            if (read.Kind != ScalarValueKind.ScalarAddressWord || read.MemoryIndex < 0 ||
                read.MemoryIndex >= _plan.Memory.Count || !MemoryIndexBelongsTo(read.MemoryIndex, read))
                return false;

            var memory = _plan.Memory[read.MemoryIndex];
            if (memory.Kind != MemoryResourceKind.ScalarAddress || memory.DataBits != 32 || memory.DataDwords != 1)
                return false;

            var offset = read.Operands[1];
            uint extra = 0;
            if (based is null)
            {
                based = offset;
            }
            else if (!_graph.Equivalent(offset, based))
            {
                if (offset.Kind != ScalarValueKind.Operation || offset.Operation != ScalarOperation.IAdd32 || offset.Operands.Length != 2)
                    return false;

                ScalarValue inner;
                if (offset.Operands[1].IsConstant)
                {
                    extra = offset.Operands[1].ConstantU32;
                    inner = offset.Operands[0];
                }
                else if (offset.Operands[0].IsConstant)
                {
                    extra = offset.Operands[0].ConstantU32;
                    inner = offset.Operands[1];
                }
                else
                {
                    return false;
                }

                if (!_graph.Equivalent(inner, based))
                    return false;
            }

            var componentOffset = checked((uint)dword * sizeof(uint));
            if ((ulong)extra + memory.Offset < componentOffset)
                return false;
            var immediate = extra + memory.Offset - componentOffset;
            if (dword == 0)
                tableImmediate = immediate;
            else if (immediate != tableImmediate)
                return false;

            var currentHandle = read.Operands[0];
            if (currentHandle.Kind != ScalarValueKind.AddressHandle ||
                (heapHandle is not null && !_graph.Equivalent(currentHandle, heapHandle)))
                return false;

            heapHandle = currentHandle;
            reads[dword] = read;
            memoryIndices[dword] = read.MemoryIndex;
            canSuppressMemoryReads &= HasOnlyImageConsumers(memory, handle);
        }

        if (based is null || heapHandle is null)
            return false;

        uint tableOffset = tableImmediate;
        var scaled = based;
        if (scaled.Kind == ScalarValueKind.Operation && scaled.Operation == ScalarOperation.IAdd32 && scaled.Operands.Length == 2)
        {
            if (scaled.Operands[1].IsConstant)
            {
                tableOffset = unchecked(tableOffset + scaled.Operands[1].ConstantU32);
                scaled = scaled.Operands[0];
            }
            else if (scaled.Operands[0].IsConstant)
            {
                tableOffset = unchecked(tableOffset + scaled.Operands[0].ConstantU32);
                scaled = scaled.Operands[1];
            }
            else
            {
                return false;
            }
        }

        if (scaled.Kind != ScalarValueKind.Operation || scaled.Operation != ScalarOperation.ShiftLeft32 ||
            scaled.Operands.Length != 2 || !scaled.Operands[1].IsConstant ||
            scaled.Operands[1].ConstantU32 != DenseIndirectImageShift)
            return false;

        var key = scaled.Operands[0];
        var bound = DenseKeyBound(key);
        var waveIndexed = TryCreateWaveIndexedImageSelector(key, reads);
        if (bound == 0 && waveIndexed is null)
        {
            return false;
        }

        foreach (var read in reads)
            if (!UsesOnly(read, [handle]))
                return false;

        if (!MakeRuntimeAddressSource(heapHandle, pc, out var heapSourceIndex, out var heapSource))
            return false;

        var imageDwords = Enumerable.Repeat(key, 8).ToArray();
        imageDwords[0] = heapSource.Dwords[0];
        imageDwords[1] = heapSource.Dwords[1];
        var imageSource = new DescriptorSource
        {
            Dwords = imageDwords,
            IndirectImage = new IndirectImageSelector(0, heapSourceIndex, 0, 0, 0)
            {
                Dense = true,
                TableOffset = tableOffset,
                DynamicOffsetBase = unchecked(tableOffset - tableImmediate),
                KeyBound = bound,
                WaveIndexed = waveIndexed,
            },
        };

        plan = new IndirectImagePlan
        {
            Handle = handle,
            Source = InternSource(imageSource),
            Key = reads[0],
            KeyIsAddressOffset = true,
            HeapSource = heapSourceIndex,
            SuppressMemoryReads = canSuppressMemoryReads,
            Memory = memoryIndices,
            Reads = reads,
        };
        return true;
    }

    private uint DenseKeyBound(ScalarValue key)
    {
        if (key.Kind == ScalarValueKind.Operation && key.Operation == ScalarOperation.FindLowestBit32 &&
            _graph.HasNonZeroBitScanInput(key))
            return 32;

        if (!_uses.TryGetValue(key, out var uses))
            return 0;

        foreach (var use in uses)
        {
            if (use.Kind != ScalarValueKind.Operation || use.Operation != ScalarOperation.ULessThan32 || use.Operands.Length != 2 ||
                !ReferenceEquals(use.Operands[0], key) || !use.Operands[1].IsConstant)
                continue;

            var limit = use.Operands[1].ConstantU32;
            if (limit is > 0 and <= MaxDenseIndirectImageEntries)
                return limit;
        }

        return 0;
    }

    // Post-process kernels can select a descriptor per active bit in a scalar mask:
    // mask -> s_ff1 -> v_mov -> (bit * stride + table) -> global_load ->
    // v_readfirstlane -> key << 5 -> s_load_dwordx8.  The global load itself is
    // lane-varying, so it deliberately stays undefined in the scalar graph.  The host
    // can nevertheless resolve the same finite key set from the mask and index table.
    private WaveIndexedImageSelector? TryCreateWaveIndexedImageSelector(
        ScalarValue key,
        IReadOnlyList<ScalarValue> reads)
    {
        if (key.Kind != ScalarValueKind.FirstLane || key.Operands.Length != 2 || reads.Count == 0)
        {
            return null;
        }

        var instructions = _graph.Program.Instructions;
        var firstLaneIndex = FindInstructionIndex(instructions, (uint)key.Payload);
        if (firstLaneIndex < 0 || instructions[firstLaneIndex] is not { Opcode: "VReadfirstlaneB32", Sources.Count: 1, Destinations.Count: 1 } firstLane ||
            firstLane.Sources[0] is not { Kind: Gen5OperandKind.VectorRegister } vectorSource ||
            firstLane.Destinations[0] is not { Kind: Gen5OperandKind.ScalarRegister } scalarDestination)
        {
            return null;
        }

        var globalIndex = FindLastDefinition(instructions, firstLaneIndex, vectorSource);
        if (globalIndex < 0 || instructions[globalIndex].Control is not Gen5GlobalMemoryControl
            {
                DwordCount: 1,
                UsesFlatAddress: false,
                OffsetBytes: 0,
            } global || global.DestinationVectorRegister != vectorSource.Value)
        {
            return null;
        }

        var descriptorMemoryIndex = reads[0].MemoryIndex;
        if (descriptorMemoryIndex < 0 || descriptorMemoryIndex >= _plan.Memory.Count)
        {
            return null;
        }

        var descriptorLoadIndex = FindInstructionIndex(instructions, _plan.Memory[descriptorMemoryIndex].Pc);
        if (descriptorLoadIndex < 0 || instructions[descriptorLoadIndex] is not
            {
                Control: Gen5ScalarMemoryControl
            {
                DestinationCount: >= 8,
                DynamicOffsetRegister: { } dynamicOffsetRegister,
            },
            } descriptorLoad || dynamicOffsetRegister != scalarDestination.Value ||
            descriptorLoad.Sources.Count == 0 || descriptorLoad.Sources[0] != Gen5Operand.Scalar(global.ScalarAddress))
        {
            return null;
        }

        var address = Gen5Operand.Vector(global.VectorAddress);
        var addressDefinitionIndex = FindLastDefinition(instructions, globalIndex, address);
        if (addressDefinitionIndex < 0 || !TryGetAddedConstant(instructions[addressDefinitionIndex], out var indexDataOffset))
        {
            return null;
        }

        var heapAddress = Gen5Operand.Scalar(global.ScalarAddress);
        if (TryGetSelfAddedConstant(instructions[addressDefinitionIndex], address, out _) &&
            TryGetWaveIndexedStride(instructions, addressDefinitionIndex, address, heapAddress,
                out var maskRegister, out var bitRegister, out var maskOffset, out var indexStride) &&
            indexDataOffset != 0 && indexStride != 0 && instructions.Any(instruction => instruction.Opcode == "SBitset0B32" &&
                instruction.Destinations.Contains(maskRegister) && instruction.Sources.Contains(bitRegister)))
        {
            return new(maskOffset, indexDataOffset, indexStride);
        }

        // Some game kernels reuse the vector address register while preparing the
        // lane mask. The address arithmetic no longer has a single SSA-looking
        // chain, but the scalar data dependency is still exact: one clean mask word,
        // its FF1 result, the matching bit clear, and a scalar multiply that gives
        // the global index stride. This is the same finite runtime-table model; it
        // merely avoids making register allocation part of the proof.
        return TryGetWaveIndexedMaskStride(instructions, addressDefinitionIndex, heapAddress, indexDataOffset);
    }

    private static WaveIndexedImageSelector? TryGetWaveIndexedMaskStride(
        IReadOnlyList<Gen5ShaderInstruction> instructions,
        int before,
        Gen5Operand heapAddress,
        uint indexTableOffset)
    {
        if (indexTableOffset == 0)
            return null;

        for (var loadIndex = before - 1; loadIndex >= 0; loadIndex--)
        {
            if (instructions[loadIndex] is not
                {
                    Control: Gen5ScalarMemoryControl { DestinationCount: 1, ImmediateOffsetBytes: >= 0, DynamicOffsetRegister: null } maskControl,
                    Sources.Count: >= 1,
                    Destinations.Count: 1,
                } maskLoad || maskLoad.Sources[0] != heapAddress ||
                maskLoad.Destinations[0] is not { Kind: Gen5OperandKind.ScalarRegister } maskRegister)
                continue;

            for (var scanIndex = loadIndex + 1; scanIndex < before; scanIndex++)
            {
                if (instructions[scanIndex] is not { Opcode: "SFF1I32B32", Sources.Count: 1, Destinations.Count: 1 } scan ||
                    scan.Sources[0] != maskRegister || scan.Destinations[0] is not { Kind: Gen5OperandKind.ScalarRegister } bitRegister ||
                    !instructions.Skip(scanIndex + 1).Take(before - scanIndex - 1)
                        .Any(instruction => instruction.Opcode == "SBitset0B32" && instruction.Destinations.Contains(maskRegister) &&
                            instruction.Sources.Contains(bitRegister)))
                    continue;

                for (var multiplyIndex = scanIndex + 1; multiplyIndex < before; multiplyIndex++)
                {
                    var multiply = instructions[multiplyIndex];
                    if (multiply.Opcode != "SMulI32" || multiply.Sources.Count != 2 || !multiply.Sources.Contains(bitRegister))
                        continue;
                    var strideOperand = multiply.Sources[0] == bitRegister ? multiply.Sources[1] : multiply.Sources[0];
                    if (!TryGetConstant(strideOperand, out var stride) || stride == 0)
                        continue;
                    return new((uint)maskControl.ImmediateOffsetBytes, indexTableOffset, stride);
                }
            }
        }

        return null;
    }

    private static bool TryGetWaveIndexedStride(
        IReadOnlyList<Gen5ShaderInstruction> instructions,
        int addressAddIndex,
        Gen5Operand address,
        Gen5Operand heapAddress,
        out Gen5Operand maskRegister,
        out Gen5Operand bitRegister,
        out uint maskOffset,
        out uint stride)
    {
        maskRegister = default;
        bitRegister = default;
        maskOffset = 0;
        stride = 0;
        var strideAddIndex = FindLastDefinition(instructions, addressAddIndex, address);
        if (strideAddIndex < 0 || instructions[strideAddIndex] is not { Opcode: "VLshlAddU32", Sources.Count: 3 } strideAdd ||
            strideAdd.Sources[0] != address || strideAdd.Sources[2] != address || !TryGetConstant(strideAdd.Sources[1], out var outerShift) || outerShift >= 31)
            return false;

        var innerShiftIndex = FindLastDefinition(instructions, strideAddIndex, address);
        if (innerShiftIndex < 0 || instructions[innerShiftIndex] is not { Opcode: "VLshlrevB32", Sources.Count: 2 } innerShift ||
            !TryGetConstant(innerShift.Sources[0], out var innerAmount) || innerAmount >= 31 ||
            innerShift.Sources[1] is not { Kind: Gen5OperandKind.VectorRegister } bitVector)
            return false;

        var moveIndex = FindLastDefinition(instructions, innerShiftIndex, bitVector);
        if (moveIndex < 0 || instructions[moveIndex] is not { Opcode: "VMovB32", Sources.Count: 1 } move ||
            move.Sources[0] is not { Kind: Gen5OperandKind.ScalarRegister } bitScalar)
            return false;

        var bitScanIndex = FindLastDefinition(instructions, moveIndex, bitScalar);
        if (bitScanIndex < 0 || instructions[bitScanIndex] is not { Opcode: "SFF1I32B32", Sources.Count: 1 } bitScan ||
            bitScan.Sources[0] is not { Kind: Gen5OperandKind.ScalarRegister } mask)
            return false;

        var maskLoadIndex = FindLastDefinition(instructions, bitScanIndex, mask);
        if (maskLoadIndex < 0 || instructions[maskLoadIndex] is not
            {
                Control: Gen5ScalarMemoryControl { DestinationCount: 1, ImmediateOffsetBytes: >= 0, DynamicOffsetRegister: null } maskControl,
                Sources.Count: >= 1,
            } maskLoad || maskLoad.Sources[0] != heapAddress)
            return false;

        var combinedStride = (ulong)(1u << (int)innerAmount) * ((1u << (int)outerShift) + 1u);
        if (combinedStride == 0 || combinedStride > uint.MaxValue)
            return false;

        maskRegister = mask;
        bitRegister = bitScalar;
        maskOffset = (uint)maskControl.ImmediateOffsetBytes;
        stride = (uint)combinedStride;
        return true;
    }

    private static int FindLastDefinition(IReadOnlyList<Gen5ShaderInstruction> instructions, int before, Gen5Operand destination)
    {
        for (var index = before - 1; index >= 0; index--)
        {
            if (instructions[index].Destinations.Contains(destination))
            {
                return index;
            }
        }

        return -1;
    }

    private static int FindInstructionIndex(IReadOnlyList<Gen5ShaderInstruction> instructions, uint pc)
    {
        for (var index = 0; index < instructions.Count; index++)
        {
            if (instructions[index].Pc == pc)
            {
                return index;
            }
        }

        return -1;
    }

    private static bool TryGetSelfAddedConstant(Gen5ShaderInstruction instruction, Gen5Operand destination, out uint constant)
    {
        constant = 0;
        if (instruction.Opcode is not ("VAddI32" or "VAddU32") || instruction.Sources.Count != 2 ||
            !instruction.Destinations.Contains(destination))
        {
            return false;
        }

        var left = instruction.Sources[0];
        var right = instruction.Sources[1];
        if (left == destination)
        {
            return TryGetConstant(right, out constant);
        }

        return right == destination && TryGetConstant(left, out constant);
    }

    private static bool TryGetAddedConstant(Gen5ShaderInstruction instruction, out uint constant)
    {
        constant = 0;
        if (instruction.Opcode is not ("VAddI32" or "VAddU32") || instruction.Sources.Count != 2)
            return false;
        return TryGetConstant(instruction.Sources[0], out constant) || TryGetConstant(instruction.Sources[1], out constant);
    }

    private static bool TryGetConstant(Gen5Operand operand, out uint constant)
    {
        if (operand.Kind == Gen5OperandKind.LiteralConstant)
        {
            constant = operand.Value;
            return true;
        }

        if (operand.Kind == Gen5OperandKind.EncodedConstant && Gen5InlineConstants.TryDecode(operand.Value, out constant))
        {
            return true;
        }

        constant = 0;
        return false;
    }
}
