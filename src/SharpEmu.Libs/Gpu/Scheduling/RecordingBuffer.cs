// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Scheduling;

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

    public nint Handle => IsInvalid ? throw SubmissionScheduler.Fatal("The command buffer is not recording.") : Buffer;

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
    }

    internal void End()
    {
        EndRendering();
        _device.EndBuffer(Handle);
    }
}
