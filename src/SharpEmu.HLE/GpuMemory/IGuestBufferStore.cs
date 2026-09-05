// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.GpuMemory;

public interface IGuestBufferStore
{
    // Returns true only when the faulting access can proceed after recovery.
    bool MarkCpuWrite(ulong address, ulong size);

    bool DownloadToCpu(ulong address, ulong size);
}

public sealed class IdleBufferStore : IGuestBufferStore
{
    public bool MarkCpuWrite(ulong address, ulong size) => false;

    public bool DownloadToCpu(ulong address, ulong size) => false;
}
