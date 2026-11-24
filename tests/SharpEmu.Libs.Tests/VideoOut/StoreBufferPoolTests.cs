// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

[Collection(SchedulingStateCollection.Name)]
public sealed class StoreBufferPoolTests : IClassFixture<HeadlessVulkanFixture>
{
    private readonly HeadlessVulkan? _vulkan;

    public StoreBufferPoolTests(HeadlessVulkanFixture fixture) => _vulkan = fixture.Vulkan;

    // Buffers come in 64 KiB classes and come back once: a second return of one is refused, and an idle buffer is reused.
    [Fact]
    public void BuffersComeIn64KiBClassesAndComeBackOnce()
    {
        var pool = new StoreBufferPool();
        var buffer = pool.Rent(StoreBufferPool.ClassBytes + 1);
        Assert.Equal(2 * StoreBufferPool.ClassBytes, buffer.Length);
        Assert.Equal((1, 0L), pool.Stats());

        Assert.Equal(StoreBufferPool.ReturnResult.Kept, pool.Return(buffer));
        Assert.Equal(StoreBufferPool.ReturnResult.NotLeased, pool.Return(buffer));
        Assert.Equal((0, (long)buffer.Length), pool.Stats());
        Assert.Same(buffer, pool.Rent(2 * StoreBufferPool.ClassBytes));
    }

    // A leased buffer is never handed out again before it comes back; returns past the idle budget are dropped.
    [Fact]
    public void ALeasedBufferIsNotHandedOutAgainAndTheIdleBudgetHolds()
    {
        var pool = new StoreBufferPool(maxIdleBytes: 3 * StoreBufferPool.ClassBytes);
        var first = pool.Rent(2 * StoreBufferPool.ClassBytes);
        var second = pool.Rent(2 * StoreBufferPool.ClassBytes);
        Assert.NotSame(first, second);

        Assert.Equal(StoreBufferPool.ReturnResult.Kept, pool.Return(first));
        Assert.Equal(StoreBufferPool.ReturnResult.Dropped, pool.Return(second));
        Assert.Equal((0, 2L * StoreBufferPool.ClassBytes), pool.Stats());
        Assert.Equal(StoreBufferPool.ReturnResult.NotLeased, pool.Return(new byte[StoreBufferPool.ClassBytes]));
    }

    // A pooled store keeps its buffer until it lands, writes only its own length, then returns the buffer to the pool.
    [Fact]
    public void APooledStoreReturnsItsBufferWhenItLands()
    {
        if (_vulkan is null || !OperatingSystem.IsWindows())
            return;

        using var presenter = new PresenterUnderTest(_vulkan);
        var address = presenter.Harness.MapBacked(0x20000, GuestPageProtection.Read | GuestPageProtection.Write);
        var thread = CommandStreamStoreIndexTests.NewThread(presenter);
        var buffer = StoreBufferPool.Shared.Rent(StoreBufferPool.ClassBytes);
        Array.Fill(buffer, (byte)7);
        thread.GetType().GetMethod("PostPooledWrite")!.Invoke(thread, [address, buffer, StoreBufferPool.ClassBytes - 16, false]);

        var store = Assert.Single(CommandStreamStoreIndexTests.Stores(thread));
        var data = store.GetType().GetField("Data")!;
        Assert.Same(buffer, data.GetValue(store));

        CommandStreamStoreIndexTests.Land(presenter, thread);
        Assert.Null(data.GetValue(store));
        Assert.Equal(7, presenter.Harness.Read(address + StoreBufferPool.ClassBytes - 17, 1)[0]);
        Assert.Equal(0, presenter.Harness.Read(address + StoreBufferPool.ClassBytes - 16, 1)[0]);
        var again = StoreBufferPool.Shared.Rent(StoreBufferPool.ClassBytes);
        Assert.Same(buffer, again);
        StoreBufferPool.Shared.Return(again);
    }
}
