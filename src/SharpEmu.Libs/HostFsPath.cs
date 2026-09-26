// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using System.Text;

namespace SharpEmu.Libs;

/// <summary>
/// Key equivalence for caches and comparisons over <em>host</em> filesystem
/// paths. Windows resolves names case-insensitively, but Linux hosts are
/// case-sensitive and the guest filesystem is too, so a dump can legitimately
/// contain "DATA.BIN" alongside "Data.bin". An ignore-case cache aliases those
/// distinct files into one entry there, which silently serves the wrong bytes
/// or drops one of them entirely.
/// </summary>
internal static class HostFsPath
{
    public static readonly StringComparer Comparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static readonly StringComparison Comparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// Maps a guest path segment onto a Windows-safe host segment. FreeBSD-style
    /// guest names may contain characters that Windows rejects or truncates
    /// (notably ':'), which previously cut save files short and broke reloads.
    /// Percent-encoding is invertible and collision-free, unlike replacing with '_'.
    /// No-op on non-Windows hosts where those characters are legal.
    /// </summary>
    public static string EncodeHostPathSegment(string segment)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(segment))
        {
            return segment;
        }

        var needsEncoding = false;
        foreach (var ch in segment)
        {
            if (NeedsWindowsEncoding(ch))
            {
                needsEncoding = true;
                break;
            }
        }

        if (!needsEncoding)
        {
            return segment;
        }

        var sb = new StringBuilder(segment.Length + 8);
        foreach (var ch in segment)
        {
            if (NeedsWindowsEncoding(ch))
            {
                sb.Append('%');
                sb.Append(((int)ch).ToString("X2", CultureInfo.InvariantCulture));
            }
            else
            {
                sb.Append(ch);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Inverse of <see cref="EncodeHostPathSegment"/> for directory listings
    /// returned to the guest. Harmless on already-decoded names.
    /// </summary>
    public static string DecodeHostPathSegment(string segment)
    {
        if (string.IsNullOrEmpty(segment) || segment.IndexOf('%') < 0)
        {
            return segment;
        }

        var sb = new StringBuilder(segment.Length);
        for (var i = 0; i < segment.Length; i++)
        {
            if (segment[i] == '%' &&
                i + 2 < segment.Length &&
                IsHex(segment[i + 1]) &&
                IsHex(segment[i + 2]))
            {
                var value = (FromHex(segment[i + 1]) << 4) | FromHex(segment[i + 2]);
                sb.Append((char)value);
                i += 2;
                continue;
            }

            sb.Append(segment[i]);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Percent-encodes characters that are illegal in a host filename on the
    /// current OS. Used for SharpEmu-owned save-slot names so ':' and similar
    /// stay round-trippable without colliding with '_' replacements.
    /// </summary>
    public static string EncodeInvalidFileNameChars(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var invalid = Path.GetInvalidFileNameChars();
        var needsEncoding = false;
        foreach (var ch in value)
        {
            if (ch == '%' || Array.IndexOf(invalid, ch) >= 0)
            {
                needsEncoding = true;
                break;
            }
        }

        if (!needsEncoding)
        {
            return value;
        }

        var sb = new StringBuilder(value.Length + 8);
        foreach (var ch in value)
        {
            if (ch == '%' || Array.IndexOf(invalid, ch) >= 0)
            {
                sb.Append('%');
                sb.Append(((int)ch).ToString("X2", CultureInfo.InvariantCulture));
            }
            else
            {
                sb.Append(ch);
            }
        }

        return sb.ToString();
    }

    private static bool NeedsWindowsEncoding(char ch) =>
        ch is '%' or ':' or '*' or '?' or '"' or '<' or '>' or '|' or '/' or '\\' ||
        char.IsControl(ch);

    private static bool IsHex(char ch) =>
        ch is >= '0' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f';

    private static int FromHex(char ch) => ch switch
    {
        >= '0' and <= '9' => ch - '0',
        >= 'A' and <= 'F' => ch - 'A' + 10,
        >= 'a' and <= 'f' => ch - 'a' + 10,
        _ => 0,
    };
}
