// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Resources;

// The descriptor words and user data captured for one program at draw time, plus the
// flattened resource table and the device-address ranges the planner could bound.
public sealed class ResourceSnapshot
{
    public const uint NoMipStatistics = uint.MaxValue;
    public const uint MipStatisticsEnabled = 1u << 8;
    public const uint MipStatisticsEnableWordMask = 1u << 25;
    public const uint MipStatisticsSlotWordMask = 0xFF;

    public uint[][] Buffers { get; init; } = [];
    public uint[][] Images { get; init; } = [];
    public uint[][] Samplers { get; init; } = [];
    public uint[] FlattenedResourceTable { get; init; } = [];
    public uint[] UserData { get; init; } = [];
    public DeviceAddressRange[] DeviceAddressRanges { get; init; } = [];
    public uint[]? MipStatistics { get; init; }
    public object? Origin { get; set; }

    public static uint MipStatisticsOf(uint word5, uint word6) =>
        (word6 & MipStatisticsSlotWordMask) | ((word5 & MipStatisticsEnableWordMask) != 0 ? MipStatisticsEnabled : 0u);
}

// One device-address handle's evaluated base and bounded extent for this draw.
public readonly record struct DeviceAddressRange(uint Handle, ulong Base, ulong Size, bool Planned, bool Written);
