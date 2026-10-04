// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.IO.Compression;
using SharpEmu.HLE;

namespace SharpEmu.Libs.PngEnc;

/// <summary>
/// Initialization surface for the system PNG encoder. The encoder owns no host
/// resource at create time: its caller-provided guest block is the live handle.
/// </summary>
public static class PngEncExports
{
    private const int PngEncErrorInvalidAddress = unchecked((int)0x80690101);
    private const int PngEncErrorInvalidSize = unchecked((int)0x80690102);
    private const int PngEncErrorInvalidParameter = unchecked((int)0x80690103);
    private const int PngEncErrorInvalidHandle = unchecked((int)0x80690104);
    private const int PngEncErrorDataOverflow = unchecked((int)0x80690110);
    private const int PngEncErrorFatal = unchecked((int)0x80690120);
    private const uint CreateParameterSize = 0x10;
    private const uint EncodeParameterSize = 0x30;
    private const uint ContextSize = 0x10;
    private const uint MaximumImageWidth = 1_000_000;
    private const uint MaximumImageHeight = 1_000_000;
    private const uint MaximumFilterCount = 4;
    private const ushort ColorSpaceRgb = 3;
    private const ushort ColorSpaceRgba = 19;
    private const ushort PixelFormatRgba = 0;
    private const ushort PixelFormatBgra = 1;
    private const ushort FilterMask = 1 | 2 | 4 | 8;
    private const ulong ContextMagic = 0x5348_4152_504E_4745; // "SHARPNGE"
    private static readonly uint[] Crc32Table = BuildCrc32Table();

    private readonly record struct CreateParameters(
        uint Size,
        uint Attribute,
        uint MaximumImageWidth,
        uint MaximumFilterCount);

    private readonly record struct EncodeParameters(
        ulong ImageAddress,
        ulong PngAddress,
        uint ImageSize,
        uint PngSize,
        uint Width,
        uint Height,
        uint Pitch,
        ushort PixelFormat,
        ushort ColorSpace,
        ushort BitDepth,
        ushort ClutCount,
        ushort FilterType,
        ushort CompressionLevel);

    [SysAbiExport(
        Nid = "9030RnBDoh4",
        ExportName = "scePngEncQueryMemorySize",
        Target = Generation.Gen5,
        LibraryName = "libScePngEnc")]
    public static int PngEncQueryMemorySize(CpuContext ctx)
    {
        var validationResult = ReadAndValidateCreateParameters(
            ctx,
            ctx[CpuRegister.Rdi],
            out _);
        if (validationResult != 0)
        {
            return ctx.SetReturn(validationResult);
        }

        return ctx.SetReturn((int)ContextSize);
    }

    [SysAbiExport(
        Nid = "7aGTPfrqT9s",
        ExportName = "scePngEncCreate",
        Target = Generation.Gen5,
        LibraryName = "libScePngEnc")]
    public static int PngEncCreate(CpuContext ctx)
    {
        var parameterAddress = ctx[CpuRegister.Rdi];
        var memoryAddress = ctx[CpuRegister.Rsi];
        var memorySize = unchecked((uint)ctx[CpuRegister.Rdx]);
        var handleAddress = ctx[CpuRegister.Rcx];

        var validationResult = ReadAndValidateCreateParameters(
            ctx,
            parameterAddress,
            out var parameters);
        if (validationResult != 0)
        {
            return ctx.SetReturn(validationResult);
        }

        if (memoryAddress == 0 || handleAddress == 0)
        {
            return ctx.SetReturn(PngEncErrorInvalidAddress);
        }

        if (memorySize < ContextSize)
        {
            return ctx.SetReturn(PngEncErrorInvalidSize);
        }

        Span<byte> context = stackalloc byte[(int)ContextSize];
        context.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(context, ContextMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(context[8..], parameters.MaximumImageWidth);
        BinaryPrimitives.WriteUInt32LittleEndian(context[12..], parameters.MaximumFilterCount);

        Span<byte> handle = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(handle, memoryAddress);

        // These are output-only ranges in the guest ABI. Requiring read access
        // would reject valid write-only mappings before the encoder can publish
        // its caller-owned context.
        if (!ctx.Memory.TryWrite(memoryAddress, context))
        {
            return ctx.SetReturn(PngEncErrorInvalidAddress);
        }

        if (!ctx.Memory.TryWrite(handleAddress, handle))
        {
            return ctx.SetReturn(PngEncErrorInvalidAddress);
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "xgDjJKpcyHo",
        ExportName = "scePngEncEncode",
        Target = Generation.Gen5,
        LibraryName = "libScePngEnc")]
    public static int PngEncEncode(CpuContext ctx)
    {
        var handleAddress = ctx[CpuRegister.Rdi];
        var parameterAddress = ctx[CpuRegister.Rsi];
        var outputInfoAddress = ctx[CpuRegister.Rdx];

        if (!TryReadContext(ctx, handleAddress, out var maximumWidth, out var maximumFilters))
            return ctx.SetReturn(PngEncErrorInvalidHandle);

        if (parameterAddress == 0)
            return ctx.SetReturn(PngEncErrorInvalidParameter);

        Span<byte> parameterPayload = stackalloc byte[(int)EncodeParameterSize];
        if (!ctx.Memory.TryRead(parameterAddress, parameterPayload))
            return ctx.SetReturn(PngEncErrorInvalidAddress);

        var parameters = new EncodeParameters(
            BinaryPrimitives.ReadUInt64LittleEndian(parameterPayload),
            BinaryPrimitives.ReadUInt64LittleEndian(parameterPayload[8..]),
            BinaryPrimitives.ReadUInt32LittleEndian(parameterPayload[16..]),
            BinaryPrimitives.ReadUInt32LittleEndian(parameterPayload[20..]),
            BinaryPrimitives.ReadUInt32LittleEndian(parameterPayload[24..]),
            BinaryPrimitives.ReadUInt32LittleEndian(parameterPayload[28..]),
            BinaryPrimitives.ReadUInt32LittleEndian(parameterPayload[32..]),
            BinaryPrimitives.ReadUInt16LittleEndian(parameterPayload[36..]),
            BinaryPrimitives.ReadUInt16LittleEndian(parameterPayload[38..]),
            BinaryPrimitives.ReadUInt16LittleEndian(parameterPayload[40..]),
            BinaryPrimitives.ReadUInt16LittleEndian(parameterPayload[42..]),
            BinaryPrimitives.ReadUInt16LittleEndian(parameterPayload[44..]),
            BinaryPrimitives.ReadUInt16LittleEndian(parameterPayload[46..]));

        var validation = ValidateEncodeParameters(parameters, maximumWidth, maximumFilters);
        if (validation != 0)
            return ctx.SetReturn(validation);

        byte[] png;
        try
        {
            var encodeResult = TryEncodePng(ctx, parameters, out png);
            if (encodeResult != 0)
            {
                WriteOutputInfo(ctx, outputInfoAddress, 0, 0);
                return ctx.SetReturn(encodeResult);
            }
        }
        catch (Exception exception) when (
            exception is OutOfMemoryException or IOException or ArgumentException or OverflowException)
        {
            WriteOutputInfo(ctx, outputInfoAddress, 0, 0);
            return ctx.SetReturn(PngEncErrorFatal);
        }

        if ((uint)png.Length > parameters.PngSize)
        {
            WriteOutputInfo(ctx, outputInfoAddress, 0, 0);
            return ctx.SetReturn(PngEncErrorDataOverflow);
        }

        if (!ctx.Memory.TryWrite(parameters.PngAddress, png))
        {
            WriteOutputInfo(ctx, outputInfoAddress, 0, 0);
            return ctx.SetReturn(PngEncErrorInvalidAddress);
        }

        if (!WriteOutputInfo(ctx, outputInfoAddress, (uint)png.Length, parameters.Height))
            return ctx.SetReturn(PngEncErrorInvalidAddress);

        return ctx.SetReturn(png.Length);
    }

    [SysAbiExport(
        Nid = "RUrWdwTWZy8",
        ExportName = "scePngEncDelete",
        Target = Generation.Gen5,
        LibraryName = "libScePngEnc")]
    public static int PngEncDelete(CpuContext ctx)
    {
        var handleAddress = ctx[CpuRegister.Rdi];
        if (!HasValidContextMagic(ctx, handleAddress))
            return ctx.SetReturn(PngEncErrorInvalidHandle);

        Span<byte> clearedMagic = stackalloc byte[sizeof(ulong)];
        clearedMagic.Clear();
        return ctx.Memory.TryWrite(handleAddress, clearedMagic)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(PngEncErrorInvalidHandle);
    }

    private static int ReadAndValidateCreateParameters(
        CpuContext ctx,
        ulong address,
        out CreateParameters parameters)
    {
        parameters = default;
        if (address == 0)
        {
            return PngEncErrorInvalidAddress;
        }

        Span<byte> payload = stackalloc byte[(int)CreateParameterSize];
        if (!ctx.Memory.TryRead(address, payload))
        {
            return PngEncErrorInvalidAddress;
        }

        parameters = new CreateParameters(
            BinaryPrimitives.ReadUInt32LittleEndian(payload),
            BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]),
            BinaryPrimitives.ReadUInt32LittleEndian(payload[8..]),
            BinaryPrimitives.ReadUInt32LittleEndian(payload[12..]));

        if (parameters.Size != CreateParameterSize ||
            parameters.Attribute != 0 ||
            parameters.MaximumFilterCount > MaximumFilterCount)
        {
            return PngEncErrorInvalidParameter;
        }

        return parameters.MaximumImageWidth is 0 or > MaximumImageWidth
            ? PngEncErrorInvalidSize
            : 0;
    }

    private static bool TryReadContext(
        CpuContext ctx,
        ulong handleAddress,
        out uint maximumWidth,
        out uint maximumFilters)
    {
        maximumWidth = 0;
        maximumFilters = 0;
        if (handleAddress == 0)
            return false;

        Span<byte> context = stackalloc byte[(int)ContextSize];
        if (!ctx.Memory.TryRead(handleAddress, context) ||
            BinaryPrimitives.ReadUInt64LittleEndian(context) != ContextMagic)
        {
            return false;
        }

        maximumWidth = BinaryPrimitives.ReadUInt32LittleEndian(context[8..]);
        maximumFilters = BinaryPrimitives.ReadUInt32LittleEndian(context[12..]);
        return maximumWidth is > 0 and <= MaximumImageWidth && maximumFilters <= MaximumFilterCount;
    }

    private static bool HasValidContextMagic(CpuContext ctx, ulong handleAddress)
    {
        if (handleAddress == 0)
            return false;

        Span<byte> magic = stackalloc byte[sizeof(ulong)];
        return ctx.Memory.TryRead(handleAddress, magic) &&
               BinaryPrimitives.ReadUInt64LittleEndian(magic) == ContextMagic;
    }

    private static int ValidateEncodeParameters(
        EncodeParameters parameters,
        uint maximumWidth,
        uint maximumFilters)
    {
        if (parameters.ImageAddress == 0 ||
            parameters.PngAddress == 0 ||
            (parameters.ImageAddress & 3) != 0)
        {
            return PngEncErrorInvalidAddress;
        }

        if (parameters.PixelFormat is not (PixelFormatRgba or PixelFormatBgra) ||
            parameters.ColorSpace is not (ColorSpaceRgb or ColorSpaceRgba) ||
            parameters.BitDepth != 8 ||
            parameters.ClutCount != 0 ||
            parameters.CompressionLevel > 9 ||
            (parameters.FilterType & ~FilterMask) != 0 ||
            CountBits(parameters.FilterType) > maximumFilters)
        {
            return PngEncErrorInvalidParameter;
        }

        if (parameters.Width == 0 ||
            parameters.Height == 0 ||
            parameters.Width > maximumWidth ||
            parameters.Width > MaximumImageWidth ||
            parameters.Height > MaximumImageHeight)
        {
            return PngEncErrorInvalidSize;
        }

        var components = parameters.ColorSpace == ColorSpaceRgba ? 4UL : 3UL;
        var rowSize = (ulong)parameters.Width * components;
        var sourceRowSize = (ulong)parameters.Width * 4;
        var filteredSize = (rowSize + 1) * parameters.Height;
        var imageSpan = (ulong)parameters.Pitch * parameters.Height;
        if (parameters.PngSize == 0 ||
            parameters.Pitch < sourceRowSize ||
            (parameters.Pitch & 3) != 0 ||
            imageSpan > parameters.ImageSize ||
            filteredSize > int.MaxValue / 2UL ||
            parameters.ImageAddress > ulong.MaxValue - imageSpan ||
            parameters.PngAddress > ulong.MaxValue - parameters.PngSize)
        {
            return PngEncErrorInvalidSize;
        }

        return 0;
    }

    private static int TryEncodePng(CpuContext ctx, EncodeParameters parameters, out byte[] png)
    {
        png = [];
        var components = parameters.ColorSpace == ColorSpaceRgba ? 4 : 3;
        var sourceRow = new byte[checked((int)parameters.Width * 4)];
        var rawRow = new byte[checked((int)parameters.Width * components)];
        var previousRow = new byte[rawRow.Length];
        var filteredRow = new byte[rawRow.Length];

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(
                   compressed,
                   ResolveCompressionLevel(parameters.CompressionLevel),
                   leaveOpen: true))
        {
            for (var y = 0U; y < parameters.Height; y++)
            {
                var sourceAddress = parameters.ImageAddress + (ulong)y * parameters.Pitch;
                if (!ctx.Memory.TryRead(sourceAddress, sourceRow))
                    return PngEncErrorInvalidAddress;

                ConvertRow(sourceRow, rawRow, parameters.PixelFormat == PixelFormatBgra, components);
                var filter = SelectFilter(parameters.FilterType, rawRow, previousRow, filteredRow, components);
                zlib.WriteByte((byte)filter);
                zlib.Write(filteredRow);
                (rawRow, previousRow) = (previousRow, rawRow);
            }
        }

        var compressedBytes = compressed.ToArray();
        using var output = new MemoryStream(checked(57 + compressedBytes.Length));
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, parameters.Width);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], parameters.Height);
        header[8] = 8;
        header[9] = parameters.ColorSpace == ColorSpaceRgba ? (byte)6 : (byte)2;
        header[10] = 0;
        header[11] = 0;
        header[12] = 0;
        WriteChunk(output, "IHDR"u8, header);
        WriteChunk(output, "IDAT"u8, compressedBytes);
        WriteChunk(output, "IEND"u8, ReadOnlySpan<byte>.Empty);
        png = output.ToArray();
        return 0;
    }

    private static void ConvertRow(ReadOnlySpan<byte> source, Span<byte> destination, bool bgra, int components)
    {
        var destinationOffset = 0;
        for (var sourceOffset = 0; sourceOffset < source.Length; sourceOffset += 4)
        {
            destination[destinationOffset] = source[sourceOffset + (bgra ? 2 : 0)];
            destination[destinationOffset + 1] = source[sourceOffset + 1];
            destination[destinationOffset + 2] = source[sourceOffset + (bgra ? 0 : 2)];
            if (components == 4)
                destination[destinationOffset + 3] = source[sourceOffset + 3];
            destinationOffset += components;
        }
    }

    private static int SelectFilter(
        ushort filterMask,
        ReadOnlySpan<byte> row,
        ReadOnlySpan<byte> previousRow,
        Span<byte> filtered,
        int bytesPerPixel)
    {
        if (filterMask != FilterMask)
        {
            var selected = (filterMask & 1) != 0 ? 1 :
                (filterMask & 2) != 0 ? 2 :
                (filterMask & 4) != 0 ? 3 :
                (filterMask & 8) != 0 ? 4 : 0;
            ApplyFilter(selected, row, previousRow, filtered, bytesPerPixel);
            return selected;
        }

        var candidate = new byte[row.Length];
        var bestScore = long.MaxValue;
        var bestFilter = 0;
        for (var filter = 0; filter <= 4; filter++)
        {
            ApplyFilter(filter, row, previousRow, candidate, bytesPerPixel);
            long score = 0;
            foreach (var value in candidate)
                score += Math.Abs((int)(sbyte)value);
            if (score >= bestScore)
                continue;
            bestScore = score;
            bestFilter = filter;
            candidate.CopyTo(filtered);
        }
        return bestFilter;
    }

    private static void ApplyFilter(
        int filter,
        ReadOnlySpan<byte> row,
        ReadOnlySpan<byte> previousRow,
        Span<byte> output,
        int bytesPerPixel)
    {
        for (var index = 0; index < row.Length; index++)
        {
            var left = index >= bytesPerPixel ? row[index - bytesPerPixel] : 0;
            var above = previousRow[index];
            var aboveLeft = index >= bytesPerPixel ? previousRow[index - bytesPerPixel] : 0;
            var predictor = filter switch
            {
                1 => left,
                2 => above,
                3 => (left + above) >> 1,
                4 => Paeth(left, above, aboveLeft),
                _ => 0,
            };
            output[index] = unchecked((byte)(row[index] - predictor));
        }
    }

    private static int Paeth(int left, int above, int aboveLeft)
    {
        var estimate = left + above - aboveLeft;
        var distanceLeft = Math.Abs(estimate - left);
        var distanceAbove = Math.Abs(estimate - above);
        var distanceAboveLeft = Math.Abs(estimate - aboveLeft);
        return distanceLeft <= distanceAbove && distanceLeft <= distanceAboveLeft
            ? left
            : distanceAbove <= distanceAboveLeft ? above : aboveLeft;
    }

    private static CompressionLevel ResolveCompressionLevel(ushort level) => level switch
    {
        0 => CompressionLevel.NoCompression,
        <= 3 => CompressionLevel.Fastest,
        9 => CompressionLevel.SmallestSize,
        _ => CompressionLevel.Optimal,
    };

    private static bool WriteOutputInfo(CpuContext ctx, ulong address, uint dataSize, uint processedHeight)
    {
        if (address == 0)
            return true;
        Span<byte> payload = stackalloc byte[sizeof(uint) * 2];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, dataSize);
        BinaryPrimitives.WriteUInt32LittleEndian(payload[sizeof(uint)..], processedHeight);
        return ctx.Memory.TryWrite(address, payload);
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> word = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(word, checked((uint)data.Length));
        output.Write(word);
        output.Write(type);
        output.Write(data);

        var crc = ComputeCrc32(type, data);
        BinaryPrimitives.WriteUInt32BigEndian(word, crc);
        output.Write(word);
    }

    private static uint ComputeCrc32(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        var crc = uint.MaxValue;
        foreach (var value in type)
            crc = Crc32Table[(int)((crc ^ value) & 0xFF)] ^ (crc >> 8);
        foreach (var value in data)
            crc = Crc32Table[(int)((crc ^ value) & 0xFF)] ^ (crc >> 8);
        return ~crc;
    }

    private static uint[] BuildCrc32Table()
    {
        var table = new uint[256];
        for (var index = 0; index < table.Length; index++)
        {
            var value = (uint)index;
            for (var bit = 0; bit < 8; bit++)
                value = (value & 1) != 0 ? 0xEDB8_8320U ^ (value >> 1) : value >> 1;
            table[index] = value;
        }
        return table;
    }

    private static uint CountBits(ushort value)
    {
        uint count = 0;
        while (value != 0)
        {
            value &= (ushort)(value - 1);
            count++;
        }
        return count;
    }
}
