// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.Host.Posix;
using SharpEmu.HLE.Host.Windows;

namespace SharpEmu.HLE.Host;

public static class HostViewMemory
{
    public static IHostViewMemory Create() =>
        OperatingSystem.IsWindows() ? new WindowsHostViews() : new PosixHostViews();

    internal static bool IsValidRange(ulong address, ulong size) =>
        address != 0 && size != 0 && size <= ulong.MaxValue - address;

    internal static bool IsValidOffset(HostBackingObject backing, ulong offset, ulong size, ulong pageSize) =>
        offset % pageSize == 0 && offset < backing.Size && size != 0 && size <= backing.Size - offset;
}
