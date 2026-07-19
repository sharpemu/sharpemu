// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Threading.Tasks;
using SharpEmu.HLE;
using SharpEmu.Libs.CxxAbi;
using Xunit;

namespace SharpEmu.Libs.Tests.CxxAbi;

public sealed class StdMutexExportsTests
{
    [Fact]
    public void MtxInit_WritesNonZeroHandleAndLockUnlockRoundTrips()
    {
        const ulong memoryBase = 0x4_0000_0000;
        const ulong mtxSlot = memoryBase + 0x100;
        var memory = new RecordingCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);

        context[CpuRegister.Rdi] = mtxSlot;
        context[CpuRegister.Rsi] = 0; // plain, non-recursive
        Assert.Equal(0, StdMutexExports.MtxInit(context));
        Assert.True(context.TryReadUInt64(mtxSlot, out var handle));
        Assert.NotEqual(0UL, handle);

        context[CpuRegister.Rdi] = handle;
        Assert.Equal(0, StdMutexExports.MtxLock(context));
        Assert.Equal(0, StdMutexExports.MtxUnlock(context));
    }

    [Fact]
    public async Task MtxTrylock_FailsWhileAlreadyLockedByPlainMutex()
    {
        const ulong memoryBase = 0x5_0000_0000;
        const ulong mtxSlot = memoryBase + 0x100;
        var memory = new RecordingCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);

        context[CpuRegister.Rdi] = mtxSlot;
        context[CpuRegister.Rsi] = 0;
        StdMutexExports.MtxInit(context);
        context.TryReadUInt64(mtxSlot, out var handle);
        context[CpuRegister.Rdi] = handle;

        Assert.Equal(0, StdMutexExports.MtxLock(context));

        // Simulate a different thread contending for the same plain mutex: run the
        // trylock on a background thread so it's a genuinely different owner.
        var task = System.Threading.Tasks.Task.Run(() =>
        {
            var otherContext = new CpuContext(memory, Generation.Gen5);
            otherContext[CpuRegister.Rdi] = handle;
            return StdMutexExports.MtxTrylock(otherContext);
        });
        Assert.Equal(4, await task); // ThrdBusy

        Assert.Equal(0, StdMutexExports.MtxUnlock(context));
    }

    [Fact]
    public void MtxLock_RecursiveMutex_AllowsSameThreadReentry()
    {
        const ulong memoryBase = 0x6_0000_0000;
        const ulong mtxSlot = memoryBase + 0x100;
        var memory = new RecordingCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);

        context[CpuRegister.Rdi] = mtxSlot;
        context[CpuRegister.Rsi] = 0x100; // _Mtx_recursive
        StdMutexExports.MtxInit(context);
        context.TryReadUInt64(mtxSlot, out var handle);
        context[CpuRegister.Rdi] = handle;

        Assert.Equal(0, StdMutexExports.MtxLock(context));
        Assert.Equal(0, StdMutexExports.MtxLock(context)); // same thread, recursive: must not deadlock
        Assert.Equal(0, StdMutexExports.MtxUnlock(context));
        Assert.Equal(0, StdMutexExports.MtxUnlock(context));
    }

    [Fact]
    public async Task CndSignal_WakesWaiterAndReacquiresMutex()
    {
        const ulong memoryBase = 0x7_0000_0000;
        const ulong mtxSlot = memoryBase + 0x100;
        const ulong condSlot = memoryBase + 0x108;
        var memory = new RecordingCpuMemory(memoryBase, 0x1000);
        var initContext = new CpuContext(memory, Generation.Gen5);

        initContext[CpuRegister.Rdi] = mtxSlot;
        initContext[CpuRegister.Rsi] = 0;
        StdMutexExports.MtxInit(initContext);
        initContext.TryReadUInt64(mtxSlot, out var mtxHandle);

        initContext[CpuRegister.Rdi] = condSlot;
        StdMutexExports.CndInit(initContext);
        initContext.TryReadUInt64(condSlot, out var condHandle);

        var waiterContext = new CpuContext(memory, Generation.Gen5);
        waiterContext[CpuRegister.Rdi] = mtxHandle;
        StdMutexExports.MtxLock(waiterContext);

        var waitTask = System.Threading.Tasks.Task.Run(() =>
        {
            waiterContext[CpuRegister.Rdi] = condHandle;
            waiterContext[CpuRegister.Rsi] = mtxHandle;
            return StdMutexExports.CndWait(waiterContext);
        });

        // Give the waiter a moment to actually enter the wait before signalling.
        System.Threading.Thread.Sleep(100);

        var signalContext = new CpuContext(memory, Generation.Gen5);
        signalContext[CpuRegister.Rdi] = condHandle;
        Assert.Equal(0, StdMutexExports.CndSignal(signalContext));

        var completed = await Task.WhenAny(waitTask, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(waitTask, completed);
        Assert.Equal(0, await waitTask);

        waiterContext[CpuRegister.Rdi] = mtxHandle;
        Assert.Equal(0, StdMutexExports.MtxUnlock(waiterContext));
    }

    private sealed class RecordingCpuMemory : ICpuMemory, IGuestMemoryAllocator
    {
        private readonly ulong _baseAddress;
        private readonly byte[] _storage;
        private ulong _nextAllocation;

        public RecordingCpuMemory(ulong baseAddress, int size)
        {
            _baseAddress = baseAddress;
            _storage = new byte[size];
            _nextAllocation = baseAddress + 0x800;
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
