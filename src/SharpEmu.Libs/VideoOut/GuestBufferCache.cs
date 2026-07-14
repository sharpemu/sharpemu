// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace SharpEmu.Libs.VideoOut;

[Flags]
internal enum GuestBufferUsage
{
    None = 0,
    Storage = 1,
    Vertex = 2,
    Index = 4,
}

[Flags]
internal enum GuestBufferAccess
{
    None = 0,
    Read = 1,
    Write = 2,
    ReadWrite = Read | Write,
}

internal readonly record struct GuestBufferAllocation(
    VkBuffer Buffer,
    DeviceMemory Memory);

internal sealed class GuestBufferResource
{
    public required ulong GuestAddress;
    public required ulong Size;
    public required VkBuffer Buffer;
    public required DeviceMemory Memory;
    public required bool Cached;
    public ulong Generation;
    public ulong LastSubmission;
    public int InFlightReferences;
}

internal sealed class GuestBufferBinding
{
    public required GuestBufferResource Resource { get; init; }
    public required ulong Offset { get; init; }
    public required ulong Size { get; init; }
    public required GuestBufferUsage Usage { get; init; }
    public required GuestBufferAccess Access { get; init; }
}

internal delegate void WriteGuestBuffer(
    GuestBufferResource resource,
    ulong offset,
    ReadOnlySpan<byte> data);

internal sealed class GuestBufferCache : IDisposable
{
    internal const ulong DefaultMaximumCachedBytes = 64UL * 1024 * 1024;

    private readonly Func<ulong, GuestBufferAllocation> _allocate;
    private readonly Action<GuestBufferResource> _destroy;
    private readonly WriteGuestBuffer _write;
    private readonly ulong _maximumCachedBytes;
    private readonly List<GuestBufferResource> _resources = [];
    private readonly Dictionary<(ulong Address, ulong Size), List<GuestBufferResource>>
        _resourcesByRange = [];
    private ulong _nextGeneration;

    public GuestBufferCache(
        Func<ulong, GuestBufferAllocation> allocate,
        Action<GuestBufferResource> destroy,
        WriteGuestBuffer write,
        ulong maximumCachedBytes = DefaultMaximumCachedBytes)
    {
        _allocate = allocate;
        _destroy = destroy;
        _write = write;
        _maximumCachedBytes = maximumCachedBytes;
    }

    public int Count => _resources.Count;
    public ulong CachedBytes { get; private set; }

    public GuestBufferBinding ObtainBuffer(
        ulong address,
        ReadOnlySpan<byte> cpuData,
        GuestBufferUsage usage,
        GuestBufferAccess access,
        ulong submission,
        bool allowCaching)
    {
        var size = (ulong)Math.Max(cpuData.Length, sizeof(uint));
        allowCaching = allowCaching &&
            address != 0 &&
            (access & GuestBufferAccess.Write) != 0;

        var resource = allowCaching
            ? FindReusableExact(address, size) ?? CreateResource(address, size, cached: true)
            : CreateResource(address, size, cached: false);

        // CPU memory remains authoritative until page-fault dirty tracking and
        // GPU-to-CPU readback exist. Never infer ownership from byte equality.
        if (!cpuData.IsEmpty)
        {
            _write(resource, 0, cpuData);
        }

        resource.LastSubmission = submission;
        resource.InFlightReferences++;
        return new GuestBufferBinding
        {
            Resource = resource,
            Offset = 0,
            Size = size,
            Usage = usage,
            Access = access,
        };
    }

    public void Release(GuestBufferBinding binding)
    {
        var resource = binding.Resource;
        if (resource.InFlightReferences <= 0)
        {
            throw new InvalidOperationException("guest buffer reference count underflow");
        }

        resource.InFlightReferences--;
        if (!resource.Cached && resource.InFlightReferences == 0)
        {
            _destroy(resource);
        }
    }

    public void Collect(ulong completedSubmission)
    {
        if (CachedBytes <= _maximumCachedBytes)
        {
            return;
        }

        foreach (var resource in _resources
            .Where(resource =>
                resource.InFlightReferences == 0 &&
                resource.LastSubmission <= completedSubmission)
            .OrderBy(resource => resource.LastSubmission)
            .ToArray())
        {
            RemoveCached(resource);
            if (CachedBytes <= _maximumCachedBytes)
            {
                break;
            }
        }
    }

    public void Dispose()
    {
        if (_resources.Any(resource => resource.InFlightReferences != 0))
        {
            throw new InvalidOperationException("guest buffer cache disposed with in-flight resources");
        }

        foreach (var resource in _resources)
        {
            _destroy(resource);
        }
        _resources.Clear();
        _resourcesByRange.Clear();
        CachedBytes = 0;
    }

    private GuestBufferResource? FindReusableExact(ulong address, ulong size) =>
        _resourcesByRange.TryGetValue((address, size), out var resources)
            ? resources.LastOrDefault(resource => resource.InFlightReferences == 0)
            : null;

    private GuestBufferResource CreateResource(ulong address, ulong size, bool cached)
    {
        var allocation = _allocate(size);
        var resource = new GuestBufferResource
        {
            GuestAddress = address,
            Size = size,
            Buffer = allocation.Buffer,
            Memory = allocation.Memory,
            Cached = cached,
            Generation = ++_nextGeneration,
        };
        if (cached)
        {
            _resources.Add(resource);
            if (!_resourcesByRange.TryGetValue((address, size), out var resources))
            {
                resources = [];
                _resourcesByRange.Add((address, size), resources);
            }
            resources.Add(resource);
            CachedBytes = checked(CachedBytes + size);
        }
        return resource;
    }

    private void RemoveCached(GuestBufferResource resource)
    {
        _resources.Remove(resource);
        var key = (resource.GuestAddress, resource.Size);
        var resources = _resourcesByRange[key];
        resources.Remove(resource);
        if (resources.Count == 0)
        {
            _resourcesByRange.Remove(key);
        }
        CachedBytes -= resource.Size;
        _destroy(resource);
    }
}
