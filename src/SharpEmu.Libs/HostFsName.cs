// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;

namespace SharpEmu.Libs;

/// <summary>
/// Reversible mapping of a single <em>guest</em> path segment to a name the host
/// filesystem can actually hold, and back.
/// <para>
/// The guest filesystem is FreeBSD-flavoured: every byte except <c>/</c> and NUL
/// is a legal filename character. Windows is far narrower, and its failure mode
/// is silent rather than loud — <c>rg_ac_Arcade Spirits: The New Challengers.dat</c>
/// does not fail to open, it opens the alternate data stream
/// <c>" The New Challengers.dat"</c> on a file called <c>rg_ac_Arcade Spirits</c>.
/// The title writes its save, sees success, and finds nothing on the next boot;
/// the reopen faults instead of returning ENOENT. Trailing dots and spaces are
/// silently trimmed the same way, and a name whose stem is a DOS device
/// (<c>nul</c>, <c>aux</c>, <c>com1</c>...) opens the device rather than a file.
/// </para>
/// <para>
/// Percent-encoding the offending characters keeps the mapping one-to-one, which
/// replacing them with <c>_</c> would not: <c>foo:bar</c> and <c>foo_bar</c> are
/// distinct guest names that must stay distinct on the host.
/// </para>
/// <para>
/// The cost of that is one ambiguity, and it is deliberately kept as small as
/// possible: a literal <c>%</c> is escaped only when two hex digits follow it,
/// so the one class of host name this cannot represent is a pre-existing file
/// that already looks like an escape (<c>Assets%20Big.pak</c> in a dump, say).
/// Every other name containing a percent sign maps to itself. Escaping <c>%</c>
/// unconditionally would trade that rare case for a common one; not escaping it
/// at all would let <c>foo%3Abar</c> and <c>foo:bar</c> collide.
/// </para>
/// <para>
/// Only Windows needs this; Linux and macOS hosts accept the guest names as-is
/// and encoding there would break dumps that legitimately use these characters.
/// Both directions are therefore the identity outside Windows, which also means
/// a save directory is not portable between a Windows host and a POSIX one —
/// that was already true, since the Windows copy never held the real name.
/// </para>
/// </summary>
internal static class HostFsName
{
    // Everything Path.GetInvalidFileNameChars() reports on Windows except the
    // separators, which callers have already consumed by splitting the path, and
    // the control characters handled below. Hard-coded rather than queried so a
    // guest name encodes identically no matter which host produced it.
    private const string AlwaysEscaped = "\"*:<>?|";

    private const string HexDigits = "0123456789ABCDEF";

    /// <summary>
    /// Encodes one guest path segment for use as a host filename. Returns
    /// <paramref name="segment"/> unchanged on non-Windows hosts, and for the
    /// overwhelming majority of names on Windows too.
    /// </summary>
    public static string EncodeSegment(string segment) =>
        OperatingSystem.IsWindows() ? EncodeWindowsSegment(segment) : segment;

    /// <summary>
    /// Recovers the guest name from a host filename produced by
    /// <see cref="EncodeSegment"/>. Used when handing host directory entries back
    /// to the guest, so a name the guest wrote survives a round trip through
    /// <c>getdents</c> and the <c>open</c> that follows it.
    /// </summary>
    public static string DecodeSegment(string segment) =>
        OperatingSystem.IsWindows() ? DecodeWindowsSegment(segment) : segment;

    /// <summary>
    /// The encoding itself, with the host check lifted out so the mapping can be
    /// tested — and reasoned about — on any platform.
    /// </summary>
    public static string EncodeWindowsSegment(string segment)
    {
        if (!NeedsEncoding(segment))
        {
            return segment;
        }

        // A name whose stem is a DOS device is unusable whatever its extension
        // ("nul.dat" is still the null device), and the stem is not made of
        // escapable characters. Escaping its first letter is enough to stop the
        // match and stays reversible.
        var escapeFirstChar = HasReservedDeviceStem(segment);
        var trailingRunStart = TrailingDotSpaceRunStart(segment);

        var builder = new StringBuilder(segment.Length + 8);
        for (var i = 0; i < segment.Length; i++)
        {
            var ch = segment[i];
            if (i >= trailingRunStart ||
                (i == 0 && escapeFirstChar) ||
                MustEscape(segment, i))
            {
                // Every escapable character is ASCII — the invalid set, the
                // control range, '.', ' ', '%' and the letter starting a device
                // stem — so two hex digits always suffice. Non-ASCII characters
                // are legal on both filesystems and pass through untouched.
                builder.Append('%').Append(HexDigits[(ch >> 4) & 0xF]).Append(HexDigits[ch & 0xF]);
                continue;
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }

    /// <summary>
    /// The inverse of <see cref="EncodeWindowsSegment"/>, likewise unconditional.
    /// </summary>
    public static string DecodeWindowsSegment(string segment)
    {
        if (!segment.Contains('%', StringComparison.Ordinal))
        {
            return segment;
        }

        var builder = new StringBuilder(segment.Length);
        for (var i = 0; i < segment.Length; i++)
        {
            if (segment[i] == '%' && IsEscapeSequenceAt(segment, i))
            {
                builder.Append((char)((HexValue(segment[i + 1]) << 4) | HexValue(segment[i + 2])));
                i += 2;
                continue;
            }

            builder.Append(segment[i]);
        }

        return builder.ToString();
    }

    private static bool NeedsEncoding(string segment)
    {
        if (segment.Length == 0)
        {
            return false;
        }

        if (TrailingDotSpaceRunStart(segment) < segment.Length || HasReservedDeviceStem(segment))
        {
            return true;
        }

        for (var i = 0; i < segment.Length; i++)
        {
            if (MustEscape(segment, i))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MustEscape(string segment, int index)
    {
        var ch = segment[index];
        return AlwaysEscaped.Contains(ch, StringComparison.Ordinal) ||
               ch < ' ' ||
               (ch == '%' && IsEscapeSequenceAt(segment, index));
    }

    // A literal '%' only has to be escaped when it would otherwise read back as
    // an escape, i.e. when two hex digits follow it. Leaving the rest alone keeps
    // names like "100% Complete.sav" — and every dump file containing a percent
    // sign — mapping to themselves.
    private static bool IsEscapeSequenceAt(string segment, int index) =>
        index + 2 < segment.Length &&
        IsHexDigit(segment[index + 1]) &&
        IsHexDigit(segment[index + 2]);

    private static bool IsHexDigit(char ch) =>
        ch is (>= '0' and <= '9') or (>= 'A' and <= 'F') or (>= 'a' and <= 'f');

    private static int HexValue(char ch) =>
        ch <= '9' ? ch - '0' : (char.ToUpperInvariant(ch) - 'A') + 10;

    // Index at which the trailing run of '.' / ' ' that CreateFile would strip
    // begins, or the segment length when there is none. "." and ".." never reach
    // here: callers resolve them as directory navigation before splitting.
    private static int TrailingDotSpaceRunStart(string segment)
    {
        var start = segment.Length;
        while (start > 0 && (segment[start - 1] == '.' || segment[start - 1] == ' '))
        {
            start--;
        }

        return start;
    }

    // Whether the part before the first '.' names a DOS device, matched
    // case-insensitively as Windows does.
    private static bool HasReservedDeviceStem(string segment)
    {
        var stemLength = segment.IndexOf('.');
        var stem = stemLength < 0 ? segment.AsSpan() : segment.AsSpan(0, stemLength);
        if (stem.Length is not (3 or 4))
        {
            return false;
        }

        var prefix = stem[..3];
        if (stem.Length == 3)
        {
            return prefix.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
                   prefix.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                   prefix.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
                   prefix.Equals("NUL", StringComparison.OrdinalIgnoreCase);
        }

        return stem[3] is >= '0' and <= '9' &&
               (prefix.Equals("COM", StringComparison.OrdinalIgnoreCase) ||
                prefix.Equals("LPT", StringComparison.OrdinalIgnoreCase));
    }
}
