// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using SharpEmu.Libs.Gpu.Images;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Images;

public sealed class ResidentImageBackingTests
{
    private const ulong Base = 0x9A_0000_0000UL;
    private const int Page = 0x1000;

    // Pages [0, backed) from the start of the range are mapped; the rest is not.
    private sealed class PrefixBacking(ulong start, params (int FirstPage, int PageCount)[] runs) : IGuestBackedSpace
    {
        private bool Page_(ulong address) => runs.Any(run =>
            address >= start + (ulong)run.FirstPage * Page && address < start + (ulong)(run.FirstPage + run.PageCount) * Page);

        public bool IsBackedRange(ulong address, ulong size)
        {
            for (var page = address & ~(ulong)(Page - 1); page < address + size; page += Page)
            {
                if (!Page_(page))
                {
                    return false;
                }
            }

            return size != 0;
        }

        public bool TryReadBacking(ulong address, Span<byte> data)
        {
            if (!IsBackedRange(address, (ulong)data.Length))
            {
                return false;
            }

            data.Fill(0xAB);
            return true;
        }

        public bool TryHoldRange(ulong address, ulong size) => false;
        public bool TryHoldRangeAtOrAbove(ulong searchStart, ulong size, ulong alignment, out ulong address) { address = 0; return false; }
        public bool TryMapBacked(ulong address, ulong size, ulong backingOffset, GuestPageProtection protection, out HostViewFailure failure) { failure = default; return false; }
        public bool TryUnmapBacked(ulong address, ulong size) => false;
        public bool TryClearBacking(ulong offset, ulong size) => false;
        public bool IsBackedView(ulong address) => Page_(address);
        public bool TryWriteBacking(ulong address, ReadOnlySpan<byte> data) => false;
    }

    [Fact]
    public void ResidentPrefixIsCopiedAndTheUnmappedTailIsZeroed()
    {
        var backing = new PrefixBacking(Base, (0, 3));
        var bytes = new byte[5 * Page];
        Array.Fill(bytes, (byte)0xFF);

        Assert.True(ResidentImageBacking.TryReadResidentPrefix(backing, Base, bytes));

        Assert.All(bytes.AsSpan(0, 3 * Page).ToArray(), value => Assert.Equal(0xAB, value));
        Assert.All(bytes.AsSpan(3 * Page).ToArray(), value => Assert.Equal(0, value));
    }

    [Fact]
    public void ABackedPageAfterAHoleIsRejected()
    {
        var backing = new PrefixBacking(Base, (0, 1), (2, 1));
        Assert.False(ResidentImageBacking.TryReadResidentPrefix(backing, Base, new byte[3 * Page]));
    }

    [Fact]
    public void AnUnmappedFirstByteIsRejected()
    {
        var backing = new PrefixBacking(Base, (1, 2));
        Assert.False(ResidentImageBacking.TryReadResidentPrefix(backing, Base, new byte[3 * Page]));
        Assert.False(ResidentImageBacking.TryRead(backing, Base, new byte[3 * Page]));
    }

    [Fact]
    public void AFullyBackedRangeReadsThroughTheNormalPath()
    {
        var backing = new PrefixBacking(Base, (0, 2));
        var bytes = new byte[2 * Page];
        Assert.True(ResidentImageBacking.TryRead(backing, Base, bytes));
        Assert.All(bytes, value => Assert.Equal(0xAB, value));
    }

    [Fact]
    public void TheTailIsZeroedWhenTheRangeStartsMidPage()
    {
        var backing = new PrefixBacking(Base, (0, 2));
        var bytes = new byte[2 * Page];
        Array.Fill(bytes, (byte)0xFF);
        Assert.True(ResidentImageBacking.TryReadResidentPrefix(backing, Base + 0x800, bytes));
        Assert.Equal(0xAB, bytes[0x17FF]);
        Assert.Equal(0, bytes[0x1800]);
    }
}
