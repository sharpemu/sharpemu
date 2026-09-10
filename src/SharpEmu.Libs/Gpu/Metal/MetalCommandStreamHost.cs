// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.VideoOut;

namespace SharpEmu.Libs.Gpu.Metal;

// The command interpreter host of the Metal backend: managed transfers mirrored into the
// snapshot images, draws and completions posted to the presenter's ordered queue in stream order.
internal sealed class MetalCommandStreamHost : AgcExports.TranslatingCommandStreamHost
{
    private readonly IGuestImageSnapshotBackend _snapshots;
    private IDisposable? _queueScope;

    public MetalCommandStreamHost(ICpuMemory memory, IGuestImageSnapshotBackend snapshots)
        : base(memory)
    {
        _snapshots = snapshots;
    }

    public override void BeginSubmission(int queueId, ulong submissionId, object? geometrySnapshots)
    {
        _queueScope?.Dispose();
        var queueName = queueId == 0 ? "dcb.graphics" : $"acb.compute[{GpuCommandInterpreter.ComputeQueueBase + queueId - 1}]";
        _queueScope = MetalVideoPresenter.EnterGuestQueue(queueName, submissionId);
        base.BeginSubmission(queueId, submissionId, geometrySnapshots);
    }

    public override void FillBuffer(ulong address, ulong size, uint value, bool isGds)
    {
        base.FillBuffer(address, size, value, isGds);
        if (!isGds)
        {
            MirrorTransferToSnapshotImage(address, size, value);
        }
    }

    public override void CopyBuffer(ulong destination, ulong source, ulong size, bool destinationIsGds, bool sourceIsGds)
    {
        base.CopyBuffer(destination, source, size, destinationIsGds, sourceIsGds);
        if (!destinationIsGds)
        {
            MirrorTransferToSnapshotImage(destination, size, null);
        }
    }

    // A transfer that covers a whole snapshot image replaces that image's pixels.
    private void MirrorTransferToSnapshotImage(ulong destination, ulong byteCount, uint? fillValue)
    {
        if (!_snapshots.TryGetGuestImageExtent(destination, out _, out _, out var imageBytes) ||
            imageBytes == 0 || byteCount < imageBytes)
        {
            return;
        }

        if (fillValue is { } fill)
        {
            _snapshots.SubmitGuestImageFill(destination, fill);
            return;
        }

        var pixels = new byte[imageBytes];
        if (Memory.TryRead(destination, pixels))
        {
            _snapshots.SubmitGuestImageWrite(destination, pixels);
        }
    }

    // Use the same ordered queue to complete each request after its preceding draws.
    public override void RecordEndOfPipe(in EndOfPipeWrite write)
    {
        var completion = write;
        if (MetalVideoPresenter.SubmitOrderedGuestAction(() => base.RecordEndOfPipe(in completion), $"command_stream completion {write.Kind}") == 0)
        {
            base.RecordEndOfPipe(in completion);
        }
    }

    public override ulong PrepareFlip(int handle, int index, int flipMode, long flipArgument)
    {
        var requestId = base.PrepareFlip(handle, index, flipMode, flipArgument);
        if (VideoOutExports.TryGetDisplayBufferInfo(handle, index, out var displayBuffer))
        {
            _ = MetalVideoPresenter.TrySubmitOrderedGuestImageFlip(
                handle,
                index,
                displayBuffer.Address,
                displayBuffer.Width,
                displayBuffer.Height,
                displayBuffer.PitchInPixel);
        }

        return requestId;
    }

    // A video-out export flip at its place in the graphics queue; the capture counts as presented.
    public override void PrepareCpuFlip(int handle, int index, ulong requestId)
    {
        if (VideoOutExports.TryGetDisplayBufferInfo(handle, index, out var displayBuffer))
        {
            _ = MetalVideoPresenter.TrySubmitOrderedGuestImageFlip(
                handle,
                index,
                displayBuffer.Address,
                displayBuffer.Width,
                displayBuffer.Height,
                displayBuffer.PitchInPixel);
        }

        VideoOutExports.MarkFlipPresented(requestId);
    }

    // The presenter keeps its own captured versions; waiting for the ordered queue is the sync point.
    public override void SynchronizeGpu()
    {
        var sequence = MetalVideoPresenter.SubmitOrderedGuestAction(static () => { }, "command_stream synchronize");
        if (sequence != 0)
        {
            _ = MetalVideoPresenter.WaitForGuestWork(sequence, Timeout.Infinite);
        }
    }
}
