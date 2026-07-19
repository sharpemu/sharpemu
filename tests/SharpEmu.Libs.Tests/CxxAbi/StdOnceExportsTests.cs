// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.CxxAbi;
using Xunit;

namespace SharpEmu.Libs.Tests.CxxAbi;

public sealed class StdOnceExportsTests
{
    [Fact]
    public void CxaDecrementExceptionRefcount_NullPointer_IsNoOpPerItaniumAbi()
    {
        var memory = new RecordingCpuMemory(0x1_0000_0000, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = 0;

        var result = StdOnceExports.CxaDecrementExceptionRefcount(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
        Assert.Empty(memory.Reads);
        Assert.Empty(memory.Writes);
    }

    [Fact]
    public void StdExecuteOnce_SameFlagAddressTwice_InvokesCallbackExactlyOnce()
    {
        // Uses a distinct base address from the other tests in this class: the
        // once-flag "already run" state is tracked in a static dictionary keyed
        // by guest address (matching the real once_flag's lifetime), so reusing
        // an address across test methods would leak state between them.
        const ulong memoryBase = 0x2_0000_0000;
        const ulong onceFlagAddress = memoryBase + 0x100;
        const ulong callbackAddress = memoryBase + 0x200;
        const ulong stateAddress = memoryBase + 0x300;

        var memory = new RecordingCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        var scheduler = new RecordingScheduler();
        GuestThreadExecution.Scheduler = scheduler;
        try
        {
            context[CpuRegister.Rdi] = onceFlagAddress;
            context[CpuRegister.Rsi] = callbackAddress;
            context[CpuRegister.Rdx] = stateAddress;

            var first = StdOnceExports.StdExecuteOnce(context);
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, first);
            Assert.Equal(1, scheduler.CallCount);
            Assert.Equal(stateAddress, scheduler.LastArg2);

            var second = StdOnceExports.StdExecuteOnce(context);
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, second);
            Assert.Equal(1, scheduler.CallCount); // must not be invoked again
        }
        finally
        {
            GuestThreadExecution.Scheduler = null;
        }
    }

    [Fact]
    public void StdExecuteOnce_DifferentFlagAddresses_InvokesCallbackForEach()
    {
        const ulong memoryBase = 0x3_0000_0000;
        const ulong callbackAddress = memoryBase + 0x200;
        const ulong stateAddress = memoryBase + 0x300;

        var memory = new RecordingCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        var scheduler = new RecordingScheduler();
        GuestThreadExecution.Scheduler = scheduler;
        try
        {
            context[CpuRegister.Rsi] = callbackAddress;
            context[CpuRegister.Rdx] = stateAddress;

            context[CpuRegister.Rdi] = memoryBase + 0x100;
            StdOnceExports.StdExecuteOnce(context);

            context[CpuRegister.Rdi] = memoryBase + 0x180;
            StdOnceExports.StdExecuteOnce(context);

            Assert.Equal(2, scheduler.CallCount);
        }
        finally
        {
            GuestThreadExecution.Scheduler = null;
        }
    }

    private sealed class RecordingScheduler : IGuestThreadScheduler
    {
        public int CallCount { get; private set; }

        public ulong LastArg2 { get; private set; }

        public bool SupportsGuestContextTransfer => false;

        public void RegisterGuestThreadContext(ulong threadHandle, CpuContext context)
        {
        }

        public bool TryStartThread(CpuContext creatorContext, GuestThreadStartRequest request, out string? error)
        {
            error = null;
            return false;
        }

        public bool TryJoinThread(CpuContext callerContext, ulong threadHandle, out ulong returnValue, out string? error)
        {
            returnValue = 0;
            error = null;
            return false;
        }

        public void Pump(CpuContext callerContext, string reason)
        {
        }

        public int WakeBlockedThreads(string wakeKey, int maxCount = int.MaxValue) => 0;

        public bool TrySetGuestThreadPriority(ulong guestThreadHandle, int guestPriority) => false;

        public bool TrySetGuestThreadAffinity(ulong guestThreadHandle, ulong affinityMask) => false;

        public IReadOnlyList<GuestThreadSnapshot> SnapshotThreads() => Array.Empty<GuestThreadSnapshot>();

        public bool TryCallGuestFunction(
            CpuContext callerContext,
            ulong entryPoint,
            ulong arg0,
            ulong arg1,
            ulong stackAddress,
            ulong stackSize,
            string reason,
            out string? error)
        {
            error = null;
            return false;
        }

        public bool TryCallGuestFunction(
            CpuContext callerContext,
            ulong entryPoint,
            ulong arg0,
            ulong arg1,
            ulong arg2,
            ulong stackAddress,
            ulong stackSize,
            string reason,
            out ulong returnValue,
            out string? error)
        {
            CallCount++;
            LastArg2 = arg2;
            returnValue = 0;
            error = null;
            return true;
        }

        public bool TryCallGuestContinuation(
            CpuContext callerContext,
            GuestCpuContinuation continuation,
            string reason,
            out string? error)
        {
            error = null;
            return false;
        }

        public bool TryRaiseGuestException(
            CpuContext callerContext,
            ulong threadHandle,
            ulong handler,
            int exceptionType,
            out string? error)
        {
            error = null;
            return false;
        }
    }

    private sealed class RecordingCpuMemory : ICpuMemory, IGuestMemoryAllocator
    {
        private readonly ulong _baseAddress;
        private readonly byte[] _storage;
        private ulong _nextAllocation;

        public List<ulong> Reads { get; } = new();

        public List<ulong> Writes { get; } = new();

        public RecordingCpuMemory(ulong baseAddress, int size)
        {
            _baseAddress = baseAddress;
            _storage = new byte[size];
            _nextAllocation = baseAddress + 0x800;
        }

        public bool TryRead(ulong virtualAddress, Span<byte> destination)
        {
            Reads.Add(virtualAddress);
            if (!TryResolve(virtualAddress, destination.Length, out var offset))
            {
                return false;
            }

            _storage.AsSpan(offset, destination.Length).CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
        {
            Writes.Add(virtualAddress);
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
