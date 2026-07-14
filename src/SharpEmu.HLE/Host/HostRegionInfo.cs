// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.Host;

/// <summary>
/// Result of <see cref="IHostMemory.Query"/>. <see cref="RawProtection"/> carries
/// the untranslated OS protection value so call sites migrated from direct
/// VirtualQuery use can keep comparing the exact native constants they compared
/// before; <see cref="Protection"/> is a best-effort neutral view.
/// </summary>
public readonly record struct HostRegionInfo(
    ulong BaseAddress,
    ulong AllocationBase,
    ulong RegionSize,
    HostRegionState State,
    HostPageProtection Protection,
    uint RawProtection);
