// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Numerics;

namespace SharpEmu.Core.Cpu.Emulation;

/// <summary>
/// Pure software implementations of the BMI1, BMI2 and ABM general-purpose-register
/// bit-manipulation instructions.
///
/// The direct-execution backend runs guest PS5 code natively on the host CPU. The PS5's
/// Zen 2 cores implement BMI1/BMI2/ABM, but a host CPU that predates those extensions raises
/// #UD (STATUS_ILLEGAL_INSTRUCTION) when it meets one of these opcodes. This class provides the
/// register-only arithmetic so the exception handler can finish the instruction in software and
/// resume, instead of aborting the title.
///
/// The methods deliberately operate on plain integers rather than on the OS CONTEXT record so the
/// semantics can be unit-tested in isolation; the unsafe register/memory plumbing lives in the
/// backend adapter. Each method returns the result already masked to the operand width and, where
/// the instruction is defined to touch flags, updates <paramref name="eflags"/> in place. Flags
/// documented as "undefined" by the vendor manuals are left untouched so behaviour stays
/// deterministic across hosts.
/// </summary>
public static class BmiInstructionEmulator
{
    private const uint FlagCarry = 1u << 0;
    private const uint FlagZero = 1u << 6;
    private const uint FlagSign = 1u << 7;
    private const uint FlagOverflow = 1u << 11;

    private static ulong WidthMask(GprOperandSize size) =>
        size == GprOperandSize.Bits64 ? ulong.MaxValue : 0xFFFF_FFFFUL;

    private static int WidthBits(GprOperandSize size) => (int)size;

    private static bool SignSet(ulong value, GprOperandSize size) =>
        size == GprOperandSize.Bits64 ? (value >> 63) != 0 : (value & 0x8000_0000UL) != 0;

    private static uint WithFlag(uint eflags, uint flag, bool set) =>
        set ? eflags | flag : eflags & ~flag;

    // Applies the CF/ZF/SF/OF set shared by ANDN/BLS*/BZHI: OF is always cleared, ZF and SF follow
    // the result, and the caller supplies CF because each instruction defines it differently.
    private static uint ApplyLogicFlags(uint eflags, ulong result, GprOperandSize size, bool carry)
    {
        eflags = WithFlag(eflags, FlagCarry, carry);
        eflags = WithFlag(eflags, FlagZero, result == 0);
        eflags = WithFlag(eflags, FlagSign, SignSet(result, size));
        eflags = WithFlag(eflags, FlagOverflow, false);
        return eflags;
    }

    /// <summary>ANDN: <c>dest = (~src1) &amp; src2</c>. CF and OF are cleared.</summary>
    public static ulong Andn(ulong src1, ulong src2, GprOperandSize size, ref uint eflags)
    {
        var result = (~src1 & src2) & WidthMask(size);
        eflags = ApplyLogicFlags(eflags, result, size, carry: false);
        return result;
    }

    /// <summary>BLSI: isolate the lowest set bit, <c>dest = (-src) &amp; src</c>. CF = (src != 0).</summary>
    public static ulong Blsi(ulong src, GprOperandSize size, ref uint eflags)
    {
        var mask = WidthMask(size);
        var s = src & mask;
        var result = ((0UL - s) & s) & mask;
        eflags = ApplyLogicFlags(eflags, result, size, carry: s != 0);
        return result;
    }

    /// <summary>BLSMSK: mask up to and including the lowest set bit, <c>dest = (src - 1) ^ src</c>. CF = (src == 0).</summary>
    public static ulong Blsmsk(ulong src, GprOperandSize size, ref uint eflags)
    {
        var mask = WidthMask(size);
        var s = src & mask;
        var result = ((s - 1) ^ s) & mask;
        eflags = ApplyLogicFlags(eflags, result, size, carry: s == 0);
        return result;
    }

    /// <summary>BLSR: reset the lowest set bit, <c>dest = (src - 1) &amp; src</c>. CF = (src == 0).</summary>
    public static ulong Blsr(ulong src, GprOperandSize size, ref uint eflags)
    {
        var mask = WidthMask(size);
        var s = src & mask;
        var result = ((s - 1) & s) & mask;
        eflags = ApplyLogicFlags(eflags, result, size, carry: s == 0);
        return result;
    }

    /// <summary>
    /// BEXTR: extract <c>len</c> bits of <paramref name="src"/> starting at bit <c>start</c>, where
    /// start = control[7:0] and len = control[15:8]. Only ZF (per result) and cleared CF/OF are defined.
    /// </summary>
    public static ulong Bextr(ulong src, ulong control, GprOperandSize size, ref uint eflags)
    {
        var bits = WidthBits(size);
        var start = (int)(control & 0xFF);
        var length = (int)((control >> 8) & 0xFF);

        ulong result;
        if (start >= bits)
        {
            result = 0;
        }
        else
        {
            var shifted = (src & WidthMask(size)) >> start;
            if (length == 0)
            {
                result = 0;
            }
            else if (length >= bits)
            {
                result = shifted;
            }
            else
            {
                result = shifted & ((1UL << length) - 1);
            }
        }

        result &= WidthMask(size);
        eflags = WithFlag(eflags, FlagZero, result == 0);
        eflags = WithFlag(eflags, FlagCarry, false);
        eflags = WithFlag(eflags, FlagOverflow, false);
        return result;
    }

    /// <summary>
    /// BZHI: zero the bits of <paramref name="src"/> from bit position <c>index[7:0]</c> upward.
    /// CF is set when the requested position is at or beyond the operand width.
    /// </summary>
    public static ulong Bzhi(ulong src, ulong index, GprOperandSize size, ref uint eflags)
    {
        var bits = WidthBits(size);
        var mask = WidthMask(size);
        var s = src & mask;
        var n = (int)(index & 0xFF);

        ulong result;
        bool carry;
        if (n >= bits)
        {
            result = s;
            carry = true;
        }
        else
        {
            result = s & ((1UL << n) - 1);
            carry = false;
        }

        eflags = ApplyLogicFlags(eflags, result, size, carry);
        return result;
    }

    /// <summary>TZCNT: count trailing zero bits. If src == 0 the result is the operand width and CF is set.</summary>
    public static ulong Tzcnt(ulong src, GprOperandSize size, ref uint eflags)
    {
        var bits = WidthBits(size);
        var s = src & WidthMask(size);

        ulong result;
        bool carry;
        if (s == 0)
        {
            result = (ulong)bits;
            carry = true;
        }
        else
        {
            result = (ulong)(size == GprOperandSize.Bits64
                ? BitOperations.TrailingZeroCount(s)
                : BitOperations.TrailingZeroCount((uint)s));
            carry = false;
        }

        eflags = WithFlag(eflags, FlagCarry, carry);
        eflags = WithFlag(eflags, FlagZero, result == 0);
        return result;
    }

    /// <summary>LZCNT: count leading zero bits. If src == 0 the result is the operand width and CF is set.</summary>
    public static ulong Lzcnt(ulong src, GprOperandSize size, ref uint eflags)
    {
        var bits = WidthBits(size);
        var s = src & WidthMask(size);

        ulong result;
        bool carry;
        if (s == 0)
        {
            result = (ulong)bits;
            carry = true;
        }
        else
        {
            result = (ulong)(size == GprOperandSize.Bits64
                ? BitOperations.LeadingZeroCount(s)
                : BitOperations.LeadingZeroCount((uint)s));
            carry = false;
        }

        eflags = WithFlag(eflags, FlagCarry, carry);
        eflags = WithFlag(eflags, FlagZero, result == 0);
        return result;
    }

    /// <summary>RORX: rotate <paramref name="src"/> right by <paramref name="count"/> (masked to the operand width). No flags.</summary>
    public static ulong Rorx(ulong src, int count, GprOperandSize size)
    {
        var bits = WidthBits(size);
        var mask = WidthMask(size);
        var s = src & mask;
        var rotate = count & (bits - 1);
        if (rotate == 0)
        {
            return s;
        }

        return ((s >> rotate) | (s << (bits - rotate))) & mask;
    }

    /// <summary>SARX: arithmetic shift right by <paramref name="count"/> (masked to the operand width). No flags.</summary>
    public static ulong Sarx(ulong src, int count, GprOperandSize size)
    {
        var bits = WidthBits(size);
        var mask = WidthMask(size);
        var shift = count & (bits - 1);
        if (size == GprOperandSize.Bits64)
        {
            return (ulong)((long)src >> shift);
        }

        return (ulong)(uint)((int)(uint)src >> shift) & mask;
    }

    /// <summary>SHLX: logical shift left by <paramref name="count"/> (masked to the operand width). No flags.</summary>
    public static ulong Shlx(ulong src, int count, GprOperandSize size)
    {
        var bits = WidthBits(size);
        var mask = WidthMask(size);
        var shift = count & (bits - 1);
        return (src << shift) & mask;
    }

    /// <summary>SHRX: logical shift right by <paramref name="count"/> (masked to the operand width). No flags.</summary>
    public static ulong Shrx(ulong src, int count, GprOperandSize size)
    {
        var bits = WidthBits(size);
        var mask = WidthMask(size);
        var s = src & mask;
        var shift = count & (bits - 1);
        return s >> shift;
    }

    /// <summary>PDEP: deposit contiguous low bits of <paramref name="src"/> into the positions selected by <paramref name="mask"/>. No flags.</summary>
    public static ulong Pdep(ulong src, ulong mask, GprOperandSize size)
    {
        var bits = WidthBits(size);
        var selector = mask & WidthMask(size);
        ulong result = 0;
        var bit = 0;
        for (var i = 0; i < bits; i++)
        {
            var position = 1UL << i;
            if ((selector & position) != 0)
            {
                if (((src >> bit) & 1UL) != 0)
                {
                    result |= position;
                }

                bit++;
            }
        }

        return result & WidthMask(size);
    }

    /// <summary>PEXT: gather the bits of <paramref name="src"/> selected by <paramref name="mask"/> into contiguous low bits. No flags.</summary>
    public static ulong Pext(ulong src, ulong mask, GprOperandSize size)
    {
        var bits = WidthBits(size);
        var selector = mask & WidthMask(size);
        ulong result = 0;
        var bit = 0;
        for (var i = 0; i < bits; i++)
        {
            if ((selector & (1UL << i)) != 0)
            {
                if (((src >> i) & 1UL) != 0)
                {
                    result |= 1UL << bit;
                }

                bit++;
            }
        }

        return result & WidthMask(size);
    }
}
