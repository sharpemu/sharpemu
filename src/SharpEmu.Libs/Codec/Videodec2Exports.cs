// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
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

    // The public ABI requires caller-owned arenas.  Keep the conservative
    // 16 MiB requirements used by other PS4/PS5 compatibility implementations.
    private const ulong MinimumMemoryBytes = 16UL * 1024 * 1024;
    private const uint FrameBufferAlignment = 0x100;

    // Null entry = TryCreate() failed; every export falls back to the stub for that handle.
    private static readonly ConcurrentDictionary<ulong, Videodec2Decoder?> Decoders = new();
    private static readonly ConcurrentDictionary<ulong, Videodec2DecodedFrame> PictureInfos = new();
    private static long _nextDecoderHandle = unchecked((long)DecoderToken);

    private static readonly bool TraceEnabled = IsTraceEnabled();
    private static long _decodeCallCount;
    private static long _decodedPictureCount;

    [SysAbiExport(
        Nid = "RnDibcGCPKw",
        ExportName = "sceVideodec2QueryComputeMemoryInfo",
        Target = Generation.Gen5,
        LibraryName = "libSceVideodec2")]
    public static int Videodec2QueryComputeMemoryInfo(CpuContext ctx)
    {
        var paramAddress = ctx[CpuRegister.Rdi];
        if (paramAddress == 0 ||
            !ctx.TryWriteUInt64(paramAddress + 0x08, MinimumMemoryBytes) ||
            !ctx.TryWriteUInt64(paramAddress + 0x10, 0))
        {
            return SetReturn(ctx, VideodecErrorInvalidArg);
        }

        return SetReturn(ctx, Ok);
    }

    private const int VideodecErrorInvalidArg = unchecked((int)0x80620801);
    private const int VideodecErrorStructSize = unchecked((int)0x811D0101);
    private const int VideodecErrorOutputInfo = unchecked((int)0x811D010F);
    private const int VideodecErrorComputeQueueId = unchecked((int)0x811D0202);

    // Reject garbage/not-yet-primed struct reads before they reach `new byte[...]`.
    private const ulong MaxPlausibleAuBytes = 32UL * 1024 * 1024;
    private const ulong MaxPlausibleSlotBytes = 64UL * 1024 * 1024;

    [SysAbiExport(
        Nid = "eD+X2SmxUt4",
        ExportName = "sceVideodec2AllocateComputeQueue",
        Target = Generation.Gen5,
        LibraryName = "libSceVideodec2")]
    public static int Videodec2AllocateComputeQueue(CpuContext ctx)
    {
        var configAddress = ctx[CpuRegister.Rdi];
        var memoryInfoAddress = ctx[CpuRegister.Rsi];
        var queueAddress = ctx[CpuRegister.Rdx];
        if (configAddress == 0 || memoryInfoAddress == 0 || queueAddress == 0 ||
            !ctx.TryReadUInt64(memoryInfoAddress + 0x10, out var computeMemoryAddress) ||
            computeMemoryAddress == 0 ||
            !ctx.TryWriteUInt64(
                queueAddress, computeMemoryAddress))
        {
            return SetReturn(ctx, VideodecErrorInvalidArg);
        }

        return SetReturn(ctx, Ok);
    }

    [SysAbiExport(
        Nid = "UvtA3FAiF4Y",
        ExportName = "sceVideodec2ReleaseComputeQueue",
        Target = Generation.Gen5,
        LibraryName = "libSceVideodec2")]
    public static int Videodec2ReleaseComputeQueue(CpuContext ctx)
    {
        return SetReturn(
            ctx,
            ctx[CpuRegister.Rdi] != 0 ? Ok : VideodecErrorComputeQueueId);
    }

    [SysAbiExport(
        Nid = "qqMCwlULR+E",
        ExportName = "sceVideodec2QueryDecoderMemoryInfo",
        Target = Generation.Gen5,
        LibraryName = "libSceVideodec2")]
    public static int Videodec2QueryDecoderMemoryInfo(CpuContext ctx)
    {
        var configAddress = ctx[CpuRegister.Rdi];
        var memoryInfoAddress = ctx[CpuRegister.Rsi];
        if (configAddress == 0 || memoryInfoAddress == 0 ||
            !ctx.TryWriteUInt64(memoryInfoAddress + 0x08, MinimumMemoryBytes) ||
            !ctx.TryWriteUInt64(memoryInfoAddress + 0x10, 0) ||
            !ctx.TryWriteUInt64(memoryInfoAddress + 0x18, MinimumMemoryBytes) ||
            !ctx.TryWriteUInt64(memoryInfoAddress + 0x20, 0) ||
            !ctx.TryWriteUInt64(memoryInfoAddress + 0x28, MinimumMemoryBytes) ||
            !ctx.TryWriteUInt64(memoryInfoAddress + 0x30, 0) ||
            !ctx.TryWriteUInt64(memoryInfoAddress + 0x38, MinimumMemoryBytes) ||
            !ctx.TryWriteUInt32(memoryInfoAddress + 0x40, FrameBufferAlignment) ||
            !ctx.TryWriteUInt32(memoryInfoAddress + 0x44, 0))
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
        var decoder = Videodec2Decoder.TryCreate();
        Decoders[handle] = decoder;
        Trace($"create handle=0x{handle:X16} real_decoder={decoder is not null}");

        if (!ctx.TryWriteUInt64(decoderAddress, handle))
        {
            Decoders.TryRemove(handle, out var created);
            created?.Dispose();
            return SetReturn(ctx, VideodecErrorInvalidArg);
        }

        return SetReturn(ctx, Ok);
    }

    [SysAbiExport(
        Nid = "l1hXwscLuCY",
        ExportName = "sceVideodec2Flush",
        Target = Generation.Gen5,
        LibraryName = "libSceVideodec2")]
    public static int Videodec2Flush(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var outputSlotObj = ctx[CpuRegister.Rsi];
        var outputInfoAddress = ctx[CpuRegister.Rdx];
        if (!TryInitializeNoPicture(ctx, outputSlotObj, outputInfoAddress))
        {
            return SetReturn(ctx, VideodecErrorInvalidArg);
        }

        if (Decoders.TryGetValue(handle, out var decoder) &&
            decoder is not null &&
            decoder.FlushOutput(out var decodedFrame) &&
            decodedFrame is not null)
        {
            _ = TryWriteDecodedFrame(
                ctx, outputSlotObj, outputInfoAddress, decodedFrame);
        }

        return SetReturn(ctx, Ok);
    }

    [SysAbiExport(
        Nid = "wJXikG6QFN8",
        ExportName = "sceVideodec2Reset",
        Target = Generation.Gen5,
        LibraryName = "libSceVideodec2")]
    public static int Videodec2Reset(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        if (Decoders.TryGetValue(handle, out var decoder) && decoder is not null)
        {
            decoder.Reset();
        }

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
        var callOrdinal = Interlocked.Increment(ref _decodeCallCount);

        if (!TryInitializeNoPicture(ctx, outputSlotObj, outputInfoAddress))
        {
            return SetReturn(ctx, VideodecErrorInvalidArg);
        }

        if (!Decoders.TryGetValue(handle, out var decoder) || decoder is null)
        {
            // No real decoder for this handle: stub behavior, "fed the AU, no picture".
            TraceDecode(callOrdinal, $"stub handle=0x{handle:X16}");
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
            TraceDecode(callOrdinal, "invalid access-unit or output-slot descriptor");
            return SetReturn(ctx, Ok);
        }

        _ = ctx.TryReadUInt64(inputAuStruct + 0x18, out var pts);
        _ = ctx.TryReadUInt64(inputAuStruct + 0x20, out var dts);
        _ = ctx.TryReadUInt64(inputAuStruct + 0x28, out var attachedData);

        var auBuffer = new byte[auDataSize];
        if (!ctx.Memory.TryRead(auDataPtr, auBuffer))
        {
            TraceDecode(callOrdinal, $"AU read failed ptr=0x{auDataPtr:X16} bytes=0x{auDataSize:X}");
            return SetReturn(ctx, Ok);
        }

        var decodeOk = decoder.DecodeAccessUnit(
            auBuffer, pts, dts, attachedData, out var decodedFrame);
        if (ShouldTrace(callOrdinal))
        {
            var prefixBytes = Math.Min(auBuffer.Length, 8);
            Trace(
                $"decode call={callOrdinal} au=0x{auDataPtr:X16}+0x{auDataSize:X} " +
                $"head={Convert.ToHexString(auBuffer.AsSpan(0, prefixBytes))} " +
                $"slot=0x{slotPtr:X16}+0x{slotSize:X} ok={decodeOk} " +
                $"picture={decodedFrame is not null}");
        }

        if (!decodeOk || decodedFrame is null)
        {
            return SetReturn(ctx, Ok);
        }

        var copied = TryWriteDecodedFrame(
            ctx, outputSlotObj, outputInfoAddress, decodedFrame);
        if (copied)
        {
            Interlocked.Increment(ref _decodedPictureCount);
        }
        TraceDecode(callOrdinal, $"guest_copy={copied} size={decodedFrame.Width}x{decodedFrame.Height} pitch={decodedFrame.Pitch}");

        return SetReturn(ctx, Ok);
    }

    [SysAbiExport(
        Nid = "NtXRa3dRzU0",
        ExportName = "sceVideodec2GetPictureInfo",
        Target = Generation.Gen5,
        LibraryName = "libSceVideodec2")]
    public static int Videodec2GetPictureInfo(CpuContext ctx)
    {
        var outputInfoAddress = ctx[CpuRegister.Rdi];
        var firstPictureInfoAddress = ctx[CpuRegister.Rsi];
        var secondPictureInfoAddress = ctx[CpuRegister.Rdx];
        if (outputInfoAddress == 0 || firstPictureInfoAddress == 0)
        {
            return SetReturn(ctx, VideodecErrorInvalidArg);
        }

        if (!ctx.TryReadUInt64(outputInfoAddress, out var outputInfoSize) ||
            (outputInfoSize != 0x30 && outputInfoSize != 0x38))
        {
            return SetReturn(ctx, VideodecErrorStructSize);
        }

        if (!TryReadByte(ctx, outputInfoAddress + 0x08, out var isValid) ||
            !TryReadByte(ctx, outputInfoAddress + 0x0A, out var pictureCount) ||
            !ctx.TryReadUInt32(outputInfoAddress + 0x0C, out var codecType) ||
            !ctx.TryReadUInt64(outputInfoAddress + 0x20, out var pixelsAddress) ||
            isValid == 0 || pictureCount == 0 || pixelsAddress == 0 || codecType != 1 ||
            !PictureInfos.TryGetValue(pixelsAddress, out var decodedFrame))
        {
            return SetReturn(ctx, VideodecErrorOutputInfo);
        }

        if (!ctx.TryReadUInt64(firstPictureInfoAddress, out var requestedSize) ||
            (requestedSize != 104 && requestedSize != 120))
        {
            return SetReturn(ctx, VideodecErrorStructSize);
        }

        var picture = new byte[(int)requestedSize];
        BinaryPrimitives.WriteUInt64LittleEndian(picture, requestedSize);
        picture[0x08] = 1;
        BinaryPrimitives.WriteUInt64LittleEndian(picture.AsSpan(0x10), decodedFrame.Pts);
        BinaryPrimitives.WriteUInt64LittleEndian(picture.AsSpan(0x18), decodedFrame.Dts);
        BinaryPrimitives.WriteUInt64LittleEndian(picture.AsSpan(0x20), decodedFrame.AttachedData);
        picture[0x28] = decodedFrame.KeyFrame ? (byte)1 : (byte)0;
        picture[0x29] = (byte)Math.Min(decodedFrame.Profile, byte.MaxValue);
        picture[0x2A] = (byte)Math.Min(decodedFrame.Level, byte.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(
            picture.AsSpan(0x2C), (decodedFrame.Width + 15u) / 16u - 1u);
        BinaryPrimitives.WriteUInt32LittleEndian(
            picture.AsSpan(0x30), (decodedFrame.Height + 15u) / 16u - 1u);
        picture[0x34] = 1;
        picture[0x4E] = 1;
        picture[0x4F] = 5;
        if (!ctx.Memory.TryWrite(firstPictureInfoAddress, picture))
        {
            return SetReturn(ctx, VideodecErrorInvalidArg);
        }

        if (secondPictureInfoAddress != 0 &&
            !TryWriteEmptyPictureInfo(ctx, secondPictureInfoAddress))
        {
            return SetReturn(ctx, VideodecErrorStructSize);
        }

        Trace(
            $"picture_info buffer=0x{pixelsAddress:X16} " +
            $"pts=0x{decodedFrame.Pts:X16} size={requestedSize}");
        return SetReturn(ctx, Ok);
    }

    private static readonly byte[] NotAccepted = [0];
    private static readonly byte[] Accepted = [1];
    private static readonly byte[] NoPicture = [0, 0, 0, 0];
    private static readonly byte[] PictureReady = [1, 0, 1, 0];
    private static readonly byte[] ErrorPictureReady = [1, 1, 1, 0];

    private static bool TryInitializeNoPicture(
        CpuContext ctx,
        ulong frameBufferAddress,
        ulong outputInfoAddress)
    {
        return frameBufferAddress != 0 &&
               outputInfoAddress != 0 &&
               ctx.Memory.TryWrite(frameBufferAddress + 0x18, NotAccepted) &&
               ctx.Memory.TryWrite(outputInfoAddress + 0x08, NoPicture);
    }

    internal static bool TryWriteDecodedFrame(
        CpuContext ctx,
        ulong frameBufferAddress,
        ulong outputInfoAddress,
        Videodec2DecodedFrame decodedFrame)
    {
        if (!ctx.TryReadUInt64(frameBufferAddress + 0x08, out var pixelsAddress) ||
            !ctx.TryReadUInt64(frameBufferAddress + 0x10, out var slotBytes) ||
            !ctx.TryReadUInt64(outputInfoAddress, out var outputInfoBytes) ||
            pixelsAddress == 0)
        {
            return false;
        }

        if ((ulong)decodedFrame.Nv12.Length > slotBytes ||
            !ctx.Memory.TryWrite(pixelsAddress, decodedFrame.Nv12))
        {
            return false;
        }

        if (!ctx.TryWriteUInt32(outputInfoAddress + 0x0C, 1) ||
            !ctx.TryWriteUInt32(outputInfoAddress + 0x10, decodedFrame.Width) ||
            !ctx.TryWriteUInt32(outputInfoAddress + 0x14, decodedFrame.Pitch) ||
            !ctx.TryWriteUInt32(outputInfoAddress + 0x18, decodedFrame.Height) ||
            !ctx.TryWriteUInt64(outputInfoAddress + 0x20, pixelsAddress) ||
            !ctx.TryWriteUInt64(outputInfoAddress + 0x28, slotBytes))
        {
            return false;
        }

        if (outputInfoBytes >= 0x38 &&
            (!ctx.TryWriteUInt32(outputInfoAddress + 0x30, 0) ||
             !ctx.TryWriteUInt32(outputInfoAddress + 0x34, decodedFrame.Pitch)))
        {
            return false;
        }

        // Publish the ready flags last, after every dependent field is valid.
        if (!ctx.Memory.TryWrite(frameBufferAddress + 0x18, Accepted) ||
            !ctx.Memory.TryWrite(
                outputInfoAddress + 0x08,
                decodedFrame.ErrorFrame ? ErrorPictureReady : PictureReady))
        {
            return false;
        }

        PictureInfos[pixelsAddress] = decodedFrame;
        return true;
    }

    private static bool TryWriteEmptyPictureInfo(CpuContext ctx, ulong address)
    {
        if (!ctx.TryReadUInt64(address, out var size) || size < 40 || size > 256)
        {
            return false;
        }

        var output = new byte[(int)size];
        BinaryPrimitives.WriteUInt64LittleEndian(output, size);
        return ctx.Memory.TryWrite(address, output);
    }

    private static bool TryReadByte(CpuContext ctx, ulong address, out byte value)
    {
        Span<byte> buffer = stackalloc byte[1];
        if (!ctx.Memory.TryRead(address, buffer))
        {
            value = 0;
            return false;
        }

        value = buffer[0];
        return true;
    }

    private static bool IsTraceEnabled()
    {
        var value = Environment.GetEnvironmentVariable("SHARPEMU_LOG_VIDEODEC2");
        return string.Equals(value, "1", StringComparison.Ordinal) ||
               string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldTrace(long ordinal) =>
        TraceEnabled && (ordinal <= 16 || ordinal % 120 == 0);

    private static void TraceDecode(long ordinal, string message)
    {
        if (ShouldTrace(ordinal))
        {
            Trace($"decode call={ordinal} {message}");
        }
    }

    private static void Trace(string message)
    {
        if (TraceEnabled)
        {
            Console.Error.WriteLine($"[VIDEODEC2][TRACE] {message}");
        }
    }

    private static int SetReturn(CpuContext ctx, int result)
    {
        ctx[CpuRegister.Rax] = unchecked((ulong)result);
        return result;
    }
}
