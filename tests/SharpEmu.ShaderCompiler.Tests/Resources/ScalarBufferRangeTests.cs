// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

public sealed class ScalarBufferRangeTests
{
    private const ulong Base = 0x200A031F00ul;

    private static RuntimeValueEvaluator.RawAddress Resolve(ulong records, uint offset, out ulong address) =>
        RuntimeValueEvaluator.ResolveRawAddress(ScalarValueKind.ScalarBufferWord, Base, 0, records, 0, offset, out address);

    [Theory]
    [InlineData(440u)]
    [InlineData(472u)]
    [InlineData(1u << 20)]
    public void DwordPastBoundBufferIsZero(uint offset)
    {
        Assert.Equal(RuntimeValueEvaluator.RawAddress.Zero, Resolve(256, offset, out _));
    }

    [Fact]
    public void LoadStraddlingEndReadsInRangeDwordsAndZeroesTheRest()
    {
        // 446-byte buffer: the dword at 440 is whole; the one at 444 is cut by the end.
        const ulong size = 446;
        Assert.Equal(RuntimeValueEvaluator.RawAddress.Read, Resolve(size, 440, out var address));
        Assert.Equal(Base + 440, address);
        // A dword only partly inside the range is out of range.
        Assert.Equal(RuntimeValueEvaluator.RawAddress.Zero, Resolve(size, 444, out _));
        Assert.Equal(RuntimeValueEvaluator.RawAddress.Zero, Resolve(size, 448, out _));
    }

    [Fact]
    public void EmptyBufferStillReadsZero()
    {
        Assert.Equal(RuntimeValueEvaluator.RawAddress.Zero, Resolve(0, 0, out _));
    }
}
