// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Scheduling;

// Counts the stores of GPU data into guest memory: buffer and image downloads, and buffer transfers
// the caches complete there. Work done ahead of the render thread from guest memory is still
// current while the count has not moved.
internal static class GpuWritebackEpoch
{
    private static long _value;

    public static long Value => Volatile.Read(ref _value);

    public static void Advance() => Interlocked.Increment(ref _value);
}
