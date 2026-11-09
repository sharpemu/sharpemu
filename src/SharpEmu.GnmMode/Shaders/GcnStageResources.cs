// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;

namespace SharpEmu.GnmMode.Shaders;

internal readonly record struct GcnStageResource(uint Resource1, uint Resource2)
{
    public uint UserScalars => (Resource2 >> 1) & 0x1F;
    public bool OffchipLocalData => (Resource2 & (1u << 7)) != 0;
    public bool GroupSize => (Resource2 & (1u << 8)) != 0;
    public uint ScalarRegisters => ((Resource1 >> 6) & 0xF) * 8 + 8;
    public uint VectorRegisters => (Resource1 & 0x3F) * 4 + 4;
}

internal static class GcnStageResources
{
    private static readonly ConcurrentDictionary<ulong, GcnStageResource> Resources = new();

    internal static void ObserveResource1(ulong address, uint value)
    {
        if (address != 0)
            Resources.AddOrUpdate(address, new GcnStageResource(value, 0), (_, old) => old with { Resource1 = value });
    }

    internal static void ObserveResource2(ulong address, uint value)
    {
        if (address != 0)
            Resources.AddOrUpdate(address, new GcnStageResource(0, value), (_, old) => old with { Resource2 = value });
    }

    internal static bool TryGet(ulong address, out GcnStageResource resource) => Resources.TryGetValue(address, out resource);

    internal static void Reset() => Resources.Clear();
}
