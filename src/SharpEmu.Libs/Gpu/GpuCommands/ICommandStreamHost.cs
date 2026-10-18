// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Gpu.GpuCommands;

public enum DrawOffsetSource
{
    Packet,
    IndirectArguments,
}

// Arguments of an indexed draw as the packet stream describes them.
public readonly record struct DrawIndexedArguments(
    ulong PacketAddress,
    uint Opcode,
    uint IndexCount,
    ulong IndexAddress,
    uint IndexTypeAndSize,
    uint InstanceCount,
    int BaseVertex,
    uint FirstInstance,
    DrawOffsetSource OffsetSource,
    // Nonzero when the counts are still in guest memory at this address, laid out as
    // (indexCount, instanceCount, firstIndex, vertexOffset, firstInstance). IndexCount is
    // then the index buffer size and IndexAddress its base.
    ulong IndirectArgumentsAddress = 0,
    // The guest set no index buffer size: IndexCount is only the range seen so far and
    // must not clamp the count read from the arguments.
    bool UnboundedIndexBuffer = false);

// Arguments of a non-indexed draw as the packet stream describes them.
public readonly record struct DrawAutoArguments(
    ulong PacketAddress,
    uint Opcode,
    uint VertexCount,
    uint InstanceCount,
    uint FirstVertex,
    uint FirstInstance,
    DrawOffsetSource OffsetSource);

public enum EndOfPipeWriteKind
{
    Write32,
    Write64,
    WriteBack32,
    WriteBack64,
    InterruptOnly,
    Interrupt32,
    Interrupt64,
    InterruptWriteBack32,
    InterruptWriteBack64,
    GdsWrite32,
    ClockWrite,
    ClockWriteBack,
    Flip,
    FlipWithWrite32,
    FlipWithInterruptWriteBack32,
}

// A completion the host records on the current command buffer. The label itself is
// already in guest memory when this is recorded.
public readonly record struct EndOfPipeWrite(
    EndOfPipeWriteKind Kind,
    ulong SubmitId,
    ulong Destination = 0,
    ulong Value = 0,
    int EventId = 0,
    uint ContextId = 0,
    uint GdsWordOffset = 0,
    uint GdsWordCount = 0,
    int FlipHandle = 0,
    int FlipIndex = 0,
    int FlipMode = 0,
    long FlipArgument = 0,
    ulong FlipRequestId = 0);

// Everything a packet handler needs from the renderer, the caches, the scheduler and video out.
public interface ICommandStreamHost
{
    ICpuMemory Memory { get; }

    bool TryRecordLodStatistics(ulong destination, uint size) => false;

    // Reads guest memory the GPU may have written; the host synchronizes GPU-owned pages first.
    bool TryReadGuest(ulong address, Span<byte> destination);

    // Reads a packet operand (a label, a predicate, indirect arguments): memory earlier work may
    // write, unlike the packets themselves. A host that defers work completes it first.
    bool TryReadGuestOperand(ulong address, Span<byte> destination) => TryReadGuest(address, destination);

    // Reads the label a wait polls. A stale value only suspends the wait until a retry, so a host
    // that runs work behind the stream need not complete it first.
    bool TryReadGuestWaitOperand(ulong address, Span<byte> destination) => TryReadGuestOperand(address, destination);

    // Runs commands other threads posted to this worker. Called before every packet.
    void RunPendingCommands();

    // Starts a slice of a submission; the geometry snapshots were captured when it was submitted.
    void BeginSubmission(int queueId, ulong submissionId, object? geometrySnapshots);

    void Flush();

    // A frame boundary: submits what the frame recorded. The submitter resumes once it returns, so a
    // host that runs the stream ahead of its recording returns only after the frame is recorded.
    void FlushFrame() => Flush();

    void FlushAndWait();

    void SynchronizeGpu();

    void RunGarbageCollector();

    void EmitGlobalBarrier();

    void FillBuffer(ulong address, ulong size, uint value, bool isGds);

    void CopyBuffer(ulong destination, ulong source, ulong size, bool destinationIsGds, bool sourceIsGds);

    // Records a direct packet write for the image-clear trace; see docs/image-clear-tracing.md.
    void TraceGuestWrite(string operation, ulong address, ulong size) { }

    // Prints recorded writers of a range for the image-clear trace.
    void TraceWritersOf(string subject, ulong address, ulong size) { }

    void ReadGds(Span<uint> destination, uint wordOffset, uint wordCount);

    // Return completed samples for counter zero. False keeps the legacy fallback.
    bool TryReadOcclusionCounter(int queueId, out ulong value)
    {
        value = 0;
        return false;
    }

    bool ResetOcclusionCounter(int queueId) => false;

    bool DumpOcclusionCounter(int queueId, ulong address, uint instanceMask, uint strideBytes) => false;

    bool HasPendingPublications(int queueId) => false;

    bool HasPendingMemoryPublication(ulong address, ulong size) => false;

    // After a wait on another queue's unpublished label, this queue's writes must not land first.
    // True when the write was held back; the host publishes it after that label.
    bool TryDeferOrderedWrite(int queueId, ulong address, ReadOnlySpan<byte> source) => false;

    // True when a read-modify-write must wait for the same reason; the caller suspends and retries.
    bool MustHoldForCrossQueueOrder(int queueId) => false;

    // Returns false when a pending publication hides the bytes from the command processor.
    bool TryReadDeferredLabel(int queueId, ulong address, ulong size, out bool deferred, out ulong value)
    {
        deferred = false;
        value = 0;
        return true;
    }

    bool TryDeferEndOfPipe(int queueId, in EndOfPipeWrite write) => false;

    void RecordEndOfPipe(in EndOfPipeWrite write);

    // Delivers the interrupt now, without waiting for the GPU.
    void TriggerInterrupt(int eventId, uint contextId);

    bool HasFlipSlot();

    // Reserves and prepares a flip; the request id is never zero.
    ulong PrepareFlip(int handle, int index, int flipMode, long flipArgument);
    ulong PrepareFlip(int queueId, int handle, int index, int flipMode, long flipArgument) =>
        PrepareFlip(handle, index, flipMode, flipArgument);

    // True when no request still targets the buffer, so the stream may render into it.
    bool IsFlipDone(int handle, int index);

    // A video-out export flip reached its place in the graphics queue: capture the buffer now.
    void PrepareCpuFlip(int handle, int index, ulong requestId);

    void DrawIndexed(ulong submitId, in DrawIndexedArguments arguments);

    void DrawAuto(ulong submitId, in DrawAutoArguments arguments);

    void DispatchDirect(ulong submitId, uint groupsX, uint groupsY, uint groupsZ, uint dispatchInitiator, ulong indirectArgumentsAddress = 0);

    // True when an indirect dispatch in workgroup units reads its group counts on the
    // GPU, so the interpreter need not wait for the GPU to read them back first.
    bool ResolvesIndirectDispatchOnGpu => false;

    // True when an indexed indirect draw may be handed over with its arguments still in
    // guest memory (DrawIndexedArguments.IndirectArgumentsAddress).
    bool ResolvesIndirectDrawOnGpu => false;

    // Called when a queue reset packet clears the processor.
    void OnQueueReset(int queueId);

    Exception Fatal(string message);
}
