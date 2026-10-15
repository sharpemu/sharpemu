// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE;

// Produces bytes straight into destination and returns how many leading bytes
// it wrote (0..destination.Length).
public delegate int GuestSpanFill<TState>(TState state, Span<byte> destination);

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

    // Lets fill write [virtualAddress, virtualAddress + length) in place in the
    // host memory behind it, skipping a staging buffer (e.g. a positional file
    // read). The bytes fill reports get the same write tracking a TryWrite of
    // them would. False, with fill never called, when this memory cannot hand
    // the range out in place; the caller then stages through TryWrite.
    // Exceptions from fill propagate, and bytes it wrote before throwing stay.
    bool TryFillInPlace<TState>(ulong virtualAddress, int length, TState state, GuestSpanFill<TState> fill, out int filled)
    {
        filled = 0;
        return false;
    }

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
