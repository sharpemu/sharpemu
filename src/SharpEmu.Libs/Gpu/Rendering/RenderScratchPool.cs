// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;

namespace SharpEmu.Libs.Gpu.Rendering;

// Render-thread-only storage. Exact lengths preserve descriptor counts, and
// clearing on return preserves new-array semantics and releases resource refs.
internal sealed class RenderScratchPool<T>(int maxBytes = 256 * 1024)
{
    private readonly Dictionary<int, Stack<T[]>> _arrays = new();
    internal int RetainedBytes { get; private set; }

    internal T[] Rent(int length)
    {
        if (length == 0) return [];
        if (_arrays.TryGetValue(length, out var bucket) && bucket.Count != 0)
        {
            var result = bucket.Pop();
            RetainedBytes -= checked(length * Unsafe.SizeOf<T>());
            return result;
        }
        return new T[length];
    }

    internal void Return(T[] array)
    {
        if (array.Length == 0) return;
        var bytes = (long)array.Length * Unsafe.SizeOf<T>();
        if (bytes > maxBytes - RetainedBytes) return;
        if (!_arrays.TryGetValue(array.Length, out var bucket))
        {
            if (_arrays.Count == 128) return;
            bucket = new Stack<T[]>(4);
            _arrays.Add(array.Length, bucket);
        }
        if (bucket.Count == 4) return;
        Array.Clear(array);
        bucket.Push(array);
        RetainedBytes += (int)bytes;
    }
}
