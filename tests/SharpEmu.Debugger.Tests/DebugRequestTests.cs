// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Debugger.Protocol;
using Xunit;

namespace SharpEmu.Debugger.Tests;

public sealed class DebugRequestTests
{
    [Fact]
    public void TryParse_AcceptsValidCommandAndNormalizesCase()
    {
        Assert.True(DebugRequest.TryParse("""{"command":"Ping"}""", out var request, out var error));
        Assert.Equal(string.Empty, error);
        Assert.Equal("ping", request.Command);
    }

    [Fact]
    public void TryParse_ReadsNumericAndHexAddresses()
    {
        Assert.True(DebugRequest.TryParse(
            """{"command":"read-memory","address":"0x1000","length":16}""",
            out var request,
            out _));

        Assert.True(request.TryGetUInt64("address", out var address));
        Assert.Equal(0x1000UL, address);
        Assert.True(request.TryGetInt32("length", out var length));
        Assert.Equal(16, length);
    }

    [Fact]
    public void TryParse_ReadsHexLengthStrings()
    {
        Assert.True(DebugRequest.TryParse(
            """{"command":"read-memory","address":4096,"length":"0x20"}""",
            out var request,
            out _));

        Assert.True(request.TryGetUInt64("address", out var address));
        Assert.Equal(4096UL, address);
        Assert.True(request.TryGetInt32("length", out var length));
        Assert.Equal(0x20, length);
    }

    [Fact]
    public void TryParse_RejectsMissingCommand()
    {
        Assert.False(DebugRequest.TryParse("""{"address":1}""", out _, out var error));
        Assert.Contains("command", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParse_RejectsNonObjectRoot()
    {
        Assert.False(DebugRequest.TryParse("""["ping"]""", out _, out var error));
        Assert.Contains("object", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParse_RejectsMalformedJson()
    {
        Assert.False(DebugRequest.TryParse("""{command:""", out _, out var error));
        Assert.Contains("Malformed JSON", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TryGetBool_ReadsJsonBooleans()
    {
        Assert.True(DebugRequest.TryParse(
            """{"command":"enable-breakpoint","id":1,"enabled":false}""",
            out var request,
            out _));

        Assert.True(request.TryGetBool("enabled", out var enabled));
        Assert.False(enabled);
        Assert.False(request.TryGetBool("missing", out _));
    }
}
