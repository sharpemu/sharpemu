// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;

namespace SharpEmu.Libs.Gpu.Images;

internal static class HostLayoutTargets
{
    private static readonly ConcurrentDictionary<ulong, byte> Addresses = new();

    public static void Mark(ulong address) => Addresses.TryAdd(address, 0);

    public static bool Holds(ulong address) => !Addresses.IsEmpty && Addresses.ContainsKey(address);

    public static void Reset() => Addresses.Clear();
}
