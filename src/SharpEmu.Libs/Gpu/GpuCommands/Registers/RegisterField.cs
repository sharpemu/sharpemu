// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.GpuCommands.Registers;

// Field extraction and the split 48-bit address idiom the register writers share.
internal static class RegisterField
{
    public static uint Get(uint value, int shift, uint mask) => (value >> shift) & mask;

    public static bool Bit(uint value, int shift) => ((value >> shift) & 1u) != 0;

    public static float AsFloat(uint value) => BitConverter.UInt32BitsToSingle(value);

    // The low register carries address bits 8..39; the extension register carries bits 40..47.
    public static ulong WithLowAddress(ulong address, uint value) => (address & 0xFFFF_FF00_0000_00FFul) | ((ulong)value << 8);

    public static ulong WithHighAddress(ulong address, uint value) => (address & 0xFFFF_00FF_FFFF_FFFFul) | ((ulong)(value & 0xFFu) << 40);

    public static int Coordinate15(uint value) => (short)(ushort)(value & 0x7FFFu);

    public static int Coordinate16(uint value) => (short)(ushort)(value & 0xFFFFu);
}
