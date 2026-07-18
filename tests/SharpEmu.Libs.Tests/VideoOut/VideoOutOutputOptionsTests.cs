// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VideoOutOutputOptionsTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong OptionsAddress = MemoryBase + 0x40;
    private const int OptionsSize = 0x40;
    private const int InvalidAddress = unchecked((int)0x80290002);

    [Theory]
    [InlineData(Generation.Gen4)]
    [InlineData(Generation.Gen5)]
    public void InitializeOutputOptions_ClearsExactlyOneStructure(Generation generation)
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, generation);
        var sentinel = new byte[OptionsSize + 1];
        Array.Fill(sentinel, (byte)0xCC);
        Assert.True(memory.TryWrite(OptionsAddress, sentinel));
        context[CpuRegister.Rdi] = OptionsAddress;

        Assert.Equal(0, VideoOutExports.VideoOutInitializeOutputOptions(context));

        var result = new byte[OptionsSize + 1];
        Assert.True(memory.TryRead(OptionsAddress, result));
        Assert.All(result.AsSpan(0, OptionsSize).ToArray(), value => Assert.Equal(0, value));
        Assert.Equal(0xCC, result[OptionsSize]);
    }

    [Fact]
    public void InitializeOutputOptions_NullAddressReturnsInvalidAddress()
    {
        var context = new CpuContext(new FakeCpuMemory(MemoryBase, 0x100), Generation.Gen5);
        context[CpuRegister.Rdi] = 0;

        Assert.Equal(InvalidAddress, VideoOutExports.VideoOutInitializeOutputOptions(context));
    }

    [Fact]
    public void InitializeOutputOptions_UnwritableStructureReturnsMemoryFault()
    {
        var context = new CpuContext(new FakeCpuMemory(MemoryBase, 0x100), Generation.Gen5);
        context[CpuRegister.Rdi] = MemoryBase + 0xC1;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            VideoOutExports.VideoOutInitializeOutputOptions(context));
    }
}
