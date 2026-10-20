// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE;

public static class GuestMemoryLayout
{
    // The direct memory a PS5 game gets: 12.5 GiB of the console's 16 GiB, the rest being the
    // system's. Games size their pools from sceKernelGetDirectMemorySize, so reporting the
    // whole 16 GiB made them claim (and fill) memory a console never gives them.
    public const ulong DirectBytes = 12800UL * 1024 * 1024;
    public const ulong FlexibleBytes = 448UL * 1024 * 1024;
    public const ulong FlexibleOffset = DirectBytes;
    public const ulong BackingBytes = DirectBytes + FlexibleBytes;
    public const ulong GuestPage = 0x4000;
}
