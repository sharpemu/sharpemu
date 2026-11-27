// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Numerics;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Buffers;

// Small buffers take a power-of-two block out of a shared, persistently mapped chunk
// instead of a dedicated allocation. Games stream constants through fresh ranges every
// frame, and the buffer cache created and freed a device allocation for each of them;
// vkAllocateMemory/vkMapMemory/vkFreeMemory then cost ~15% of the render thread in busy
// Astro Bot scenes, and every live allocation counts against maxMemoryAllocationCount.
internal sealed unsafe class GpuMemorySlabs
{
    public const ulong MinBlockSize = 4096;
    public const ulong MaxBlockSize = 1024 * 1024;
    private const ulong ChunkSize = 8 * 1024 * 1024;
    private static readonly int ClassCount = BitOperations.Log2(MaxBlockSize) - BitOperations.Log2(MinBlockSize) + 1;

    private readonly GpuDeviceInfo _device;
    private readonly object _gate = new();
    private readonly Dictionary<(uint MemoryType, bool DeviceAddress), Pool> _pools = [];
    private readonly Dictionary<DeviceMemory, Chunk> _chunks = [];
    private readonly List<Block> _quarantine = [];
    private int _foreignReaders;

    public GpuMemorySlabs(GpuDeviceInfo device) => _device = device;

    // Recycled blocks still hold their previous buffer's bytes; fresh ones come zeroed with their chunk.
    public readonly record struct Block(DeviceMemory Memory, ulong Offset, ulong Size, nint MappedAddress, uint MemoryType, bool DeviceAddress, bool Recycled)
    {
        public byte* Mapped => (byte*)MappedAddress;
    }

    private sealed class Chunk(DeviceMemory memory, Bucket bucket)
    {
        public readonly DeviceMemory Memory = memory;
        public readonly Bucket Bucket = bucket;
        // Includes retired blocks until the last foreign read completes.
        public int Outstanding;
    }

    private sealed class Bucket
    {
        public readonly List<(Chunk Chunk, ulong Offset, nint Mapped, bool Recycled)> Free = [];
        public Chunk? Warm;
    }

    private sealed class Pool
    {
        public readonly Bucket[] Buckets = Enumerable.Range(0, ClassCount).Select(_ => new Bucket()).ToArray();
    }

    // A readback on the second queue copies from buffers the render thread may retire (and
    // recycle) while the copy is still pending. A dedicated allocation kept its old bytes
    // after vkFreeMemory, which hid that race; a recycled block does not. Released blocks
    // therefore wait until no readback that might have picked their buffer is running.
    public ForeignReadScope BeginForeignRead()
    {
        Interlocked.Increment(ref _foreignReaders);
        return new ForeignReadScope(this);
    }

    public readonly struct ForeignReadScope(GpuMemorySlabs owner) : IDisposable
    {
        public void Dispose()
        {
            if (Interlocked.Decrement(ref owner._foreignReaders) == 0)
                lock (owner._gate) owner.CollectReleased();
        }
    }

    public static bool Fits(in MemoryRequirements requirements) =>
        requirements.Size <= MaxBlockSize && requirements.Alignment <= MaxBlockSize;

    public bool TryAllocate(uint memoryType, bool deviceAddress, in MemoryRequirements requirements, out Block block)
    {
        block = default;
        var blockSize = Math.Max(MinBlockSize, BitOperations.RoundUpToPowerOf2(Math.Max(requirements.Size, requirements.Alignment)));
        if (blockSize > MaxBlockSize)
        {
            return false;
        }

        var sizeClass = BitOperations.Log2(blockSize) - BitOperations.Log2(MinBlockSize);
        lock (_gate)
        {
            CollectReleased();

            if (!_pools.TryGetValue((memoryType, deviceAddress), out var pool))
            {
                pool = new Pool();
                _pools.Add((memoryType, deviceAddress), pool);
            }

            var bucket = pool.Buckets[sizeClass];
            var free = bucket.Free;
            if (free.Count == 0 && !TryAddChunk(memoryType, deviceAddress, blockSize, bucket))
            {
                return false;
            }

            var (chunk, offset, mapped, recycled) = free[^1];
            free.RemoveAt(free.Count - 1);
            if (bucket.Warm == chunk) bucket.Warm = null;
            chunk.Outstanding++;
            block = new Block(chunk.Memory, offset, blockSize, mapped, memoryType, deviceAddress, recycled);
            return true;
        }
    }

    public void Release(in Block block)
    {
        lock (_gate)
        {
            _quarantine.Add(block);
            CollectReleased();
        }
    }

    private void CollectReleased()
    {
        if (Volatile.Read(ref _foreignReaders) != 0) return;
        foreach (var released in _quarantine)
        {
            var chunk = _chunks[released.Memory];
            chunk.Bucket.Free.Add((chunk, released.Offset, released.MappedAddress, true));
            if (--chunk.Outstanding == 0)
            {
                _device.Residency?.Find(chunk.Memory)?.Use(GpuResidency.Role.EmptySlab);
                KeepOneEmptyChunk(chunk);
            }
        }
        _quarantine.Clear();
    }

    private void KeepOneEmptyChunk(Chunk chunk)
    {
        var bucket = chunk.Bucket;
        if (bucket.Warm is null || bucket.Warm == chunk)
        {
            bucket.Warm = chunk;
            return;
        }
        bucket.Free.RemoveAll(slot => slot.Chunk == chunk);
        _chunks.Remove(chunk.Memory);
        _device.FreeMemory(chunk.Memory);
    }

    // Frees every chunk; only valid once no buffer still uses one (device teardown).
    public void Destroy()
    {
        lock (_gate)
        {
            foreach (var chunk in _chunks.Keys)
            {
                _device.FreeMemory(chunk);
            }

            _chunks.Clear();
            _pools.Clear();
            _quarantine.Clear();
        }
    }

    private bool TryAddChunk(uint memoryType, bool deviceAddress, ulong blockSize, Bucket bucket)
    {
        var flagsInfo = new MemoryAllocateFlagsInfo
        {
            SType = StructureType.MemoryAllocateFlagsInfo,
            Flags = MemoryAllocateFlags.DeviceAddressBit,
        };
        var allocateInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            PNext = deviceAddress ? &flagsInfo : null,
            AllocationSize = ChunkSize,
            MemoryTypeIndex = memoryType,
        };
        if (_device.AllocateMemory(allocateInfo, out var memory, GpuResidency.Role.Slab) != Result.Success)
        {
            return false;
        }

        byte* mapped = null;
        if ((_device.GetMemoryTypeFlags(memoryType) & MemoryPropertyFlags.HostVisibleBit) != 0)
        {
            void* pointer;
            if (_device.Vk.MapMemory(_device.Device, memory, 0, Vk.WholeSize, 0, &pointer) != Result.Success)
            {
                _device.FreeMemory(memory);
                return false;
            }

            mapped = (byte*)pointer;
        }

        var chunk = new Chunk(memory, bucket);
        _chunks.Add(memory, chunk);
        // Push in reverse so blocks come out in address order.
        for (var offset = ChunkSize - blockSize; ; offset -= blockSize)
        {
            bucket.Free.Add((chunk, offset, mapped == null ? 0 : (nint)(mapped + offset), false));
            if (offset == 0)
            {
                break;
            }
        }

        return true;
    }
}
