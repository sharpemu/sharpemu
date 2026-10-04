// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Tests;

// Small discontiguous guest address space for tests whose mappings are deliberately
// farther apart than a practical host byte array can represent.
internal sealed class SparseCpuMemory : ICpuMemory
{
    private readonly List<(ulong Address, byte[] Bytes)> _segments = [];

    public SparseCpuMemory(params (ulong Address, int Size)[] segments)
    {
        foreach (var (address, size) in segments)
        {
            if (size <= 0 || address > ulong.MaxValue - (ulong)size)
            {
                throw new ArgumentOutOfRangeException(nameof(segments));
            }

            _segments.Add((address, new byte[size]));
        }
    }

    public bool TryRead(ulong virtualAddress, Span<byte> destination)
    {
        if (!TryResolve(virtualAddress, destination.Length, out var bytes, out var offset))
        {
            return false;
        }

        bytes.AsSpan(offset, destination.Length).CopyTo(destination);
        return true;
    }

    public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
    {
        if (!TryResolve(virtualAddress, source.Length, out var bytes, out var offset))
        {
            return false;
        }

        source.CopyTo(bytes.AsSpan(offset, source.Length));
        return true;
    }

    public bool CanRead(ulong address, ulong size) =>
        size <= int.MaxValue && TryResolve(address, (int)size, out _, out _);

    private bool TryResolve(
        ulong address,
        int length,
        out byte[] bytes,
        out int offset)
    {
        foreach (var segment in _segments)
        {
            if (address < segment.Address)
            {
                continue;
            }

            var relative = address - segment.Address;
            if (relative <= (ulong)segment.Bytes.Length &&
                (ulong)length <= (ulong)segment.Bytes.Length - relative)
            {
                bytes = segment.Bytes;
                offset = (int)relative;
                return true;
            }
        }

        bytes = [];
        offset = 0;
        return false;
    }
}
