// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Tests;

// A single contiguous guest region backed by a byte[]. Enough to hand C strings and small
// structures to HLE exports under test without a live guest.
internal sealed class FakeCpuMemory : ICpuMemory, IGuestMemoryAllocator
{
    private readonly ulong _base;
    private readonly byte[] _storage;
    private ulong _allocBump;

    public FakeCpuMemory(ulong baseAddress, int size)
    {
        _base = baseAddress;
        _storage = new byte[size];
        // Bump from the top so test fixtures at low offsets stay intact.
        _allocBump = baseAddress + (ulong)size;
    }

    public bool TryRead(ulong virtualAddress, Span<byte> destination)
    {
        if (!TryResolve(virtualAddress, destination.Length, out var offset))
        {
            return false;
        }

        _storage.AsSpan(offset, destination.Length).CopyTo(destination);
        return true;
    }

    public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
    {
        if (!TryResolve(virtualAddress, source.Length, out var offset))
        {
            return false;
        }

        source.CopyTo(_storage.AsSpan(offset, source.Length));
        return true;
    }

    public bool TryAllocateGuestMemory(ulong size, ulong alignment, out ulong address)
    {
        address = 0;
        if (size == 0 || alignment == 0 || (alignment & (alignment - 1)) != 0)
        {
            return false;
        }

        var alignedSize = (size + alignment - 1) & ~(alignment - 1);
        if (alignedSize > _allocBump - _base)
        {
            return false;
        }

        var next = (_allocBump - alignedSize) & ~(alignment - 1);
        if (next < _base)
        {
            return false;
        }

        _allocBump = next;
        address = next;
        return true;
    }

    public bool TryFreeGuestMemory(ulong address) => false;

    public ulong WriteCString(ulong virtualAddress, string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        TryWrite(virtualAddress, bytes);
        TryWrite(virtualAddress + (ulong)bytes.Length, stackalloc byte[] { 0 });
        return virtualAddress;
    }

    private bool TryResolve(ulong virtualAddress, int length, out int offset)
    {
        offset = 0;
        if (virtualAddress < _base)
        {
            return false;
        }

        var relative = virtualAddress - _base;
        if (relative + (ulong)length > (ulong)_storage.Length)
        {
            return false;
        }

        offset = (int)relative;
        return true;
    }
}
