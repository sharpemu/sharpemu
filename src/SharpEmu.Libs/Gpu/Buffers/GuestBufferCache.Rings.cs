// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Numerics;
using SharpEmu.Libs.Gpu.Scheduling;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Buffers;

public sealed partial class GuestBufferCache
{
    // A reservation always belongs to one immutable buffer. Retire replaced generations after the render
    // preparation closes and its commands (including priority readbacks) have completed.
    private sealed class TransferRing
    {
        private readonly GuestBufferCache _cache;
        private readonly GpuBufferUsage _usage;
        private readonly BufferUsageFlags _flags;
        private bool IsStream => _flags == GpuBuffer.AddressedFlags;
        private readonly ulong _minimum, _maximum;
        private long _nextReview;
        private ulong _peak, _tick, _tickBytes;
        private bool _exported;
        public GpuRingBuffer Buffer { get; private set; }

        public TransferRing(GuestBufferCache cache, GpuBufferUsage usage, ulong minimum, ulong maximum, BufferUsageFlags flags = GpuBuffer.AllFlags)
        {
            (_cache, _usage, _minimum, _maximum) = (cache, usage, minimum, maximum);
            _flags = flags;
            Buffer = Create(minimum);
            _nextReview = Environment.TickCount64 + 10_000;
        }

        private GpuRingBuffer Create(ulong size) => new(_cache._device, _cache._scheduler, _usage, size, _flags);

        public GpuRingBuffer Export()
        {
            // A stream reservation already returned to a caller must still identify this ring.
            // Once exported, keep the generation stable for the caller's lifetime.
            if (!IsStream) Resize(_maximum);
            _exported = true;
            return Buffer;
        }

        public GpuRingBuffer Request(ulong bytes)
        {
            if (_exported) return Buffer;
            var tick = _cache._scheduler.CurrentTick;
            if (tick != _tick) { _tick = tick; _tickBytes = 0; }
            bytes = Math.Min(bytes, _maximum);
            _tickBytes = Math.Min(_maximum, _tickBytes + bytes);
            // A device scratch copy uses offset zero repeatedly, unlike a mapped ring reservation.
            var demand = _usage == GpuBufferUsage.DeviceLocal ? bytes : _tickBytes;
            _peak = Math.Max(_peak, demand);
            if (demand > Buffer.Size && (!IsStream || !Buffer.HasRetentions)) Resize(BitOperations.RoundUpToPowerOf2(demand));
            return Buffer;
        }

        public void Review(long now)
        {
            if (_exported || (IsStream && Buffer.HasRetentions)) return;
            if (now < _nextReview) return;
            _nextReview = now + 10_000;
            var target = Math.Clamp(BitOperations.RoundUpToPowerOf2(Math.Max(1, _peak)) * 2, _minimum, _maximum);
            if (target < Buffer.Size || (IsStream && _peak > Buffer.Size)) Resize(target);
            _peak = 0;
        }

        private void Resize(ulong capacity)
        {
            if (capacity == Buffer.Size || (IsStream && Buffer.HasRetentions)) return;
            var next = Create(capacity);
            _cache._retiredRings.Add(Buffer);
            Buffer = next;
            if (IsStream) _cache._streamCopyShares = new();
            if (_usage == GpuBufferUsage.Upload)
                _cache._uploader = new(_cache._device, _cache._scheduler, _cache._guest, next, _cache._sweepCopy);
        }
    }

    internal GpuRingBuffer GetTransferRing(GpuBufferUsage usage, ulong bytes)
    {
        if (SubmissionScheduler.ReadOnlyThread) throw SubmissionScheduler.ReadOnlyViolation("utility buffer");
        return usage switch
        {
            GpuBufferUsage.Stream => _streamCapacity.Request(bytes),
            GpuBufferUsage.DeviceLocal => _deviceCapacity.Request(bytes),
            GpuBufferUsage.Download => _downloadCapacity.Request(bytes),
            _ => throw new ArgumentOutOfRangeException(nameof(usage)),
        };
    }

    private long _nextRingReview;
    private void MaintainTransferRings()
    {
        if (_preparationsOpen != 0) return;
        var now = Environment.TickCount64;
        if (now >= _nextRingReview)
        {
            _nextRingReview = now + 1000;
            _stageCapacity.Review(now);
            _streamCapacity.Review(now);
            _deviceCapacity.Review(now);
            _downloadCapacity.Review(now);
        }
        foreach (var ring in _retiredRings) _scheduler.QueueCompletionAction(ring.Dispose);
        _retiredRings.Clear();
    }
}
