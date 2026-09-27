// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

/// <summary>
/// The guest names files with FreeBSD rules; Windows silently rewrites the ones
/// it cannot hold, so a save is written under a name the reopen never finds.
/// These pin the encoding that makes the mapping total and reversible. The
/// <c>WindowsSegment</c> entry points skip the host check so the mapping is
/// exercised on every CI platform, not just Windows.
/// </summary>
public sealed class HostFsNameTests
{
    [Theory]
    // Ordinary names are their own encoding — the common case must not churn.
    [InlineData("SAVE0000", "SAVE0000")]
    [InlineData("param.json", "param.json")]
    // The reported failure: a ':' makes Windows write an alternate data stream
    // on a truncated file rather than the file the guest asked for.
    [InlineData(
        "rg_ac_Arcade Spirits: The New Challengers_0.dat",
        "rg_ac_Arcade Spirits%3A The New Challengers_0.dat")]
    [InlineData("a*b", "a%2Ab")]
    [InlineData("a?b", "a%3Fb")]
    [InlineData("a\"b", "a%22b")]
    [InlineData("a<b>c", "a%3Cb%3Ec")]
    [InlineData("a|b", "a%7Cb")]
    [InlineData("tab\there", "tab%09here")]
    // Trailing dots and spaces are stripped by CreateFile, which is the same
    // silent-rename failure by another route.
    [InlineData("save.", "save%2E")]
    [InlineData("save ", "save%20")]
    [InlineData("save. .", "save%2E%20%2E")]
    // A DOS device stem opens the device whatever the extension, so break the
    // match on the first letter.
    [InlineData("nul", "%6Eul")]
    [InlineData("AUX.dat", "%41UX.dat")]
    [InlineData("com1.sav", "%63om1.sav")]
    [InlineData("lpt9", "%6Cpt9")]
    // ...but only for the real device names.
    [InlineData("con.dat", "%63on.dat")]
    [InlineData("conf.dat", "conf.dat")]
    [InlineData("com.dat", "com.dat")]
    [InlineData("comA", "comA")]
    // A literal '%' is escaped only when it would otherwise read back as an
    // escape, so real dump filenames containing one map to themselves.
    [InlineData("100% Complete.sav", "100% Complete.sav")]
    [InlineData("50%", "50%")]
    [InlineData("a%3Ab", "a%253Ab")]
    [InlineData("a%zzb", "a%zzb")]
    public void EncodeRewritesOnlyWhatWindowsCannotHold(string guestName, string expectedHostName)
    {
        Assert.Equal(expectedHostName, HostFsName.EncodeWindowsSegment(guestName));
        Assert.Equal(guestName, HostFsName.DecodeWindowsSegment(expectedHostName));
    }

    [Fact]
    public void EncodedNamesAreValidWindowsFilenames()
    {
        // Path.GetInvalidFileNameChars() is the host's own answer on Windows and
        // the superset (POSIX reports only '/' and NUL) elsewhere, so assert
        // against the Windows set directly to keep the check meaningful on CI.
        const string windowsInvalid = "\"<>|:*?\\/";
        foreach (var guestName in Corpus())
        {
            var encoded = HostFsName.EncodeWindowsSegment(guestName);
            foreach (var ch in encoded)
            {
                Assert.False(windowsInvalid.Contains(ch) || ch < ' ', encoded);
            }

            Assert.False(encoded.EndsWith('.') || encoded.EndsWith(' '), encoded);

            var stem = encoded.Split('.', 2)[0];
            Assert.DoesNotContain(
                stem,
                new[] { "CON", "PRN", "AUX", "NUL" },
                StringComparer.OrdinalIgnoreCase);
            Assert.False(
                stem.Length == 4 &&
                stem[3] is >= '0' and <= '9' &&
                (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                 stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)),
                encoded);
        }
    }

    [Fact]
    public void EncodeRoundTripsAndStaysOneToOne()
    {
        // Injectivity is the whole reason for percent-encoding over replacing
        // with '_': "foo:bar" and "foo_bar" are different saves. A successful
        // round trip for every name proves no two of them collide.
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var guestName in Corpus())
        {
            var encoded = HostFsName.EncodeWindowsSegment(guestName);
            Assert.Equal(guestName, HostFsName.DecodeWindowsSegment(encoded));

            Assert.False(
                seen.TryGetValue(encoded, out var previous),
                $"'{guestName}' and '{previous}' both encode to '{encoded}'");
            seen[encoded] = guestName;
        }
    }

    [Fact]
    public void HostGatedEntryPointsAreIdentityOffWindows()
    {
        const string guestName = "rg_ac_Arcade Spirits: The New Challengers_0.dat";
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(HostFsName.EncodeWindowsSegment(guestName), HostFsName.EncodeSegment(guestName));
            return;
        }

        // POSIX hosts hold the guest name as-is, and encoding there would break
        // dumps that legitimately use these characters.
        Assert.Equal(guestName, HostFsName.EncodeSegment(guestName));
        Assert.Equal(guestName, HostFsName.DecodeSegment(guestName));
        Assert.Equal("a%3Ab", HostFsName.DecodeSegment("a%3Ab"));
    }

    // Names built from the awkward characters in every position, plus the
    // literals the theory above calls out by hand.
    private static IEnumerable<string> Corpus()
    {
        string[] pieces = ["a", ":", "%", "%25", "%3A", ".", " ", "*", "", "|", "nul", "1"];
        foreach (var first in pieces)
        {
            foreach (var second in pieces)
            {
                foreach (var third in pieces)
                {
                    var candidate = string.Concat(first, second, third);
                    // "", "." and ".." never reach the encoder: empty segments are
                    // dropped by the split and the dot segments are resolved as
                    // directory navigation first.
                    if (candidate is "" or "." or "..")
                    {
                        continue;
                    }

                    yield return candidate;
                }
            }
        }

        yield return "SAVE0000";
        yield return "rg_ac_Arcade Spirits: The New Challengers_0.dat";
        yield return "100% Complete.sav";
        yield return new string('.', 8);
        yield return new StringBuilder().Append('x', 200).Append(':').ToString();
    }
}
