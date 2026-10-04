// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.ShaderCompiler.Vulkan;

public static partial class Gen5SpirvTranslator
{
    private sealed partial class CompilationContext
    {
        private bool UsesScratch() =>
            _request.Program.Instructions.Any(static instruction =>
                instruction.Control is Gen5ScratchMemoryControl ||
                instruction.Opcode.StartsWith("Scratch", StringComparison.Ordinal)) ||
            _request.Memory.Entries.Any(static memory =>
                memory.AddressSpace is FlatAddressSpace.Private or FlatAddressSpace.SharedOrPrivate);

        private void DeclareScratch()
        {
            if (!UsesScratch())
            {
                return;
            }

            if (_request.ScratchDwords > uint.MaxValue / sizeof(uint))
            {
                throw new InvalidOperationException(
                    $"scratch allocation {_request.ScratchDwords} dwords exceeds the 32-bit byte address space");
            }

            // The architectural size can be zero. SPIR-V arrays cannot, so keep
            // one inaccessible backing dword while retaining the exact logical
            // byte bound for all reads and writes.
            _scratchDwordCount = Math.Max(_request.ScratchDwords, 1u);
            _scratchByteCount = checked(_request.ScratchDwords * sizeof(uint));

            var scratchArrayType = _module.TypeArray(_uintType, _scratchDwordCount);
            var scratchArrayPointer =
                _module.TypePointer(SpirvStorageClass.Private, scratchArrayType);
            _scratchElementPointer =
                _module.TypePointer(SpirvStorageClass.Private, _uintType);
            var zero = _module.ConstantNull(scratchArrayType);

            _scratchLow = _module.AddGlobalVariable(
                scratchArrayPointer,
                SpirvStorageClass.Private,
                zero);
            _scratch = _scratchLow;
            _module.AddName(_scratchLow, "scratch");
            _interfaces.Add(_scratchLow);

            // Paired wave64 translates two guest invocations through one host
            // invocation. Give each half an independent private scratch file.
            if (_pairWave64)
            {
                _scratchHigh = _module.AddGlobalVariable(
                    scratchArrayPointer,
                    SpirvStorageClass.Private,
                    zero);
                _module.AddName(_scratchHigh, "scratchHigh");
                _interfaces.Add(_scratchHigh);
            }
        }

        private bool TryEmitScratchMemory(
            Gen5ShaderInstruction instruction,
            Gen5ScratchMemoryControl control,
            out string error)
        {
            error = string.Empty;
            if (_scratch == 0 || _scratchElementPointer == 0)
            {
                error = "scratch instruction has no private scratch allocation";
                return false;
            }

            if (control.AddressMode == Gen5ScratchAddressMode.Invalid)
            {
                error = $"invalid scratch SADDR encoding {control.ScalarAddress}";
                return false;
            }

            var address = control.AddressMode switch
            {
                Gen5ScratchAddressMode.Vector => LoadV(control.VectorAddress),
                Gen5ScratchAddressMode.Scalar => LoadS(control.ScalarAddress),
                Gen5ScratchAddressMode.Immediate => UInt(0),
                _ => UInt(0),
            };
            if (control.OffsetBytes != 0)
            {
                // Scratch arithmetic is 32-bit and wraps before the unsigned
                // bounds test; a negative underflow therefore becomes OOB.
                address = IAdd(
                    address,
                    UInt(unchecked((uint)control.OffsetBytes)));
            }

            if (instruction.Opcode.StartsWith("ScratchStore", StringComparison.Ordinal))
            {
                var isSubdwordStore = TryGetSubdwordStoreInfo(
                    instruction.Opcode,
                    out var byteCount,
                    out var sourceShift);
                if (!isSubdwordStore && control.DwordCount == 0)
                {
                    error = $"unsupported scratch opcode {instruction.Opcode}";
                    return false;
                }

                EmitExecConditional(() =>
                {
                    if (isSubdwordStore)
                    {
                        StoreBoundedScratchBytes(
                            address,
                            LoadV(control.SourceVectorRegister),
                            byteCount,
                            sourceShift);
                        return;
                    }

                    for (uint component = 0; component < control.DwordCount; component++)
                    {
                        StoreBoundedScratchBytes(
                            AddScratchByteOffset(address, component * sizeof(uint)),
                            LoadV(control.SourceVectorRegister + component),
                            sizeof(uint),
                            sourceShift: 0);
                    }
                });
                return true;
            }

            if (!instruction.Opcode.StartsWith("ScratchLoad", StringComparison.Ordinal))
            {
                error = $"unsupported scratch opcode {instruction.Opcode}";
                return false;
            }

            if (TryGetSubdwordLoadInfo(
                    instruction.Opcode,
                    out var loadByteCount,
                    out var signExtend,
                    out var d16,
                    out var d16High))
            {
                StoreV(
                    control.DestinationVectorRegister,
                    LoadScratchValue(
                        address,
                        LoadV(control.DestinationVectorRegister),
                        loadByteCount,
                        signExtend,
                        d16,
                        d16High));
                return true;
            }

            if (control.DwordCount == 0)
            {
                error = $"unsupported scratch opcode {instruction.Opcode}";
                return false;
            }

            for (uint component = 0; component < control.DwordCount; component++)
            {
                StoreV(
                    control.DestinationVectorRegister + component,
                    LoadScratchBytes(
                        AddScratchByteOffset(address, component * sizeof(uint)),
                        sizeof(uint)));
            }

            return true;
        }

        private uint AddScratchByteOffset(uint address, uint offset) =>
            offset == 0 ? address : IAdd(address, UInt(offset));

        private uint IsScratchAccessInBounds(uint address, uint byteCount)
        {
            if (byteCount > _scratchByteCount)
            {
                return _module.ConstantBool(false);
            }

            return _module.AddInstruction(
                SpirvOp.ULessThanEqual,
                _boolType,
                address,
                UInt(_scratchByteCount - byteCount));
        }

        private uint BoundedScratchPointer(uint byteAddress, uint valid)
        {
            // Select a known-valid address before AccessChain. This keeps even
            // speculative host evaluation inside the at-least-one-dword array.
            var safeAddress = _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                valid,
                byteAddress,
                UInt(0));
            return _module.AddInstruction(
                SpirvOp.AccessChain,
                _scratchElementPointer,
                _scratch,
                ShiftRightLogical(safeAddress, UInt(2)));
        }

        private uint LoadScratchBytes(uint address, uint byteCount)
        {
            var valid = IsScratchAccessInBounds(address, byteCount);
            var value = UInt(0);
            for (uint index = 0; index < byteCount; index++)
            {
                var byteAddress = AddScratchByteOffset(address, index);
                var word = Load(
                    _uintType,
                    BoundedScratchPointer(byteAddress, valid));
                var wordShift = ShiftLeftLogical(
                    BitwiseAnd(byteAddress, UInt(3)),
                    UInt(3));
                var nextByte = BitwiseAnd(
                    ShiftRightLogical(word, wordShift),
                    UInt(0xFF));
                value = BitwiseOr(
                    value,
                    ShiftLeftLogical(nextByte, UInt(index * 8)));
            }

            return _module.AddInstruction(
                SpirvOp.Select,
                _uintType,
                valid,
                value,
                UInt(0));
        }

        private uint LoadScratchValue(
            uint address,
            uint previous,
            uint byteCount,
            bool signExtend,
            bool d16,
            bool d16High)
        {
            var raw = LoadScratchBytes(address, byteCount);
            if (signExtend)
            {
                raw = Bitcast(
                    _uintType,
                    _module.AddInstruction(
                        SpirvOp.BitFieldSExtract,
                        _intType,
                        Bitcast(_intType, raw),
                        UInt(0),
                        UInt(byteCount * 8)));
            }

            if (!d16)
            {
                return raw;
            }

            var half = BitwiseAnd(raw, UInt(0xFFFF));
            return d16High
                ? BitwiseOr(
                    BitwiseAnd(previous, UInt(0x0000_FFFF)),
                    ShiftLeftLogical(half, UInt(16)))
                : BitwiseOr(
                    BitwiseAnd(previous, UInt(0xFFFF_0000)),
                    half);
        }

        private void StoreBoundedScratchBytes(
            uint address,
            uint value,
            uint byteCount,
            uint sourceShift)
        {
            var valid = IsScratchAccessInBounds(address, byteCount);
            EmitConditional(valid, () =>
            {
                for (uint index = 0; index < byteCount; index++)
                {
                    var byteAddress = AddScratchByteOffset(address, index);
                    var pointer = BoundedScratchPointer(
                        byteAddress,
                        _module.ConstantBool(true));
                    var word = Load(_uintType, pointer);
                    var wordShift = ShiftLeftLogical(
                        BitwiseAnd(byteAddress, UInt(3)),
                        UInt(3));
                    var mask = ShiftLeftLogical(UInt(0xFF), wordShift);
                    var sourceByte = BitwiseAnd(
                        ShiftRightLogical(
                            value,
                            UInt(sourceShift + index * 8)),
                        UInt(0xFF));
                    Store(
                        pointer,
                        BitwiseOr(
                            BitwiseAnd(
                                word,
                                _module.AddInstruction(
                                    SpirvOp.Not,
                                    _uintType,
                                    mask)),
                            ShiftLeftLogical(sourceByte, wordShift)));
                }
            });
        }
    }
}
