// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Vulkan;

// Advanced by every graphics pipeline bind or dynamic state change outside the guest draw path:
// such a pipeline may fix the state the guest pipelines leave dynamic, so the dynamic state a
// guest draw last set no longer holds in the command buffer.
internal static class GraphicsDynamicStateEpoch
{
    private static long _value;

    public static long Value => Volatile.Read(ref _value);

    public static void Advance() => Interlocked.Increment(ref _value);
}
