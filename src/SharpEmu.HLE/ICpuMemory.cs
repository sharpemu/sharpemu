// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE;

public interface ICpuMemory
{
    bool TryRead(ulong virtualAddress, Span<byte> destination);

    bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source);

    bool TryCompare(ulong virtualAddress, ReadOnlySpan<byte> expected) =>
        TryCompare(virtualAddress, expected, out var equal) && equal;

    bool TryCompare(
        ulong virtualAddress,
        ReadOnlySpan<byte> expected,
        out bool equal)
    {
        equal = false;
        return false;
    }

    bool TryCopy(ulong destinationAddress, ulong sourceAddress, ulong length) => false;

    // Finds the first (or last) byte equal to needle in the NUL-terminated string
    // at address, the terminator included, scanning mapped memory in place.
    // match is 0 when absent. False means this memory cannot scan directly and
    // the caller should read the string through TryRead instead.
    bool TryScanCString(ulong address, byte needle, bool findLast, ulong maxLength, out ulong match)
    {
        match = 0;
        return false;
    }

    // True when the whole range is mapped guest memory; no bytes are copied.
    bool CanRead(ulong address, ulong size) => false;

    string DescribeReadRange(ulong address, ulong size) => "Memory mapping details are unavailable.";
}
