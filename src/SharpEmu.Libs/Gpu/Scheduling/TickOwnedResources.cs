// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Scheduling;

// A GPU feedback resource belongs to one submission until its completion callback
// has consumed it. Later submissions must never reuse its mapped storage.
internal sealed class TickOwnedResources<T> : IDisposable where T : class, IDisposable
{
    private readonly Dictionary<ulong, T> _resources = [];
    private readonly object _gate = new();

    public T Acquire(ulong tick, Func<T> create)
    {
        lock (_gate)
        {
            if (!_resources.TryGetValue(tick, out var resource))
            {
                resource = create();
                _resources.Add(tick, resource);
            }
            return resource;
        }
    }

    public void Complete(ulong tick, Action<T> consume)
    {
        lock (_gate)
        {
            if (!_resources.Remove(tick, out var resource)) return;
            try { consume(resource); }
            finally { resource.Dispose(); }
        }
    }

    // The caller must wait for GPU completion before disposing pending resources.
    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var resource in _resources.Values) resource.Dispose();
            _resources.Clear();
        }
    }
}
