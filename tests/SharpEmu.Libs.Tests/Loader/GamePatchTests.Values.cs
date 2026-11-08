// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using System.Xml.Linq;
using Xunit;

namespace SharpEmu.Libs.Tests.Loader;

public sealed partial class GamePatchTests
{
    [Theory]
    [InlineData("byte", "255", "ff")]
    [InlineData("byte", "0x80", "80")]
    [InlineData("byte", "#12", "12")]
    [InlineData("byte", "$12", "12")]
    [InlineData("byte", "-128", "80")]
    [InlineData("bytes16", "4660", "3412")]
    [InlineData("bytes16", "$abcd", "cdab")]
    [InlineData("bytes16", "-32768", "0080")]
    [InlineData("bytes16", "-1", "ffff")]
    [InlineData("bytes32", "0x12345678", "78563412")]
    [InlineData("bytes32", "4294967295", "ffffffff")]
    [InlineData("bytes32", "-2147483648", "00000080")]
    [InlineData("bytes64", "0x0123456789abcdef", "efcdab8967452301")]
    [InlineData("bytes64", "18446744073709551615", "ffffffffffffffff")]
    [InlineData("bytes64", "-9223372036854775808", "0000000000000080")]
    [InlineData("bytes64", "+#ff", "ff00000000000000")]
    [InlineData("float32", "1.5", "0000c03f")]
    [InlineData("float32", "-2.5", "000020c0")]
    [InlineData("float32", "-0", "00000080")]
    [InlineData("float64", "1.5", "000000000000f83f")]
    [InlineData("float64", "-2.5", "00000000000004c0")]
    [InlineData("utf8", "Hello", "48656c6c6f00")]
    [InlineData("utf8", "", "00")]
    [InlineData("utf8", "\u0130\u011f\u6c34\U0001f600", "c4b0c49fe6b0b4f09f988000")]
    [InlineData("utf16", "Hello", "480065006c006c006f000000")]
    [InlineData("utf16", "", "0000")]
    [InlineData("utf16", "\u0130\u011f\u6c34\U0001f600", "30011f01346c3dd800de0000")]
    public void TypedValuesAreEncodedAndAppliedInLittleEndian(string type, string value, string expectedHex)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            var line = new XElement("Line", new XAttribute("Type", type),
                new XAttribute("Address", "0x10000010"), new XAttribute("Value", value));
            var patch = WritePatch(line.ToString());
            Assert.Null(patch.Error);
            var expected = Convert.FromHexString(expectedHex);
            Assert.Equal(expected, Assert.Single(patch.Writes).Bytes);
            var memory = Memory();
            Assert.True(patch.TryApply(memory, [Segment()], 0x8_0000_0000, out var error), error);
            var actual = new byte[expected.Length];
            Assert.True(memory.TryRead(0x8_0000_0010, actual));
            Assert.Equal(expected, actual);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData("byte", "256")]
    [InlineData("byte", "-129")]
    [InlineData("byte", "0xfff")]
    [InlineData("bytes16", "65536")]
    [InlineData("bytes16", "-32769")]
    [InlineData("bytes32", "4294967296")]
    [InlineData("bytes64", "18446744073709551616")]
    [InlineData("bytes64", "-9223372036854775809")]
    [InlineData("bytes16", "12garbage")]
    [InlineData("bytes16", "0x")]
    [InlineData("bytes32", "1,000")]
    [InlineData("float32", "1,5")]
    [InlineData("float32", "1e100")]
    [InlineData("float64", "1e1000")]
    [InlineData("float32", "NaN")]
    [InlineData("float64", "Infinity")]
    [InlineData("utf7", "test")]
    public void InvalidTypedValuesInvalidateTheEntirePatch(string type, string value)
    {
        var invalid = new XElement("Line", new XAttribute("Type", type),
            new XAttribute("Address", "0x10000010"), new XAttribute("Value", value));
        var patch = WritePatch("<Line Type=\"bytes\" Address=\"0x10000020\" Value=\"90\"/>" + invalid);
        Assert.NotNull(patch.Error);
        Assert.Empty(patch.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReplacementByteLimitIncludesTheGeneratedBranchAndReturnJump(bool branch)
    {
        var payload = new string('0', 2 * (1 << 20));
        var line = branch
            ? new XElement("Line", new XAttribute("Type", "mask_jump32"), new XAttribute("Address", "aa bb"),
                new XAttribute("Target", "cc dd"), new XAttribute("Size", "5"), new XAttribute("Value", payload))
            : new XElement("Line", new XAttribute("Type", "bytes"), new XAttribute("Address", "0x10000000"),
                new XAttribute("Value", payload));
        var patch = WritePatch(line.ToString());
        Assert.Equal(branch, patch.Error is not null);
        if (branch) Assert.Empty(patch.Writes);
        else Assert.Equal(1 << 20, Assert.Single(patch.Writes).Bytes.Length);
    }

    [Theory]
    [InlineData(4096, true)]
    [InlineData(4097, false)]
    public void PatchLineLimitIsEnforced(int count, bool valid)
    {
        var patch = WritePatch(string.Concat(Enumerable.Repeat("<Line Type=\"byte\" Address=\"0x10000000\" Value=\"1\"/>", count)));
        Assert.Equal(valid, patch.Error is null);
        Assert.Equal(valid ? count : 0, patch.Writes.Count);
    }
}
