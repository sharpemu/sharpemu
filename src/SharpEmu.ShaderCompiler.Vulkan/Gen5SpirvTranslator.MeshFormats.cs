// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Vulkan;

public static partial class Gen5SpirvTranslator
{
    private sealed partial class CompilationContext
    {
        private bool TryEmitSpecializedMeshBufferLoad(int bindingIndex, uint byteAddress,
            uint format, uint swizzle, uint vectorData, uint componentCount)
        {
            if (!Gfx10UnifiedFormat.TryDecode(format, out var dataFormat, out var numberFormat)) return false;
            var one = UInt(numberFormat is 4 or 5 ? 1u : 0x3f800000u);
            var values = new uint[4];
            var bounds = new uint[4];
            var words = new Dictionary<uint, uint>();
            var firstWord = ShiftRightLogical(byteAddress, UInt(2));
            var alignmentBits = ShiftLeftLogical(BitwiseAnd(byteAddress, UInt(3)), UInt(3));
            uint ReadWord(uint offset)
            {
                if (words.TryGetValue(offset, out var value)) return value;
                // Guest byte addresses wrap at 32 bits before conversion to dword addresses.
                var address = BitwiseAnd(IAdd(firstWord, UInt(offset)), UInt(uint.MaxValue >> 2));
                value = LoadBufferWord(bindingIndex, address);
                words.Add(offset, value);
                return value;
            }
            var inBounds = _module.ConstantBool(true);
            for (uint destination = 0; destination < componentCount; destination++)
            {
                var selector = (swizzle >> (int)(destination * 3)) & 7;
                if (selector < 4) continue;
                var component = selector - 4;
                if (values[component] == 0)
                {
                    bounds[component] = _module.ConstantBool(true);
                    if (Gfx10UnifiedFormat.TryGetComponentLayout(dataFormat, component,
                        out var byteOffset, out var bitOffset, out var bitCount))
                    {
                        var address = IAdd(byteAddress, UInt(byteOffset));
                        var componentBit = byteOffset * 8 + bitOffset;
                        var wordOffset = componentBit / 32;
                        var bitInWord = componentBit % 32;
                        var shift = IAdd(alignmentBits, UInt(bitInWord));
                        var packed = bitInWord + bitCount + 24 > 32
                            ? Narrow(ShiftRightLogical64(Pair64(ReadWord(wordOffset), ReadWord(wordOffset + 1)), Widen(shift)))
                            : ShiftRightLogical(ReadWord(wordOffset), shift);
                        var raw = _module.AddInstruction(SpirvOp.BitFieldUExtract, _uintType,
                            packed, UInt(0), UInt(bitCount));
                        values[component] = ConvertKnownMeshBufferComponent(raw, bitCount, numberFormat, dataFormat);
                        bounds[component] = IsBufferElementInRange(bindingIndex, address,
                            UInt((bitOffset + bitCount + 7) / 8 - 1));
                    }
                    else values[component] = component == 3 ? one : UInt(0);
                    inBounds = LogicalAnd(inBounds, bounds[component]);
                }
            }
            for (uint destination = 0; destination < componentCount; destination++)
            {
                var selector = (swizzle >> (int)(destination * 3)) & 7;
                var fallback = selector == 1 ? one : UInt(0);
                var value = selector >= 4 ? values[selector - 4] : fallback;
                StoreV(vectorData + destination,
                    _module.AddInstruction(SpirvOp.Select, _uintType, inBounds, value, fallback));
            }
            return true;
        }

        private uint ConvertKnownMeshBufferComponent(uint raw, uint bitCount, uint numberFormat, uint dataFormat)
        {
            if (numberFormat == 4) return raw;
            if (numberFormat == 5)
                return Bitcast(_uintType, _module.AddInstruction(SpirvOp.BitFieldSExtract,
                    _intType, Bitcast(_intType, raw), UInt(0), UInt(bitCount)));
            if (numberFormat == 7)
            {
                if (dataFormat is 6 or 7) return DecodeUnsignedMiniFloat(raw, UInt(bitCount));
                if (bitCount != 16) return raw;
                return Bitcast(_uintType, _module.AddInstruction(SpirvOp.CompositeExtract, _floatType,
                    Ext(62, _vec2Type, BitwiseAnd(raw, UInt(0xffff))), 0));
            }
            return ConvertGfx10BufferComponent(raw, UInt(bitCount), UInt(numberFormat), UInt(dataFormat));
        }
    }
}
