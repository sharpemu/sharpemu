// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using SharpEmu.Core.Loader;
using Xunit;

namespace SharpEmu.Libs.Tests.Loader;

public sealed class Ps5ParamJsonReaderTests
{
    [Fact]
    public void ReadsTheConceptIdSharedByRegionalReleases()
    {
        var json = """
            { "titleId": "PPSA04263", "conceptId": "201930", "contentVersion": "01.000.000",
              "localizedParameters": { "defaultLanguage": "en-US", "en-US": { "titleName": "Test" } } }
            """;
        var info = Ps5ParamJsonReader.TryReadPs5Param(Encoding.UTF8.GetBytes(json));
        Assert.Equal("PPSA04263", info.TitleId);
        Assert.Equal("201930", info.ConceptId);
        Assert.Equal("Test", info.Title);
        Assert.Equal("01.000.000", info.Version);
    }

    [Fact]
    public void ConceptIdIsNullWhenParamJsonHasNone()
    {
        var info = Ps5ParamJsonReader.TryReadPs5Param(Encoding.UTF8.GetBytes("""{ "titleId": "PPSA00001" }"""));
        Assert.Equal("PPSA00001", info.TitleId);
        Assert.Null(info.ConceptId);
    }
}
