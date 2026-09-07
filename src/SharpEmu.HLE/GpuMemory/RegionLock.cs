// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.GpuMemory;

// Spin lock that runs in the fault handler; it must not allocate or block in the kernel.
public sealed class RegionLock
{
    private int _held;
    private int _owner;

    public readonly ref struct Held
    {
        private readonly RegionLock _lock;

        public Held(RegionLock regionLock)
        {
            _lock = regionLock;
            regionLock.Enter();
        }

        public void Dispose() => _lock.Exit();
    }

    public Held Hold() => new(this);

    public void Enter()
    {
        var thread = Environment.CurrentManagedThreadId;
        if (Volatile.Read(ref _owner) == thread)
        {
            PageGuard.OnFatal("Cannot acquire the region lock twice on the same thread.");
            return;
        }

        while (Interlocked.CompareExchange(ref _held, 1, 0) != 0)
        {
            Thread.SpinWait(1);
        }

        Volatile.Write(ref _owner, thread);
    }

    public void Exit()
    {
        if (Volatile.Read(ref _owner) != Environment.CurrentManagedThreadId)
        {
            PageGuard.OnFatal("Only the thread that holds the region lock can release it.");
            return;
        }

        Volatile.Write(ref _owner, 0);
        Volatile.Write(ref _held, 0);
    }
}
