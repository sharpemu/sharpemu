// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Vulkan;

public static partial class Gen5SpirvTranslator
{
    private sealed partial class CompilationContext
    {
        // These virtual segments are outside the guest global address space.
        private const ulong SharedApertureBase = Gen5InlineConstants.SharedApertureBase;
        private const ulong PrivateApertureBase = Gen5InlineConstants.PrivateApertureBase;

        private uint EmitPackedIntegerAddSubtract(Gen5ShaderInstruction instruction)
        {
            var control = (Gen5Vop3pControl)instruction.Control!;
            uint Half(bool high)
            {
                uint Operand(int index)
                {
                    var mask = high ? control.OpSelHiMask : control.OpSelMask;
                    var value = _module.AddInstruction(SpirvOp.BitFieldSExtract, _intType,
                        Bitcast(_intType, GetRawSource(instruction, index)),
                        UInt((mask & (1u << index)) != 0 ? 16u : 0u), UInt(16));
                    return value;
                }
                var value = _module.AddInstruction(instruction.Opcode == "VPkAddI16" ? SpirvOp.IAdd : SpirvOp.ISub,
                    _intType, Operand(0), Operand(1));
                if (control.Clamp)
                    value = Ext(45, _intType, value, Bitcast(_intType, UInt(0xFFFF8000)), Bitcast(_intType, UInt(0x7FFF)));
                return BitwiseAnd(Bitcast(_uintType, value), UInt(0xFFFF));
            }
            return BitwiseOr(Half(false), ShiftLeftLogical(Half(true), UInt(16)));
        }

        private bool TryEmitFlatDwordMemory(Gen5ShaderInstruction instruction, Gen5GlobalMemoryControl control, out string error)
        {
            var address = IAdd64(Pair64(LoadV(control.VectorAddress), LoadV(control.VectorAddress + 1)),
                SignedOffset64(control.OffsetBytes));
            var segment = And64(address, ULong(0xFFFFFFFF00000000));
            var shared = _module.AddInstruction(SpirvOp.IEqual, _boolType, segment, ULong(SharedApertureBase));
            var privateMemory = _module.AddInstruction(SpirvOp.IEqual, _boolType, segment, ULong(PrivateApertureBase));
            shared = _module.AddInstruction(SpirvOp.LogicalOr, _boolType, shared,
                _module.AddInstruction(SpirvOp.IEqual, _boolType, segment, ULong(Gen5InlineConstants.SharedFlatApertureBase)));
            privateMemory = _module.AddInstruction(SpirvOp.LogicalOr, _boolType, privateMemory,
                _module.AddInstruction(SpirvOp.IEqual, _boolType, segment, ULong(Gen5InlineConstants.PrivateFlatApertureBase)));
            var local = _module.AddInstruction(SpirvOp.LogicalOr, _boolType, shared, privateMemory);
            var success = true;
            var failure = string.Empty;
            EmitConditional(local, () => EmitExecConditional(() =>
            {
                var offset = _module.AddInstruction(SpirvOp.UConvert, _uintType, address);
                EmitConditional(shared,
                    () => EmitFlatLocalDwords(instruction, control, offset, true),
                    () => EmitFlatLocalDwords(instruction, control, offset, false));
            }), () =>
            {
                // Invalid segments must not alias global memory after address truncation.
                EmitConditional(ULessThan64(address, ULong(1ul << 48)), () =>
                {
                    success = TryEmitLayoutGlobalMemory(instruction with { Opcode = "Global" + instruction.Opcode[4..] },
                        control with { UsesFlatAddress = false, ScalarAddress = 125 }, out failure);
                }, () => EmitExecConditional(() =>
                {
                    if (instruction.Opcode.StartsWith("FlatLoad", StringComparison.Ordinal))
                        for (uint index = 0; index < control.DwordCount; index++)
                            StoreV(control.DestinationVectorRegister + index, UInt(0));
                }));
            });
            error = failure;
            return success;
        }

        private void EmitFlatLocalDwords(Gen5ShaderInstruction instruction, Gen5GlobalMemoryControl control, uint offset, bool shared)
        {
            var count = shared ? _ldsDwordCount : _scratchDwordCount;
            var store = instruction.Opcode.StartsWith("FlatStore", StringComparison.Ordinal);
            for (uint index = 0; index < control.DwordCount; index++)
            {
                var byteOffset = IAdd64(Widen(offset), ULong(index * 4ul));
                var valid = ULessThan64(IAdd64(byteOffset, ULong(3)), ULong(count * 4ul));
                if (!store) StoreV(control.DestinationVectorRegister + index, UInt(0));
                if (count == 0) continue;
                var component = index;
                EmitConditional(valid, () =>
                {
                    var pointer = shared ? LdsPointer(offset, component * 4) : ScratchPointer(offset, component * 4);
                    if (store)
                    {
                        var value = LoadV(control.SourceVectorRegister + component);
                        if (shared) StoreLds(pointer, value); else Store(pointer, value);
                    }
                    else
                    {
                        StoreV(control.DestinationVectorRegister + component, Load(_uintType, pointer));
                    }
                });
            }
        }
    }
}
