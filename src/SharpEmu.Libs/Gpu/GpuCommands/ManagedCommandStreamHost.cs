// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.Scheduling;

namespace SharpEmu.Libs.Gpu.GpuCommands;

// Track flips for hosts that present without a GPU tick.
public interface ICommandStreamFlipTarget
{
    ulong Prepare(int handle, int index, int flipMode, long flipArgument);

    void Complete(ulong requestId);

    // The presenter is done with the frame of a flip the export path completed itself.
    void Presented(ulong requestId);

    bool IsDone(int handle, int index);
}

// A host without a GPU: transfers are managed copies and completions finish at once.
public class ManagedCommandStreamHost : ICommandStreamHost
{
    public const int GdsBytes = 64 * 1024;
    private const int TransferChunkBytes = 64 * 1024;

    private readonly IEndOfPipeSink _interrupts;
    private readonly ICommandStreamFlipTarget _flips;
    private readonly byte[] _gds = new byte[GdsBytes];

    public ManagedCommandStreamHost(ICpuMemory memory, IEndOfPipeSink interrupts, ICommandStreamFlipTarget flips)
    {
        Memory = memory;
        _interrupts = interrupts;
        _flips = flips;
    }

    public ICpuMemory Memory { get; }

    public ReadOnlySpan<byte> Gds => _gds;

    public virtual bool TryReadGuest(ulong address, Span<byte> destination) => Memory.TryRead(address, destination);

    public virtual void RunPendingCommands()
    {
    }

    public virtual void BeginSubmission(int queueId, ulong submissionId, object? geometrySnapshots)
    {
    }

    public virtual void Flush()
    {
    }

    public virtual void FlushAndWait()
    {
    }

    public virtual void SynchronizeGpu()
    {
    }

    public virtual void RunGarbageCollector()
    {
    }

    public virtual void EmitGlobalBarrier()
    {
    }

    public virtual void FillBuffer(ulong address, ulong size, uint value, bool isGds)
    {
        if ((address & 3) != 0 || (size & 3) != 0)
        {
            throw Fatal($"The fill range is not dword aligned: address=0x{address:X16} size=0x{size:X}.");
        }

        if (isGds)
        {
            if (address > GdsBytes || size > (ulong)GdsBytes - address)
            {
                throw Fatal($"The GDS fill range is outside the buffer: offset=0x{address:X} size=0x{size:X}.");
            }

            var words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(_gds.AsSpan((int)address, (int)size));
            words.Fill(value);
            return;
        }

        if (address == 0)
        {
            throw Fatal($"The fill address is zero: size=0x{size:X} value=0x{value:X8}.");
        }

        var pattern = new byte[Math.Min(size, (ulong)TransferChunkBytes)];
        for (var offset = 0; offset < pattern.Length; offset += sizeof(uint))
        {
            BinaryPrimitives.WriteUInt32LittleEndian(pattern.AsSpan(offset), value);
        }

        var remaining = size;
        var cursor = address;
        while (remaining != 0)
        {
            var chunk = (int)Math.Min(remaining, (ulong)pattern.Length);
            if (!Memory.TryWrite(cursor, pattern.AsSpan(0, chunk)))
            {
                throw Fatal($"The fill cannot write guest memory: address=0x{cursor:X16} size=0x{chunk:X}.");
            }

            cursor += (ulong)chunk;
            remaining -= (ulong)chunk;
        }
    }

    public virtual void CopyBuffer(ulong destination, ulong source, ulong size, bool destinationIsGds, bool sourceIsGds)
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

        var chunk = new byte[Math.Min(size, (ulong)TransferChunkBytes)];
        var remaining = size;
        var offset = 0UL;
        while (remaining != 0)
        {
            var count = (int)Math.Min(remaining, (ulong)chunk.Length);
            var span = chunk.AsSpan(0, count);
            if (sourceIsGds)
            {
                _gds.AsSpan((int)(source + offset), count).CopyTo(span);
            }
            else if (!Memory.TryRead(source + offset, span))
            {
                throw Fatal($"The copy cannot read guest memory: address=0x{source + offset:X16} size=0x{count:X}.");
            }

            if (destinationIsGds)
            {
                span.CopyTo(_gds.AsSpan((int)(destination + offset), count));
            }
            else if (!Memory.TryWrite(destination + offset, span))
            {
                throw Fatal($"The copy cannot write guest memory: address=0x{destination + offset:X16} size=0x{count:X}.");
            }

            offset += (ulong)count;
            remaining -= (ulong)count;
        }
    }

    public void ReadGds(Span<uint> destination, uint wordOffset, uint wordCount) =>
        EndOfPipe.ReadGdsWords(_gds, destination, wordOffset, wordCount);

    // Without a GPU tick every completion is due as soon as it is recorded.
    public virtual void RecordEndOfPipe(in EndOfPipeWrite write)
    {
        switch (write.Kind)
        {
            case EndOfPipeWriteKind.InterruptOnly:
            case EndOfPipeWriteKind.Interrupt32:
            case EndOfPipeWriteKind.Interrupt64:
            case EndOfPipeWriteKind.InterruptWriteBack32:
            case EndOfPipeWriteKind.InterruptWriteBack64:
                _interrupts.TriggerInterrupt(write.EventId, write.ContextId);
                break;
            case EndOfPipeWriteKind.Flip:
            case EndOfPipeWriteKind.FlipWithWrite32:
                _flips.Complete(write.FlipRequestId);
                break;
            case EndOfPipeWriteKind.FlipWithInterruptWriteBack32:
                _flips.Complete(write.FlipRequestId);
                _interrupts.TriggerInterrupt(write.EventId, 0);
                break;
        }
    }

    public void TriggerInterrupt(int eventId, uint contextId) => _interrupts.TriggerInterrupt(eventId, contextId);

    public virtual bool HasFlipSlot() => true;

    public virtual ulong PrepareFlip(int handle, int index, int flipMode, long flipArgument)
    {
        var requestId = _flips.Prepare(handle, index, flipMode, flipArgument);
        if (requestId == 0)
        {
            throw Fatal($"The flip request id is zero: handle={handle} index={index} mode={flipMode} arg={flipArgument}.");
        }

        return requestId;
    }

    public virtual bool IsFlipDone(int handle, int index) => _flips.IsDone(handle, index);

    // Without a presenter there is nothing to capture; the frame is done at once.
    public virtual void PrepareCpuFlip(int handle, int index, ulong requestId) => _flips.Presented(requestId);

    // No GPU is attached: draws and dispatches have no host work to record.
    public virtual void DrawIndexed(ulong submitId, in DrawIndexedArguments arguments)
    {
    }

    public virtual void DrawAuto(ulong submitId, in DrawAutoArguments arguments)
    {
    }

    public virtual void DispatchDirect(ulong submitId, uint groupsX, uint groupsY, uint groupsZ, uint dispatchInitiator)
    {
    }

    public virtual void OnQueueReset(int queueId)
    {
    }

    public Exception Fatal(string message) => SubmissionScheduler.Fatal(message);
}
