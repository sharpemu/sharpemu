// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.GpuMemory;

public interface IGuestImageStore
{
    // Returns true only when the faulting access can proceed after recovery.
    bool MarkCpuWrite(ulong address, ulong size);

    void Unregister(ulong address, ulong size);
}

public sealed class IdleImageStore : IGuestImageStore
{
    public bool MarkCpuWrite(ulong address, ulong size) => false;

    public void Unregister(ulong address, ulong size)
    {
    }
}
