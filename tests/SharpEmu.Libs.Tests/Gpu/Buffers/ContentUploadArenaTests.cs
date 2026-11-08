// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Buffers;

[Collection(SchedulingStateCollection.Name)]
public sealed class ContentUploadArenaTests : IClassFixture<HeadlessVulkanFixture>
{
    private const int ChunkBytes = 64 * 1024;
    private const int ChunksPerArena = 32 * 1024 * 1024 / ChunkBytes;

    private readonly HeadlessVulkan? _vulkan;

    public ContentUploadArenaTests(HeadlessVulkanFixture fixture) => _vulkan = fixture.Vulkan;

    [Fact]
    public void SameContentHitsAndSameLengthOtherContentStoresAgain()
    {
        if (_vulkan is null) return;
        using var worker = new CacheWorker(_vulkan);
        worker.Run(() =>
        {
            using var arena = new ContentUploadArena(_vulkan.DeviceInfo, worker.Scheduler);
            var first = Pattern(256, 1);
            Assert.True(arena.TryUpload(first, 16, out var firstBuffer, out var firstOffset));
            Assert.True(arena.TryUpload(Pattern(256, 1), 16, out var hitBuffer, out var hitOffset));
            Assert.Equal(firstBuffer.Handle, hitBuffer.Handle);
            Assert.Equal(firstOffset, hitOffset);

            Assert.True(arena.TryUpload(Pattern(256, 2), 16, out var otherBuffer, out var otherOffset));
            Assert.Equal(firstBuffer.Handle, otherBuffer.Handle);
            Assert.NotEqual(firstOffset, otherOffset);
            Assert.Equal(0UL, otherOffset % 16);
            Assert.Contains("hits=", ContentUploadArena.TakeReport(), StringComparison.Ordinal);
        });
    }

    [Fact]
    public void AFullArenaRecyclesTheOtherOnlyAfterItsWorkCompleted()
    {
        if (_vulkan is null) return;
        using var worker = new CacheWorker(_vulkan);
        worker.Run(() =>
        {
            var scheduler = worker.Scheduler;
            scheduler.Begin(new SubmissionContext());
            using var arena = new ContentUploadArena(_vulkan.DeviceInfo, scheduler);
            var kept = Pattern(256, 7);
            Assert.True(arena.TryUpload(kept, 16, out var firstArena, out var keptOffset));

            var seed = 100;
            for (var chunk = 1; chunk < ChunksPerArena; chunk++)
            {
                Assert.True(arena.TryUpload(Pattern(ChunkBytes, seed++), 16, out _, out _));
            }

            Assert.True(arena.TryUpload(Pattern(ChunkBytes, seed++), 16, out var secondArena, out var secondOffset));
            Assert.NotEqual(firstArena.Handle, secondArena.Handle);
            Assert.True(arena.TryUpload(kept, 16, out var keptBuffer, out var keptAgain));
            Assert.Equal(firstArena.Handle, keptBuffer.Handle);
            Assert.Equal(keptOffset, keptAgain);

            for (var chunk = 1; chunk < ChunksPerArena; chunk++)
            {
                Assert.True(arena.TryUpload(Pattern(ChunkBytes, seed++), 16, out _, out _));
            }

            Assert.False(arena.TryUpload(Pattern(ChunkBytes, seed), 16, out _, out _));

            scheduler.Finish();
            Assert.True(arena.TryUpload(Pattern(ChunkBytes, seed++), 16, out var recycled, out var recycledOffset));
            Assert.Equal(firstArena.Handle, recycled.Handle);
            Assert.Equal(0UL, recycledOffset);
            Assert.True(arena.TryUpload(kept, 16, out _, out var storedAgain));
            Assert.NotEqual(keptOffset, storedAgain);
            Assert.Equal(0UL, secondOffset);
        });
    }

    private static byte[] Pattern(int length, int seed)
    {
        var data = new byte[length];
        new System.Random(seed).NextBytes(data);
        return data;
    }
}
