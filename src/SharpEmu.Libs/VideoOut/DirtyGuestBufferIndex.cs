// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

internal sealed class DirtyGuestBufferIndex<TAllocation>
    where TAllocation : class
{
    private readonly HashSet<TAllocation> _all =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, HashSet<TAllocation>> _byQueue =
        new(StringComparer.Ordinal);
    private readonly Dictionary<TAllocation, HashSet<string>> _queuesByAllocation =
        new(ReferenceEqualityComparer.Instance);

    public int AllocationCount => _all.Count;

    public int QueueCount => _byQueue.Count;

    public void Mark(TAllocation allocation, string queueName)
    {
        ArgumentNullException.ThrowIfNull(allocation);
        ArgumentException.ThrowIfNullOrEmpty(queueName);

        _all.Add(allocation);
        if (!_byQueue.TryGetValue(queueName, out var allocations))
        {
            allocations = new HashSet<TAllocation>(ReferenceEqualityComparer.Instance);
            _byQueue.Add(queueName, allocations);
        }

        allocations.Add(allocation);
        if (!_queuesByAllocation.TryGetValue(allocation, out var queues))
        {
            queues = new HashSet<string>(StringComparer.Ordinal);
            _queuesByAllocation.Add(allocation, queues);
        }

        queues.Add(queueName);
    }

    public void CopyCandidates(string? queueName, List<TAllocation> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Clear();

        if (queueName is null)
        {
            destination.AddRange(_all);
            return;
        }

        if (_byQueue.TryGetValue(queueName, out var allocations))
        {
            destination.AddRange(allocations);
        }
    }

    public void Remove(TAllocation allocation, string queueName)
    {
        ArgumentNullException.ThrowIfNull(allocation);
        ArgumentException.ThrowIfNullOrEmpty(queueName);

        if (_byQueue.TryGetValue(queueName, out var allocations))
        {
            allocations.Remove(allocation);
            if (allocations.Count == 0)
            {
                _byQueue.Remove(queueName);
            }
        }

        if (!_queuesByAllocation.TryGetValue(allocation, out var queues))
        {
            return;
        }

        queues.Remove(queueName);
        if (queues.Count != 0)
        {
            return;
        }

        _queuesByAllocation.Remove(allocation);
        _all.Remove(allocation);
    }

    public void Remove(TAllocation allocation)
    {
        ArgumentNullException.ThrowIfNull(allocation);
        if (!_queuesByAllocation.Remove(allocation, out var queues))
        {
            _all.Remove(allocation);
            return;
        }

        foreach (var queueName in queues)
        {
            if (!_byQueue.TryGetValue(queueName, out var allocations))
            {
                continue;
            }

            allocations.Remove(allocation);
            if (allocations.Count == 0)
            {
                _byQueue.Remove(queueName);
            }
        }

        _all.Remove(allocation);
    }

    public bool Contains(TAllocation allocation, string queueName) =>
        _byQueue.TryGetValue(queueName, out var allocations) &&
        allocations.Contains(allocation);

    public void Clear()
    {
        _all.Clear();
        _byQueue.Clear();
        _queuesByAllocation.Clear();
    }
}
