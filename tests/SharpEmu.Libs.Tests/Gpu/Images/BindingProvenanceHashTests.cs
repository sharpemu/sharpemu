// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Images;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Images;

public sealed class BindingProvenanceHashTests
{
    [Theory]
    [InlineData("", 0xcbf29ce484222325UL)]
    [InlineData("a", 0xaf63dc4c8601ec8cUL)]
    [InlineData("hello", 0xa430d84680aabd0bUL)]
    public void UsesStableByteHashes(string text, ulong expected)
    {
        Assert.Equal(expected, GuestImageCache.HashBytes(System.Text.Encoding.ASCII.GetBytes(text)));
    }
}
