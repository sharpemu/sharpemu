// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GpuMemory;
using Xunit;

namespace SharpEmu.Libs.Tests.Memory.GpuMemory;

[Collection(GpuMemoryStateCollection.Name)]
public sealed class GuestGpuMemoryTests
{
    private sealed class RecordingStores : IGuestBufferStore, IGuestImageStore
    {
        public List<string> Calls { get; } = new();

        public bool BufferHandles { get; set; }

        public bool ImageHandles { get; set; }

        bool IGuestBufferStore.MarkCpuWrite(ulong address, ulong size)
        {
            Calls.Add($"buffer.write {address:X}+{size:X}");
            return BufferHandles;
        }

        public bool DownloadToCpu(ulong address, ulong size)
        {
            Calls.Add($"buffer.pull {address:X}+{size:X}");
            return BufferHandles;
        }

        bool IGuestImageStore.MarkCpuWrite(ulong address, ulong size)
        {
            Calls.Add($"image.write {address:X}+{size:X}");
            return ImageHandles;
        }

        public void Unregister(ulong address, ulong size) => Calls.Add($"image.unregister {address:X}+{size:X}");
    }

    private sealed class InlineQueue : IGpuQueueRelay
    {
        public int Runs { get; private set; }

        public void RunOnGpuQueue(Action work)
        {
            Runs++;
            work();
        }
    }

    private readonly RecordingStores _stores = new();
    private readonly GuestGpuMemory _memory;

    public GuestGpuMemoryTests()
    {
        _memory = new GuestGpuMemory(new RecordingAddressSpace(), _stores, _stores);
    }

    [Fact]
    public void Register_ThenCoversSubSpansOnly()
    {
        _memory.Register(0x10000, 0x4000);

        Assert.True(_memory.Covers(0x10000, 0x4000));
        Assert.True(_memory.Covers(0x11000, 0x8));
        Assert.False(_memory.Covers(0x13FF8, 0x10));
        Assert.False(_memory.Covers(1UL << 40, 0x8));
        Assert.Empty(_stores.Calls);

        _memory.Unregister(0x10000, 0x4000);
        Assert.False(_memory.Covers(0x11000, 0x8));
        Assert.Equal(new[] { "buffer.write 10000+4000", "image.unregister 10000+4000" }, _stores.Calls);
        _memory.Dispose();
    }

    [Theory]
    [InlineData(FaultKind.Write, "buffer.write 10010+8", "image.write 10010+8")]
    [InlineData(FaultKind.Read, "buffer.pull 10010+8")]
    [InlineData(FaultKind.Execute, "buffer.pull 10010+8")]
    [InlineData(FaultKind.Unknown, "buffer.pull 10010+8")]
    public void TryResolveFault_NotifiesStoresButDeclinesWhenNoneRecovers(FaultKind kind, params string[] expected)
    {
        _memory.Register(0x10000, 0x1000);

        Assert.False(_memory.TryResolveFault(kind, 0x10010));
        Assert.Equal(expected, _stores.Calls);

        _memory.Unregister(0x10000, 0x1000);
        _memory.Dispose();
    }

    [Theory]
    [InlineData(FaultKind.Write, true, false)]
    [InlineData(FaultKind.Write, false, true)]
    [InlineData(FaultKind.Read, true, false)]
    public void TryResolveFault_ClaimsWhenAStoreRecovers(FaultKind kind, bool buffer, bool image)
    {
        _stores.BufferHandles = buffer;
        _stores.ImageHandles = image;
        _memory.Register(0x10000, 0x1000);

        Assert.True(_memory.TryResolveFault(kind, 0x10010));

        _memory.Unregister(0x10000, 0x1000);
        _memory.Dispose();
    }

    [Fact]
    public void TryResolveFault_WriteNotifiesBothStoresEvenWhenTheFirstRecovers()
    {
        _stores.BufferHandles = true;
        _memory.Register(0x10000, 0x1000);

        Assert.True(_memory.TryResolveFault(FaultKind.Write, 0x10010));
        Assert.Equal(new[] { "buffer.write 10010+8", "image.write 10010+8" }, _stores.Calls);

        _memory.Unregister(0x10000, 0x1000);
        _memory.Dispose();
    }

    [Fact]
    public void TryResolveFault_DeclinesWhileAnotherWatcherStillBlocksThePage()
    {
        _stores.BufferHandles = true;
        _stores.ImageHandles = true;
        _memory.Register(0x10000, 0x1000);
        _memory.Pages.AddWatch(0x10000, 0x1000, blockReads: false);

        Assert.False(_memory.TryResolveFault(FaultKind.Write, 0x10010));
        Assert.True(_memory.TryResolveFault(FaultKind.Read, 0x10010));

        _memory.Pages.AddWatch(0x10000, 0x1000, blockReads: true);
        Assert.False(_memory.TryResolveFault(FaultKind.Read, 0x10010));

        _memory.Pages.RemoveWatch(0x10000, 0x1000, blockReads: true);
        _memory.Pages.RemoveWatch(0x10000, 0x1000, blockReads: false);
        Assert.True(_memory.TryResolveFault(FaultKind.Write, 0x10010));

        _memory.Unregister(0x10000, 0x1000);
        _memory.Dispose();
    }

    [Fact]
    public void TryResolveFault_OutsideMappedSpansIsDeclined()
    {
        _stores.BufferHandles = true;
        _memory.Register(0x10000, 0x1000);

        Assert.False(_memory.TryResolveFault(FaultKind.Write, 0x20000));
        Assert.False(_memory.TryResolveFault(FaultKind.Write, 0x10FFC));
        Assert.False(_memory.MarkCpuWrite(0x20000, 0x10));
        Assert.Empty(_stores.Calls);

        Assert.True(_memory.MarkCpuWrite(0x10100, 0x10));
        Assert.Equal(new[] { "buffer.write 10100+10", "image.write 10100+10" }, _stores.Calls);

        _memory.Unregister(0x10000, 0x1000);
        _memory.Dispose();
    }

    [Fact]
    public void Unregister_RunsOnTheGpuQueueWhenAttached()
    {
        var queue = new InlineQueue();
        _memory.Register(0x10000, 0x1000);
        _memory.AttachGpuQueue(queue);

        _memory.Unregister(0x10000, 0x1000);

        Assert.Equal(1, queue.Runs);
        Assert.False(_memory.Covers(0x10000, 0x1000));
        _memory.Dispose();
    }
}
