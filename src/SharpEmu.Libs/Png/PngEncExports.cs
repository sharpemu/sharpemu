// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SkiaSharp;

namespace SharpEmu.Libs.Png;

public static class PngEncExports
{
    private const int InvalidAddress = unchecked((int)0x80690101);
    private const int InvalidSize = unchecked((int)0x80690102);
    private const int InvalidParameter = unchecked((int)0x80690103);
    private const int InvalidHandle = unchecked((int)0x80690104);
    private const int DataOverflow = unchecked((int)0x80690110);
    private const int Fatal = unchecked((int)0x80690120);
    private const int WorkSize = 16;
    private const ulong HandleMagic = 0x434E45474E504553;

    [SysAbiExport(Nid = "9030RnBDoh4", ExportName = "scePngEncQueryMemorySize",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePngEnc")]
    public static int QueryMemorySize(CpuContext ctx) =>
        ctx.SetReturn(ReadCreateParameter(ctx, out _));

    [SysAbiExport(Nid = "7aGTPfrqT9s", ExportName = "scePngEncCreate",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePngEnc")]
    public static int Create(CpuContext ctx)
    {
        var result = ReadCreateParameter(ctx, out var maxWidth);
        if (result < 0)
            return ctx.SetReturn(result);
        var memory = ctx[CpuRegister.Rsi];
        var output = ctx[CpuRegister.Rcx];
        if (memory == 0 || output == 0)
            return ctx.SetReturn(InvalidAddress);
        if ((uint)ctx[CpuRegister.Rdx] < WorkSize)
            return ctx.SetReturn(InvalidSize);
        Span<byte> handle = stackalloc byte[WorkSize];
        handle.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(handle, HandleMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(handle[8..], maxWidth);
        if (!ctx.Memory.TryWrite(memory, handle) || !ctx.TryWriteUInt64(output, memory))
            return ctx.SetReturn(InvalidAddress);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "RUrWdwTWZy8", ExportName = "scePngEncDelete",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePngEnc")]
    public static int Delete(CpuContext ctx)
    {
        if (!ReadHandle(ctx, out _))
            return ctx.SetReturn(InvalidHandle);
        return ctx.SetReturn(ctx.TryWriteUInt64(ctx[CpuRegister.Rdi], 0) ? 0 : InvalidAddress);
    }

    [SysAbiExport(Nid = "xgDjJKpcyHo", ExportName = "scePngEncEncode",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libScePngEnc")]
    public static int Encode(CpuContext ctx)
    {
        if (!ReadHandle(ctx, out var maxWidth))
            return ctx.SetReturn(InvalidHandle);
        Span<byte> parameter = stackalloc byte[48];
        if (ctx[CpuRegister.Rsi] == 0 || !ctx.Memory.TryRead(ctx[CpuRegister.Rsi], parameter))
            return ctx.SetReturn(InvalidAddress);
        var source = BinaryPrimitives.ReadUInt64LittleEndian(parameter);
        var destination = BinaryPrimitives.ReadUInt64LittleEndian(parameter[8..]);
        var sourceSize = BinaryPrimitives.ReadUInt32LittleEndian(parameter[16..]);
        var destinationSize = BinaryPrimitives.ReadUInt32LittleEndian(parameter[20..]);
        var width = BinaryPrimitives.ReadUInt32LittleEndian(parameter[24..]);
        var height = BinaryPrimitives.ReadUInt32LittleEndian(parameter[28..]);
        var pitch = BinaryPrimitives.ReadUInt32LittleEndian(parameter[32..]);
        var pixelFormat = BinaryPrimitives.ReadUInt16LittleEndian(parameter[36..]);
        var colorSpace = BinaryPrimitives.ReadUInt16LittleEndian(parameter[38..]);
        var bitDepth = BinaryPrimitives.ReadUInt16LittleEndian(parameter[40..]);
        var clutNumber = BinaryPrimitives.ReadUInt16LittleEndian(parameter[42..]);
        var filter = BinaryPrimitives.ReadUInt16LittleEndian(parameter[44..]);
        var compression = BinaryPrimitives.ReadUInt16LittleEndian(parameter[46..]);
        if (source == 0 || destination == 0)
            return ctx.SetReturn(InvalidAddress);
        if (pixelFormat > 1 || colorSpace is not (3 or 19) || bitDepth != 8 || clutNumber != 0 || filter > 15)
            return ctx.SetReturn(InvalidParameter);
        var rowBytes = (ulong)width * 4;
        var imageBytes = rowBytes * height;
        if (width == 0 || width > maxWidth || height == 0 || imageBytes > int.MaxValue ||
            pitch < rowBytes || sourceSize < (ulong)(height - 1) * pitch + rowBytes || destinationSize == 0)
            return ctx.SetReturn(InvalidSize);
        if (source > ulong.MaxValue - sourceSize || destination > ulong.MaxValue - destinationSize)
            return ctx.SetReturn(InvalidAddress);
        var output = ctx[CpuRegister.Rdx];
        Span<byte> outputInfo = stackalloc byte[8];
        outputInfo.Clear();
        if (output != 0 && !ctx.Memory.TryWrite(output, outputInfo))
            return ctx.SetReturn(InvalidAddress);

        try
        {
            using var bitmap = new SKBitmap((int)width, (int)height,
                pixelFormat == 0 ? SKColorType.Rgba8888 : SKColorType.Bgra8888,
                colorSpace == 19 ? SKAlphaType.Unpremul : SKAlphaType.Opaque);
            if (bitmap.GetPixels() == IntPtr.Zero)
                return ctx.SetReturn(Fatal);
            var pixels = bitmap.GetPixelSpan();
            for (var y = 0; y < (int)height; y++)
            {
                if (!ctx.Memory.TryRead(source + (ulong)y * pitch, pixels.Slice(y * bitmap.RowBytes, (int)rowBytes)))
                    return ctx.SetReturn(InvalidAddress);
            }
            var filters = filter switch
            {
                0 => SKPngEncoderFilterFlags.NoFilters,
                15 => SKPngEncoderFilterFlags.AllFilters,
                _ => (SKPngEncoderFilterFlags)(filter << 4),
            };
            using var pixmap = bitmap.PeekPixels();
            using var png = pixmap.Encode(new SKPngEncoderOptions(filters, Math.Min((int)compression, 9)));
            if (png is null)
                return ctx.SetReturn(Fatal);
            if (png.Size > destinationSize || png.Size > int.MaxValue)
                return ctx.SetReturn(DataOverflow);
            if (!ctx.Memory.TryWrite(destination, png.AsSpan()))
                return ctx.SetReturn(InvalidAddress);
            BinaryPrimitives.WriteUInt32LittleEndian(outputInfo, (uint)png.Size);
            BinaryPrimitives.WriteUInt32LittleEndian(outputInfo[4..], height);
            if (output != 0 && !ctx.Memory.TryWrite(output, outputInfo))
                return ctx.SetReturn(InvalidAddress);
            return ctx.SetReturn((int)png.Size);
        }
        catch (OutOfMemoryException)
        {
            return ctx.SetReturn(Fatal);
        }
    }

    private static int ReadCreateParameter(CpuContext ctx, out uint maxWidth)
    {
        maxWidth = 0;
        Span<byte> parameter = stackalloc byte[16];
        if (ctx[CpuRegister.Rdi] == 0 || !ctx.Memory.TryRead(ctx[CpuRegister.Rdi], parameter))
            return InvalidAddress;
        if (BinaryPrimitives.ReadUInt32LittleEndian(parameter) != 16 ||
            BinaryPrimitives.ReadUInt32LittleEndian(parameter[4..]) != 0 ||
            BinaryPrimitives.ReadUInt32LittleEndian(parameter[12..]) > 5)
            return InvalidParameter;
        maxWidth = BinaryPrimitives.ReadUInt32LittleEndian(parameter[8..]);
        return maxWidth is > 0 and <= 1_000_000 ? WorkSize : InvalidSize;
    }

    private static bool ReadHandle(CpuContext ctx, out uint maxWidth)
    {
        maxWidth = 0;
        var address = ctx[CpuRegister.Rdi];
        return address != 0 && ctx.TryReadUInt64(address, out var magic) && magic == HandleMagic &&
            ctx.TryReadUInt32(address + 8, out maxWidth) && maxWidth is > 0 and <= 1_000_000;
    }
}
