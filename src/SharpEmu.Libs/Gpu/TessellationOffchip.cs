// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu;

// The hull-shader off-chip configuration the guest driver programs with
// sceAgcDriverSetHsOffchipParam (VGT_HS_OFFCHIP_PARAM). Each hull threadgroup in flight
// owns one buffer, so this bounds how many groups can run together and how far apart
// their off-chip data lies.
public static class TessellationOffchip
{
    private static long _configuration;

    // Zero buffers means the guest never configured off-chip memory.
    public static (uint Buffers, uint SlotBytes) Configuration
    {
        get
        {
            var value = Volatile.Read(ref _configuration);
            return ((uint)value, (uint)(value >> 32));
        }
    }

    // OFFCHIP_BUFFERING [9:0] holds the buffer count minus one; OFFCHIP_GRANULARITY [11:10]
    // selects 8K, 16K, 32K or 64K dwords per buffer.
    public static (uint Buffers, uint SlotBytes) Decode(uint parameter) =>
        ((parameter & 0x3FF) + 1, (8u * 1024 * sizeof(uint)) << (int)((parameter >> 10) & 3));

    public static void Configure(uint parameter)
    {
        var (buffers, slotBytes) = Decode(parameter);
        Volatile.Write(ref _configuration, (long)((ulong)slotBytes << 32 | buffers));
    }

    internal static void Reset() => Volatile.Write(ref _configuration, 0);
}
