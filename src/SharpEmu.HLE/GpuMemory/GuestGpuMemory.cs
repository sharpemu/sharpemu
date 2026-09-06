// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.GpuMemory;

public interface IGpuQueueRelay
{
    bool IsGpuQueueThread { get; }

    void Post(Action work);

    // Return after the work completes. Run it directly when called on the GPU worker.
    void RunOnGpuQueue(Action work);

    // Return false if the relay rejects the work. Rejected work does not run.
    bool TryRunOnGpuQueue(Action work);
}

public sealed class GuestGpuMemory : IDisposable
{
    private readonly PageGuard _pages;
    private readonly IGuestBufferStore _buffers;
    private readonly IGuestImageStore _images;
    private readonly ReaderWriterLockSlim _spansLock = new();
    private readonly SpanSet _spans = new();
    private sealed record GpuAttachment(IGpuQueueRelay? Gpu, IGpuTickScheduler? Scheduler);

    private readonly object _attachGate = new();
    private GpuAttachment? _attachment;

    public GuestGpuMemory(IGuestAddressSpace addressSpace, IGuestBufferStore buffers, IGuestImageStore images)
    {
        _pages = new PageGuard(addressSpace);
        _buffers = buffers;
        _images = images;
    }

    public IGuestBufferStore Buffers => _buffers;

    public IGuestImageStore Images => _images;

    internal PageGuard Pages => _pages;

    // Claims a fault only when a store recovered it and the page now permits the access.
    public bool TryResolveFault(FaultKind kind, ulong address)
    {
        const ulong faultSize = 8;
        if (!Covers(address, faultSize))
        {
            return false;
        }

        bool handled;
        if (kind == FaultKind.Write)
        {
            var buffers = _buffers.MarkCpuWrite(address, faultSize);
            var images = _images.MarkCpuWrite(address, faultSize);
            handled = buffers | images;
        }
        else
        {
            handled = _buffers.DownloadToCpu(address, faultSize);
        }

        return handled && _pages.Allows(address, kind);
    }

    public bool MarkCpuWrite(ulong address, ulong size)
    {
        if (!Covers(address, size))
        {
            return false;
        }

        _ = _buffers.MarkCpuWrite(address, size);
        _ = _images.MarkCpuWrite(address, size);
        return true;
    }

    public bool Covers(ulong address, ulong size)
    {
        if (!new GuestSpan(address, size).IsValid)
        {
            return false;
        }

        _spansLock.EnterReadLock();
        try
        {
            return _spans.Contains(address, size);
        }
        finally
        {
            _spansLock.ExitReadLock();
        }
    }

    public void Register(ulong address, ulong size)
    {
        _spansLock.EnterWriteLock();
        try
        {
            _spans.Add(address, size);
        }
        finally
        {
            _spansLock.ExitWriteLock();
        }

        _pages.NoteMapped(address, size);
    }

    public void Unregister(ulong address, ulong size)
    {
        for (;;)
        {
            var attachment = Volatile.Read(ref _attachment);
            if (attachment?.Scheduler?.InsideTickCallback == true)
            {
                PageGuard.OnFatal($"Cannot unmap memory from a GPU completion callback: addr=0x{address:X16} size=0x{size:X16}");
                return;
            }

            if (attachment?.Gpu is { } gpu && !gpu.IsGpuQueueThread)
            {
                if (gpu.TryRunOnGpuQueue(() => Unmap(attachment.Scheduler)))
                {
                    return;
                }

                // The relay is closed. The worker completes its GPU work and detaches first.
                WaitForDetach(attachment);
                continue;
            }

            Unmap(attachment?.Scheduler);
            return;
        }

        void Unmap(IGpuTickScheduler? scheduler)
        {
            if (scheduler is { Active: true })
            {
                var tick = scheduler.CurrentTick;
                scheduler.Finish();
                scheduler.WaitForPriorityOperations(tick);
            }

            _ = _buffers.MarkCpuWrite(address, size);
            _images.Unregister(address, size);
            _pages.NoteUnmapped(address, size);
            _spansLock.EnterWriteLock();
            try
            {
                _spans.Remove(address, size);
            }
            finally
            {
                _spansLock.ExitWriteLock();
            }
        }
    }

    // Publish both references together so an unmap cannot use a mismatched pair.
    public void AttachGpuQueue(IGpuQueueRelay? gpu, IGpuTickScheduler? scheduler)
    {
        lock (_attachGate)
        {
            Volatile.Write(ref _attachment, gpu == null && scheduler == null ? null : new GpuAttachment(gpu, scheduler));
            Monitor.PulseAll(_attachGate);
        }
    }

    private void WaitForDetach(GpuAttachment attachment)
    {
        lock (_attachGate)
        {
            while (ReferenceEquals(Volatile.Read(ref _attachment), attachment))
            {
                Monitor.Wait(_attachGate);
            }
        }
    }

    public void Dispose()
    {
        _pages.Dispose();
        _spansLock.Dispose();
    }
}
