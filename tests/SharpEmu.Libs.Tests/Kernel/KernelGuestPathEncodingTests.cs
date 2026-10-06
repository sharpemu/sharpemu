// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

// #683: the guest filesystem has FreeBSD semantics, so a title may write a name
// containing characters Windows rejects outright ("rg_ac_Arcade Spirits: The New
// Challengers_0.dat"). Translating it verbatim truncated the save at the ':',
// so the title reopened a file it had just written and faulted.
//
// The character mapping itself is platform-independent, so it is tested
// directly. Only the platform gate and the end-to-end guest->host resolution are
// host-conditional, mirroring how KernelPathCaseSensitivityTests self-skips.
public sealed class KernelGuestPathEncodingTests
{
    [Theory]
    [InlineData(":", "%3A")]
    [InlineData("*", "%2A")]
    [InlineData("?", "%3F")]
    [InlineData("\"", "%22")]
    [InlineData("<", "%3C")]
    [InlineData(">", "%3E")]
    [InlineData("|", "%7C")]
    [InlineData("%", "%25")]
    [InlineData("/", "%2F")]
    [InlineData("\\", "%5C")]
    [InlineData("\t", "%09")]
    [InlineData("\n", "%0A")]
    public void WindowsInvalidCharacters_ArePercentEncoded(string guest, string expected)
    {
        Assert.Equal(expected, KernelMemoryCompatExports.EncodeWindowsPathSegment(guest));
    }

    [Fact]
    public void ValidName_IsLeftUnchangedAndDoesNotAllocate()
    {
        const string name = "shader_Base2d-0123456789.bin";

        Assert.Same(name, KernelMemoryCompatExports.EncodeWindowsPathSegment(name));
    }

    [Fact]
    public void RealSaveName_IsEncodedWithoutTruncation()
    {
        const string name = "rg_ac_Arcade Spirits: The New Challengers_0.dat";

        var encoded = KernelMemoryCompatExports.EncodeWindowsPathSegment(name);

        // The whole name survives - the old behaviour dropped everything after
        // the ':', leaving "rg_ac_Arcade Spirits".
        Assert.Equal("rg_ac_Arcade Spirits%3A The New Challengers_0.dat", encoded);
        Assert.DoesNotContain(':', encoded);
    }

    [Fact]
    public void EncodingIsInjective_SoDistinctGuestNamesNeverCollide()
    {
        // Substituting '_' instead would merge these two distinct save names.
        var colon = KernelMemoryCompatExports.EncodeWindowsPathSegment("foo:bar");
        var underscore = KernelMemoryCompatExports.EncodeWindowsPathSegment("foo_bar");

        Assert.NotEqual(colon, underscore);
    }

    [Fact]
    public void EncodingIsInjective_AnEncodedLookingNameCannotForgeAnother()
    {
        // "foo%3Abar" must not decode back to "foo:bar"'s host name.
        var literal = KernelMemoryCompatExports.EncodeWindowsPathSegment("foo%3Abar");
        var colon = KernelMemoryCompatExports.EncodeWindowsPathSegment("foo:bar");

        Assert.NotEqual(literal, colon);
    }

    [Fact]
    public void HostGate_AppliesOnlyOnWindows()
    {
        const string name = "Arcade Spirits: The New Challengers.dat";

        if (OperatingSystem.IsWindows())
        {
            Assert.Contains("%3A", KernelMemoryCompatExports.EncodeHostPathSegment(name));
        }
        else
        {
            // Linux and macOS accept these characters natively; encoding there
            // would needlessly rename every existing user's saves.
            Assert.Same(name, KernelMemoryCompatExports.EncodeHostPathSegment(name));
        }
    }
}
