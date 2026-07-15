// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.SourceGenerators;
using Xunit;

namespace SharpEmu.SourceGenerators.Tests;

public sealed class Ps5NidTests
{
    // Real pairs taken from [SysAbiExport] attributes in the emulator: the computation
    // must reproduce the catalog's NIDs exactly or every derived registration is wrong.
    [Theory]
    [InlineData("sceKernelWaitSema", "Zxa0VhQVTsk")]
    [InlineData("sceKernelSignalSema", "4czppHBiriw")]
    [InlineData("sceKernelCreateSema", "188x57JYp0g")]
    [InlineData("sceAudioOutOpen", "ekNvsT22rsY")]
    [InlineData("sceAudioOutOutput", "QOQtbeDqsT4")]
    [InlineData("memcpy", "Q3VBxCXhUHs")]
    [InlineData("memmove", "+P6FRGH4LfA")]
    public void ComputeMatchesKnownCatalogPairs(string exportName, string expectedNid) =>
        Assert.Equal(expectedNid, Ps5Nid.Compute(exportName));

    [Theory]
    [InlineData("Zxa0VhQVTsk", true)]
    [InlineData("+P6FRGH4LfA", true)]
    [InlineData("4R6-OvI2cEA", true)]
    [InlineData("Zxa0VhQVTs", false)]   // ten characters
    [InlineData("Zxa0VhQVTskk", false)] // twelve characters
    [InlineData("Zxa0VhQVTs=", false)]  // padding character
    [InlineData("Zxa0VhQVT/k", false)]  // '/' is remapped to '-' in this alphabet
    public void IsValidFormatChecksLengthAndAlphabet(string nid, bool expected) =>
        Assert.Equal(expected, Ps5Nid.IsValidFormat(nid));
}
