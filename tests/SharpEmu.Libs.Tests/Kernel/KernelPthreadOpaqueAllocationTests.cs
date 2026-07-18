// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

[CollectionDefinition("KernelPthreadOpaqueAllocation", DisableParallelization = true)]
public sealed class KernelPthreadOpaqueAllocationCollectionDefinition;

[Collection("KernelPthreadOpaqueAllocation")]
public sealed class KernelPthreadOpaqueAllocationTests
{
    private const ulong MemoryBase = 0x0000_7FFF_4000_0000;
    private const ulong MutexAddress = MemoryBase + 0x1000;
    private const ulong CondAddress = MemoryBase + 0x2000;
    private const ulong AllocationBase = MemoryBase + 0x1_0000;

    [Fact]
    public void InitDestroy_FreesOpaqueHandlesClearsSlotsAndSupportsHandleReuse()
    {
        var memory = new TrackingGuestMemory(MemoryBase, 0x2_0000, AllocationBase);
        var context = new CpuContext(memory, Generation.Gen5);

        var firstHandles = InitializePair(context, memory);
        DestroyPair(context, memory, firstHandles);

        var secondHandles = InitializePair(context, memory);
        Assert.Equal(
            new[] { firstHandles.Mutex, firstHandles.Cond }.Order(),
            new[] { secondHandles.Mutex, secondHandles.Cond }.Order());
        DestroyPair(context, memory, secondHandles);

        Assert.Equal(0, memory.ActiveAllocationCount);
        Assert.Equal(2, memory.GetSuccessfulFreeCount(firstHandles.Mutex));
        Assert.Equal(2, memory.GetSuccessfulFreeCount(firstHandles.Cond));
    }

    private static (ulong Mutex, ulong Cond) InitializePair(
        CpuContext context,
        TrackingGuestMemory memory)
    {
        context[CpuRegister.Rdi] = MutexAddress;
        context[CpuRegister.Rsi] = 0;
        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexInit(context));
        Assert.True(context.TryReadUInt64(MutexAddress, out var mutexHandle));
        Assert.NotEqual(0UL, mutexHandle);
        Assert.True(memory.IsAllocationActive(mutexHandle));

        context[CpuRegister.Rdi] = CondAddress;
        context[CpuRegister.Rsi] = 0;
        Assert.Equal(0, KernelPthreadCompatExports.PthreadCondInit(context));
        Assert.True(context.TryReadUInt64(CondAddress, out var condHandle));
        Assert.NotEqual(0UL, condHandle);
        Assert.NotEqual(mutexHandle, condHandle);
        Assert.True(memory.IsAllocationActive(condHandle));
        Assert.Equal(2, memory.ActiveAllocationCount);

        return (mutexHandle, condHandle);
    }

    private static void DestroyPair(
        CpuContext context,
        TrackingGuestMemory memory,
        (ulong Mutex, ulong Cond) handles)
    {
        context[CpuRegister.Rdi] = MutexAddress;
        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexDestroy(context));
        Assert.True(context.TryReadUInt64(MutexAddress, out var mutexSlot));
        Assert.Equal(0UL, mutexSlot);
        Assert.False(memory.IsAllocationActive(handles.Mutex));
        Assert.True(memory.IsAllocationActive(handles.Cond));

        context[CpuRegister.Rdi] = CondAddress;
        Assert.Equal(0, KernelPthreadCompatExports.PthreadCondDestroy(context));
        Assert.True(context.TryReadUInt64(CondAddress, out var condSlot));
        Assert.Equal(0UL, condSlot);
        Assert.False(memory.IsAllocationActive(handles.Cond));
        Assert.Equal(0, memory.ActiveAllocationCount);
    }

    private sealed class TrackingGuestMemory : ICpuMemory, IGuestMemoryAllocator
    {
        private readonly ulong _baseAddress;
        private readonly byte[] _storage;
        private readonly List<(ulong Address, ulong Size)> _freeBlocks = new();
        private readonly Dictionary<ulong, ulong> _activeAllocations = new();
        private readonly Dictionary<ulong, int> _successfulFreeCounts = new();
        private ulong _nextAllocationAddress;

        public TrackingGuestMemory(ulong baseAddress, int size, ulong allocationBase)
        {
            _baseAddress = baseAddress;
            _storage = new byte[size];
            _nextAllocationAddress = allocationBase;
        }

        public int ActiveAllocationCount => _activeAllocations.Count;

        public bool TryRead(ulong virtualAddress, Span<byte> destination)
        {
            if (!TryResolve(virtualAddress, destination.Length, out var offset))
            {
                return false;
            }

            _storage.AsSpan(offset, destination.Length).CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
        {
            if (!TryResolve(virtualAddress, source.Length, out var offset))
            {
                return false;
            }

            source.CopyTo(_storage.AsSpan(offset, source.Length));
            return true;
        }

        public bool TryAllocateGuestMemory(ulong size, ulong alignment, out ulong address)
        {
            alignment = Math.Max(alignment, 1);
            for (var index = 0; index < _freeBlocks.Count; index++)
            {
                var block = _freeBlocks[index];
                if (block.Size != size || block.Address % alignment != 0)
                {
                    continue;
                }

                _freeBlocks.RemoveAt(index);
                _activeAllocations.Add(block.Address, block.Size);
                address = block.Address;
                return true;
            }

            var remainder = _nextAllocationAddress % alignment;
            address = remainder == 0
                ? _nextAllocationAddress
                : _nextAllocationAddress + alignment - remainder;
            if (!TryResolve(address, checked((int)size), out _))
            {
                address = 0;
                return false;
            }

            _activeAllocations.Add(address, size);
            _nextAllocationAddress = address + size;
            return true;
        }

        public bool TryFreeGuestMemory(ulong address)
        {
            if (!_activeAllocations.Remove(address, out var size))
            {
                return false;
            }

            _freeBlocks.Add((address, size));
            _successfulFreeCounts[address] = GetSuccessfulFreeCount(address) + 1;
            return true;
        }

        public bool IsAllocationActive(ulong address) => _activeAllocations.ContainsKey(address);

        public int GetSuccessfulFreeCount(ulong address) =>
            _successfulFreeCounts.GetValueOrDefault(address);

        private bool TryResolve(ulong virtualAddress, int length, out int offset)
        {
            offset = 0;
            if (virtualAddress < _baseAddress || length < 0)
            {
                return false;
            }

            var relative = virtualAddress - _baseAddress;
            if (relative + (ulong)length > (ulong)_storage.Length)
            {
                return false;
            }

            offset = checked((int)relative);
            return true;
        }
    }
}
