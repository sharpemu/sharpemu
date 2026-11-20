// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

// Buffers for posted word copies of 64 KiB or more, in 64 KiB size classes, with at most 48 MiB idle. The command stream
// rents one before it peeks the copy's source; the queued store returns it once it lands on the render thread, or the
// command stream returns it when the copy is refused. A buffer is never handed out again before it comes back.
internal sealed class StoreBufferPool(long maxIdleBytes = StoreBufferPool.DefaultMaxIdleBytes)
{
    public const int ClassBytes = 64 * 1024;
    public const long DefaultMaxIdleBytes = 48L * 1024 * 1024;

    public static readonly StoreBufferPool Shared = new();

    public enum ReturnResult : byte
    {
        Kept,
        Dropped,
        NotLeased,
    }

    private readonly object _gate = new();
    private readonly Dictionary<int, Stack<byte[]>> _idle = new();
    private readonly HashSet<byte[]> _leases = new(ReferenceEqualityComparer.Instance);
    private long _idleBytes;

    public byte[] Rent(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        var size = checked((length + ClassBytes - 1) / ClassBytes * ClassBytes);
        lock (_gate)
        {
            if (_idle.TryGetValue(size, out var stack) && stack.TryPop(out var idle))
            {
                _idleBytes -= idle.Length;
                _leases.Add(idle);
                return idle;
            }
        }

        var array = GC.AllocateUninitializedArray<byte>(size);
        lock (_gate)
        {
            _leases.Add(array);
        }

        return array;
    }

    public ReturnResult Return(byte[] array)
    {
        lock (_gate)
        {
            if (!_leases.Remove(array))
            {
                return ReturnResult.NotLeased;
            }

            if (_idleBytes + array.Length > maxIdleBytes)
            {
                return ReturnResult.Dropped;
            }

            if (!_idle.TryGetValue(array.Length, out var stack))
            {
                stack = new Stack<byte[]>();
                _idle.Add(array.Length, stack);
            }

            stack.Push(array);
            _idleBytes += array.Length;
            return ReturnResult.Kept;
        }
    }

    internal (int Leases, long IdleBytes) Stats()
    {
        lock (_gate)
        {
            return (_leases.Count, _idleBytes);
        }
    }
}
