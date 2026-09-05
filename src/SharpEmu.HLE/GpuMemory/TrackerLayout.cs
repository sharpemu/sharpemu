// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.GpuMemory;

public static class TrackerLayout
{
    public const ulong PageBytes = 4096;
    public const ulong BlockBytes = 4UL * 1024 * 1024;
    public const ulong SpaceBytes = 1UL << 40;
    public const int PagesPerBlock = (int)(BlockBytes / PageBytes);
    public const int BlockCount = (int)(SpaceBytes / BlockBytes);
}

public readonly record struct GuestSpan(ulong Address, ulong Size)
{
    public static GuestSpan Empty => default;

    public bool IsValid =>
        Address < TrackerLayout.SpaceBytes && Size <= TrackerLayout.SpaceBytes - Address;

    public ulong End => Address + Size;
}

public enum WriteOrigin
{
    Cpu,
    Gpu,
}
