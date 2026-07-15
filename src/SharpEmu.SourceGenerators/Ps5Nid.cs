// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Security.Cryptography;
using System.Text;

namespace SharpEmu.SourceGenerators;

/// <summary>
/// The PS NID derivation: base64 (with the '+','-' alphabet, no padding) of the
/// byte-reversed first eight bytes of SHA1(symbolName + fixed suffix). The same
/// algorithm scripts/generate_aerolib_binary.py uses to build the runtime catalog,
/// in C# so the analyzer and generator can validate and derive NIDs at compile time.
/// </summary>
public static class Ps5Nid
{
    private static readonly byte[] Suffix =
    [
        0x51, 0x8D, 0x64, 0xA6, 0x35, 0xDE, 0xD8, 0xC1,
        0xE6, 0xB0, 0x39, 0xB1, 0xC3, 0xE5, 0x52, 0x30,
    ];

    public static string Compute(string symbolName)
    {
        using var sha1 = SHA1.Create();
        return Compute(symbolName, sha1);
    }

    /// <summary>
    /// Bulk-callable overload: the aerolib build task hashes every catalog name
    /// (~150k), so the caller owns one SHA1 instance instead of churning one per name.
    /// </summary>
    public static string Compute(string symbolName, SHA1 sha1)
    {
        var nameBytes = Encoding.UTF8.GetBytes(symbolName);
        var input = new byte[nameBytes.Length + Suffix.Length];
        nameBytes.CopyTo(input, 0);
        Suffix.CopyTo(input, nameBytes.Length);

        var hash = sha1.ComputeHash(input);

        // The script reads the first eight bytes as a little-endian integer and
        // formats it big-endian before encoding — a byte reversal.
        var reversed = new byte[8];
        for (var index = 0; index < 8; index++)
        {
            reversed[index] = hash[7 - index];
        }

        return Convert.ToBase64String(reversed)
            .TrimEnd('=')
            .Replace('/', '-');
    }

    /// <summary>Eleven characters of the '+','-' base64 alphabet.</summary>
    public static bool IsValidFormat(string nid)
    {
        if (nid.Length != 11)
        {
            return false;
        }

        foreach (var character in nid)
        {
            var valid = character is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or
                (>= '0' and <= '9') or '+' or '-';
            if (!valid)
            {
                return false;
            }
        }

        return true;
    }
}
