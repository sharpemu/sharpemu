// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.GpuMemory;

public interface IGpuQueueRelay
{
    void RunOnGpuQueue(Action work);
}

public sealed class GuestGpuMemory : IDisposable
{
    private readonly PageGuard _pages;
    private readonly IGuestBufferStore _buffers;
    private readonly IGuestImageStore _images;
    private readonly ReaderWriterLockSlim _spansLock = new();
    private readonly SpanSet _spans = new();
    private IGpuQueueRelay? _gpu;

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
        void Unmap()
        {
            // Section 2 adds the scheduler drain here.
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

        if (_gpu == null)
        {
            Unmap();
            return;
        }

        _gpu.RunOnGpuQueue(Unmap);
    }

    public void AttachGpuQueue(IGpuQueueRelay? gpu) => _gpu = gpu;

    public void Dispose()
    {
        _pages.Dispose();
        _spansLock.Dispose();
    }
}
