// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace SharpEmu.Core.Patches;

public sealed partial record GamePatch
{
    private static byte[] EncodeValue(string type, string value)
    {
        switch (type)
        {
            case "bytes":
            case "mask":
            case "mask_jump32":
                return Convert.FromHexString(string.Concat(value.Where(character => !char.IsWhiteSpace(character))));
            case "byte": return EncodeInteger(value, 1);
            case "bytes16": return EncodeInteger(value, 2);
            case "bytes32": return EncodeInteger(value, 4);
            case "bytes64": return EncodeInteger(value, 8);
            case "float32":
            {
                var number = float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
                if (!float.IsFinite(number)) throw new FormatException("float32 must be finite and within range.");
                var bytes = new byte[4];
                BinaryPrimitives.WriteSingleLittleEndian(bytes, number);
                return bytes;
            }
            case "float64":
            {
                var number = double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
                if (!double.IsFinite(number)) throw new FormatException("float64 must be finite and within range.");
                var bytes = new byte[8];
                BinaryPrimitives.WriteDoubleLittleEndian(bytes, number);
                return bytes;
            }
            case "utf8": return new UTF8Encoding(false, true).GetBytes(value + '\0');
            case "utf16": return new UnicodeEncoding(false, false, true).GetBytes(value + '\0');
            default: throw new FormatException($"Unsupported patch type: {type}.");
        }
    }

    private static byte[] EncodeInteger(string value, int size)
    {
        var text = value.AsSpan().Trim();
        var negative = text.StartsWith("-", StringComparison.Ordinal);
        if (negative || text.StartsWith("+", StringComparison.Ordinal)) text = text[1..];
        var hexadecimal = false;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            hexadecimal = true;
            text = text[2..];
        }
        else if (!text.IsEmpty && text[0] is '#' or '$')
        {
            hexadecimal = true;
            text = text[1..];
        }

        var number = ulong.Parse(text, hexadecimal ? NumberStyles.AllowHexSpecifier : NumberStyles.None, CultureInfo.InvariantCulture);
        if (negative)
        {
            if (number > (1UL << (size * 8 - 1))) throw new OverflowException("Signed patch value is outside its type's range.");
            number = unchecked(0UL - number);
        }
        else if (size < 8 && number >= (1UL << (size * 8)))
        {
            throw new OverflowException("Unsigned patch value is outside its type's range.");
        }

        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, number);
        return bytes[..size].ToArray();
    }
}
