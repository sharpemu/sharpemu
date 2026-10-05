// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.VideoOut;

namespace SharpEmu.Libs.Gpu.Metal;

// The command interpreter host of the Metal backend: managed transfers mirrored into the
// snapshot images, draws and completions posted to the presenter's ordered queue in stream order.
internal sealed partial class MetalCommandStreamHost : AgcExports.TranslatingCommandStreamHost
{
    private readonly IGuestImageSnapshotBackend _snapshots;
    private readonly IGuestGpuBackend _backend;
    private IDisposable? _queueScope;

    public MetalCommandStreamHost(ICpuMemory memory, IGuestGpuBackend backend, IGuestImageSnapshotBackend snapshots)
        : base(memory)
    {
        _backend = backend;
        _snapshots = snapshots;
        _context = new CpuContext(memory, Generation.Gen5);
    }

    public override void BeginSubmission(int queueId, ulong submissionId, object? geometrySnapshots)
    {
        _queueScope?.Dispose();
        var queueName = queueId == 0 ? "dcb.graphics" : $"acb.compute[{GpuCommandInterpreter.ComputeQueueBase + queueId - 1}]";
        _queueScope = MetalVideoPresenter.EnterGuestQueue(queueName, submissionId);
        base.BeginSubmission(queueId, submissionId, geometrySnapshots);
    }

    // The global data share lives on the GPU; its transfers queue behind the draws before them.
    public override void FillBuffer(ulong address, ulong size, uint value, bool isGds)
    {
        if (isGds)
        {
            if ((address & 3) != 0 || (size & 3) != 0)
            {
                throw Fatal($"The fill range is not dword aligned: address=0x{address:X16} size=0x{size:X}.");
            }

            if (address > GdsBytes || size > (ulong)GdsBytes - address)
            {
                throw Fatal($"The GDS fill range is outside the buffer: offset=0x{address:X} size=0x{size:X}.");
            }

            // A blit fill repeats one byte, so a pattern with unequal bytes is expanded and copied.
            Span<byte> bytes = stackalloc byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
            if (bytes[0] == bytes[1] && bytes[1] == bytes[2] && bytes[2] == bytes[3])
            {
                _ = _snapshots.SubmitGlobalDataShareFill(address, size, bytes[0]);
                return;
            }

            var expanded = new byte[size];
            for (var offset = 0; offset < expanded.Length; offset += sizeof(uint))
            {
                BinaryPrimitives.WriteUInt32LittleEndian(expanded.AsSpan(offset), value);
            }

            _ = _snapshots.SubmitGlobalDataShareCopyFromGuest(address, expanded);
            return;
        }

        base.FillBuffer(address, size, value, isGds);
        MirrorTransferToSnapshotImage(address, size, value);
    }

    public override void CopyBuffer(ulong destination, ulong source, ulong size, bool destinationIsGds, bool sourceIsGds)
    {
        if (size == 0)
        {
            return;
        }

        if (destinationIsGds && sourceIsGds)
        {
            throw Fatal($"A GDS-to-GDS copy is not supported: destination=0x{destination:X} source=0x{source:X} size=0x{size:X}.");
        }

        if ((destinationIsGds && (destination > GdsBytes || size > (ulong)GdsBytes - destination)) ||
            (sourceIsGds && (source > GdsBytes || size > (ulong)GdsBytes - source)) ||
            (!destinationIsGds && destination == 0) ||
            (!sourceIsGds && source == 0))
        {
            throw Fatal($"The copy range is invalid: destination=0x{destination:X16} source=0x{source:X16} size=0x{size:X} dstGds={destinationIsGds} srcGds={sourceIsGds}.");
        }

        if (destinationIsGds)
        {
            var bytes = new byte[checked((int)size)];
            if (!Memory.TryRead(source, bytes))
            {
                throw Fatal($"The copy cannot read guest memory: address=0x{source:X16} size=0x{size:X}.");
            }

            _ = _snapshots.SubmitGlobalDataShareCopyFromGuest(destination, bytes);
            return;
        }

        if (sourceIsGds)
        {
            _ = _snapshots.SubmitGlobalDataShareCopyToGuest(destination, source, size);
            return;
        }

        base.CopyBuffer(destination, source, size, destinationIsGds, sourceIsGds);
        MirrorTransferToSnapshotImage(destination, size, null);
    }

    // Reached only after SynchronizeGpu, so the shared buffer holds every queued transfer and shader write.
    public override void ReadGds(Span<uint> destination, uint wordOffset, uint wordCount) =>
        _snapshots.ReadGlobalDataShare(destination, wordOffset, wordCount);

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

    // The ordered queue runs every earlier record and then waits for the GPU to complete them.
    public override void SynchronizeGpu()
    {
        var sequence = _snapshots.SubmitGpuSynchronization("command_stream synchronize");
        if (sequence != 0)
        {
            _ = MetalVideoPresenter.WaitForGuestWork(sequence, Timeout.Infinite);
        }
    }
}
