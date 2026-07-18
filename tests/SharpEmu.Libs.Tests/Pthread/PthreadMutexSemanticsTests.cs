// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Pthread;

public sealed class PthreadMutexSemanticsTests
{
    [Fact]
    public void AdaptiveMutex_SelfLockUsesCompatibilityRecursion()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong mutexAddress = memoryBase + 0x100;
        var memory = new AllocatingCpuMemory(memoryBase, 0x4000);
        var context = new CpuContext(memory, Generation.Gen5);
        Assert.True(context.TryWriteUInt64(mutexAddress, 1)); // Static adaptive initializer.
        context[CpuRegister.Rdi] = mutexAddress;

        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexLock(context));
        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexLock(context));
        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexUnlock(context));
        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexUnlock(context));
    }

    private sealed class AllocatingCpuMemory : ICpuMemory, IGuestMemoryAllocator
    {
        private readonly ulong _baseAddress;
        private readonly byte[] _storage;
        private ulong _nextAllocation;

        public AllocatingCpuMemory(ulong baseAddress, int size)
        {
            _baseAddress = baseAddress;
            _storage = new byte[size];
            _nextAllocation = baseAddress + 0x1000;
        }

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
            var mask = alignment - 1;
            var aligned = (_nextAllocation + mask) & ~mask;
            if (!TryResolve(aligned, checked((int)size), out _))
            {
                address = 0;
                return false;
            }

            address = aligned;
            _nextAllocation = aligned + size;
            return true;
        }

        public bool TryFreeGuestMemory(ulong address) =>
            address >= _baseAddress && address < _baseAddress + (ulong)_storage.Length;

        private bool TryResolve(ulong virtualAddress, int length, out int offset)
        {
            offset = 0;
            if (virtualAddress < _baseAddress)
            {
                return false;
            }

            var relative = virtualAddress - _baseAddress;
            if (relative + (ulong)length > (ulong)_storage.Length)
            {
                return false;
            }

            offset = (int)relative;
            return true;
        }
    }
}
