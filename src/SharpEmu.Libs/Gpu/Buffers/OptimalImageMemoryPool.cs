// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Buffers;

// A place in device memory an image is bound to: its own allocation, or a range of a block.
public readonly record struct OptimalImageMemoryAllocation(DeviceMemory Memory, ulong Offset, ulong Size, int Block, uint MemoryType = 0);

// Images share large device-memory blocks instead of one allocation each: a driver caps the
// number of live allocations (4096 on common Windows drivers) far below the number of textures
// a bindless title keeps resident. Images are all optimal-tiled, so no linear/optimal
// granularity separates them; each range only honours its own alignment.
public sealed unsafe class OptimalImageMemoryPool
{
    public const ulong BlockSize = 256UL << 20;
    public const ulong DedicatedThreshold = 64UL << 20;

    // Freed memory kept for the next image instead of going back to the driver. The image
    // cache recreates images all the time (a target that grows, overlapping views) mostly at
    // the same sizes; handing each one back and allocating it again costs a driver call and
    // a zeroed allocation every time, and capture tools keep every freed allocation alive.
    public const int MaxEmptyBlocks = 4;
    public const ulong RetainedDedicatedLimit = 1UL << 30;

    private sealed class Block
    {
        public required DeviceMemory Memory;
        public required uint MemoryType;
        public required ulong Size;
        public readonly SortedDictionary<ulong, ulong> Free = new();
        public ulong Used;
    }

    private readonly IDeviceMemoryAllocator _device;
    private readonly ulong _blockSize;
    private readonly bool _deviceAddress;
    private readonly List<Block?> _blocks = [];
    private readonly List<(DeviceMemory Memory, ulong Size, uint MemoryType)> _retainedDedicated = [];
    private ulong _retainedDedicatedBytes;
    private readonly object _gate = new();
    private long _allocatedBytes;
    private long _placedBytes;

    // `deviceAddress` allocates the blocks with VK_MEMORY_ALLOCATE_DEVICE_ADDRESS_BIT, so
    // buffers with a shader device address can live in them.
    public OptimalImageMemoryPool(IDeviceMemoryAllocator device, ulong blockSize = BlockSize, bool deviceAddress = false)
    {
        _device = device;
        _blockSize = blockSize;
        _deviceAddress = deviceAddress;
    }

    // Bytes reserved from the Vulkan driver, including free space left inside
    // pooled blocks and dedicated allocations still held by the pool.
    public ulong AllocatedBytes => (ulong)Math.Max(Volatile.Read(ref _allocatedBytes), 0);

    // Bytes occupied by live image placements. This excludes free ranges inside
    // pooled blocks and is therefore the useful fragmentation comparison.
    public ulong PlacedBytes => (ulong)Math.Max(Volatile.Read(ref _placedBytes), 0);

    public ulong UnusedBytes
    {
        get
        {
            lock (_gate) return checked((ulong)(_allocatedBytes - _placedBytes));
        }
    }

    // Ranges placed in blocks, and blocks held from the driver (empty ones included).
    public int Placements => Volatile.Read(ref _placements);

    public int Blocks
    {
        get
        {
            lock (_gate)
            {
                return _blocks.Count(block => block is not null);
            }
        }
    }

    private int _placements;

    public Result Allocate(in MemoryRequirements requirements, uint memoryType, out OptimalImageMemoryAllocation memory)
    {
        memory = default;
        if (requirements.Size >= DedicatedThreshold)
        {
            lock (_gate)
            {
                var size = requirements.Size;
                var retained = _retainedDedicated.FindIndex(entry => entry.Size == size && entry.MemoryType == memoryType);
                if (retained >= 0)
                {
                    var entry = _retainedDedicated[retained];
                    _retainedDedicated.RemoveAt(retained);
                    _retainedDedicatedBytes -= entry.Size;
                    memory = new OptimalImageMemoryAllocation(entry.Memory, 0, entry.Size, -1, entry.MemoryType);
                    Interlocked.Add(ref _placedBytes, checked((long)entry.Size));
                    return Result.Success;
                }
            }

            var info = new MemoryAllocateInfo { SType = StructureType.MemoryAllocateInfo, AllocationSize = requirements.Size, MemoryTypeIndex = memoryType };
            var dedicated = _device.AllocateMemory(info, out var handle);
            if (dedicated == Result.Success)
            {
                memory = new OptimalImageMemoryAllocation(handle, 0, requirements.Size, -1, memoryType);
                Interlocked.Add(ref _allocatedBytes, checked((long)requirements.Size));
                Interlocked.Add(ref _placedBytes, checked((long)requirements.Size));
            }

            return dedicated;
        }

        var alignment = Math.Max(requirements.Alignment, 1UL);
        lock (_gate)
        {
            for (var index = 0; index < _blocks.Count; index++)
            {
                if (_blocks[index] is { } block && block.MemoryType == memoryType && TryPlace(block, requirements.Size, alignment, out var offset))
                {
                    memory = new OptimalImageMemoryAllocation(block.Memory, offset, requirements.Size, index);
                    Interlocked.Increment(ref _placements);
                    Interlocked.Add(ref _placedBytes, checked((long)requirements.Size));
                    return Result.Success;
                }
            }

            var addressFlags = new MemoryAllocateFlagsInfo { SType = StructureType.MemoryAllocateFlagsInfo, Flags = MemoryAllocateFlags.DeviceAddressBit };
            var blockInfo = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                PNext = _deviceAddress ? &addressFlags : null,
                AllocationSize = Math.Max(_blockSize, requirements.Size),
                MemoryTypeIndex = memoryType,
            };
            var result = _device.AllocateMemory(blockInfo, out var blockMemory);
            if (result == Result.ErrorOutOfDeviceMemory && blockInfo.AllocationSize > requirements.Size)
            {
                // A pooling policy must not reject an image whose actual storage
                // still fits. Keep all allocation flags, but request its exact size.
                blockInfo.AllocationSize = requirements.Size;
                result = _device.AllocateMemory(blockInfo, out blockMemory);
            }
            if (result != Result.Success)
            {
                return result;
            }

            var created = new Block { Memory = blockMemory, MemoryType = memoryType, Size = blockInfo.AllocationSize };
            created.Free.Add(0, blockInfo.AllocationSize);
            var slot = _blocks.IndexOf(null);
            if (slot < 0)
            {
                slot = _blocks.Count;
                _blocks.Add(created);
            }
            else
            {
                _blocks[slot] = created;
            }

            TryPlace(created, requirements.Size, alignment, out var placed);
            Interlocked.Add(ref _allocatedBytes, checked((long)blockInfo.AllocationSize));
            Interlocked.Add(ref _placedBytes, checked((long)requirements.Size));
            memory = new OptimalImageMemoryAllocation(blockMemory, placed, requirements.Size, slot);
            Interlocked.Increment(ref _placements);
            return Result.Success;
        }
    }

    public void Free(in OptimalImageMemoryAllocation memory)
    {
        if (memory.Memory.Handle == 0)
        {
            return;
        }

        if (memory.Block < 0)
        {
            Interlocked.Add(ref _placedBytes, -checked((long)memory.Size));
            lock (_gate)
            {
                _retainedDedicated.Add((memory.Memory, memory.Size, memory.MemoryType));
                _retainedDedicatedBytes += memory.Size;
                // Past the limit the oldest retained allocations go back to the driver.
                while (_retainedDedicatedBytes > RetainedDedicatedLimit)
                {
                    var oldest = _retainedDedicated[0];
                    _retainedDedicated.RemoveAt(0);
                    _retainedDedicatedBytes -= oldest.Size;
                    _device.FreeMemory(oldest.Memory);
                    Interlocked.Add(ref _allocatedBytes, -checked((long)oldest.Size));
                }
            }

            return;
        }

        lock (_gate)
        {
            var block = _blocks[memory.Block] ?? throw new InvalidOperationException("The image memory block was already released.");
            var start = memory.Offset;
            var end = memory.Offset + memory.Size;

            // Merge with the free ranges on either side.
            foreach (var (freeStart, freeLength) in block.Free)
            {
                if (freeStart + freeLength == start)
                {
                    start = freeStart;
                    block.Free.Remove(freeStart);
                    break;
                }
            }

            if (block.Free.TryGetValue(end, out var nextLength))
            {
                block.Free.Remove(end);
                end += nextLength;
            }

            block.Free[start] = end - start;
            block.Used -= memory.Size;
            Interlocked.Decrement(ref _placements);
            Interlocked.Add(ref _placedBytes, -checked((long)memory.Size));
            // An emptied block stays for the next images while few enough blocks are empty.
            if (block.Used == 0 && _blocks.Count(candidate => candidate is { Used: 0 }) > MaxEmptyBlocks)
            {
                _device.FreeMemory(block.Memory);
                _blocks[memory.Block] = null;
                Interlocked.Add(ref _allocatedBytes, -checked((long)block.Size));
            }
        }
    }

    // Hands every retained allocation and empty block back to the driver, for memory pressure.
    public void ReleaseRetained()
    {
        lock (_gate)
        {
            foreach (var (retained, size, _) in _retainedDedicated)
            {
                _device.FreeMemory(retained);
                Interlocked.Add(ref _allocatedBytes, -checked((long)size));
            }

            _retainedDedicated.Clear();
            _retainedDedicatedBytes = 0;
            for (var index = 0; index < _blocks.Count; index++)
            {
                if (_blocks[index] is { Used: 0 } empty)
                {
                    _device.FreeMemory(empty.Memory);
                    _blocks[index] = null;
                    Interlocked.Add(ref _allocatedBytes, -checked((long)empty.Size));
                }
            }
        }
    }

    private static bool TryPlace(Block block, ulong size, ulong alignment, out ulong offset)
    {
        foreach (var (start, length) in block.Free)
        {
            var aligned = (start + alignment - 1) / alignment * alignment;
            var padding = aligned - start;
            if (padding > length || length - padding < size)
            {
                continue;
            }

            block.Free.Remove(start);
            if (padding != 0)
            {
                block.Free[start] = padding;
            }

            var tail = length - padding - size;
            if (tail != 0)
            {
                block.Free[aligned + size] = tail;
            }

            block.Used += size;
            offset = aligned;
            return true;
        }

        offset = 0;
        return false;
    }
}
