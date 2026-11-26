// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

// Producers already serialize sequence assignment. The sole consumer can take published items
// without competing for that monitor. Overflow stays in the original list until it is swapped.
internal sealed class CommandHandoff<T> where T : class
{
    private const int Capacity = 256;
    private readonly T?[] _slots = new T?[Capacity];
    private int _read, _written, _knownRead;

    public bool TryPublish(T item)
    {
        var written = _written;
        if (unchecked(written - _knownRead) == Capacity)
        {
            _knownRead = Volatile.Read(ref _read);
            if (unchecked(written - _knownRead) == Capacity) return false;
        }
        _slots[written & (Capacity - 1)] = item;
        Volatile.Write(ref _written, unchecked(written + 1));
        return true;
    }

    public bool Drain(List<T> destination)
    {
        var read = _read;
        var end = Volatile.Read(ref _written);
        if (read == end) return false;
        while (read != end)
        {
            int slot = read & (Capacity - 1);
            destination.Add(_slots[slot]!);
            _slots[slot] = null;
            read = unchecked(read + 1);
        }
        Volatile.Write(ref _read, end);
        return true;
    }
}
