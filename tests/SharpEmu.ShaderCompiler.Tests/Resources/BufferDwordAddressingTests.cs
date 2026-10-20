// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

// A buffer is dword addressed when some access indexes the bound words directly; only the
// others may be bound at a base inside a dword.
public sealed class BufferDwordAddressingTests
{
    private static bool DwordAddressed(params Gen5ShaderInstruction[] accesses) =>
        Assert.Single(Extract(Program([.. accesses, EndProgram(accesses[^1].Pc + 8)])).Info.Buffers).DwordAddressed;

    [Theory]
    [InlineData("BufferLoadDword")]
    [InlineData("BufferLoadDwordx4")]
    [InlineData("BufferStoreDword")]
    [InlineData("BufferStoreDwordx2")]
    public void RawDwordAccessesAreDwordAddressed(string opcode) =>
        Assert.True(DwordAddressed(BufferAccess(4, opcode, 0, dwords: opcode.EndsWith("x4") ? 4u : opcode.EndsWith("x2") ? 2u : 1u)));

    [Theory]
    [InlineData("BufferLoadUbyte")]
    [InlineData("BufferLoadSbyte")]
    [InlineData("BufferLoadUshort")]
    [InlineData("BufferLoadSshort")]
    [InlineData("BufferStoreByte")]
    [InlineData("BufferStoreShort")]
    public void SubwordAccessesAreByteAddressed(string opcode) =>
        Assert.False(DwordAddressed(BufferAccess(4, opcode, 0)));

    [Fact]
    public void FormattedAccessesAreByteAddressed()
    {
        Assert.False(DwordAddressed(BufferLoad(4, 0, formatted: true, dwords: 4), BufferStore(12, 0, offset: 16, formatted: true, dwords: 4)));
    }

    [Fact]
    public void TypedAccessesAreByteAddressed()
    {
        var typed = BufferAccess(4, "TBufferLoadFormatXyzw", 0, dwords: 4);
        typed = typed with { Control = Assert.IsType<Gen5BufferMemoryControl>(typed.Control) with { Typed = true, TypedFormat = 60 } };
        Assert.False(DwordAddressed(typed));
    }

    [Theory]
    [InlineData("TBufferStoreFormatXyzw", 77u, 4u, true)]
    [InlineData("TBufferStoreFormatXy", 14u, 2u, false)]
    [InlineData("TBufferLoadFormatXyzw", 77u, 4u, false)]
    public void TypedStoresOfWholeDwordFormatsAreDwordAddressed(string opcode, uint format, uint dwords, bool expected)
    {
        var typed = BufferAccess(4, opcode, 0, dwords: dwords);
        typed = typed with { Control = Assert.IsType<Gen5BufferMemoryControl>(typed.Control) with { Typed = true, TypedFormat = format } };
        Assert.Equal(expected, DwordAddressed(typed));
    }

    [Fact]
    public void AtomicsAreDwordAddressed() =>
        Assert.True(DwordAddressed(BufferAtomicAdd(4, 0)));

    [Fact]
    public void ScalarBufferLoadsAreDwordAddressed() =>
        Assert.True(DwordAddressed(ScalarBufferLoad(4, 0, destination: 8)));

    [Fact]
    public void OneDwordAccessMakesTheWholeBufferDwordAddressed() =>
        Assert.True(DwordAddressed(BufferAccess(4, "BufferLoadUbyte", 0), BufferLoad(12, 0)));

    [Fact]
    public void BuffersAreClassifiedIndependently()
    {
        var plan = Extract(Program(
            BufferAccess(4, "BufferStoreByte", 0),
            BufferLoad(12, 4),
            EndProgram(20)));
        Assert.Equal(2, plan.Info.Buffers.Count);
        Assert.False(plan.Info.Buffers[0].DwordAddressed);
        Assert.True(plan.Info.Buffers[1].DwordAddressed);
    }
}
