// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcCopyDataTests
{
    private const ulong BaseAddress = 0x1_0000_0000;

    [Fact]
    public void Decoder_ReadsImmediateToMemory64Layout()
    {
        var control = 5u |
            (2u << 8) |
            (1u << 16) |
            (1u << 20) |
            (2u << 25);
        var packet = AgcExports.DecodeCopyDataPacket(
            control,
            0x5566_7788,
            0x1122_3344,
            0x0000_0107,
            1,
            usesAsyncEncoding: true);

        Assert.True(packet.SourceIsImmediate);
        Assert.True(packet.DestinationIsMemory);
        Assert.True(packet.Is64Bit);
        Assert.True(packet.WriteConfirm);
        Assert.Equal(2u, packet.DestinationCachePolicy);
        Assert.Equal(0x1122_3344_5566_7788UL, packet.SourceValue);
        Assert.Equal(BaseAddress + 0x100, packet.DestinationAddress);
    }

    [Fact]
    public void ImmediateCopy_Writes64BitValue()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var packet = AgcExports.DecodeCopyDataPacket(
            5u | (2u << 8) | (1u << 16),
            0xEEFF_0011,
            0xAABB_CCDD,
            0x0000_0200,
            1,
            usesAsyncEncoding: true);

        Assert.True(AgcExports.TryApplyCopyData(memory, packet, out var value));
        Assert.Equal(0xAABB_CCDD_EEFF_0011UL, value);
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        Assert.True(memory.TryRead(BaseAddress + 0x200, bytes));
        Assert.Equal(0xAABB_CCDD_EEFF_0011UL, BitConverter.ToUInt64(bytes));
    }

    [Fact]
    public void MemoryCopy_ReadsSourceAtExecution()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var source = BaseAddress + 0x300;
        var destination = BaseAddress + 0x400;
        Span<byte> sourceBytes = stackalloc byte[sizeof(uint)];
        BitConverter.TryWriteBytes(sourceBytes, 0xCAFE_BABEu);
        Assert.True(memory.TryWrite(source, sourceBytes));
        var packet = AgcExports.DecodeCopyDataPacket(
            2u | (2u << 8),
            (uint)source,
            (uint)(source >> 32),
            (uint)destination,
            (uint)(destination >> 32),
            usesAsyncEncoding: true);

        Assert.True(AgcExports.TryApplyCopyData(memory, packet, out var value));
        Assert.Equal(0xCAFE_BABEUL, value);
        Span<byte> destinationBytes = stackalloc byte[sizeof(uint)];
        Assert.True(memory.TryRead(destination, destinationBytes));
        Assert.Equal(0xCAFE_BABEu, BitConverter.ToUInt32(destinationBytes));
    }

    [Fact]
    public void AtomicReturnCopy_RequiresAValidReturnValue()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var packet = AgcExports.DecodeCopyDataPacket(
            6u | (2u << 8) | (1u << 16),
            0,
            0,
            0x0000_0500,
            1,
            usesAsyncEncoding: true);

        Assert.False(AgcExports.TryApplyCopyData(memory, packet, out _));
        Assert.True(AgcExports.TryApplyCopyData(
            memory,
            packet,
            0x1122_3344_5566_7788,
            atomicReturnDataValid: true,
            out var value));
        Assert.Equal(0x1122_3344_5566_7788UL, value);
    }

    [Theory]
    [InlineData(0u, 2u)]
    [InlineData(2u, 0u)]
    [InlineData(5u, 3u)]
    public void UnsupportedSelectors_AreRejected(uint source, uint destination)
    {
        var packet = AgcExports.DecodeCopyDataPacket(
            source | (destination << 8),
            0,
            0,
            0x100,
            1,
            usesAsyncEncoding: true);

        Assert.False(packet.IsSupported);
    }

    [Theory]
    [InlineData(2u, false, 4u, 4u)]
    [InlineData(2u, true, 5u, 5u)]
    [InlineData(5u, false, 10u, 4u)]
    [InlineData(5u, true, 11u, 5u)]
    [InlineData(6u, false, 12u, 4u)]
    public void GraphicsSelectors_DecodeSelectorAndEngine(
        uint rawSource,
        bool usePfp,
        uint expectedSource,
        uint expectedDestination)
    {
        var control = rawSource | (2u << 8);
        if (usePfp)
        {
            control |= 1u << 30;
        }

        var packet = AgcExports.DecodeCopyDataPacket(
            control,
            0,
            0,
            0x100,
            1,
            usesAsyncEncoding: false);

        Assert.Equal(expectedSource, packet.SourceSelection);
        Assert.Equal(expectedDestination, packet.DestinationSelection);
        Assert.True(packet.IsSupported);
    }

    [Theory]
    [InlineData(3u, false)]
    [InlineData(4u, false)]
    [InlineData(6u, true)]
    public void GraphicsSelectors_RejectUnsupportedSources(
        uint rawSource,
        bool usePfp)
    {
        var control = rawSource | (2u << 8);
        if (usePfp)
        {
            control |= 1u << 30;
        }

        var packet = AgcExports.DecodeCopyDataPacket(
            control,
            0,
            0,
            0x100,
            1,
            usesAsyncEncoding: false);

        Assert.False(packet.IsSupported);
    }
}
