// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

[Collection("KernelPthreadOpaqueAllocation")]
public sealed class KernelPthreadRwlockTests
{
    private const ulong MemoryBase = 0x0000_7FFF_5000_0000;
    private const ulong RwlockAddress = MemoryBase + 0x1000;

    [Fact]
    public async Task ConcurrentLazyTryWriteLock_UsesOneSharedState()
    {
        using var memory = new ConcurrentFirstReadMemory(MemoryBase, 0x2000, RwlockAddress);
        using var acquired = new Barrier(2);

        var first = Task.Run(() => TryLockThenRelease(memory, acquired));
        var second = Task.Run(() => TryLockThenRelease(memory, acquired));
        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, results.Count(result => result == (int)OrbisGen2Result.ORBIS_GEN2_OK));
        Assert.Equal(1, results.Count(result => result == (int)OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY));

        var destroyContext = new CpuContext(memory, Generation.Gen5);
        destroyContext[CpuRegister.Rdi] = RwlockAddress;
        Assert.Equal(0, KernelPthreadExtendedCompatExports.PthreadRwlockDestroy(destroyContext));
        Assert.True(destroyContext.TryReadUInt64(RwlockAddress, out var slot));
        Assert.Equal(0UL, slot);
    }

    private static int TryLockThenRelease(ConcurrentFirstReadMemory memory, Barrier acquired)
    {
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = RwlockAddress;
        var result = KernelPthreadExtendedCompatExports.PthreadRwlockTrywrlock(context);

        Assert.True(acquired.SignalAndWait(TimeSpan.FromSeconds(10)));
        if (result == (int)OrbisGen2Result.ORBIS_GEN2_OK)
        {
            Assert.Equal(0, KernelPthreadExtendedCompatExports.PthreadRwlockUnlock(context));
        }

        return result;
    }

    private sealed class ConcurrentFirstReadMemory : ICpuMemory, IDisposable
    {
        private readonly ulong _baseAddress;
        private readonly ulong _synchronizedAddress;
        private readonly byte[] _storage;
        private readonly object _gate = new();
        private readonly Barrier _firstReads = new(2);
        private int _synchronizedReads;

        public ConcurrentFirstReadMemory(ulong baseAddress, int size, ulong synchronizedAddress)
        {
            _baseAddress = baseAddress;
            _synchronizedAddress = synchronizedAddress;
            _storage = new byte[size];
        }

        public bool TryRead(ulong virtualAddress, Span<byte> destination)
        {
            if (!TryResolve(virtualAddress, destination.Length, out var offset))
            {
                return false;
            }

            lock (_gate)
            {
                _storage.AsSpan(offset, destination.Length).CopyTo(destination);
            }

            if (virtualAddress == _synchronizedAddress &&
                destination.Length == sizeof(ulong) &&
                Interlocked.Increment(ref _synchronizedReads) <= 2)
            {
                return _firstReads.SignalAndWait(TimeSpan.FromSeconds(10));
            }

            return true;
        }

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
        {
            if (!TryResolve(virtualAddress, source.Length, out var offset))
            {
                return false;
            }

            lock (_gate)
            {
                source.CopyTo(_storage.AsSpan(offset, source.Length));
            }

            return true;
        }

        public void Dispose() => _firstReads.Dispose();

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
