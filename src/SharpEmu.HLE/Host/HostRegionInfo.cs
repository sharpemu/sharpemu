// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.Host;

/// <summary>
/// Result of <see cref="IHostMemory.Query"/>. The Raw* fields carry the
/// untranslated OS values so call sites migrated from direct VirtualQuery use
/// keep comparing (and logging) the exact native words they did before;
/// <see cref="State"/> and <see cref="Protection"/> are neutral views.
/// </summary>
public readonly record struct HostRegionInfo(
    ulong BaseAddress,
    ulong AllocationBase,
    ulong RegionSize,
    HostRegionState State,
    uint RawState,
    HostPageProtection Protection,
    uint RawProtection,
    uint RawAllocationProtection);
