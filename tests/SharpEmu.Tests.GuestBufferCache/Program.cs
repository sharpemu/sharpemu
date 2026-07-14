// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.VideoOut;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

var storage = new Dictionary<GuestBufferResource, byte[]>();
ulong nextHandle = 1;
var destroyed = 0;

using var cache = new GuestBufferCache(
    size => new GuestBufferAllocation(
        new VkBuffer(nextHandle++),
        new DeviceMemory(nextHandle++)),
    resource =>
    {
        storage.Remove(resource);
        destroyed++;
    },
    (resource, offset, data) =>
    {
        if (!storage.TryGetValue(resource, out var bytes))
        {
            bytes = new byte[checked((int)resource.Size)];
            storage.Add(resource, bytes);
        }
        data.CopyTo(bytes.AsSpan(checked((int)offset)));
    },
    maximumCachedBytes: 32);

var zero = Words(0);
var first = cache.ObtainBuffer(
    0x1000,
    zero,
    GuestBufferUsage.Storage,
    GuestBufferAccess.Write,
    1,
    allowCaching: true);
cache.Release(first);

// Stand in for a compute shader changing the cached allocation.
Words(37).CopyTo(storage[first.Resource], 0);
var rewritten = cache.ObtainBuffer(
    0x1000,
    zero,
    GuestBufferUsage.Storage,
    GuestBufferAccess.Write,
    2,
    allowCaching: true);
Assert(ReferenceEquals(first.Resource, rewritten.Resource),
    "an idle exact allocation was not reused");
Assert(storage[rewritten.Resource].SequenceEqual(zero),
    "CPU rewrite with the same bytes did not replace GPU contents");

var renamed = cache.ObtainBuffer(
    0x1000,
    Words(9),
    GuestBufferUsage.Storage,
    GuestBufferAccess.Write,
    3,
    allowCaching: true);
Assert(!ReferenceEquals(rewritten.Resource, renamed.Resource),
    "an in-flight allocation was overwritten instead of renamed");
Assert(storage[renamed.Resource].SequenceEqual(Words(9)),
    "renamed allocation did not receive current CPU data");
cache.Release(rewritten);
cache.Release(renamed);

var readOnly = cache.ObtainBuffer(
    0x3000,
    Words(5),
    GuestBufferUsage.Storage,
    GuestBufferAccess.Read,
    4,
    allowCaching: true);
Assert(!readOnly.Resource.Cached, "read-only buffer entered the persistent cache");
cache.Release(readOnly);

var zeroAddress = cache.ObtainBuffer(
    0,
    Words(6),
    GuestBufferUsage.Index,
    GuestBufferAccess.Read,
    5,
    allowCaching: true);
Assert(!zeroAddress.Resource.Cached, "guest address zero was used as a persistent key");
cache.Release(zeroAddress);

var baseRange = cache.ObtainBuffer(
    0x5000,
    Words(1, 2, 3, 4),
    GuestBufferUsage.Storage,
    GuestBufferAccess.Write,
    6,
    allowCaching: true);
cache.Release(baseRange);
var overlap = cache.ObtainBuffer(
    0x5008,
    Words(7, 8, 9, 10),
    GuestBufferUsage.Storage,
    GuestBufferAccess.Write,
    7,
    allowCaching: true);
Assert(!ReferenceEquals(baseRange.Resource, overlap.Resource),
    "overlapping logical ranges were merged into one allocation");
cache.Release(overlap);

cache.Collect(100);
Assert(cache.CachedBytes <= 32, "cache exceeded its byte budget after collection");
Assert(destroyed >= 3, "transient or over-budget allocations were not destroyed");

Console.WriteLine("GuestBufferCache CPU ownership, renaming, fallback, and budget tests passed.");

static byte[] Words(params uint[] values)
{
    var bytes = new byte[values.Length * sizeof(uint)];
    for (var index = 0; index < values.Length; index++)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(index * sizeof(uint)),
            values[index]);
    }
    return bytes;
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
