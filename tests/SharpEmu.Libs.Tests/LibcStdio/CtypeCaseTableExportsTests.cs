// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.HLE;
using SharpEmu.Libs.LibcStdio;
using Xunit;

namespace SharpEmu.Libs.Tests.LibcStdio;

public sealed class CtypeCaseTableExportsTests
{
    [Fact]
    public void GetPtolower_TableMapsUppercaseToLowercaseAndLeavesOthersUnchanged()
    {
        var context = new CpuContext(new EmptyCpuMemory(), Generation.Gen5);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, LibcStdioExports.GetPtolower(context));
        var table = unchecked((nint)context[CpuRegister.Rax]);

        Assert.Equal((int)'a', ReadEntry(table, 'A'));
        Assert.Equal((int)'z', ReadEntry(table, 'Z'));
        Assert.Equal((int)'a', ReadEntry(table, 'a')); // already lowercase: unchanged
        Assert.Equal((int)'5', ReadEntry(table, '5')); // non-letter: unchanged
        Assert.Equal(-1, ReadEntry(table, -1)); // in-range per Dinkumware bounds, not a letter: unchanged
    }

    [Fact]
    public void GetPtoupper_TableMapsLowercaseToUppercaseAndLeavesOthersUnchanged()
    {
        var context = new CpuContext(new EmptyCpuMemory(), Generation.Gen5);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, LibcStdioExports.GetPtoupper(context));
        var table = unchecked((nint)context[CpuRegister.Rax]);

        Assert.Equal((int)'A', ReadEntry(table, 'a'));
        Assert.Equal((int)'Z', ReadEntry(table, 'z'));
        Assert.Equal((int)'A', ReadEntry(table, 'A')); // already uppercase: unchanged
        Assert.Equal((int)'5', ReadEntry(table, '5')); // non-letter: unchanged
    }

    [Fact]
    public void GetPtolower_SecondCall_ReturnsSameCachedTable()
    {
        var context = new CpuContext(new EmptyCpuMemory(), Generation.Gen5);

        LibcStdioExports.GetPtolower(context);
        var first = context[CpuRegister.Rax];
        LibcStdioExports.GetPtolower(context);
        var second = context[CpuRegister.Rax];

        Assert.Equal(first, second);
    }

    private static int ReadEntry(nint table, int character) =>
        Marshal.ReadInt16(table, character * sizeof(short));

    private sealed class EmptyCpuMemory : ICpuMemory
    {
        public bool TryRead(ulong virtualAddress, Span<byte> destination) => false;

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source) => false;
    }
}
