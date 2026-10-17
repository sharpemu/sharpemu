// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.GpuMemory;

public interface IGuestImageStore
{
    // Returns true when recovery completes, even if a later operation adds a new watch.
    bool MarkCpuWrite(ulong address, ulong size);

    void Unregister(ulong address, ulong size);
}
