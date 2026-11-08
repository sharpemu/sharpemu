// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;

namespace SharpEmu.Libs.Agc;

public static partial class AgcExports
{
    public const uint DefaultTessellationFactorRingBytes = 128 * 1024;
    private const ulong TessellationFactorRingAlignment = 0x10000;

    public static bool TryGetTessellationFactorRingAddress(ICpuMemory memory, out ulong address)
    {
        var root = (ICpuMemory)CanonicalMemory(memory);
        var state = _submittedGpuStates.GetValue(root, static _ => new SubmittedGpuState());
        lock (state.Gate)
        {
            if (state.TessellationFactorRingAddress != 0)
            {
                address = state.TessellationFactorRingAddress;
                return true;
            }

            address = 0;
            // The caller maps its own direct memory here; a private allocation cannot be replaced by that view.
            if (!KernelMemoryCompatExports.TryReserveDriverMemory(root, DefaultTessellationFactorRingBytes,
                    TessellationFactorRingAlignment, out var allocated))
                return false;
            state.TessellationFactorRingAddress = allocated;
            address = allocated;
            return true;
        }
    }

    public static void ResetTessellationFactorRing(ICpuMemory memory)
    {
        if (!_submittedGpuStates.TryGetValue(CanonicalMemory(memory), out var state))
            return;
        lock (state.Gate)
        {
            state.TessellationFactorRingAddress = 0;
        }
    }
}
