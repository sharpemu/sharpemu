// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.GpuMemory;

public interface IGuestBufferStore
{
    // Returns true when recovery completes, even if a later operation adds a new watch.
    bool MarkCpuWrite(ulong address, ulong size);

    // After a managed write has landed: pages an upload cleaned between MarkCpuWrite and the write
    // turn CPU-dirty again. Never recovers GPU data, which would replace the new bytes.
    void MarkCpuWriteLanded(ulong address, ulong size)
    {
    }

    bool DownloadToCpu(ulong address, ulong size);

    bool NoteEmulatorWrite(ulong address, ulong size)
    {
        MarkCpuWriteLanded(address, size);
        return true;
    }

    bool MayHoldGpuData(ulong address, ulong size) => true;

    // True for current CPU data or an untracked range; false when required recovery fails.
    bool TrySynchronizeCpuRead(ulong address, ulong size) => DownloadToCpu(address, size);
}
