// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Vulkan;

internal static class NativeBindingEpoch
{
    private static long _value;

    public static long Value => Volatile.Read(ref _value);

    public static void Advance() => Interlocked.Increment(ref _value);
}
