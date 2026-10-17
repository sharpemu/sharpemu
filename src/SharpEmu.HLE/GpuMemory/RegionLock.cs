// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.HLE.GpuMemory;

// Spin lock that runs in the fault handler; it must not allocate or block in the kernel.
public sealed class RegionLock
{
    public enum Category
    {
        MemoryTracker,
        ImageCache,
    }

    private readonly Category _category;
    private int _held;
    private int _owner;
    private long _holdStarted;
    private GpuMemoryAccessProfile.Operation _holdOperation;

    public RegionLock(Category category = Category.MemoryTracker) => _category = category;

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

        if (Interlocked.CompareExchange(ref _held, 1, 0) != 0)
        {
            var waitStarted = GpuMemoryAccessProfile.Enabled ? Stopwatch.GetTimestamp() : 0;
            do
            {
                Thread.SpinWait(1);
            }
            while (Interlocked.CompareExchange(ref _held, 1, 0) != 0);
            if (GpuMemoryAccessProfile.Enabled)
                GpuMemoryAccessProfile.RecordLockWait(_category, Stopwatch.GetTimestamp() - waitStarted);
        }

        Volatile.Write(ref _owner, thread);
        if (GpuMemoryAccessProfile.Enabled)
        {
            _holdOperation = GpuMemoryAccessProfile.GetLockOperation(_category, hold: true);
            _holdStarted = Stopwatch.GetTimestamp();
        }
    }

    public void Exit()
    {
        if (Volatile.Read(ref _owner) != Environment.CurrentManagedThreadId)
        {
            PageGuard.OnFatal("Only the thread that holds the region lock can release it.");
            return;
        }

        // Copy measurements before release; the next owner can then replace the lock's fields.
        var holdOperation = _holdOperation;
        var holdTicks = GpuMemoryAccessProfile.Enabled ? Stopwatch.GetTimestamp() - _holdStarted : 0;
        Volatile.Write(ref _owner, 0);
        Volatile.Write(ref _held, 0);
        if (GpuMemoryAccessProfile.Enabled)
            GpuMemoryAccessProfile.RecordLockHold(holdOperation, holdTicks);
    }
}
