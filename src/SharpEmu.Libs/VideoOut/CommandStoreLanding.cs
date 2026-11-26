// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

internal static class CommandStoreLanding
{
    // A physical alias preserves the offset within a host page. Prove byte disjointness even
    // across aliases without taking a mapping lock while the renderer services the store.
    internal static bool DisjointOffsets(ulong address, int length, ulong other, int otherLength)
    {
        const ulong page = 4096;
        var begin = address % page;
        var second = other % page;
        var end = begin + (uint)length;
        var secondEnd = second + (uint)otherLength;
        return end <= page && secondEnd <= page && (end <= second || secondEnd <= begin);
    }
}
