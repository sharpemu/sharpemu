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
    DrawOffsetSource OffsetSource);

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

    // Reads guest memory the GPU may have written; the host synchronizes GPU-owned pages first.
    bool TryReadGuest(ulong address, Span<byte> destination);

    // Runs commands other threads posted to this worker. Called before every packet.
    void RunPendingCommands();

    // Starts a slice of a submission; the geometry snapshots were captured when it was submitted.
    void BeginSubmission(int queueId, ulong submissionId, object? geometrySnapshots);

    void Flush();

    void FlushAndWait();

    void SynchronizeGpu();

    void RunGarbageCollector();

    void EmitGlobalBarrier();

    void FillBuffer(ulong address, ulong size, uint value, bool isGds);

    void CopyBuffer(ulong destination, ulong source, ulong size, bool destinationIsGds, bool sourceIsGds);

    void ReadGds(Span<uint> destination, uint wordOffset, uint wordCount);

    void RecordEndOfPipe(in EndOfPipeWrite write);

    // Delivers the interrupt now, without waiting for the GPU.
    void TriggerInterrupt(int eventId, uint contextId);

    bool HasFlipSlot();

    // Reserves and prepares a flip; the request id is never zero.
    ulong PrepareFlip(int handle, int index, int flipMode, long flipArgument);

    // True when no request still targets the buffer, so the stream may render into it.
    bool IsFlipDone(int handle, int index);

    // A video-out export flip reached its place in the graphics queue: capture the buffer now.
    void PrepareCpuFlip(int handle, int index, ulong requestId);

    void DrawIndexed(ulong submitId, in DrawIndexedArguments arguments);

    void DrawAuto(ulong submitId, in DrawAutoArguments arguments);

    void DispatchDirect(ulong submitId, uint groupsX, uint groupsY, uint groupsZ, uint dispatchInitiator);

    // Called when a queue reset packet clears the processor.
    void OnQueueReset(int queueId);

    Exception Fatal(string message);
}
