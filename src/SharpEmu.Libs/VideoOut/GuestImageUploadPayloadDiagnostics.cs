// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

internal static class GuestImageUploadPayloadDiagnostics
{
    internal static (long NonzeroBytes, ulong Hash) Summarize(ReadOnlySpan<byte> pixels)
    {
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;

        var nonzeroBytes = 0L;
        var hash = offsetBasis;
        foreach (var value in pixels)
        {
            nonzeroBytes += value == 0 ? 0 : 1;
            hash = (hash ^ value) * prime;
        }

        return (nonzeroBytes, hash);
    }
}
