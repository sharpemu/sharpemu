// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Vulkan;

public static partial class Gen5SpirvTranslator
{
    private sealed partial class CompilationContext
    {
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

    }
}
