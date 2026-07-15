// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE;

public interface IGuestMemoryAllocator
{
    bool TryAllocateGuestMemory(ulong size, ulong alignment, out ulong address);

    bool TryFreeGuestMemory(ulong address);
}
