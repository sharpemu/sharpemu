// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers;

namespace SharpEmu.Libs.Gpu;

/// <summary>
/// The pool backing AGC-to-presenter ownership transfers, shared by every backend
/// (the AGC layer rents, the presenter returns, so both sides must use one pool).
/// Guest draw snapshots churn through a small set of 128 KiB-16 MiB size classes
/// thousands of times per second; the process-wide shared pool trims and
/// repartitions those large arrays aggressively under GC load, causing hundreds of
/// MiB/s of replacement byte[] allocations, so this pool is bounded and non-shared.
/// </summary>
internal static class GuestDataPool
{
    public static ArrayPool<byte> Shared { get; } = ArrayPool<byte>.Create(
        maxArrayLength: 16 * 1024 * 1024,
        maxArraysPerBucket: 96);
}
