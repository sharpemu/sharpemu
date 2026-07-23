// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Tests;

// A single contiguous guest region backed by a byte[]. Enough to hand C strings and small
// structures to HLE exports under test without a live guest.
internal sealed class FakeCpuMemory : ICpuMemory
{
    private readonly ulong _base;
    private readonly byte[] _storage;

    public FakeCpuMemory(ulong baseAddress, int size)
    {
        _base = baseAddress;
        _storage = new byte[size];
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
