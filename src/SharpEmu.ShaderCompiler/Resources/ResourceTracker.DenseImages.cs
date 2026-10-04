// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Ir;

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
        var bound = BoundBySamplerLoads(DenseKeyBound(key), heapHandle, tableOffset);
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

    private uint BoundBySamplerLoads(uint bound, ScalarValue heapHandle, uint tableOffset)
    {
        foreach (var access in _plan.Accesses)
        {
            if (access?.SamplerHandle is not { } sampler)
                continue;

            foreach (var operand in sampler.Operands)
            {
                var word = ScalarValueEquivalence.ResolveInvariantPhi(_plan.Memory, operand) ?? operand;
                if (word.Kind == ScalarValueKind.ResourceTableWord && word.Payload < (ulong)_plan.TableReads.Count)
                    word = _plan.TableReads[(int)word.Payload].Value;
                if (word.Kind != ScalarValueKind.ScalarAddressWord || word.MemoryIndex < 0 || word.MemoryIndex >= _plan.Memory.Count ||
                    !word.Operands[1].IsConstant || !_graph.Equivalent(word.Operands[0], heapHandle))
                    continue;

                var offset = (ulong)word.Operands[1].ConstantU32 + _plan.Memory[word.MemoryIndex].Offset;
                if (offset > tableOffset)
                    bound = (uint)Math.Min(bound, (offset - tableOffset) >> (int)DenseIndirectImageShift);
            }
        }

        return bound;
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

    // Post-process kernels can select one descriptor per active bit in a scalar
    // mask. Recognize only instruction chains whose heap, mask, index address,
    // selected key and descriptor loads are proven to be the same chain.
    private WaveIndexedImageSelector? TryCreateWaveIndexedImageSelector(
        ScalarValue key,
        IReadOnlyList<ScalarValue> reads)
    {
        if (key.Kind != ScalarValueKind.FirstLane || key.Operands.Length != 2 || reads.Count == 0)
        {
            return null;
        }

        var instructions = _graph.Program.Instructions;
        var selectIndex = FindInstructionIndex(instructions, (uint)key.Payload);
        if (selectIndex < 0 || instructions[selectIndex] is not
            {
                Sources.Count: > 0,
                Destinations.Count: 1,
            } select ||
            !HasPlainVectorSemantics(select) ||
            select.Sources[0] is not { Kind: Gen5OperandKind.VectorRegister } vectorSource ||
            select.Destinations[0] is not { Kind: Gen5OperandKind.ScalarRegister } scalarDestination)
        {
            return null;
        }

        var readsFirstLane = select is { Opcode: "VReadfirstlaneB32", Sources.Count: 1 };
        var readsIndexedLane = select.Opcode == "VReadlaneB32" && select.Sources.Count >= 2;
        if (!readsFirstLane && !readsIndexedLane)
        {
            return null;
        }

        var globalIndex = FindLastDefinition(instructions, selectIndex, vectorSource);
        if (globalIndex < 0 || instructions[globalIndex] is not
            {
                Opcode: "GlobalLoadDword",
                Control: Gen5GlobalMemoryControl
                {
                    DwordCount: 1,
                    UsesFlatAddress: false,
                    OffsetBytes: 0,
                } global,
            } globalLoad ||
            global.DestinationVectorRegister != vectorSource.Value ||
            (readsFirstLane && !AreInSameBasicBlock(globalLoad.Pc, select.Pc)))
        {
            return null;
        }

        ActiveLaneProof? activeLane = null;
        if (readsIndexedLane &&
            !TryProveActiveLaneRead(
                instructions,
                globalIndex,
                selectIndex,
                vectorSource,
                scalarDestination,
                out activeLane))
        {
            return null;
        }

        var heapHandle = reads[0].Operands[0];
        if (heapHandle.Kind != ScalarValueKind.AddressHandle ||
            !AccessUsesEquivalentAddressHandle(globalLoad.Pc, heapHandle))
        {
            return null;
        }

        if (!ValidateDescriptorLoads(
                instructions,
                selectIndex,
                select,
                scalarDestination,
                global,
                reads,
                activeLane))
        {
            return null;
        }

        var address = Gen5Operand.Vector(global.VectorAddress);
        var addressDefinitionIndex = FindLastDefinition(instructions, globalIndex, address);
        if (addressDefinitionIndex < 0 ||
            !TryGetAddedConstant(
                instructions[addressDefinitionIndex],
                out var indexDataOffset,
                out var indexedAddress) ||
            indexDataOffset == 0 ||
            !HasPlainVectorSemantics(instructions[addressDefinitionIndex]) ||
            !AreInSameBasicBlock(instructions[addressDefinitionIndex].Pc, globalLoad.Pc))
        {
            return null;
        }

        if (readsFirstLane && !HasSignedNonNegativeKeyGuard(
                instructions,
                globalIndex,
                selectIndex,
                vectorSource))
        {
            return null;
        }

        var heapAddress = Gen5Operand.Scalar(global.ScalarAddress);
        if (!TryGetWaveIndexedStride(
                instructions,
                addressDefinitionIndex,
                globalIndex,
                indexedAddress,
                heapAddress,
                heapHandle,
                out var maskOffset,
                out var indexStride) &&
            !TryGetScalarWaveIndexedStride(
                instructions,
                addressDefinitionIndex,
                globalIndex,
                indexedAddress,
                heapAddress,
                heapHandle,
                out maskOffset,
                out indexStride))
        {
            return null;
        }

        return new WaveIndexedImageSelector(maskOffset, indexDataOffset, indexStride)
        {
            RejectNegativeKeys = readsFirstLane,
        };
    }

    private bool ValidateDescriptorLoads(
        IReadOnlyList<Gen5ShaderInstruction> instructions,
        int selectIndex,
        Gen5ShaderInstruction select,
        Gen5Operand scalarDestination,
        Gen5GlobalMemoryControl global,
        IReadOnlyList<ScalarValue> reads,
        ActiveLaneProof? activeLane)
    {
        var loadPcs = reads
            .Select(read => _plan.Memory[read.MemoryIndex].Pc)
            .Distinct()
            .ToArray();
        if (loadPcs.Length == 0)
        {
            return false;
        }

        for (var loadNumber = 0; loadNumber < loadPcs.Length; loadNumber++)
        {
            var loadPc = loadPcs[loadNumber];
            var loadIndex = FindInstructionIndex(instructions, loadPc);
            if (loadIndex <= selectIndex || instructions[loadIndex] is not
                {
                    Opcode: { } opcode,
                    Control: Gen5ScalarMemoryControl
                    {
                        DestinationCount: > 0,
                        DynamicOffsetRegister: { } dynamicOffsetRegister,
                    } loadControl,
                    Sources.Count: > 0,
                } load ||
                !opcode.StartsWith("SLoadDword", StringComparison.Ordinal) ||
                load.Sources[0] != Gen5Operand.Scalar(global.ScalarAddress) ||
                reads.Count(read => _plan.Memory[read.MemoryIndex].Pc == loadPc) != loadControl.DestinationCount)
            {
                return false;
            }

            if (loadNumber == 0 && dynamicOffsetRegister != scalarDestination.Value)
            {
                return false;
            }

            if (activeLane is { } active)
            {
                if (loadIndex <= active.SaveIndex || loadIndex >= active.RestoreIndex)
                {
                    return false;
                }
            }
            else if (!AreInSameBasicBlock(select.Pc, load.Pc))
            {
                return false;
            }
        }

        return true;
    }

    private bool HasSignedNonNegativeKeyGuard(
        IReadOnlyList<Gen5ShaderInstruction> instructions,
        int globalIndex,
        int selectIndex,
        Gen5Operand loadedKey)
    {
        for (var index = globalIndex + 1; index < selectIndex; index++)
        {
            var instruction = instructions[index];
            if (instruction.Opcode != "VCmpxLeI32" || instruction.Sources.Count != 2 ||
                !HasPlainVectorSemantics(instruction) ||
                instruction.Sources[1] != loadedKey ||
                !TryGetConstant(instruction.Sources[0], out var lowerBound) || lowerBound != 0 ||
                !AreInSameBasicBlock(instruction.Pc, instructions[selectIndex].Pc))
            {
                continue;
            }

            if (!instructions
                    .Skip(index + 1)
                    .Take(selectIndex - index - 1)
                    .Any(WritesExecutionMask))
            {
                return true;
            }
        }

        return false;
    }

    private bool TryGetWaveIndexedStride(
        IReadOnlyList<Gen5ShaderInstruction> instructions,
        int addressAddIndex,
        int globalIndex,
        Gen5Operand indexedAddress,
        Gen5Operand heapAddress,
        ScalarValue heapHandle,
        out uint maskOffset,
        out uint stride)
    {
        maskOffset = 0;
        stride = 0;
        var strideAddIndex = FindLastDefinition(instructions, addressAddIndex, indexedAddress);
        if (strideAddIndex < 0 || instructions[strideAddIndex] is not
            {
                Opcode: "VLshlAddU32",
                Sources.Count: 3,
            } strideAdd ||
            strideAdd.Sources[0] != strideAdd.Sources[2] ||
            !HasPlainVectorSemantics(strideAdd) ||
            !AreInSameBasicBlock(strideAdd.Pc, instructions[addressAddIndex].Pc) ||
            !TryGetConstant(strideAdd.Sources[1], out var outerShift) || outerShift >= 31)
        {
            return false;
        }

        var strideInput = strideAdd.Sources[0];
        var innerShiftIndex = FindLastDefinition(instructions, strideAddIndex, strideInput);
        if (innerShiftIndex < 0 || instructions[innerShiftIndex] is not
            {
                Opcode: "VLshlrevB32",
                Sources.Count: 2,
            } innerShift ||
            !TryGetConstant(innerShift.Sources[0], out var innerAmount) || innerAmount >= 31 ||
            !HasPlainVectorSemantics(innerShift) ||
            !AreInSameBasicBlock(innerShift.Pc, strideAdd.Pc) ||
            innerShift.Sources[1] is not { Kind: Gen5OperandKind.VectorRegister } bitVector)
        {
            return false;
        }

        var moveIndex = FindLastDefinition(instructions, innerShiftIndex, bitVector);
        if (moveIndex < 0 || instructions[moveIndex] is not
            {
                Opcode: "VMovB32",
                Sources.Count: 1,
            } move ||
            !HasPlainVectorSemantics(move) ||
            !AreInSameBasicBlock(move.Pc, innerShift.Pc) ||
            move.Sources[0] is not { Kind: Gen5OperandKind.ScalarRegister } bitScalar)
        {
            return false;
        }

        var combinedStride = (ulong)(1u << (int)innerAmount) * ((1u << (int)outerShift) + 1u);
        if (combinedStride == 0 || combinedStride > uint.MaxValue ||
            !TryProveMaskSelection(
                instructions,
                moveIndex,
                globalIndex,
                bitScalar,
                heapAddress,
                heapHandle,
                out maskOffset))
        {
            return false;
        }

        stride = (uint)combinedStride;
        return true;
    }

    // A second safe lowering covers kernels that multiply the selected scalar bit
    // and then copy that exact result into the vector address. Unlike the upstream
    // heuristic, the multiply must be the reaching definition of the address input.
    private bool TryGetScalarWaveIndexedStride(
        IReadOnlyList<Gen5ShaderInstruction> instructions,
        int addressAddIndex,
        int globalIndex,
        Gen5Operand indexedAddress,
        Gen5Operand heapAddress,
        ScalarValue heapHandle,
        out uint maskOffset,
        out uint stride)
    {
        maskOffset = 0;
        stride = 0;
        var moveIndex = FindLastDefinition(instructions, addressAddIndex, indexedAddress);
        if (moveIndex < 0 || instructions[moveIndex] is not
            {
                Opcode: "VMovB32",
                Sources.Count: 1,
            } move ||
            !HasPlainVectorSemantics(move) ||
            !AreInSameBasicBlock(move.Pc, instructions[addressAddIndex].Pc) ||
            move.Sources[0] is not { Kind: Gen5OperandKind.ScalarRegister } scaledBit)
        {
            return false;
        }

        var multiplyIndex = FindLastDefinition(instructions, moveIndex, scaledBit);
        if (multiplyIndex < 0 || instructions[multiplyIndex] is not
            {
                Opcode: "SMulI32",
                Sources.Count: 2,
                Destinations.Count: 1,
            } multiply ||
            multiply.Destinations[0] != scaledBit ||
            !AreInSameBasicBlock(multiply.Pc, move.Pc))
        {
            return false;
        }

        Gen5Operand bitScalar;
        uint scalarStride;
        if (TryGetConstant(multiply.Sources[0], out scalarStride) &&
            multiply.Sources[1] is { Kind: Gen5OperandKind.ScalarRegister } rightBit)
        {
            bitScalar = rightBit;
        }
        else if (TryGetConstant(multiply.Sources[1], out scalarStride) &&
            multiply.Sources[0] is { Kind: Gen5OperandKind.ScalarRegister } leftBit)
        {
            bitScalar = leftBit;
        }
        else
        {
            return false;
        }

        if (scalarStride == 0 ||
            !TryProveMaskSelection(
                instructions,
                multiplyIndex,
                globalIndex,
                bitScalar,
                heapAddress,
                heapHandle,
                out maskOffset))
        {
            return false;
        }

        stride = scalarStride;
        return true;
    }

    private bool TryProveMaskSelection(
        IReadOnlyList<Gen5ShaderInstruction> instructions,
        int consumerIndex,
        int globalIndex,
        Gen5Operand bitScalar,
        Gen5Operand heapAddress,
        ScalarValue heapHandle,
        out uint maskOffset)
    {
        maskOffset = 0;
        var bitScanIndex = FindLastDefinition(instructions, consumerIndex, bitScalar);
        if (bitScanIndex < 0 || instructions[bitScanIndex] is not
            {
                Opcode: "SFF1I32B32",
                Sources.Count: 1,
                Destinations.Count: 1,
            } bitScan ||
            bitScan.Destinations[0] != bitScalar ||
            bitScan.Sources[0] is not { Kind: Gen5OperandKind.ScalarRegister } mask ||
            !AreInSameBasicBlock(bitScan.Pc, instructions[consumerIndex].Pc))
        {
            return false;
        }

        var maskLoadIndex = FindLastDefinition(instructions, bitScanIndex, mask);
        if (maskLoadIndex < 0 || instructions[maskLoadIndex] is not
            {
                Opcode: "SLoadDword",
                Control: Gen5ScalarMemoryControl
                {
                    DestinationCount: 1,
                    ImmediateOffsetBytes: >= 0,
                    DynamicOffsetRegister: null,
                } maskControl,
                Sources.Count: > 0,
                Destinations.Count: 1,
            } maskLoad ||
            maskLoad.Sources[0] != heapAddress ||
            maskLoad.Destinations[0] != mask ||
            !AreInSameBasicBlock(maskLoad.Pc, bitScan.Pc) ||
            !AccessUsesEquivalentAddressHandle(maskLoad.Pc, heapHandle) ||
            !ClearsMaskBit(
                instructions,
                bitScanIndex,
                globalIndex,
                maskLoadIndex,
                bitScanIndex,
                mask,
                bitScalar))
        {
            return false;
        }

        maskOffset = (uint)maskControl.ImmediateOffsetBytes;
        return true;
    }

    private bool ClearsMaskBit(
        IReadOnlyList<Gen5ShaderInstruction> instructions,
        int start,
        int end,
        int maskLoadIndex,
        int bitScanIndex,
        Gen5Operand mask,
        Gen5Operand bit)
    {
        for (var index = start + 1; index < end; index++)
        {
            var instruction = instructions[index];
            if (!AreInSameBasicBlock(instructions[bitScanIndex].Pc, instruction.Pc))
            {
                continue;
            }

            if (instruction is
                {
                    Opcode: "SBitset0B32",
                    Sources.Count: 1,
                    Destinations.Count: 1,
                } &&
                instruction.Destinations[0] == mask &&
                instruction.Sources[0] == bit &&
                FindLastDefinition(instructions, index, mask) == maskLoadIndex &&
                FindLastDefinition(instructions, index, bit) == bitScanIndex)
            {
                return true;
            }

            if (instruction is not
                {
                    Opcode: "SLshlB32",
                    Sources.Count: 2,
                    Destinations.Count: 1,
                } shift ||
                !TryGetConstant(shift.Sources[0], out var one) || one != 1 ||
                shift.Sources[1] != bit ||
                shift.Destinations[0] is not { Kind: Gen5OperandKind.ScalarRegister } single ||
                FindLastDefinition(instructions, index, bit) != bitScanIndex)
            {
                continue;
            }

            for (var clearIndex = index + 1; clearIndex < end; clearIndex++)
            {
                var clear = instructions[clearIndex];
                if (!AreInSameBasicBlock(shift.Pc, clear.Pc))
                {
                    break;
                }

                var clears = clear.Destinations.Count == 1 && clear.Destinations[0] == mask &&
                    clear.Sources.Count == 2 &&
                    ((clear.Opcode == "SXorB32" && clear.Sources.Contains(mask) && clear.Sources.Contains(single)) ||
                     (clear.Opcode == "SAndn2B32" && clear.Sources[0] == mask && clear.Sources[1] == single));
                if (clears &&
                    FindLastDefinition(instructions, clearIndex, mask) == maskLoadIndex &&
                    FindLastDefinition(instructions, clearIndex, bit) == bitScanIndex &&
                    FindLastDefinition(instructions, clearIndex, single) == index)
                {
                    return true;
                }

                if (clear.Destinations.Contains(mask) || clear.Destinations.Contains(bit) ||
                    clear.Destinations.Contains(single))
                {
                    break;
                }
            }
        }

        return false;
    }

    private bool TryProveActiveLaneRead(
        IReadOnlyList<Gen5ShaderInstruction> instructions,
        int globalIndex,
        int readLaneIndex,
        Gen5Operand vectorSource,
        Gen5Operand selectedKey,
        out ActiveLaneProof? proof)
    {
        proof = null;
        var exec = Gen5Operand.Scalar(126);
        var readLane = instructions[readLaneIndex];
        if (readLane.Sources[1] is not { Kind: Gen5OperandKind.ScalarRegister } lane)
        {
            return false;
        }

        var scanIndex = FindLastDefinition(instructions, readLaneIndex, lane);
        if (scanIndex < 0 || instructions[scanIndex] is not
            {
                Opcode: "SFF1I32B64",
                Sources.Count: 1,
                Destinations.Count: 1,
            } scan ||
            scan.Destinations[0] != lane ||
            scan.Sources[0] is not { Kind: Gen5OperandKind.ScalarRegister } candidates)
        {
            return false;
        }

        var copyIndex = FindLastDefinition(instructions, scanIndex, candidates);
        if (copyIndex < 0 || instructions[copyIndex] is not
            {
                Opcode: "SMovB64",
                Sources.Count: 1,
                Destinations.Count: 1,
            } copy ||
            copy.Destinations[0] != candidates ||
            copy.Sources[0] != exec ||
            globalIndex >= copyIndex || copyIndex >= scanIndex ||
            !AreInSameBasicBlock(instructions[globalIndex].Pc, copy.Pc) ||
            !AreInSameBasicBlock(scan.Pc, readLane.Pc) ||
            !FlowsDirectlyTo(instructions[globalIndex].Pc, scan.Pc))
        {
            return false;
        }

        for (var index = copyIndex + 1; index < readLaneIndex; index++)
        {
            if (WritesScalarPair(instructions[index], exec) ||
                WritesScalarPair(instructions[index], candidates) ||
                Gen5IrBranchResolver.Instance.IsBranch(instructions[index]))
            {
                return false;
            }
        }

        var backIndex = -1;
        for (var index = readLaneIndex + 1; index < instructions.Count; index++)
        {
            if (Gen5IrBranchResolver.Instance.TryGetBranchTarget(instructions[index], out var target) &&
                target == instructions[scanIndex].Pc)
            {
                backIndex = index;
                break;
            }
        }

        if (backIndex < 0)
        {
            return false;
        }

        for (var index = readLaneIndex + 1; index < backIndex; index++)
        {
            if (Gen5VectorRegisterWrites.Enumerate(instructions[index]).Contains(vectorSource.Value))
            {
                return false;
            }
        }

        var restoreIndex = -1;
        for (var index = backIndex - 1; index > readLaneIndex; index--)
        {
            if (WritesScalarPair(instructions[index], exec))
            {
                restoreIndex = index;
                break;
            }
        }

        if (restoreIndex < 0 || instructions[restoreIndex] is not
            {
                Opcode: "SMovB64",
                Sources.Count: 1,
                Destinations.Count: 1,
            } restore ||
            restore.Destinations[0] != exec ||
            restore.Sources[0] is not { Kind: Gen5OperandKind.ScalarRegister } saved)
        {
            return false;
        }

        var saveIndex = FindLastDefinition(instructions, restoreIndex, saved);
        if (saveIndex <= readLaneIndex || instructions[saveIndex] is not
            {
                Opcode: "SAndSaveexecB64",
                Sources.Count: 1,
                Destinations.Count: 1,
            } save ||
            save.Destinations[0] != saved ||
            save.Sources[0] is not { Kind: Gen5OperandKind.ScalarRegister } matchingLanes)
        {
            return false;
        }

        var compareIndex = FindLastDefinition(instructions, saveIndex, matchingLanes);
        if (compareIndex <= readLaneIndex || instructions[compareIndex] is not
            {
                Opcode: "VCmpEqU32" or "VCmpEqI32",
                Sources.Count: 2,
            } compare ||
            !compare.Sources.Contains(selectedKey) ||
            !compare.Sources.Contains(vectorSource) ||
            FindLastDefinition(instructions, compareIndex, vectorSource) != globalIndex ||
            !AreInSameBasicBlock(readLane.Pc, save.Pc))
        {
            return false;
        }

        var clearsCandidates = false;
        for (var index = saveIndex + 1; index < restoreIndex; index++)
        {
            var instruction = instructions[index];
            if (instruction is
                {
                    Opcode: "SAndn2B64",
                    Sources.Count: 2,
                    Destinations.Count: 1,
                } &&
                instruction.Destinations[0] == candidates &&
                instruction.Sources[0] == candidates &&
                instruction.Sources[1] == matchingLanes)
            {
                clearsCandidates = true;
                continue;
            }

            if (index > saveIndex && WritesScalarPair(instruction, saved))
            {
                return false;
            }

            if (WritesScalarPair(instruction, candidates))
            {
                return false;
            }

            if (Gen5IrBranchResolver.Instance.TryGetBranchTarget(instruction, out var target) &&
                (target <= instruction.Pc || target > restore.Pc))
            {
                return false;
            }
        }

        if (!clearsCandidates)
        {
            return false;
        }

        proof = new ActiveLaneProof(saveIndex, restoreIndex);
        return true;
    }

    private static bool WritesScalarPair(Gen5ShaderInstruction instruction, Gen5Operand register)
    {
        if (register.Value is 106 or 107 && instruction.Opcode.StartsWith('V'))
        {
            return true;
        }

        if (instruction.Control is Gen5Vop3Control { ScalarDestination: { } scalarDestination } &&
            scalarDestination + 1 >= register.Value && scalarDestination <= register.Value + 1)
        {
            return true;
        }

        if (register.Value == 126 &&
            (instruction.Opcode.Contains("Saveexec", StringComparison.Ordinal) ||
             instruction.Opcode.StartsWith("VCmpx", StringComparison.Ordinal)))
        {
            return true;
        }

        var width = instruction.Opcode.Contains("64", StringComparison.Ordinal) ? 2u : 1u;
        return instruction.Destinations.Any(destination =>
            destination.Kind == Gen5OperandKind.ScalarRegister &&
            destination.Value + width > register.Value &&
            destination.Value <= register.Value + 1);
    }

    private bool AccessUsesEquivalentAddressHandle(uint pc, ScalarValue expected)
    {
        return _plan.Memory.TryGetIndex(pc, 0, out var memoryIndex) &&
            memoryIndex >= 0 && memoryIndex < _plan.Accesses.Length &&
            _plan.Accesses[memoryIndex]?.Handle is { Kind: ScalarValueKind.AddressHandle } actual &&
            _graph.Equivalent(actual, expected);
    }

    // Definition matching is lexical. Keep every accepted dependency inside one
    // basic block unless a dedicated control-flow proof covers the crossing.
    private bool AreInSameBasicBlock(uint firstPc, uint secondPc)
    {
        return _graph.ControlFlow.Blocks.Any(block =>
            firstPc >= block.StartPc && firstPc < block.EndPc &&
            secondPc >= block.StartPc && secondPc < block.EndPc);
    }

    private bool FlowsDirectlyTo(uint fromPc, uint toPc)
    {
        var fromBlock = FindBlock(fromPc);
        var toBlock = FindBlock(toPc);
        return fromBlock >= 0 && toBlock >= 0 &&
            (fromBlock == toBlock ||
             _graph.ControlFlow.Successors[fromBlock].Contains(toBlock));
    }

    private int FindBlock(uint pc)
    {
        for (var index = 0; index < _graph.ControlFlow.Blocks.Count; index++)
        {
            var block = _graph.ControlFlow.Blocks[index];
            if (pc >= block.StartPc && pc < block.EndPc)
            {
                return index;
            }
        }

        return -1;
    }

    private static bool WritesExecutionMask(Gen5ShaderInstruction instruction)
    {
        return instruction.Opcode.StartsWith("VCmpx", StringComparison.Ordinal) ||
            instruction.Opcode.Contains("Saveexec", StringComparison.Ordinal) ||
            instruction.Destinations.Any(destination =>
                destination.Kind == Gen5OperandKind.ScalarRegister && destination.Value is 126 or 127);
    }

    private static bool HasPlainVectorSemantics(Gen5ShaderInstruction instruction)
    {
        return instruction.Control switch
        {
            null => true,
            Gen5Vop3Control
            {
                AbsoluteMask: 0,
                NegateMask: 0,
                OutputModifier: 0,
                Clamp: false,
                OperandSelect: 0,
            } => true,
            _ => false,
        };
    }

    private static int FindLastDefinition(
        IReadOnlyList<Gen5ShaderInstruction> instructions,
        int before,
        Gen5Operand destination)
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

    private static int FindInstructionIndex(
        IReadOnlyList<Gen5ShaderInstruction> instructions,
        uint pc)
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

    private static bool TryGetAddedConstant(
        Gen5ShaderInstruction instruction,
        out uint constant,
        out Gen5Operand value)
    {
        constant = 0;
        value = default;
        if (instruction.Opcode is not ("VAddI32" or "VAddU32") || instruction.Sources.Count != 2)
        {
            return false;
        }

        if (TryGetConstant(instruction.Sources[0], out constant))
        {
            value = instruction.Sources[1];
            return value.Kind == Gen5OperandKind.VectorRegister;
        }

        if (TryGetConstant(instruction.Sources[1], out constant))
        {
            value = instruction.Sources[0];
            return value.Kind == Gen5OperandKind.VectorRegister;
        }

        return false;
    }

    private static bool TryGetConstant(Gen5Operand operand, out uint constant)
    {
        if (operand.Kind == Gen5OperandKind.LiteralConstant)
        {
            constant = operand.Value;
            return true;
        }

        if (operand.Kind == Gen5OperandKind.EncodedConstant &&
            Gen5InlineConstants.TryDecode(operand.Value, out constant))
        {
            return true;
        }

        constant = 0;
        return false;
    }

    private readonly record struct ActiveLaneProof(int SaveIndex, int RestoreIndex);
}
