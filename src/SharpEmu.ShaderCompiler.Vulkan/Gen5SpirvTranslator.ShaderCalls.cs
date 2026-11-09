// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;

namespace SharpEmu.ShaderCompiler.Vulkan;

public static partial class Gen5SpirvTranslator
{
    private sealed partial class CompilationContext
    {
        private void EmitShaderCallFault(ShaderCallFaultControl call)
        {
            var length = _module.AddInstruction(SpirvOp.ArrayLength, _uintType, _faultBuffer, 0);
            var start = _module.AddInstruction(SpirvOp.ISub, _uintType, length, UInt(8));
            var claim = _module.AddInstruction(SpirvOp.AtomicCompareExchange, _uintType,
                BlockWordPointer(_faultBuffer, start), UInt(1), UInt(0), UInt(0), UInt(2), UInt(0));
            EmitConditional(_module.AddInstruction(SpirvOp.IEqual, _boolType, claim, UInt(0)), () =>
            {
                uint[] values = [UInt((uint)_request.Hash), UInt((uint)(_request.Hash >> 32)), UInt(call.CallPc),
                    LoadS(call.AddressRegister), LoadS(call.AddressRegister + 1), UInt((uint)_request.Stage), UInt(1)];
                for (var index = 0; index < values.Length; index++)
                    Store(BlockWordPointer(_faultBuffer, IAdd(start, UInt((uint)index + 1))), values[index]);
            });
        }
    }
}
