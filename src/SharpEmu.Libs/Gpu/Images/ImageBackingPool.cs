// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Images;

// Keeps the Vulkan image and memory of retired cached images so an image created with the
// same parameters reuses them instead of calling vkCreateImage and vkAllocateMemory again.
// Astro Bot turns one surface into a depth target and back into a color image every frame,
// which otherwise creates and frees three images a frame. A retired image is returned only
// after the GPU finished with it (the cache erases images on tick completion), and a reused
// image starts again from the undefined layout, so no contents or hazards carry over.
public sealed unsafe class ImageBackingPool : IDisposable
{
    private const int MaxPerKey = 4;
    private const ulong MaxPooledBytes = 256UL << 20;

    public readonly record struct Key(
        Format Format,
        ImageType Type,
        Extent3D Extent,
        uint Layers,
        uint MipLevels,
        SampleCountFlags Samples,
        ImageCreateFlags Flags,
        ImageUsageFlags Usage);

    private readonly record struct Entry(Image Handle, DeviceMemory Memory, ulong Size,
        OptimalImageMemoryAllocation? Placement, OptimalImageMemoryPool? Allocator);

    private readonly GpuDeviceInfo _device;
    private readonly Dictionary<Key, Stack<Entry>> _entries = new();
    private readonly LinkedList<(Key Key, Entry Entry)> _order = new();
    private ulong _pooledBytes;
    private bool _disposed;

    internal ulong PooledBytes => _pooledBytes;

    public ImageBackingPool(GpuDeviceInfo device) => _device = device;

    // SHARPEMU_IMAGE_BACKING_POOL=0 destroys every retired image as before.
    public static readonly bool Enabled = !string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_IMAGE_BACKING_POOL"), "0", StringComparison.Ordinal);

    internal static Key KeyOf(in ImageCreateInfo create) =>
        new(create.Format, create.ImageType, create.Extent, create.ArrayLayers, create.MipLevels, create.Samples, create.Flags, create.Usage);

    public bool TryTake(in Key key, out Image handle, out DeviceMemory memory, out ulong size)
        => TryTake(key, out handle, out memory, out size, out _);

    internal bool TryTake(in Key key, out Image handle, out DeviceMemory memory, out ulong size,
        out OptimalImageMemoryAllocation? placement)
    {
        if (_entries.TryGetValue(key, out var stack) && stack.TryPop(out var entry))
        {
            RemoveOrder(key, entry);
            _pooledBytes -= entry.Size;
            (handle, memory, size) = (entry.Handle, entry.Memory, entry.Size);
            placement = entry.Placement;
            return true;
        }

        handle = default;
        memory = default;
        size = 0;
        placement = null;
        return false;
    }

    // False when the pool is full for this key or disposed; the caller then destroys it.
    public bool TryReturn(in Key key, Image handle, DeviceMemory memory, ulong size,
        OptimalImageMemoryAllocation? placement = null, OptimalImageMemoryPool? allocator = null)
    {
        if (_disposed || size > MaxPooledBytes)
        {
            return false;
        }

        if (!_entries.TryGetValue(key, out var stack))
        {
            stack = new Stack<Entry>();
            _entries.Add(key, stack);
        }

        if (stack.Count >= MaxPerKey)
        {
            return false;
        }

        var entry = new Entry(handle, memory, size, placement, allocator);
        stack.Push(entry);
        _order.AddLast((key, entry));
        _pooledBytes += size;
        while (_pooledBytes > MaxPooledBytes && _order.First is { } oldest)
        {
            _order.RemoveFirst();
            var (oldestKey, oldestEntry) = oldest.Value;
            RemoveFromStack(oldestKey, oldestEntry);
            _pooledBytes -= oldestEntry.Size;
            Destroy(oldestEntry);
        }

        return true;
    }

    private void RemoveOrder(Key key, Entry entry)
    {
        for (var node = _order.Last; node is not null; node = node.Previous)
        {
            if (node.Value.Key == key && node.Value.Entry == entry)
            {
                _order.Remove(node);
                return;
            }
        }
    }

    private void RemoveFromStack(Key key, Entry entry)
    {
        if (!_entries.TryGetValue(key, out var stack))
        {
            return;
        }

        var kept = stack.Where(item => item != entry).Reverse().ToArray();
        stack.Clear();
        foreach (var item in kept)
        {
            stack.Push(item);
        }
    }

    private void Destroy(Entry entry)
    {
        _device.Vk.DestroyImage(_device.Device, entry.Handle, null);
        if (entry.Placement is { } placement) entry.Allocator!.Free(placement);
        else _device.FreeMemory(entry.Memory);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ReleaseRetained();
    }

    // Entries arrive only after their last GPU submission completes. Trimming
    // their storage under pressure does not disable subsequent backing reuse.
    public void ReleaseRetained()
    {
        foreach (var (_, entry) in _order)
        {
            Destroy(entry);
        }

        _order.Clear();
        _entries.Clear();
        _pooledBytes = 0;
    }
}
