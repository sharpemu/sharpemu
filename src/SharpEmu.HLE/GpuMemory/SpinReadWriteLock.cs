// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.GpuMemory;

public sealed class SpinReadWriteLock
{
    private const int Writer = -1;

    private int _state;
    private int _writersWaiting;
    private int _owner;

    public readonly ref struct ReadHeld
    {
        private readonly SpinReadWriteLock? _lock;

        internal ReadHeld(SpinReadWriteLock? held) => _lock = held;

        public void Dispose() => _lock?.ExitRead();
    }

    public readonly ref struct WriteHeld
    {
        private readonly SpinReadWriteLock _lock;

        internal WriteHeld(SpinReadWriteLock held)
        {
            _lock = held;
            held.EnterWrite();
        }

        public void Dispose() => _lock.ExitWrite();
    }

    public ReadHeld Read()
    {
        if (Volatile.Read(ref _owner) == Environment.CurrentManagedThreadId)
        {
            return new ReadHeld(null);
        }

        EnterRead();
        return new ReadHeld(this);
    }

    public WriteHeld Hold() => new(this);

    public bool IsWriteHeldByCurrentThread => Volatile.Read(ref _owner) == Environment.CurrentManagedThreadId;

    public void EnterRead()
    {
        for (;;)
        {
            var state = Volatile.Read(ref _state);
            if (state >= 0 && Volatile.Read(ref _writersWaiting) == 0 && Interlocked.CompareExchange(ref _state, state + 1, state) == state)
            {
                return;
            }

            Thread.SpinWait(1);
        }
    }

    public void ExitRead() => Interlocked.Decrement(ref _state);

    public void EnterWrite()
    {
        var thread = Environment.CurrentManagedThreadId;
        if (Volatile.Read(ref _owner) == thread)
        {
            PageGuard.OnFatal("Cannot acquire the region lock twice on the same thread.");
            return;
        }

        Interlocked.Increment(ref _writersWaiting);
        while (Interlocked.CompareExchange(ref _state, Writer, 0) != 0)
        {
            Thread.SpinWait(1);
        }

        Interlocked.Decrement(ref _writersWaiting);
        Volatile.Write(ref _owner, thread);
    }

    public void ExitWrite()
    {
        if (Volatile.Read(ref _owner) != Environment.CurrentManagedThreadId)
        {
            PageGuard.OnFatal("Only the thread that holds the region lock can release it.");
            return;
        }

        Volatile.Write(ref _owner, 0);
        Volatile.Write(ref _state, 0);
    }
}
