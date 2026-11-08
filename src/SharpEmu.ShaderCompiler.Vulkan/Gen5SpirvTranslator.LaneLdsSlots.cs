// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.ShaderCompiler.Vulkan;

public static partial class Gen5SpirvTranslator
{
    private sealed partial class CompilationContext
    {
        private const uint M0Register = 124;

        private Dictionary<uint, uint>? _laneLdsSlotByPc;

        private bool TryDeclareLaneLdsSlots()
        {
            if (_stage is Gen5SpirvStage.Compute or Gen5SpirvStage.Mesh ||
                _waveLowering.ReserveLdsDwords(PrivateLdsDwordCount) != PrivateLdsDwordCount ||
                CollectLaneLdsSlotKeys() is not { } keys)
            {
                return false;
            }

            var pointer = _module.TypePointer(SpirvStorageClass.Private, _uintType);
            var variables = new Dictionary<uint, uint>();
            _laneLdsSlotByPc = new Dictionary<uint, uint>(keys.Count);
            foreach (var (pc, key) in keys)
            {
                if (!variables.TryGetValue(key, out var variable))
                {
                    variable = _module.AddGlobalVariable(pointer, SpirvStorageClass.Private, UInt(0));
                    _module.AddName(variable, $"ldsLane{key}");
                    _interfaces.Add(variable);
                    variables.Add(key, variable);
                }

                _laneLdsSlotByPc.Add(pc, variable);
            }

            return true;
        }

        private Dictionary<uint, uint>? CollectLaneLdsSlotKeys()
        {
            if (_request.Memory.Entries.Any(static memory => memory.AddressSpace is FlatAddressSpace.Shared or FlatAddressSpace.SharedOrPrivate))
            {
                return null;
            }

            var instructions = _request.Program.Instructions;
            var targets = new HashSet<uint>();
            foreach (var instruction in instructions)
            {
                if (IsLaneLdsBranch(instruction))
                {
                    targets.Add(instruction.Pc + sizeof(uint) + (uint)((short)(instruction.Words[0] & 0xFFFF) * sizeof(uint)));
                }
                else if (instruction.Opcode is "SSetpcB64" or "SSwappcB64" or "SGetpcB64")
                {
                    return null;
                }
            }

            var keys = new Dictionary<uint, uint>();
            uint? m0 = null;
            foreach (var instruction in instructions)
            {
                if (targets.Contains(instruction.Pc))
                {
                    m0 = null;
                }

                if (instruction.Opcode is "DsWriteAddtidB32" or "DsReadAddtidB32")
                {
                    if (instruction.Control is not Gen5DataShareControl { Gds: false } control || m0 is not { } known)
                    {
                        return null;
                    }

                    keys.Add(instruction.Pc, (((known & 0xFFFF) + control.SingleOffsetBytes) >> 2) & (PrivateLdsDwordCount - 1));
                }
                else if ((instruction.Control is Gen5DataShareControl { Gds: false } &&
                          instruction.Opcode is not ("DsSwizzleB32" or "DsBpermuteB32")) ||
                         instruction.Control is Gen5GlobalMemoryControl { UsesFlatAddress: true } ||
                         instruction.Sources.Any(static source => source.Kind == Gen5OperandKind.EncodedConstant && source.Value is 235 or 236))
                {
                    return null;
                }

                if (instruction.Destinations.Any(static destination =>
                        destination.Kind == Gen5OperandKind.ScalarRegister && destination.Value is M0Register or M0Register - 1))
                {
                    m0 = KnownM0Write(instruction);
                }

                if (IsLaneLdsBranch(instruction))
                {
                    m0 = null;
                }
            }

            return keys.Count == 0 ? null : keys;
        }

        private static bool IsLaneLdsBranch(Gen5ShaderInstruction instruction) =>
            instruction.Opcode.StartsWith("SBranch", StringComparison.Ordinal) ||
            instruction.Opcode.StartsWith("SCbranch", StringComparison.Ordinal);

        private static uint? KnownM0Write(Gen5ShaderInstruction instruction)
        {
            if (instruction.Destinations is not [{ Kind: Gen5OperandKind.ScalarRegister, Value: M0Register }])
            {
                return null;
            }

            if (instruction.Opcode == "SMovkI32" && instruction.Encoding == Gen5ShaderEncoding.Sopk)
            {
                return unchecked((uint)(short)(instruction.Words[0] & 0xFFFF));
            }

            if (instruction.Opcode != "SMovB32" || instruction.Sources is not [var source])
            {
                return null;
            }

            return source.Kind switch
            {
                Gen5OperandKind.LiteralConstant => source.Value,
                Gen5OperandKind.EncodedConstant when source.Value is >= 128 and <= 192 => source.Value - 128,
                Gen5OperandKind.EncodedConstant when source.Value is >= 193 and <= 208 => unchecked((uint)(192 - (int)source.Value)),
                _ => null,
            };
        }
    }
}
