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

    bool TryRunAfterPendingWork(Action work) => TryRunOnGpuQueue(work);
}

public sealed class GuestGpuMemory : IDisposable
{
    private readonly PageGuard _pages;
    private readonly ReaderWriterLockSlim _spansLock = new();
    private readonly SpanSet _spans = new();
    private sealed record GpuAttachment(IGpuQueueRelay? Gpu, IGpuTickScheduler? Scheduler);

    private readonly object _attachGate = new();
    private GpuAttachment? _attachment;
    private IGuestBufferStore? _buffers;
    private IGuestImageStore? _images;

    public GuestGpuMemory(IGuestAddressSpace addressSpace)
    {
        AddressSpace = addressSpace;
        _pages = new PageGuard(addressSpace);
    }

    public IGuestAddressSpace AddressSpace { get; }

    public IGuestBufferStore? Buffers => Volatile.Read(ref _buffers);

    public IGuestImageStore? Images => Volatile.Read(ref _images);

    public PageGuard Pages => _pages;

    // Stores arrive once the host GPU is ready; null stores decline every fault.
    public void AttachStores(IGuestBufferStore? buffers, IGuestImageStore? images)
    {
        Volatile.Write(ref _buffers, buffers);
        Volatile.Write(ref _images, images);
    }

    // Claims a fault only when the guest permits it and a store completed recovery.
    public bool TryResolveFault(FaultKind kind, ulong address)
    {
        const ulong faultSize = 8;
        if (!Covers(address, faultSize))
        {
            TraceFault("unregistered");
            return false;
        }
        if (!GuestPermits(kind, address))
        {
            TraceFault("guest-denied");
            return false;
        }

        var buffers = Buffers;
        var images = Images;
        bool handled;
        if (kind == FaultKind.Write)
        {
            handled = (buffers?.MarkCpuWrite(address, faultSize) ?? false) | (images?.MarkCpuWrite(address, faultSize) ?? false);
        }
        else
        {
            handled = buffers?.DownloadToCpu(address, faultSize) ?? false;
        }

        // A new watch can follow recovery. Let the retried access fault again if necessary.
        TraceFault(handled ? "resolved" : "store-declined");
        return handled;

        void TraceFault(string result)
        {
            if (GuestGpuMemoryHook.Traces(address, faultSize))
                GuestGpuMemoryHook.Trace(address, faultSize,
                    $"fault={kind} result={result} guest={_pages.Permissions.Lookup(address)} buffers={Buffers != null} images={Images != null} {_pages.DescribeWatchers(address)}");
        }
    }

    public bool MarkCpuWrite(ulong address, ulong size)
    {
        if (!Covers(address, size))
        {
            return false;
        }

        _ = Buffers?.MarkCpuWrite(address, size);
        _ = Images?.MarkCpuWrite(address, size);
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

    public void ForEachSpan(Action<ulong, ulong> visit)
    {
        _spansLock.EnterReadLock();
        try
        {
            _spans.ForEach(visit);
        }
        finally
        {
            _spansLock.ExitReadLock();
        }
    }

    // The host mapping takes the guest protection here; the views themselves are mapped read-write.
    public void Register(ulong address, ulong size, GuestPageProtection protection)
    {
        if (GuestGpuMemoryHook.Traces(address, size))
            GuestGpuMemoryHook.Trace(address, size, $"map guest={protection}");
        _spansLock.EnterWriteLock();
        try
        {
            _spans.Add(address, size);
        }
        finally
        {
            _spansLock.ExitWriteLock();
        }

        _pages.Permissions.Set(address, size, protection);
        _pages.Reapply(address, size);
    }

    // A guest mprotect on a covered range: the ledger changes, then every page gets its derived protection.
    public bool NoteProtected(ulong address, ulong size, GuestPageProtection protection)
    {
        if (GuestGpuMemoryHook.Traces(address, size))
            GuestGpuMemoryHook.Trace(address, size, $"protect-request guest={protection}");
        if (!Covers(address, size))
        {
            return false;
        }

        _pages.Permissions.Set(address, size, protection);
        _pages.Reapply(address, size);
        return true;
    }

    public void Unregister(ulong address, ulong size)
    {
        GuestGpuMemoryHook.Trace(address, size, "unmap-request");
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
                if (gpu.TryRunAfterPendingWork(() => Unmap(attachment.Scheduler)))
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

            _ = Buffers?.MarkCpuWrite(address, size);
            Images?.Unregister(address, size);
            _spansLock.EnterWriteLock();
            try
            {
                _spans.Remove(address, size);
            }
            finally
            {
                _spansLock.ExitWriteLock();
            }

            _pages.Permissions.Clear(address, size);
            GuestGpuMemoryHook.Trace(address, size, "unmap-complete");
        }
    }

    // Enter the GPU worker before the caller takes locks used by GPU memory reads.
    public void RunMappingChange(Action change)
    {
        for (;;)
        {
            var attachment = Volatile.Read(ref _attachment);
            if (attachment?.Scheduler?.InsideTickCallback == true)
            {
                PageGuard.OnFatal("Cannot change memory mappings from a GPU completion callback.");
                return;
            }

            if (attachment?.Gpu is { } gpu && !gpu.IsGpuQueueThread)
            {
                if (gpu.TryRunAfterPendingWork(() => ApplyChange(attachment.Scheduler)))
                    return;
                WaitForDetach(attachment);
                continue;
            }

            ApplyChange(attachment?.Scheduler);
            return;
        }

        void ApplyChange(IGpuTickScheduler? scheduler)
        {
            // Finish callbacks before the mapping transaction takes its locks.
            if (scheduler is { Active: true })
            {
                var tick = scheduler.CurrentTick;
                scheduler.Finish();
                scheduler.WaitForPriorityOperations(tick);
            }
            change();
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

    private bool GuestPermits(FaultKind kind, ulong address)
    {
        var needed = kind switch
        {
            FaultKind.Write => GuestPageProtection.Write,
            FaultKind.Execute => GuestPageProtection.Execute,
            _ => GuestPageProtection.Read,
        };
        return (_pages.Permissions.Lookup(address) & needed) != 0;
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
