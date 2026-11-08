// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Buffers;

public readonly record struct BufferSliceUse(ulong Address, ulong Size, bool Written, bool Texel, bool InvalidateImages, GpuBuffer Buffer);

// A frozen view of the registered buffers for recording threads that run ahead of the stream order,
// rebuilt by the render thread whenever a registration changed.
public sealed class BufferSnapshot
{
    public static readonly BufferSnapshot Empty = new([], [], [], [], -1);

    private readonly ulong[] _addresses;
    private readonly ulong[] _ends;
    private readonly GpuBuffer[] _buffers;
    private readonly ResourceSlotIdentifier[] _identifiers;

    internal BufferSnapshot(ulong[] addresses, ulong[] ends, GpuBuffer[] buffers, ResourceSlotIdentifier[] identifiers, long serial)
    {
        _addresses = addresses;
        _ends = ends;
        _buffers = buffers;
        _identifiers = identifiers;
        Serial = serial;
    }

    public long Serial { get; }

    public int Count => _addresses.Length;

    public bool TryFind(ulong address, ulong size, out GpuBuffer buffer, out ResourceSlotIdentifier identifier)
    {
        buffer = null!;
        identifier = ResourceSlotIdentifier.Invalid;
        var low = 0;
        var high = _addresses.Length - 1;
        var found = -1;
        while (low <= high)
        {
            var middle = (low + high) >> 1;
            if (_addresses[middle] <= address)
            {
                found = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        if (found < 0 || address + size > _ends[found] || address + size < address)
        {
            return false;
        }

        buffer = _buffers[found];
        identifier = _identifiers[found];
        return true;
    }
}

public sealed unsafe partial class GuestBufferCache
{
    private BufferSnapshot _snapshot = BufferSnapshot.Empty;

    public BufferSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public BufferSnapshot RefreshSnapshot()
    {
        var serial = _registry.RegistrationSerial;
        var current = _snapshot;
        if (current.Serial == serial)
        {
            return current;
        }

        var count = _registry.RegisteredCount;
        var addresses = new ulong[count];
        var ends = new ulong[count];
        var buffers = new GpuBuffer[count];
        var identifiers = new ResourceSlotIdentifier[count];
        for (var index = 0; index < count; index++)
        {
            var identifier = _registry.GetRegisteredIdentifier(index);
            var buffer = _registry.GetBuffer(identifier);
            addresses[index] = buffer.CpuAddress;
            ends[index] = buffer.CpuAddress + buffer.Size;
            buffers[index] = buffer;
            identifiers[index] = identifier;
        }

        var snapshot = new BufferSnapshot(addresses, ends, buffers, identifiers, serial);
        Volatile.Write(ref _snapshot, snapshot);
        return snapshot;
    }

    // Whether today's obtain would copy the range into the stream ring instead of binding the cache.
    public bool PrefersStreamCopy(ulong guestAddress, ulong size, bool isTexelBuffer) =>
        _tracker.HasCpuDirtyPages(guestAddress, size) &&
        !(_tracker.MayHaveGpuDirtyPages(guestAddress, size) && _tracker.HasGpuDirtyPages(guestAddress, size)) &&
        (size <= CachingPageSize || (!isTexelBuffer && _tracker.IsCpuWriteHotRange(guestAddress, size)));

    public bool MayHaveImageBytes(ulong guestAddress, ulong size) => RequireImageCache().MayHaveImageBytes(guestAddress, size);

    public bool MayHaveCpuDirtyPages(ulong guestAddress, ulong size) => _tracker.MayHaveCpuDirtyPages(guestAddress, size);

    public long RegionStamp(ulong guestAddress, ulong size) => _registry.RegionStamp(guestAddress, size);

    // Whether TryCopyWordsOnHost would copy, without copying.
    public bool WouldCopyWordsOnHost(ulong destination, ulong source, ulong sourceWords, ulong words)
    {
        if (destination == 0 || source == 0 || sourceWords == 0 || words == 0 || ((destination | source) & 3) != 0 ||
            words > MaxHostCopyWords || sourceWords > MaxHostCopyWords)
        {
            return false;
        }

        var size = words * sizeof(uint);
        var sourceSize = Math.Min(sourceWords, words) * sizeof(uint);
        if (size > ulong.MaxValue - destination || sourceSize > ulong.MaxValue - source ||
            (source < destination + size && destination < source + sourceSize))
        {
            return false;
        }

        var images = RequireImageCache();
        return !HasGpuDirtyBytes(source, sourceSize) && !HasGpuDirtyBytes(destination, size) &&
               !images.QueryRegion(source, sourceSize).GpuImageBytes && !images.QueryRegion(destination, size).GpuImageBytes;
    }

    public bool ValidateSliceBuffers(List<BufferSliceUse> uses)
    {
        foreach (var use in uses)
        {
            var owner = _registry.TryGetRegisteredBuffer(_registry.FindContainingBuffer(use.Address, use.Size));
            if (!ReferenceEquals(owner, use.Buffer) || !use.Buffer.IsInBounds(use.Address, use.Size))
            {
                return false;
            }
        }

        return true;
    }

    public void ApplySliceUse(in BufferSliceUse use)
    {
        _ = ObtainBuffer(use.Address, use.Size, use.Written, use.Texel, allowStream: false);
        if (use.InvalidateImages)
        {
            RequireImageCache().InvalidateMemoryFromGpu(use.Address, use.Size);
        }
        else if (use.Written)
        {
            RequireImageCache().InvalidateMemoryCopiesFromGpu(use.Address, use.Size);
        }
    }
}
