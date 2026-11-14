// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Resources;

public static partial class DispatchBufferFootprint
{
    private readonly record struct Word(uint Min, uint Max)
    {
        public static Word Unknown => new(0, uint.MaxValue);
        public static Word Of(uint value) => new(value, value);
        public bool Exact => Min == Max;
        public Word Join(Word other) => new(Math.Min(Min, other.Min), Math.Max(Max, other.Max));
        public Word Not() => new(uint.MaxValue - Max, uint.MaxValue - Min);

        public static Word Binary(string operation, Word a, Word b)
        {
            if (a.Exact && b.Exact)
            {
                var x = a.Min; var y = b.Min;
                return Of(operation switch
                {
                    "add" => unchecked(x + y), "sub" => unchecked(x - y), "mul" => unchecked(x * y),
                    "hi" => (uint)((ulong)x * y >> 32), "and" => x & y, "or" => x | y, "xor" => x ^ y,
                    "shl" => x << (int)(y & 31), "shr" => x >> (int)(y & 31),
                    _ => throw new ArgumentOutOfRangeException(nameof(operation)),
                });
            }
            switch (operation)
            {
                case "add" when (ulong)a.Max + b.Max <= uint.MaxValue: return new(a.Min + b.Min, a.Max + b.Max);
                case "sub" when a.Min >= b.Max: return new(a.Min - b.Max, a.Max - b.Min);
                case "mul" when (ulong)a.Max * b.Max <= uint.MaxValue: return new(a.Min * b.Min, a.Max * b.Max);
                case "hi": return new((uint)((ulong)a.Min * b.Min >> 32), (uint)((ulong)a.Max * b.Max >> 32));
                case "and": return new(0, Math.Min(a.Max, b.Max));
                case "or": case "xor":
                    var top = Math.Max(a.Max, b.Max);
                    top |= top >> 1; top |= top >> 2; top |= top >> 4; top |= top >> 8; top |= top >> 16;
                    return new(0, top);
                case "shr" when b.Exact: return new(a.Min >> (int)(b.Min & 31), a.Max >> (int)(b.Min & 31));
                case "shl" when b.Exact && (ulong)a.Max << (int)(b.Min & 31) <= uint.MaxValue:
                    return new(a.Min << (int)(b.Min & 31), a.Max << (int)(b.Min & 31));
                default: return Unknown;
            }
        }

        public static Word Compare(string operation, Word a, Word b)
        {
            switch (operation)
            {
                case "eq":
                    if (a.Max < b.Min || b.Max < a.Min) return Of(0);
                    if (a.Exact && a == b) return Of(1);
                    break;
                case "ne":
                    var equal = Compare("eq", a, b);
                    return equal.Exact ? Of(1 - equal.Min) : equal;
                case "lt":
                    if (a.Max < b.Min) return Of(1);
                    if (a.Min >= b.Max) return Of(0);
                    break;
                case "le":
                    if (a.Max <= b.Min) return Of(1);
                    if (a.Min > b.Max) return Of(0);
                    break;
                case "gt": return Compare("lt", b, a);
                case "ge": return Compare("le", b, a);
            }
            return new(0, 1);
        }
    }
}
