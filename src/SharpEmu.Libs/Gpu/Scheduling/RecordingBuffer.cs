// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Scheduling;

public readonly record struct MemoryBarrierRequest(
    PipelineStageFlags SourceStages,
    PipelineStageFlags DestinationStages,
    AccessFlags SourceAccess,
    AccessFlags DestinationAccess)
{
    public MemoryBarrierRequest Union(in MemoryBarrierRequest other) => new(
        SourceStages | other.SourceStages,
        DestinationStages | other.DestinationStages,
        SourceAccess | other.SourceAccess,
        DestinationAccess | other.DestinationAccess);

    private const AccessFlags WriteAccess = AccessFlags.ShaderWriteBit | AccessFlags.ColorAttachmentWriteBit |
        AccessFlags.DepthStencilAttachmentWriteBit | AccessFlags.TransferWriteBit | AccessFlags.HostWriteBit | AccessFlags.MemoryWriteBit;

    public bool Covers(in MemoryBarrierRequest other) =>
        CoversStages(SourceStages, other.SourceStages) && CoversStages(DestinationStages, other.DestinationStages) &&
        CoversAccess(SourceAccess, other.SourceAccess) && CoversAccess(DestinationAccess, other.DestinationAccess);

    private static bool CoversStages(PipelineStageFlags stages, PipelineStageFlags other) =>
        (stages & PipelineStageFlags.AllCommandsBit) != 0 || (other & ~stages) == 0;

    private static bool CoversAccess(AccessFlags access, AccessFlags other)
    {
        var uncovered = other & ~access;
        if ((access & AccessFlags.MemoryWriteBit) != 0)
        {
            uncovered &= ~WriteAccess;
        }

        if ((access & AccessFlags.MemoryReadBit) != 0)
        {
            uncovered &= WriteAccess;
        }

        return uncovered == 0;
    }
}

public enum RecordedOperation : uint
{
    DispatchDirect,
    DrawIndex,
    DrawIndexAuto,
    EopWrite,
    EopInterrupt,
    EopWriteBack,
    EopFlip,
    EopWriteBackFlip,
    EopOnlyFlip,
    Unknown,
}

public sealed class RecordingBuffer
{
    private static long s_promotionGeneration;
    private static long s_recordingGeneration;

    private readonly IGpuCommandBufferSource _source;
    private readonly TickedBufferRing _ring;
    private readonly IRenderingState _rendering;
    private SubmissionContext? _context;
    private bool _handleWasRequested;
    private MemoryBarrierRequest _pendingBarrier;
    private bool _hasPendingBarrier;
    private MemoryBarrierRequest _deferredBarrier;
    private bool _hasDeferredBarrier;
    private bool _checkedWindow;
    private long _recordedBarriersAtBegin;
    private bool _hostReadRecorded;

    internal nint Buffer;

    internal RecordingBuffer(SubmissionScheduler owner, IGpuCommandBufferSource source, IRenderingState rendering)
    {
        Owner = owner;
        _source = source;
        _rendering = rendering;
        _ring = new TickedBufferRing(source, owner.Timeline);
    }

    public SubmissionScheduler Owner { get; }

    public uint DebugOp { get; private set; }

    public ulong DebugSubmitId { get; private set; }

    public uint DebugArg0 { get; private set; }

    public uint DebugArg1 { get; private set; }

    public uint DebugArg2 { get; private set; }

    public uint DebugArg3 { get; private set; }

    public ulong DebugArg4 { get; private set; }

    public bool IsInvalid => Buffer == 0;

    public bool Ended { get; private set; }

    public long Generation { get; private set; }

    internal bool HasPendingCommands => _handleWasRequested || _hasPendingBarrier || _hasDeferredBarrier || _rendering.IsRendering;

    // Changes whenever a deferred barrier is recorded or a buffer starts, so a caller can tell whether
    // the work it deferred is still outstanding.
    public long DeferredPromotions { get; private set; }

    public long MergedBarriers { get; private set; }

    public long RecordedBarriers { get; private set; }

    public bool MayWriteMappedMemory { get; private set; }

    public void NoteMappedMemoryWrite() => MayWriteMappedMemory = true;

    // The native handle stays valid only until the next submission.
    public nint Handle
    {
        get
        {
            if (IsInvalid)
            {
                throw SubmissionScheduler.Fatal("The command buffer is not recording.");
            }

            // A caller can record native commands after it obtains the handle.
            if (_hasDeferredBarrier && !_checkedWindow)
            {
                PromoteDeferredBarrier(Vulkan.DeferredBarrierPromotion.Handle);
            }

            FlushPendingBarrier();
            _handleWasRequested = true;
            return Buffer;
        }
    }

    // For transfers that record their own range barriers: a pending barrier still flushes, a
    // deferred one stays deferred because the transfer orders itself against earlier work.
    public nint TransferHandle
    {
        get
        {
            if (IsInvalid)
            {
                throw SubmissionScheduler.Fatal("The command buffer is not recording.");
            }

            if (_hasDeferredBarrier && Vulkan.GpuSyncProfile.Enabled)
            {
                Vulkan.GpuSyncProfile.CountTransferKeptDeferred();
            }

            FlushPendingBarrier();
            _handleWasRequested = true;
            return Buffer;
        }
    }

    public nint StateHandle
    {
        get
        {
            if (IsInvalid)
            {
                throw SubmissionScheduler.Fatal("The command buffer is not recording.");
            }

            _handleWasRequested = true;
            return Buffer;
        }
    }

    public void RequestMemoryBarrier(in MemoryBarrierRequest request)
    {
        if (IsInvalid || _rendering.IsRendering)
        {
            throw SubmissionScheduler.Fatal("A memory barrier needs a recording command buffer outside rendering.");
        }

        var merged = request;
        if (_hasDeferredBarrier)
        {
            merged = merged.Union(_deferredBarrier);
            _hasDeferredBarrier = false;
            DeferredPromotions = Interlocked.Increment(ref s_promotionGeneration);
            if (Vulkan.GpuSyncProfile.Enabled)
            {
                Vulkan.GpuSyncProfile.CountDeferredPromotion(Vulkan.DeferredBarrierPromotion.Request);
            }
        }

        if (_hasPendingBarrier)
        {
            if (Vulkan.GpuSyncProfile.Enabled)
            {
                Vulkan.GpuSyncProfile.CountMergedRequest();
                if (_pendingBarrier.Covers(merged))
                {
                    Vulkan.BarrierTally.IdleRequests.Increment();
                }
            }

            _pendingBarrier = _pendingBarrier.Union(merged);
            MergedBarriers++;
            return;
        }

        _pendingBarrier = merged;
        _hasPendingBarrier = true;
    }

    // Recorded by the next native command outside a checked window, or folded into the next request.
    public void DeferMemoryBarrier(in MemoryBarrierRequest request)
    {
        if (IsInvalid || _rendering.IsRendering)
        {
            throw SubmissionScheduler.Fatal("A memory barrier needs a recording command buffer outside rendering.");
        }

        if (_hasPendingBarrier)
        {
            _pendingBarrier = _pendingBarrier.Union(request);
            return;
        }

        _deferredBarrier = _hasDeferredBarrier ? _deferredBarrier.Union(request) : request;
        _hasDeferredBarrier = true;
        if (Vulkan.GpuSyncProfile.Enabled)
        {
            Vulkan.GpuSyncProfile.CountDeferredBarrier();
        }
    }

    // Native commands inside the window were checked against the deferred barrier's writes.
    public void EnterCheckedWindow()
    {
        if (_hasDeferredBarrier && Vulkan.GpuSyncProfile.Enabled)
        {
            Vulkan.GpuSyncProfile.CountSkippedBarrier();
        }

        _checkedWindow = true;
    }

    public void ExitCheckedWindow() => _checkedWindow = false;

    private void PromoteDeferredBarrier(Vulkan.DeferredBarrierPromotion cause)
    {
        if (cause == Vulkan.DeferredBarrierPromotion.Handle && Diagnostics.PromotionSiteTally.Enabled)
        {
            Diagnostics.PromotionSiteTally.Note();
        }

        var request = _deferredBarrier;
        _hasDeferredBarrier = false;
        DeferredPromotions = Interlocked.Increment(ref s_promotionGeneration);
        if (Vulkan.GpuSyncProfile.Enabled)
        {
            Vulkan.GpuSyncProfile.CountDeferredPromotion(cause);
        }

        RequestMemoryBarrier(request);
    }

    private void FlushPendingBarrier()
    {
        if (!_hasPendingBarrier)
        {
            return;
        }

        _hasPendingBarrier = false;
        if (_rendering.IsRendering)
        {
            throw SubmissionScheduler.Fatal("A pending memory barrier reached a rendering scope.");
        }

        var recorder = Owner.MemoryBarrierRecorder ??
            throw SubmissionScheduler.Fatal("A memory barrier was requested without a recorder.");
        recorder(Buffer, _pendingBarrier);
        RecordedBarriers++;
    }

    public SubmissionContext Context => _context ?? throw SubmissionScheduler.Fatal("The command buffer has no submission context.");

    public void SetDebugInfo(uint op, ulong submitId, uint arg0 = 0, uint arg1 = 0, uint arg2 = 0, uint arg3 = 0, ulong arg4 = 0)
    {
        DebugOp = op;
        DebugSubmitId = submitId;
        DebugArg0 = arg0;
        DebugArg1 = arg1;
        DebugArg2 = arg2;
        DebugArg3 = arg3;
        DebugArg4 = arg4;
    }

    public void EndRendering()
    {
        if (_rendering.IsRendering)
        {
            _rendering.EndRendering();
        }
    }

    internal void Bind(SubmissionContext context) => _context = context;

    internal void Acquire()
    {
        if (!IsInvalid)
        {
            throw SubmissionScheduler.Fatal("Cannot start recording. The command buffer is already recording.");
        }

        Buffer = _ring.AcquireBuffer();
        Begin();
    }

    internal void Submitted(ulong tick)
    {
        _ring.Submitted(Buffer, tick);
        Buffer = 0;
        Ended = false;
    }

    internal void Discard()
    {
        if (IsInvalid)
        {
            return;
        }

        if (!Ended)
        {
            EndDiscarded();
        }

        if (Vulkan.GpuSyncProfile.Enabled)
        {
            Vulkan.BarrierTally.DiscardedBuffers.Increment();
            Vulkan.BarrierTally.DiscardedBarriers.Add(RecordedBarriers - _recordedBarriersAtBegin + (_hostReadRecorded ? 1 : 0));
        }

        _ring.Submitted(Buffer, 0);
        Buffer = 0;
        Ended = false;
    }

    internal void Begin()
    {
        if (_rendering.IsRendering || IsInvalid)
        {
            throw SubmissionScheduler.Fatal("Cannot start recording while rendering is active or the command buffer is missing.");
        }

        _source.BeginBuffer(Buffer);
        Ended = false;
        Generation = Interlocked.Increment(ref s_recordingGeneration);
        _handleWasRequested = false;
        _hasPendingBarrier = false;
        _hasDeferredBarrier = false;
        _checkedWindow = false;
        _recordedBarriersAtBegin = RecordedBarriers;
        _hostReadRecorded = false;
        MayWriteMappedMemory = false;
        DeferredPromotions = Interlocked.Increment(ref s_promotionGeneration);
        SetDebugInfo((uint)RecordedOperation.Unknown, 0);
    }

    internal void End()
    {
        EndRendering();
        _checkedWindow = false;
        _hostReadRecorded = true;
        _source.EndBuffer(Handle, _hostReadRecorded);
        if (Vulkan.GpuSyncProfile.Enabled)
        {
            (_hostReadRecorded ? Vulkan.BarrierTally.HostReadRecorded : Vulkan.BarrierTally.HostReadSkipped).Increment();
        }

        Ended = true;
    }

    internal void EndDiscarded()
    {
        var avoided = DropBarriers();
        EndRendering();
        avoided += DropBarriers();
        _checkedWindow = false;
        _hostReadRecorded = false;
        _source.EndBuffer(Handle, hostRead: false);
        if (Vulkan.GpuSyncProfile.Enabled)
        {
            Vulkan.BarrierTally.DiscardAvoided.Add(avoided + (MayWriteMappedMemory ? 1 : 0));
        }

        Ended = true;
    }

    private int DropBarriers()
    {
        var dropped = (_hasPendingBarrier ? 1 : 0) + (_hasDeferredBarrier ? 1 : 0);
        _hasPendingBarrier = false;
        _hasDeferredBarrier = false;
        return dropped;
    }
}
