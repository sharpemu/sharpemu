// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.GpuMemory;

public interface IGpuTickScheduler
{
    bool Active { get; }

    ulong CurrentTick { get; }

    // True while any scheduler runs a tick callback on the calling thread.
    bool InsideTickCallback { get; }

    void Finish();

    void WaitForPriorityOperations(ulong tick);

    // Complete GPU memory use and its callbacks before mappings change.
    void FinishMemoryAccess();
}
