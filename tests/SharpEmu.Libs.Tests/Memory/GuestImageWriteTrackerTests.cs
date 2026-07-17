// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.Core.Cpu;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Memory;

public sealed unsafe class GuestImageWriteTrackerTests
{
    private const nuint PageSize = 4096;

    [Fact]
    public void TrackedCpuMemoryManagedWriteMarksTrackedImageDirty()
    {
        if (!GuestImageWriteTracker.Enabled)
        {
            return;
        }

        var page = (byte*)NativeMemory.AlignedAlloc(PageSize, PageSize);
        Assert.NotEqual(nint.Zero, (nint)page);
        new Span<byte>(page, checked((int)PageSize)).Clear();
        var address = (ulong)page;
        var memory = new TrackedCpuMemory(
            new PointerCpuMemory(address, checked((ulong)PageSize)));

        GuestImageWriteTracker.Track(address, checked((ulong)PageSize));
        try
        {
            Assert.True(memory.TryWrite(address + 128, [1, 2, 3, 4]));
            Assert.True(GuestImageWriteTracker.ConsumeDirty(address));
            Assert.False(GuestImageWriteTracker.ConsumeDirty(address));

            Span<byte> actual = stackalloc byte[4];
            Assert.True(memory.TryRead(address + 128, actual));
            Assert.Equal([1, 2, 3, 4], actual.ToArray());

            GuestImageWriteTracker.Rearm(address);
            Assert.True(memory.TryWrite(address + 256, [5]));
            Assert.True(GuestImageWriteTracker.ConsumeDirty(address));
        }
        finally
        {
            GuestImageWriteTracker.Untrack(address);
            NativeMemory.AlignedFree(page);
        }
    }

    [Fact]
    public void ManagedWriteMarksEveryTrackedOwnerOfSharedPageDirty()
    {
        if (!GuestImageWriteTracker.Enabled)
        {
            return;
        }

        var page = (byte*)NativeMemory.AlignedAlloc(PageSize, PageSize);
        Assert.NotEqual(nint.Zero, (nint)page);
        new Span<byte>(page, checked((int)PageSize)).Clear();
        var firstAddress = (ulong)page + 64;
        var secondAddress = (ulong)page + 2048;

        GuestImageWriteTracker.Track(firstAddress, 512);
        GuestImageWriteTracker.Track(secondAddress, 512);
        try
        {
            GuestImageWriteTracker.NotifyManagedWrite(secondAddress + 32, 1);

            Assert.True(GuestImageWriteTracker.ConsumeDirty(firstAddress));
            Assert.True(GuestImageWriteTracker.ConsumeDirty(secondAddress));
        }
        finally
        {
            GuestImageWriteTracker.Untrack(firstAddress);
            GuestImageWriteTracker.Untrack(secondAddress);
            NativeMemory.AlignedFree(page);
        }
    }

    private sealed class PointerCpuMemory(ulong baseAddress, ulong length) : ICpuMemory
    {
        private readonly ulong _baseAddress = baseAddress;
        private readonly ulong _length = length;

        public bool TryRead(ulong virtualAddress, Span<byte> destination)
        {
            if (!Contains(virtualAddress, destination.Length))
            {
                return false;
            }

            new ReadOnlySpan<byte>((void*)virtualAddress, destination.Length).CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
        {
            if (!Contains(virtualAddress, source.Length))
            {
                return false;
            }

            source.CopyTo(new Span<byte>((void*)virtualAddress, source.Length));
            return true;
        }

        private bool Contains(ulong virtualAddress, int byteCount)
        {
            var length = checked((ulong)byteCount);
            return virtualAddress >= _baseAddress &&
                length <= _length &&
                virtualAddress - _baseAddress <= _length - length;
        }
    }
}
