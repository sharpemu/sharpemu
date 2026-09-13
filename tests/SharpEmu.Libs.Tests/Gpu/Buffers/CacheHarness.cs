// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Memory;
using SharpEmu.HLE;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.HLE.Host;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using SharpEmu.Libs.Tests.Memory.HostViews;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Buffers;

// Models the render loop: relay commands first, then one unit of guest work.
// Scheduler callbacks a test supplies when a presenter under test must see the ticks.
internal sealed record SchedulerHooks(IRenderingState Rendering, Action<SubmitBundle>? PrepareSubmit, Action<ulong>? Submitted);

internal sealed class CacheWorker : IDisposable
{
    private readonly object _gate = new();
    private readonly Queue<Action> _guestWork = new();
    private readonly Thread _thread;
    private bool _stop;

    public CacheWorker(HeadlessVulkan vulkan, SchedulerHooks? hooks = null)
    {
        Relay = new GpuWorkerRelay(Wake);
        Device = new LoggingTickDevice(vulkan.NewTickDevice());
        Scheduler = new SubmissionScheduler(Device, hooks?.Rendering ?? new RecordingRenderingState(), hooks?.PrepareSubmit, hooks?.Submitted);
        _thread = new Thread(Run) { IsBackground = true };
        _thread.Start();
    }

    public GpuWorkerRelay Relay { get; }

    public LoggingTickDevice Device { get; }

    public SubmissionScheduler Scheduler { get; }

    public void Post(Action work)
    {
        lock (_gate)
        {
            _guestWork.Enqueue(work);
            Monitor.PulseAll(_gate);
        }
    }

    public void Run(Action work)
    {
        Exception? failure = null;
        using var done = new ManualResetEventSlim();
        Post(() =>
        {
            try
            {
                work();
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            done.Set();
        });
        done.Wait();
        if (failure != null)
        {
            throw failure;
        }
    }

    public T Run<T>(Func<T> work)
    {
        T result = default!;
        Run(new Action(() => result = work()));
        return result;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _stop = true;
            Monitor.PulseAll(_gate);
        }

        _thread.Join();
        Scheduler.Dispose();
    }

    private void Wake()
    {
        lock (_gate)
        {
            Monitor.PulseAll(_gate);
        }
    }

    private void Run()
    {
        Relay.BindCurrentThread();
        for (;;)
        {
            Action? guest;
            lock (_gate)
            {
                while (!_stop && !Relay.HasPendingCommands && _guestWork.Count == 0)
                {
                    Monitor.Wait(_gate);
                }

                if (_stop)
                {
                    return;
                }

                Relay.RunPendingCommands();
                _guestWork.TryDequeue(out guest);
            }

            guest?.Invoke();
        }
    }
}

// Backed guest memory, the manager with its ledger, the worker and the cache under test.
internal sealed class CacheHarness : IDisposable
{
    private const ulong GuestBase = 0x2_0000_0000;
    private const ulong BackingBytes = 32UL * 1024 * 1024;

    private readonly IHostMemory _host = HostViewTestSupport.PlatformMemory;
    private readonly IHostViewMemory _views;
    private readonly HeadlessVulkan _vulkan;
    private readonly Action<string> _previousPageFatal = PageGuard.OnFatal;
    private ulong _nextBackingOffset;
    private bool _shutDown;
    private bool _shutdownFailed;

    public CacheHarness(
        HeadlessVulkan vulkan,
        Func<PhysicalVirtualMemory, IGuestBackedSpace>? backing = null,
        bool readbackLinearImages = false,
        ulong backingBytes = BackingBytes,
        SchedulerHooks? hooks = null,
        bool startScheduler = true,
        IHostViewMemory? viewHost = null)
    {
        _vulkan = vulkan;
        _views = viewHost ?? HostViewMemory.Create();
        PageGuard.OnFatal = message => throw new SchedulerFatalException(message);
        Memory = new PhysicalVirtualMemory(viewHost: _views, backingBytes: backingBytes);
        Gpu = new GuestGpuMemory(Memory);
        Worker = new CacheWorker(vulkan, hooks);
        if (startScheduler)
        {
            Worker.Run(() => Worker.Scheduler.Begin(new SubmissionContext { QueueName = "cache.test", SubmissionId = 1 }));
        }

        var space = backing?.Invoke(Memory) ?? Memory;
        Cache = new GuestBufferCache(vulkan.DeviceInfo, Worker.Scheduler, Worker.Relay, Gpu.Pages, Memory, space);
        Images = new GuestImageCache(vulkan.DeviceInfo, Worker.Scheduler, Gpu.Pages, Cache, space, readbackLinearImages);
        Cache.ImageCache = Images;
        Gpu.AttachStores(Cache, Images);
        Gpu.AttachGpuQueue(Worker.Relay, Worker.Scheduler);
    }

    public PhysicalVirtualMemory Memory { get; }

    public GuestGpuMemory Gpu { get; }

    public CacheWorker Worker { get; }

    public GuestBufferCache Cache { get; }

    public GuestImageCache Images { get; }

    public IGuestBufferStore Store => Cache;

    public IGuestImageStore ImageStore => Images;

    public HeadlessVulkan Vulkan => _vulkan;

    public SubmissionScheduler Scheduler => Worker.Scheduler;

    // A backed view inside the guest window, registered with the manager under the given protection.
    public ulong MapBacked(ulong size, GuestPageProtection protection)
    {
        var hole = HostViewTestSupport.AlignUp(size, _views.Granularity);
        Assert.True(Memory.TryHoldRangeAtOrAbove(GuestBase, hole, _views.Granularity, out var address));
        Assert.True(Memory.TryMapBacked(address, size, _nextBackingOffset, protection, out _));
        _nextBackingOffset += hole;
        Gpu.Register(address, size, protection);
        return address;
    }

    // Two backed granules around one that stays a bare hole: registered, but without backing.
    public (ulong First, ulong Granule, ulong Last) MapBackedSandwich()
    {
        var granule = _views.Granularity;
        Assert.True(Memory.TryHoldRangeAtOrAbove(GuestBase, 3 * granule, granule, out var first));
        Assert.True(Memory.TryMapBacked(first, granule, _nextBackingOffset, GuestPageProtection.Read | GuestPageProtection.Write, out _));
        Assert.True(Memory.TryMapBacked(first + 2 * granule, granule, _nextBackingOffset + granule, GuestPageProtection.Read | GuestPageProtection.Write, out _));
        _nextBackingOffset += 2 * granule;
        Gpu.Register(first, granule, GuestPageProtection.Read | GuestPageProtection.Write);
        Gpu.Register(first + 2 * granule, granule, GuestPageProtection.Read | GuestPageProtection.Write);
        return (first, granule, first + 2 * granule);
    }

    public ulong MapPrivate(ulong size)
    {
        Assert.True(Memory.TryAllocateAtOrAbove(GuestBase, size, executable: false, 0x4000, out var address));
        Gpu.Register(address, size, GuestPageProtection.Read | GuestPageProtection.Write);
        return address;
    }

    public HostPageProtection Protection(ulong address)
    {
        Assert.True(_host.Query(address, out var info));
        return info.Protection;
    }

    public byte[] Read(ulong address, int size)
    {
        var bytes = new byte[size];
        Assert.True(Memory.TryRead(address, bytes));
        return bytes;
    }

    public void Write(ulong address, byte[] bytes) => Assert.True(Memory.TryWriteBacking(address, bytes));

    // Copies a device range into a fresh download buffer and waits for it.
    public byte[] ReadBack(GpuBuffer buffer, ulong offset, ulong size) => Worker.Run(() =>
    {
        using var download = new GpuBuffer(_vulkan.DeviceInfo, Scheduler, GpuBufferUsage.Download, 0, GpuBuffer.AllFlags, size);
        download.CopyFrom(Scheduler.Current, buffer, offset, 0, size, AccessFlags.MemoryWriteBit, AccessFlags.None, AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit, AccessFlags.HostReadBit);
        Scheduler.Finish();
        download.Invalidate(0, size);
        return download.Mapped[..(int)size].ToArray();
    });

    // A test that shut the stores down through another path marks the harness as done.
    public void MarkShutDown() => _shutDown = true;

    public void Shutdown()
    {
        Worker.Run(ShutdownOnWorker);
        _vulkan.AssertNoValidationMessages();
    }

    private void ShutdownOnWorker()
    {
        _shutDown = true;
        Worker.Relay.StopAcceptingWork();
        Worker.Relay.RunPendingCommands();
        try
        {
            Images.Shutdown();
            Cache.Shutdown();
        }
        catch (Exception)
        {
            _shutdownFailed = true;
            throw;
        }
        finally
        {
            Gpu.AttachStores(null, null);
            if (Scheduler.Active)
            {
                Scheduler.Finish();
                Scheduler.WaitForAllPriorityOperations();
            }

            Scheduler.Shutdown();
            Gpu.AttachGpuQueue(null, null);
        }
    }

    // A failed test still tears down in order so the guard fatal does not hide the assertion.
    public void Dispose()
    {
        if (!_shutDown)
        {
            try
            {
                Shutdown();
            }
            catch (Exception)
            {
            }
        }

        Images.Dispose();
        Cache.Dispose();
        Worker.Dispose();
        if (_shutdownFailed)
        {
            PageGuard.OnFatal = _ => { };
        }

        Gpu.Dispose();
        Memory.Dispose();
        PageGuard.OnFatal = _previousPageFatal;
    }
}
