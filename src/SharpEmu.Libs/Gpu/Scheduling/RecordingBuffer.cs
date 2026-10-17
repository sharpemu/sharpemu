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
    private readonly IGpuTickDevice _device;
    private readonly IRenderingState _rendering;
    private SubmissionContext? _context;
    private bool _handleWasRequested;
    private MemoryBarrierRequest _pendingBarrier;
    private bool _hasPendingBarrier;

    internal nint Buffer;

    internal RecordingBuffer(SubmissionScheduler owner, IGpuTickDevice device, IRenderingState rendering)
    {
        Owner = owner;
        _device = device;
        _rendering = rendering;
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

    internal bool HasPendingCommands => _handleWasRequested || _hasPendingBarrier || _rendering.IsRendering;

    public long MergedBarriers { get; private set; }

    public long RecordedBarriers { get; private set; }

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

        if (_hasPendingBarrier)
        {
            _pendingBarrier = _pendingBarrier.Union(request);
            MergedBarriers++;
            if (Vulkan.GpuSyncProfile.Enabled)
            {
                Vulkan.GpuSyncProfile.CountMergedRequest();
            }

            return;
        }

        _pendingBarrier = request;
        _hasPendingBarrier = true;
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

    internal void Begin()
    {
        if (_rendering.IsRendering || IsInvalid)
        {
            throw SubmissionScheduler.Fatal("Cannot start recording while rendering is active or the command buffer is missing.");
        }

        _device.BeginBuffer(Buffer);
        _handleWasRequested = false;
        _hasPendingBarrier = false;
        SetDebugInfo((uint)RecordedOperation.Unknown, 0);
    }

    internal void End()
    {
        EndRendering();
        _device.EndBuffer(Handle);
    }
}
