// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Codec;

namespace SharpEmu.GnmMode.Libraries.Codec;

public static class GnmVideodec2Exports
{
    private const int ArgumentPointer = unchecked((int)0x811D0102);
    private const int FrameBufferSize = unchecked((int)0x811D0106);
    private readonly record struct Extent(uint Width, uint Height);
    private static readonly ConditionalWeakTable<ICpuMemory, ConcurrentDictionary<ulong, Extent>> Extents = new();

    internal static void Reset(ICpuMemory memory) => Extents.Remove(Root(memory));

    public static int Decode(CpuContext context)
    {
        var frameBuffer = context[CpuRegister.Rdx];
        var outputInfo = context[CpuRegister.Rcx];
        var result = Videodec2Exports.Videodec2Decode(context);
        return result != 0 ? result : CompleteOutput(context, frameBuffer, outputInfo);
    }

    public static int Flush(CpuContext context)
    {
        var frameBuffer = context[CpuRegister.Rsi];
        var outputInfo = context[CpuRegister.Rdx];
        var result = Videodec2Exports.Videodec2Flush(context);
        return result != 0 ? result : CompleteOutput(context, frameBuffer, outputInfo);
    }

    private static int CompleteOutput(CpuContext context, ulong frameBuffer, ulong outputInfo)
    {
        var result = NormalizeOutput(context, frameBuffer, outputInfo);
        if (result != 0)
        {
            context.Memory.TryWrite(outputInfo + 8, [0, 0, 0, 0]);
            context.Memory.TryWrite(frameBuffer + 0x18, [0]);
        }
        return context.SetReturn(result);
    }

    private static int NormalizeOutput(CpuContext context, ulong frameBuffer, ulong outputInfo)
    {
        Span<byte> ready = stackalloc byte[1];
        if (!context.Memory.TryRead(outputInfo + 8, ready))
            return ArgumentPointer;
        if (ready[0] == 0)
            return 0;
        if (!context.TryReadUInt64(outputInfo, out var infoSize) ||
            !context.TryReadUInt32(outputInfo + 0x10, out var width) ||
            !context.TryReadUInt32(outputInfo + 0x14, out var sourcePitch) ||
            !context.TryReadUInt32(outputInfo + 0x18, out var height) ||
            !context.TryReadUInt64(outputInfo + 0x20, out var pixels) ||
            !context.TryReadUInt64(frameBuffer + 0x10, out var capacity))
            return ArgumentPointer;
        if (infoSize is not (0x30 or 0x38) || width is 0 or > 16384 || height is 0 or > 16384 ||
            sourcePitch < ((width + 1) & ~1u) || sourcePitch > 16384 || pixels == 0)
            return ArgumentPointer;
        var pitch = (width + 63) & ~63u;
        var codedWidth = (width + 15) & ~15u;
        var codedHeight = (height + 15) & ~15u;
        var sourceBytes = (ulong)sourcePitch * (height + (height + 1) / 2);
        var destinationBytes = (ulong)pitch * codedHeight * 3 / 2;
        if (sourceBytes > capacity || destinationBytes > capacity || Math.Max(sourceBytes, destinationBytes) > 64 * 1024 * 1024)
            return FrameBufferSize;
        if (sourcePitch != pitch || height != codedHeight)
        {
            var source = ArrayPool<byte>.Shared.Rent((int)sourceBytes);
            var destination = ArrayPool<byte>.Shared.Rent((int)destinationBytes);
            try
            {
                if (!context.Memory.TryRead(pixels, source.AsSpan(0, (int)sourceBytes)))
                    return ArgumentPointer;
                CopyPlane(source, destination, width, height, codedHeight, sourcePitch, pitch, 1);
                CopyPlane(source.AsSpan((int)(sourcePitch * height)), destination.AsSpan((int)(pitch * codedHeight)),
                    (width + 1) & ~1u, (height + 1) / 2, codedHeight / 2, sourcePitch, pitch, 2);
                if (!context.Memory.TryWrite(pixels, destination.AsSpan(0, (int)destinationBytes)))
                    return ArgumentPointer;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(source);
                ArrayPool<byte>.Shared.Return(destination);
            }
        }
        if (!context.TryWriteUInt32(outputInfo + 0x10, codedWidth) ||
            !context.TryWriteUInt32(outputInfo + 0x14, pitch) ||
            !context.TryWriteUInt32(outputInfo + 0x18, codedHeight) ||
            !context.TryWriteUInt64(outputInfo + 0x28, destinationBytes) ||
            infoSize == 0x38 && !context.TryWriteUInt32(outputInfo + 0x34, pitch))
            return ArgumentPointer;
        Extents.GetValue(Root(context.Memory), static _ => new())[pixels] = new Extent(width, height);
        return 0;
    }

    private static void CopyPlane(ReadOnlySpan<byte> source, Span<byte> destination, uint width, uint sourceRows,
        uint targetRows, uint sourcePitch, uint targetPitch, int elementBytes)
    {
        for (var row = 0u; row < targetRows; row++)
        {
            var input = source.Slice((int)(Math.Min(row, sourceRows - 1) * sourcePitch), (int)width);
            var output = destination.Slice((int)(row * targetPitch), (int)targetPitch);
            input.CopyTo(output);
            for (var column = (int)width; column < output.Length; column += elementBytes)
                input[^elementBytes..].CopyTo(output[column..]);
        }
    }

    public static int GetPictureInfo(CpuContext context)
    {
        var outputInfo = context[CpuRegister.Rdi];
        var picture = context[CpuRegister.Rsi];
        var result = Videodec2Exports.Videodec2GetPictureInfo(context);
        if (result != 0)
            return result;
        if (!context.TryReadUInt64(outputInfo + 0x20, out var pixels) ||
            !Extents.TryGetValue(Root(context.Memory), out var frames) || !frames.TryGetValue(pixels, out var extent))
            return result;
        var right = ((extent.Width + 15) & ~15u) - extent.Width;
        var bottom = ((extent.Height + 15) & ~15u) - extent.Height;
        Span<byte> flag = stackalloc byte[1];
        flag[0] = right != 0 || bottom != 0 ? (byte)1 : (byte)0;
        return context.SetReturn(context.Memory.TryWrite(picture + 0x35, flag) &&
            context.TryWriteUInt32(picture + 0x38, 0) && context.TryWriteUInt32(picture + 0x3C, right) &&
            context.TryWriteUInt32(picture + 0x40, 0) && context.TryWriteUInt32(picture + 0x44, bottom) ? 0 : ArgumentPointer);
    }

    private static ICpuMemory Root(ICpuMemory memory)
    {
        for (var depth = 0; depth < 4 && memory is ICpuMemoryWrapper wrapper && !ReferenceEquals(memory, wrapper.Inner); depth++)
            memory = wrapper.Inner;
        return memory;
    }
}
