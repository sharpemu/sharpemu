// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Emulation;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class ClzeroInstructionEmulatorTests
{
    [Fact]
    public void TryDecode_AcceptsPlainEncoding()
    {
        Assert.True(ClzeroInstructionEmulator.TryDecode([0x0F, 0x01, 0xFC], out var length, out var addr32));
        Assert.Equal(3, length);
        Assert.False(addr32);
    }

    [Fact]
    public void TryDecode_AcceptsAddressSizePrefix()
    {
        Assert.True(ClzeroInstructionEmulator.TryDecode([0x67, 0x0F, 0x01, 0xFC], out var length, out var addr32));
        Assert.Equal(4, length);
        Assert.True(addr32);
    }

    [Theory]
    [InlineData(0xFA)]
    [InlineData(0xFB)]
    [InlineData(0xFD)]
    public void TryDecode_RejectsNeighbouringOpcodes(byte last)
    {
        Assert.False(ClzeroInstructionEmulator.TryDecode([0x0F, 0x01, last], out _, out _));
    }

    [Fact]
    public void GetCacheLineAddress_AlignsDownTo64Bytes()
    {
        Assert.Equal(0x1234_5678_9AC0UL, ClzeroInstructionEmulator.GetCacheLineAddress(0x1234_5678_9AFFUL, false));
    }

    [Fact]
    public void GetCacheLineAddress_TruncatesTo32BitsWithPrefix()
    {
        Assert.Equal(0x5678_9AC0UL, ClzeroInstructionEmulator.GetCacheLineAddress(0x1234_5678_9AFFUL, true));
    }

    [Fact]
    public unsafe void ZeroCacheLine_ClearsOnlyTheContainingLine()
    {
        var buffer = GC.AllocateArray<byte>(256, pinned: true);
        Array.Fill(buffer, (byte)0xAA);
        var start = (ulong)(nint)System.Runtime.CompilerServices.Unsafe.AsPointer(ref buffer[0]);
        var line = (start + 64) & ~63UL;
        var offset = (int)(line - start);

        ClzeroInstructionEmulator.ZeroCacheLine(line + 17, addressSize32: false);

        for (var i = 0; i < buffer.Length; i++)
        {
            var inLine = i >= offset && i < offset + 64;
            Assert.Equal(inLine ? (byte)0 : (byte)0xAA, buffer[i]);
        }
    }
}
