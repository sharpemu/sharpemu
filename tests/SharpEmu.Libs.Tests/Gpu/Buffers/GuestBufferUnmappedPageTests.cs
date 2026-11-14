// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.Buffers;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Buffers;

// A buffer upload reads pages the guest has no mapping for as zero; a mapped page that cannot be read is a failure.
public sealed class GuestBufferUnmappedPageTests
{
    private const ulong Base = 0x10000;

    // Four 4 KiB pages filled with 0xAB; the pages in `unmapped` have no mapping, the ones in `unreadable` are mapped
    // but refuse reads.
    private sealed class PagedMemory(ulong[] unmapped, ulong[]? unreadable = null) : ICpuMemory
    {
        private bool Mapped(ulong address, ulong size)
        {
            for (var page = address & ~0xFFFUL; page < address + size; page += 0x1000)
            {
                if (page < Base || page >= Base + 0x4000 || unmapped.Contains(page))
                    return false;
            }

            return true;
        }

        public bool CanRead(ulong address, ulong size) => size != 0 && Mapped(address, size);

        public bool TryRead(ulong virtualAddress, Span<byte> destination)
        {
            if (!Mapped(virtualAddress, (ulong)destination.Length))
                return false;
            for (var page = virtualAddress & ~0xFFFUL; page < virtualAddress + (ulong)destination.Length; page += 0x1000)
            {
                if (unreadable?.Contains(page) == true)
                    return false;
            }

            destination.Fill(0xAB);
            return true;
        }

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source) => false;
    }

    [Fact]
    public void UnmappedPagesReadAsZeroAndMappedPagesKeepTheirBytes()
    {
        var memory = new PagedMemory([Base + 0x1000, Base + 0x2000]);
        var destination = new byte[0x3000];
        destination.AsSpan().Fill(0xCD);

        Assert.True(GuestBufferUploader.TryReadMappedPages(memory, Base + 0x800, destination, out var unmapped));

        Assert.Equal(0x2000UL, unmapped);
        Assert.All(destination.AsSpan(0, 0x800).ToArray(), value => Assert.Equal(0xAB, value));
        Assert.All(destination.AsSpan(0x800, 0x2000).ToArray(), value => Assert.Equal(0, value));
        Assert.All(destination.AsSpan(0x2800, 0x800).ToArray(), value => Assert.Equal(0xAB, value));
    }

    [Fact]
    public void AMappedPageThatCannotBeReadIsAFailure()
    {
        var memory = new PagedMemory([Base + 0x1000], unreadable: [Base + 0x2000]);
        var destination = new byte[0x3000];

        Assert.False(GuestBufferUploader.TryReadMappedPages(memory, Base, destination, out _));
    }

    [Fact]
    public void AFullyMappedRangeReadsWithoutUnmappedBytes()
    {
        var memory = new PagedMemory([]);
        var destination = new byte[0x1800];

        Assert.True(GuestBufferUploader.TryReadMappedPages(memory, Base + 0x400, destination, out var unmapped));

        Assert.Equal(0UL, unmapped);
        Assert.All(destination, value => Assert.Equal(0xAB, value));
    }
}
