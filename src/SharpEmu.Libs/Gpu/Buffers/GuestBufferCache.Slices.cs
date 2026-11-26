// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.Diagnostics;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Scheduling;

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

    // Recording workers can resolve the immutable mapping to an ordered list of owners.
    // A newer registry snapshot owns a different plan. A mutable caller must still match every span.
    private sealed record TouchPlan(GuestSpan[] Spans, ResourceSlotIdentifier[] Owners);
    private TouchPlan? _touchPlan;

    internal void PrepareTouches(List<GuestSpan> spans)
    {
        if (TryGetTouches(spans, out _)) return;
        var copy = spans.ToArray();
        var owners = new List<ResourceSlotIdentifier>();
        var index = 0;
        ulong previousEnd = 0;
        foreach (var span in copy)
        {
            if (!span.IsValid || span.Size == 0 || span.Address < previousEnd) return;
            previousEnd = span.End;
            while (index < Count && _ends[index] <= span.Address) index++;
            for (var next = index; next < Count && _addresses[next] < span.End; next++)
            {
                if (owners.Count == 0 || owners[^1] != _identifiers[next])
                    owners.Add(_identifiers[next]);
            }
        }
        Volatile.Write(ref _touchPlan, new TouchPlan(copy, owners.ToArray()));
    }

    internal bool TryGetTouches(IReadOnlyList<GuestSpan> spans, out ResourceSlotIdentifier[] owners)
    {
        var plan = Volatile.Read(ref _touchPlan);
        if (plan is not null && spans is List<GuestSpan> list &&
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list).SequenceEqual(plan.Spans))
        {
            owners = plan.Owners;
            return true;
        }
        owners = [];
        return false;
    }

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

    private static readonly bool HeldSliceUses = Environment.GetEnvironmentVariable("SHARPEMU_HELD_SLICE_USES") != "0";
    private ObtainMemo[]? _sliceReadProofs;
    private static readonly ThreadCounter _heldSliceUses = new();
    private static readonly ThreadCounter _obtainedSliceUses = new();

    public static string TakeHeldSliceUseReport() =>
        FormattableString.Invariant($"held_uses={_heldSliceUses.Take()}/obtained:{_obtainedSliceUses.Take()}");

    private bool TryHoldSliceUse(in BufferSliceUse use)
    {
        if (!HeldSliceUses || use.Written || use.Texel || use.InvalidateImages || ObtainMemoUse != ObtainMemoMode.On || use.Buffer is null)
        {
            return false;
        }

        CompleteGuestPublications?.Invoke(use.Address, use.Size, PublicationAccess.OverlaidRead);
        var buffer = use.Buffer;
        ResourceSlotIdentifier identifier;
        _sliceReadProofs ??= new ObtainMemo[512];
        // Page-aligned buffers often have equal low address bits.
        var slot = ((ObtainMemoSet(use.Address, use.Size) >> 1) ^ (int)(use.Address >> 12)) & 511;
        ref var proof = ref _sliceReadProofs[slot];
        if (!proof.Holds(use.Address, use.Size) || !ReferenceEquals(proof.Buffer, buffer))
        {
            identifier = _registry.FindContainingBuffer(use.Address, use.Size);
            proof = new ObtainMemo(use.Address, use.Size, identifier, 0, buffer);
        }
        if (!IsCleanOwner(ref proof, use.Address, use.Size)) return false;
        identifier = proof.Identifier;

        TouchBuffer(identifier);
        OverlayGuestPublications?.Invoke(buffer, buffer.Offset(use.Address), use.Address, use.Size);
        _heldSliceUses.Increment();
        return true;
    }

    internal long CpuDirtyEpoch => _tracker.CpuDirtyEpoch;

    // Only the presenter supplies this proof. Its two read callbacks complete or overlay
    // overlapping dumps. Writes, texel reads and invalidations retain their ordered effects.
    internal int ApplyRecordedBufferUses(List<BufferSliceUse> uses, long serial, long? cleanEpoch,
        PendingGuestPublications publications)
    {
        if (!cleanEpoch.HasValue || !HeldSliceUses || ObtainMemoUse != ObtainMemoMode.On)
        {
            foreach (var use in uses) ApplySliceUse(in use);
            return 0;
        }

        var held = 0;
        var previousOwner = ResourceSlotIdentifier.Invalid;
        foreach (var use in uses)
        {
            // A preceding full obtain can merge owners, submit or upload. Recheck at the position of each use.
            if (use.Written || use.Texel || use.InvalidateImages || serial != RegistrationSerial ||
                !use.Buffer.CacheIdentifier.IsValid || !publications.TryGetClearReadSnapshot(use.Address, use.Size, out var readDumps) ||
                (cleanEpoch.Value != CpuDirtyEpoch && _tracker.MayHaveCpuDirtyPages(use.Address, use.Size)))
            {
                ApplySliceUse(in use);
                previousOwner = ResourceSlotIdentifier.Invalid;
                continue;
            }

            // No collection tick can change between consecutive fast reads on this thread.
            if (previousOwner != use.Buffer.CacheIdentifier)
            {
                previousOwner = use.Buffer.CacheIdentifier;
                TouchBuffer(previousOwner);
            }
            // A dump may have arrived since the completion check, as in TryHoldSliceUse.
            if (publications.MayNeedNewOverlay(readDumps))
            {
                OverlayGuestPublications?.Invoke(use.Buffer, use.Buffer.Offset(use.Address), use.Address, use.Size);
                previousOwner = ResourceSlotIdentifier.Invalid;
            }
            held++;
        }
        if (held != 0) _heldSliceUses.Add(held);
        return held;
    }

    public void ApplySliceUse(in BufferSliceUse use)
    {
        if (TryHoldSliceUse(in use))
        {
            return;
        }

        _obtainedSliceUses.Increment();
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
