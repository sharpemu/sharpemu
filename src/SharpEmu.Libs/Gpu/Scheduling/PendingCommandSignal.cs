// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Scheduling;

public sealed class PendingCommandSignal
{
    private int _pending;

    public static PendingCommandSignal AlwaysRaised { get; } = new() { _pending = 1 };

    public bool IsRaised => Volatile.Read(ref _pending) != 0;

    internal void Raise() => Interlocked.Increment(ref _pending);

    internal void Lower() => Interlocked.Decrement(ref _pending);
}
