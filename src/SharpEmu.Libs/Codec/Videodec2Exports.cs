// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Codec;

/// <summary>
/// libSceVideodec2 (hardware compute-based decoder). sceVideodec2Decode
/// feeds a real FFmpeg H.264 session (Videodec2Decoder) when one can be
/// opened, falling back to the original "no picture" stub otherwise.
/// </summary>
public static class Videodec2Exports
{
    private const int Ok = 0;

    // Null entry = TryCreate() failed; every export falls back to the stub for that handle.
    private static readonly ConcurrentDictionary<ulong, Videodec2Decoder?> Decoders = new();
    private static long _nextDecoderHandle = unchecked((long)DecoderToken);

    [SysAbiExport(
        Nid = "RnDibcGCPKw",
        ExportName = "sceVideodec2QueryComputeMemoryInfo",
        Target = Generation.Gen5,
        LibraryName = "libSceVideodec2")]
    public static int Videodec2QueryComputeMemoryInfo(CpuContext ctx)
    {
        var paramAddress = ctx[CpuRegister.Rdi];
        if (paramAddress == 0)
        {
            return SetReturn(ctx, VideodecErrorInvalidArg);
        }

        // Success needs no memory writes; the game initializes from its own fields.
        return SetReturn(ctx, Ok);
    }

    private const int VideodecErrorInvalidArg = unchecked((int)0x80620801);

    // Reject garbage/not-yet-primed struct reads before they reach `new byte[...]`.
    private const ulong MaxPlausibleAuBytes = 32UL * 1024 * 1024;
    private const ulong MaxPlausibleSlotBytes = 64UL * 1024 * 1024;

    // Opaque token the game hands back unmodified to later Videodec2 calls.
    private const ulong ComputeQueueToken = 0x56D2_C0DE_0001UL;

    [SysAbiExport(
        Nid = "eD+X2SmxUt4",
        ExportName = "sceVideodec2AllocateComputeQueue",
        Target = Generation.Gen5,
        LibraryName = "libSceVideodec2")]
    public static int Videodec2AllocateComputeQueue(CpuContext ctx)
    {
        var queueAddress = ctx[CpuRegister.Rdi];
        if (queueAddress == 0 || !ctx.TryWriteUInt64(queueAddress, ComputeQueueToken))
        {
            return SetReturn(ctx, VideodecErrorInvalidArg);
        }

        return SetReturn(ctx, Ok);
    }

    // A zero size at +0x08/+0x28 makes the game skip its own arena allocation cleanly.
    [SysAbiExport(
        Nid = "qqMCwlULR+E",
        ExportName = "sceVideodec2QueryDecoderMemoryInfo",
        Target = Generation.Gen5,
        LibraryName = "libSceVideodec2")]
    public static int Videodec2QueryDecoderMemoryInfo(CpuContext ctx)
    {
        var memoryInfoAddress = ctx[CpuRegister.Rsi];
        if (memoryInfoAddress == 0 ||
            !ctx.TryWriteUInt64(memoryInfoAddress + 0x08, 0) ||
            !ctx.TryWriteUInt64(memoryInfoAddress + 0x28, 0) ||
            // Frame-slot size: must be nonzero or the game divides its arena by zero.
            !ctx.TryWriteUInt64(memoryInfoAddress + 0x38, 0x1000))
        {
            return SetReturn(ctx, VideodecErrorInvalidArg);
        }

        return SetReturn(ctx, Ok);
    }

    private const ulong DecoderToken = 0x56D2_C0DE_0002UL;

    // Handle is opaque to the game; a monotonic counter seeded at the old fixed token.
    [SysAbiExport(
        Nid = "CNNRoRYd8XI",
        ExportName = "sceVideodec2CreateDecoder",
        Target = Generation.Gen5,
        LibraryName = "libSceVideodec2")]
    public static int Videodec2CreateDecoder(CpuContext ctx)
    {
        var decoderAddress = ctx[CpuRegister.Rdx];
        if (decoderAddress == 0)
        {
            return SetReturn(ctx, VideodecErrorInvalidArg);
        }

        var handle = unchecked((ulong)Interlocked.Increment(ref _nextDecoderHandle));
        Decoders[handle] = Videodec2Decoder.TryCreate();

        if (!ctx.TryWriteUInt64(decoderAddress, handle))
        {
            Decoders.TryRemove(handle, out var created);
            created?.Dispose();
            return SetReturn(ctx, VideodecErrorInvalidArg);
        }

        return SetReturn(ctx, Ok);
    }

    // Clearing the picture-ready byte at [rdx] tells the player "no buffered pictures remain".
    [SysAbiExport(
        Nid = "l1hXwscLuCY",
        ExportName = "sceVideodec2Flush",
        Target = Generation.Gen5,
        LibraryName = "libSceVideodec2")]
    public static int Videodec2Flush(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var outputInfoAddress = ctx[CpuRegister.Rdx];
        if (outputInfoAddress == 0 || !ctx.Memory.TryWrite(outputInfoAddress, NoPicture))
        {
            return SetReturn(ctx, VideodecErrorInvalidArg);
        }

        if (Decoders.TryGetValue(handle, out var decoder) && decoder is not null)
        {
            // Drain in order: report an already-finished frame before queuing a new drain request.
            if (decoder.TryConsumeProtocolReadySignal(out var width, out var height))
            {
                if (ctx.TryWriteUInt64(outputInfoAddress + 0x08, width) &&
                    ctx.TryWriteUInt64(outputInfoAddress + 0x10, height))
                {
                    _ = ctx.Memory.TryWrite(outputInfoAddress, PictureReady);
                }
            }
            else
            {
                decoder.RequestDrain();
            }
        }

        return SetReturn(ctx, Ok);
    }

    // No state to reset.
    [SysAbiExport(
        Nid = "wJXikG6QFN8",
        ExportName = "sceVideodec2Reset",
        Target = Generation.Gen5,
        LibraryName = "libSceVideodec2")]
    public static int Videodec2Reset(CpuContext ctx)
    {
        return SetReturn(ctx, Ok);
    }

    [SysAbiExport(
        Nid = "jwImxXRGSKA",
        ExportName = "sceVideodec2DeleteDecoder",
        Target = Generation.Gen5,
        LibraryName = "libSceVideodec2")]
    public static int Videodec2DeleteDecoder(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        if (Decoders.TryRemove(handle, out var decoder))
        {
            decoder?.Dispose();
        }

        return SetReturn(ctx, Ok);
    }

    // rcx[0] is the picture-ready flag (1 = frame published); it lives in
    // uninitialized stack and must always be written explicitly.
    [SysAbiExport(
        Nid = "852F5+q6+iM",
        ExportName = "sceVideodec2Decode",
        Target = Generation.Gen5,
        LibraryName = "libSceVideodec2")]
    public static int Videodec2Decode(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var inputAuStruct = ctx[CpuRegister.Rsi];
        var outputSlotObj = ctx[CpuRegister.Rdx];
        var outputInfoAddress = ctx[CpuRegister.Rcx];

        if (outputInfoAddress == 0 || !ctx.Memory.TryWrite(outputInfoAddress, NoPicture))
        {
            return SetReturn(ctx, VideodecErrorInvalidArg);
        }

        if (!Decoders.TryGetValue(handle, out var decoder) || decoder is null)
        {
            // No real decoder for this handle: stub behavior, "fed the AU, no picture".
            return SetReturn(ctx, Ok);
        }

        if (inputAuStruct == 0 ||
            !ctx.TryReadUInt64(inputAuStruct + 0x08, out var auDataPtr) ||
            !ctx.TryReadUInt64(inputAuStruct + 0x10, out var auDataSize) ||
            auDataPtr == 0 || auDataSize == 0 || auDataSize > MaxPlausibleAuBytes ||
            outputSlotObj == 0 ||
            !ctx.TryReadUInt64(outputSlotObj + 0x08, out var slotPtr) ||
            !ctx.TryReadUInt64(outputSlotObj + 0x10, out var slotSize) ||
            slotPtr == 0 || slotSize == 0 || slotSize > MaxPlausibleSlotBytes)
        {
            // Nothing sane to feed/fill this call; not an error.
            return SetReturn(ctx, Ok);
        }

        var auBuffer = new byte[auDataSize];
        if (!ctx.Memory.TryRead(auDataPtr, auBuffer))
        {
            return SetReturn(ctx, Ok);
        }

        // Queues the AU and returns immediately; decode/present happen on Videodec2Decoder's own threads.
        decoder.EnqueueAccessUnit(auBuffer);

        if (!decoder.TryConsumeProtocolReadySignal(out var width, out var height))
        {
            return SetReturn(ctx, Ok);
        }

        if (!ctx.TryWriteUInt64(outputInfoAddress + 0x08, width) ||
            !ctx.TryWriteUInt64(outputInfoAddress + 0x10, height) ||
            !ctx.Memory.TryWrite(outputInfoAddress, PictureReady))
        {
            return SetReturn(ctx, Ok);
        }

        return SetReturn(ctx, Ok);
    }

    private static readonly byte[] NoPicture = [0];
    private static readonly byte[] PictureReady = [1];

    private static int SetReturn(CpuContext ctx, int result)
    {
        ctx[CpuRegister.Rax] = unchecked((ulong)result);
        return result;
    }
}
