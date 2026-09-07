// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using SharpEmu.HLE;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.Libs.Tests.Memory.GpuMemory;

[Collection(GpuMemoryStateCollection.Name)]
public sealed class ScalarDescriptorCoherenceTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ScalarReadSynchronizesOnlyRegisteredMemory(bool registered)
    {
        const ulong address = 0x10000;
        var backing = new FakeCpuMemory(address, 4096);
        using var memory = new GuestGpuMemory(new RecordingAddressSpace());
        var store = new DownloadStore(backing);
        memory.AttachStores(store, null);
        if (registered)
            memory.Register(address, 4096, GuestPageProtection.Read | GuestPageProtection.Write);
        GuestGpuMemoryHook.Attach(memory);
        try
        {
            var read = typeof(Gen5ShaderScalarEvaluator).GetMethod("TryReadUInt32",
                BindingFlags.NonPublic | BindingFlags.Static)!;
            object?[] arguments = [new CpuContext(backing, Generation.Gen5), address, 0u];

            Assert.True((bool)read.Invoke(null, arguments)!);

            Assert.Equal(registered ? 0x12345678u : 0u, (uint)arguments[2]!);
            Assert.Equal(registered ? 1 : 0, store.Downloads);
        }
        finally
        {
            GuestGpuMemoryHook.Attach(null);
        }
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    public void ScalarReadDistinguishesUntrackedMemoryFromFailedRecovery(
        bool tracked, bool recovered, bool expectedSuccess)
    {
        const ulong address = 0x10000;
        var backing = new FakeCpuMemory(address, 4096);
        Assert.True(backing.TryWrite(address, [0xAA, 0xBB, 0xCC, 0xDD]));
        using var memory = new GuestGpuMemory(new RecordingAddressSpace());
        var store = new DownloadStore(backing) { Tracked = tracked, Recovered = recovered };
        memory.AttachStores(store, null);
        memory.Register(address, 4096, GuestPageProtection.Read | GuestPageProtection.Write);
        GuestGpuMemoryHook.Attach(memory);
        try
        {
            var read = typeof(Gen5ShaderScalarEvaluator).GetMethod("TryReadUInt32",
                BindingFlags.NonPublic | BindingFlags.Static)!;
            object?[] arguments = [new CpuContext(backing, Generation.Gen5), address, 0u];

            Assert.Equal(expectedSuccess, (bool)read.Invoke(null, arguments)!);
            Assert.Equal(expectedSuccess ? 0xDDCCBBAAu : 0u, (uint)arguments[2]!);
            Assert.Equal(tracked ? 1 : 0, store.Downloads);
        }
        finally
        {
            GuestGpuMemoryHook.Attach(null);
        }
    }

    private sealed class DownloadStore(FakeCpuMemory backing) : IGuestBufferStore
    {
        public int Downloads { get; private set; }
        public bool Tracked { get; init; } = true;
        public bool Recovered { get; init; } = true;
        public bool MarkCpuWrite(ulong address, ulong size) => false;
        public bool TrySynchronizeCpuRead(ulong address, ulong size) =>
            !Tracked || DownloadToCpu(address, size);
        public bool DownloadToCpu(ulong address, ulong size)
        {
            Assert.Equal(4UL, size);
            Downloads++;
            return Recovered && backing.TryWrite(address, [0x78, 0x56, 0x34, 0x12]);
        }
    }
}
