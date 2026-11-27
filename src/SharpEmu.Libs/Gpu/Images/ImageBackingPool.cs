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

    private readonly record struct Entry(Image Handle, DeviceMemory Memory, ulong Size, uint Heap, long ReturnedAt);

    private readonly GpuDeviceInfo _device;
    private readonly Dictionary<Key, Stack<Entry>> _entries = new();
    private Stack<Entry>? _spareStack;
    private readonly LinkedList<(Key Key, Entry Entry)> _order = new();
    private readonly Lock _gate = new();
    private ulong _pooledBytes;
    private bool _disposed;

    public ImageBackingPool(GpuDeviceInfo device) => _device = device;

    // SHARPEMU_IMAGE_BACKING_POOL=0 destroys every retired image as before.
    public static readonly bool Enabled = !string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_IMAGE_BACKING_POOL"), "0", StringComparison.Ordinal);

    internal static Key KeyOf(in ImageCreateInfo create) =>
        new(create.Format, create.ImageType, create.Extent, create.ArrayLayers, create.MipLevels, create.Samples, create.Flags, create.Usage);

    public bool TryTake(in Key key, out Image handle, out DeviceMemory memory, out ulong size, out uint heap)
    {
        using var held = _gate.EnterScope();
        if (_entries.TryGetValue(key, out var stack) && stack.TryPop(out var entry))
        {
            RemoveOrder(key, entry);
            ReleaseEmptyStack(key, stack);
            _pooledBytes -= entry.Size;
            (handle, memory, size, heap) = (entry.Handle, entry.Memory, entry.Size, entry.Heap);
            return true;
        }

        handle = default;
        memory = default;
        size = 0;
        heap = uint.MaxValue;
        return false;
    }

    // False when the pool is full for this key or disposed; the caller then destroys it.
    public bool TryReturn(in Key key, Image handle, DeviceMemory memory, ulong size, uint heap)
    {
        using var held = _gate.EnterScope();
        if (_disposed || size > MaxPooledBytes)
        {
            return false;
        }

        if (!_entries.TryGetValue(key, out var stack))
        {
            stack = _spareStack ?? new Stack<Entry>();
            _spareStack = null;
            _entries.Add(key, stack);
        }

        if (stack.Count >= MaxPerKey)
        {
            return false;
        }

        var entry = new Entry(handle, memory, size, heap, Environment.TickCount64);
        _device.Residency?.Find(memory)?.Use(GpuResidency.Role.IdleImage);
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

        Span<Entry> kept = stackalloc Entry[MaxPerKey];
        var count = 0;
        while (stack.TryPop(out var item) && item != entry)
        {
            kept[count++] = item;
        }
        while (count > 0)
            stack.Push(kept[--count]);
        ReleaseEmptyStack(key, stack);
    }

    private void ReleaseEmptyStack(Key key, Stack<Entry> stack)
    {
        if (stack.Count != 0) return;
        _entries.Remove(key);
        _spareStack ??= stack;
    }

    private void Destroy(Entry entry)
    {
        _device.Vk.DestroyImage(_device.Device, entry.Handle, null);
        _device.FreeMemory(entry.Memory);
    }

    internal ulong Trim(uint? heap, ulong target, long returnedThrough, int limit = int.MaxValue)
    {
        using var held = _gate.EnterScope();
        ulong freed = 0;
        for (var node = _order.First; node is not null && freed < target && limit > 0;)
        {
            var next = node.Next;
            var (key, entry) = node.Value;
            if (entry.ReturnedAt > returnedThrough)
                break;
            if (heap is null || entry.Heap == heap)
            {
                _order.Remove(node);
                RemoveFromStack(key, entry);
                _pooledBytes -= entry.Size;
                Destroy(entry);
                freed += entry.Size;
                limit--;
            }
            node = next;
        }
        return freed;
    }

    public void Dispose()
    {
        using var held = _gate.EnterScope();
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var (_, entry) in _order)
        {
            Destroy(entry);
        }

        _order.Clear();
        _entries.Clear();
        _spareStack = null;
        _pooledBytes = 0;
    }
}
