// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Buffers;

[Collection(SchedulingStateCollection.Name)]
public sealed class GpuBufferTests : IClassFixture<HeadlessVulkanFixture>
{
    private readonly HeadlessVulkan? _vulkan;

    public GpuBufferTests(HeadlessVulkanFixture fixture) => _vulkan = fixture.Vulkan;

    [Fact]
    public void IsInBounds_IsOverflowSafe()
    {
        if (_vulkan is null) return;
        using var worker = new CacheWorker(_vulkan);
        using var buffer = new GpuBuffer(_vulkan.DeviceInfo, worker.Scheduler, GpuBufferUsage.DeviceLocal, 0x10000, GpuBuffer.AllFlags, 0x4000);

        Assert.True(buffer.IsInBounds(0x10000, 0x4000));
        Assert.True(buffer.IsInBounds(0x13FFF, 1));
        Assert.False(buffer.IsInBounds(0x13FFF, 2));
        Assert.False(buffer.IsInBounds(0xFFFF, 1));
        Assert.False(buffer.IsInBounds(0x10000, ulong.MaxValue));
        Assert.Equal(0x1000UL, buffer.Offset(0x11000));
        Assert.True(buffer.Mapped.IsEmpty);
        Assert.False(buffer.IsDeleted);
    }

    [Fact]
    public void FillAndCopyFrom_ReachTheHostThroughADownloadBuffer()
    {
        if (_vulkan is null) return;
        using var worker = new CacheWorker(_vulkan);
        var info = _vulkan.DeviceInfo;
        using var device = new GpuBuffer(info, worker.Scheduler, GpuBufferUsage.DeviceLocal, 0, GpuBuffer.AllFlags, 0x1000);
        using var upload = new GpuBuffer(info, worker.Scheduler, GpuBufferUsage.Upload, 0, GpuBuffer.AllFlags, 0x1000);
        using var download = new GpuBuffer(info, worker.Scheduler, GpuBufferUsage.Download, 0, GpuBuffer.AllFlags, 0x1000);
        Assert.False(upload.Mapped.IsEmpty);
        Assert.False(download.Mapped.IsEmpty);

        var pattern = new byte[0x100];
        for (var i = 0; i < pattern.Length; i++)
        {
            pattern[i] = (byte)(i * 3);
        }

        worker.Run(() =>
        {
            worker.Scheduler.Begin(new SubmissionContext());
            device.Fill(0, 0x1000, 0xA5A5A5A5);
            upload.Write(0, pattern);
            device.CopyFrom(worker.Scheduler.Current, upload, 0, 0x200, 0x100, AccessFlags.HostWriteBit);
            download.CopyFrom(worker.Scheduler.Current, device, 0, 0, 0x1000, AccessFlags.MemoryWriteBit, AccessFlags.None, AccessFlags.MemoryReadBit, AccessFlags.HostReadBit);
            worker.Scheduler.Finish();
            download.Invalidate(0, 0x1000);
        });

        var bytes = download.Mapped.ToArray();
        Assert.All(bytes[..0x200], value => Assert.Equal(0xA5, value));
        Assert.Equal(pattern, bytes[0x200..0x300]);
        Assert.All(bytes[0x300..], value => Assert.Equal(0xA5, value));

        using var fatal = new FatalScope();
        worker.Run(() => Assert.Throws<SchedulerFatalException>(() => device.Fill(2, 4, 0)));
        Assert.Contains("The buffer fill range must be aligned to four bytes.", fatal.Messages);
        worker.Run(() => Assert.Throws<SchedulerFatalException>(() => device.CopyFrom(worker.Scheduler.Current, device, 0, 0x80, 0x100)));
        Assert.Contains("Cannot copy overlapping ranges of the same buffer.", fatal.Messages);
        worker.Run(() => worker.Scheduler.Shutdown());
    }
}
