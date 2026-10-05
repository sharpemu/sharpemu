// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using SharpEmu.HLE;
using SkiaSharp;

namespace SharpEmu.Libs.Font;

public static class FontExports
{
    private const ushort GlyphMagic = 0x0F03;
    private const int GlyphSize = 0x100;
    private const int RenderOutputSize = 0x40;

    private static readonly object AllocationGate = new();
    private static readonly Stack<ulong> FreeGlyphs = new();
    private static ulong _librarySelectionAddress;
    private static ulong _rendererSelectionAddress;
    private static readonly ConcurrentDictionary<ulong, (float Width, float Height)> FontScales = new();
    private static readonly ConcurrentDictionary<ulong, FontFace> FontFaces = new();
    private static readonly Lazy<FontFace> DefaultFont = new(() => new(SKTypeface.FromFamilyName(null)));

    [SysAbiExport(
        Nid = "oaJ1BpN2FQk",
        ExportName = "sceFontTextSourceInit",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int TextSourceInit(CpuContext ctx)
    {
        var sourceAddress = ctx[CpuRegister.Rdi];
        var textAddress = ctx[CpuRegister.Rsi];
        var textSize = (uint)ctx[CpuRegister.Rdx];
        if (sourceAddress == 0 || textAddress > ulong.MaxValue - textSize)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        Span<byte> source = stackalloc byte[0x60];
        source.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(source[0x08..], textAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(source[0x10..], textAddress == 0 ? 0 : textAddress + textSize);
        BinaryPrimitives.WriteUInt64LittleEndian(source[0x18..], textAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(source[0x20..], ctx[CpuRegister.Rcx]);
        BinaryPrimitives.WriteUInt64LittleEndian(source[0x28..], ctx[CpuRegister.R8]);
        BinaryPrimitives.WriteUInt64LittleEndian(source[0x38..], 0x10);
        BinaryPrimitives.WriteUInt64LittleEndian(source[0x40..], textSize);
        return ctx.Memory.TryWrite(sourceAddress, source)
            ? SetSuccess(ctx)
            : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "whrS4oksXc4",
        ExportName = "sceFontMemoryInit",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int MemoryInit(CpuContext ctx)
    {
        var descriptorAddress = ctx[CpuRegister.Rdi];
        var regionAddress = ctx[CpuRegister.Rsi];
        var regionSize = (uint)ctx[CpuRegister.Rdx];
        var interfaceAddress = ctx[CpuRegister.Rcx];
        var mspaceAddress = ctx[CpuRegister.R8];
        var destroyCallback = ctx[CpuRegister.R9];
        if (descriptorAddress == 0 ||
            !TryWriteUInt32(ctx, descriptorAddress, 0x00000F00) ||
            !TryWriteUInt32(ctx, descriptorAddress + 0x04, regionSize) ||
            !ctx.TryWriteUInt64(descriptorAddress + 0x08, regionAddress) ||
            !ctx.TryWriteUInt64(descriptorAddress + 0x10, mspaceAddress) ||
            !ctx.TryWriteUInt64(descriptorAddress + 0x18, interfaceAddress) ||
            !ctx.TryWriteUInt64(descriptorAddress + 0x20, destroyCallback) ||
            !ctx.TryWriteUInt64(descriptorAddress + 0x28, 0) ||
            !ctx.TryWriteUInt64(descriptorAddress + 0x30, 0) ||
            !ctx.TryWriteUInt64(descriptorAddress + 0x38, mspaceAddress))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return SetSuccess(ctx);
    }

    [SysAbiExport(
        Nid = "oM+XCzVG3oM",
        ExportName = "sceFontSelectLibraryFt",
        Target = Generation.Gen5,
        LibraryName = "libSceFontFt")]
    public static int SelectLibraryFt(CpuContext ctx) =>
        ReturnSelection(ctx, ref _librarySelectionAddress, 0x38);

    [SysAbiExport(
        Nid = "Xx974EW-QFY",
        ExportName = "sceFontSelectRendererFt",
        Target = Generation.Gen5,
        LibraryName = "libSceFontFt")]
    public static int SelectRendererFt(CpuContext ctx) =>
        ReturnSelection(ctx, ref _rendererSelectionAddress, 0x100);

    [SysAbiExport(
        Nid = "n590hj5Oe-k",
        ExportName = "sceFontCreateLibraryWithEdition",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int CreateLibraryWithEdition(CpuContext ctx) =>
        CreateOpaqueHandle(ctx, ctx[CpuRegister.Rcx], 0x100, magic: 0x0F01);

    [SysAbiExport(
        Nid = "WaSFJoRWXaI",
        ExportName = "sceFontCreateRendererWithEdition",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int CreateRendererWithEdition(CpuContext ctx) =>
        CreateOpaqueHandle(ctx, ctx[CpuRegister.Rcx], 0x100, magic: 0x0F07);

    [SysAbiExport(
        Nid = "3OdRkSjOcog",
        ExportName = "sceFontBindRenderer",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int BindRenderer(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "Z2cdsqJH+5k",
        ExportName = "sceFontRebindRenderer",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int RebindRenderer(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "N1EBMeGhf7E",
        ExportName = "sceFontSetScalePixel",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int SetScalePixel(CpuContext ctx) => SetFontHeight(ctx);

    [SysAbiExport(
        Nid = "TMtqoFQjjbA",
        ExportName = "sceFontSetEffectSlant",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int SetEffectSlant(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "v0phZwa4R5o",
        ExportName = "sceFontSetEffectWeight",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int SetEffectWeight(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "6vGCkkQJOcI",
        ExportName = "sceFontSetupRenderScalePixel",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int SetupRenderScalePixel(CpuContext ctx) => SetFontHeight(ctx);

    [SysAbiExport(
        Nid = "lz9y9UFO2UU",
        ExportName = "sceFontSetupRenderEffectSlant",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int SetupRenderEffectSlant(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "XIGorvLusDQ",
        ExportName = "sceFontSetupRenderEffectWeight",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int SetupRenderEffectWeight(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "imxVx8lm+KM",
        ExportName = "sceFontGetHorizontalLayout",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int GetHorizontalLayout(CpuContext ctx)
    {
        var layoutAddress = ctx[CpuRegister.Rsi];
        if (layoutAddress == 0)
        {
            layoutAddress = ctx[CpuRegister.Rdx];
        }
        if (layoutAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var fontHandle = ctx[CpuRegister.Rdi];
        var scale = GetFontScale(fontHandle);
        float[] values = [scale.Height * 0.75f, scale.Height, 0];
        if (FontFaces.TryGetValue(fontHandle, out var face))
        {
            var metrics = face.GetMetrics(scale.Width, scale.Height);
            values = [-metrics.Ascent, metrics.Descent - metrics.Ascent + metrics.Leading, 0];
        }
        for (var index = 0; index < values.Length; index++)
        {
            if (!TryWriteUInt32(
                    ctx,
                    layoutAddress + (ulong)(index * sizeof(float)),
                    BitConverter.SingleToUInt32Bits(values[index])))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }
        }

        return SetSuccess(ctx);
    }

    [SysAbiExport(
        Nid = "3BrWWFU+4ts",
        ExportName = "sceFontGetVerticalLayout",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int GetVerticalLayout(CpuContext ctx)
    {
        var layoutAddress = ctx[CpuRegister.Rsi];
        if (layoutAddress == 0)
        {
            layoutAddress = ctx[CpuRegister.Rdx];
        }
        if (layoutAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // Baseline (horizontal offset), line advance, decoration extent.
        // Mirrors the same three-float layout as GetHorizontalLayout, but
        // interpreted for vertical writing (e.g. CJK text rendered top-to-bottom).
        // Scaled to the font's current pixel height; the default height of 16
        // reproduces the original fixed geometry.
        var scale = GetFontScale(ctx[CpuRegister.Rdi]);
        var values = new[] { scale.Width * 0.5f, scale.Width, 0.0f };
        for (var index = 0; index < values.Length; index++)
        {
            if (!TryWriteUInt32(
                    ctx,
                    layoutAddress + (ulong)(index * sizeof(float)),
                    BitConverter.SingleToUInt32Bits(values[index])))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }
        }

        return SetSuccess(ctx);
    }

    [SysAbiExport(
        Nid = "cKYtVmeSTcw",
        ExportName = "sceFontOpenFontSet",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int OpenFontSet(CpuContext ctx)
    {
        var fontSet = (uint)ctx[CpuRegister.Rsi];
        var bold = (fontSet & 0xf) >= 7;
        var italic = ((fontSet >> 20) & 0xf) == 1;
        var style = bold ? (italic ? SKFontStyle.BoldItalic : SKFontStyle.Bold) :
            (italic ? SKFontStyle.Italic : SKFontStyle.Normal);
        return OpenFace(ctx, new(SKTypeface.FromFamilyName(null, style)));
    }

    [SysAbiExport(
        Nid = "KXUpebrFk1U",
        ExportName = "sceFontOpenFontMemory",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int OpenFontMemory(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rsi];
        var size = (uint)ctx[CpuRegister.Rdx];
        if (address == 0 || size is < 12 or > 64 * 1024 * 1024 || ctx[CpuRegister.R8] == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }
        if (!ctx.Memory.CanRead(address, size))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
        var bytes = new byte[(int)size];
        if (!ctx.Memory.TryRead(address, bytes))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
        uint index = 0;
        if (ctx[CpuRegister.Rcx] != 0 && !ctx.TryReadUInt32(ctx[CpuRegister.Rcx] + 8, out index))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
        if (index > int.MaxValue)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }
        using var data = SKData.CreateCopy(bytes);
        var typeface = SKTypeface.FromData(data, (int)index);
        return typeface is null
            ? SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT)
            : OpenFace(ctx, new(typeface));
    }

    private static int OpenFace(CpuContext ctx, FontFace face)
    {
        var result = CreateOpaqueHandle(ctx, ctx[CpuRegister.R8], 0x100, magic: 0x0F02);
        if (result == 0 && ctx.TryReadUInt64(ctx[CpuRegister.R8], out var handle))
        {
            FontFaces[handle] = face;
            FontScales.TryRemove(handle, out _);
        }
        return result;
    }

    [SysAbiExport(
        Nid = "JzCH3SCFnAU",
        ExportName = "sceFontOpenFontInstance",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int OpenFontInstance(CpuContext ctx)
    {
        var sourceHandle = ctx[CpuRegister.Rdi];
        var setupHandle = ctx[CpuRegister.Rsi];
        var outputAddress = ctx[CpuRegister.Rdx];
        if (outputAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (setupHandle != 0)
        {
            if (FontFaces.TryGetValue(sourceHandle, out var face)) FontFaces[setupHandle] = face;
            FontScales[setupHandle] = GetFontScale(sourceHandle);
            return ctx.TryWriteUInt64(outputAddress, setupHandle)
                ? SetSuccess(ctx)
                : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (!TryAllocateOpaque(ctx, 0x100, out var handle))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (sourceHandle != 0)
        {
            Span<byte> source = stackalloc byte[0x100];
            if (ctx.Memory.TryRead(sourceHandle, source))
            {
                _ = ctx.Memory.TryWrite(handle, source);
            }
        }

        _ = TryWriteUInt16(ctx, handle, 0x0F02);
        if (FontFaces.TryGetValue(sourceHandle, out var sourceFace)) FontFaces[handle] = sourceFace;
        FontScales[handle] = GetFontScale(sourceHandle);
        return ctx.TryWriteUInt64(outputAddress, handle)
            ? SetSuccess(ctx)
            : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "SsRbbCiWoGw",
        ExportName = "sceFontSupportSystemFonts",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int SupportSystemFonts(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "mz2iTY0MK4A",
        ExportName = "sceFontSupportExternalFonts",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int SupportExternalFonts(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "CUKn5pX-NVY",
        ExportName = "sceFontAttachDeviceCacheBuffer",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int AttachDeviceCacheBuffer(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "IQtleGLL5pQ",
        ExportName = "sceFontGetRenderCharGlyphMetrics",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int GetRenderCharGlyphMetrics(CpuContext ctx)
    {
        var metricsAddress = ctx[CpuRegister.Rdx];
        if (metricsAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var code = (uint)ctx[CpuRegister.Rsi];
        if (!IsValidCode(code)) return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        return WriteGlyphMetrics(ctx, metricsAddress, GetGlyph(ctx[CpuRegister.Rdi], code), GetFontScale(ctx[CpuRegister.Rdi]).Height)
            ? SetSuccess(ctx)
            : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "L97d+3OgMlE",
        ExportName = "sceFontGetCharGlyphMetrics",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int GetCharGlyphMetrics(CpuContext ctx) => GetRenderCharGlyphMetrics(ctx);

    [SysAbiExport(
        Nid = "sDuhHGNhHvE",
        ExportName = "sceFontGetKerning",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int GetKerning(CpuContext ctx)
    {
        var kerningAddress = ctx[CpuRegister.Rcx];
        if (kerningAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // OrbisFontKerning is four floats (offsetX, offsetY, positionX, positionY).
        // Until the font backend exposes real kerning data, match the safe fallback
        // used when no kerning-capable face is available and return zero offsets.
        for (var offset = 0; offset < 16; offset += sizeof(float))
        {
            if (!TryWriteUInt32(ctx, kerningAddress + (ulong)offset, 0))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }
        }

        return SetSuccess(ctx);
    }

    [SysAbiExport(
        Nid = "gdUCnU0gHdI",
        ExportName = "sceFontRenderSurfaceInit",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int RenderSurfaceInit(CpuContext ctx)
    {
        var surfaceAddress = ctx[CpuRegister.Rdi];
        var bufferAddress = ctx[CpuRegister.Rsi];
        var widthBytes = (uint)ctx[CpuRegister.Rdx];
        var pixelBytes = (uint)ctx[CpuRegister.Rcx] & 0xFF;
        var width = (uint)ctx[CpuRegister.R8];
        var height = (uint)ctx[CpuRegister.R9];
        if (surfaceAddress == 0 ||
            !ctx.TryWriteUInt64(surfaceAddress, bufferAddress) ||
            !TryWriteUInt32(ctx, surfaceAddress + 0x08, widthBytes) ||
            !TryWriteUInt32(ctx, surfaceAddress + 0x0C, pixelBytes) ||
            !TryWriteUInt32(ctx, surfaceAddress + 0x10, width) ||
            !TryWriteUInt32(ctx, surfaceAddress + 0x14, height) ||
            !TryWriteUInt32(ctx, surfaceAddress + 0x18, 0) ||
            !TryWriteUInt32(ctx, surfaceAddress + 0x1C, 0) ||
            !TryWriteUInt32(ctx, surfaceAddress + 0x20, width) ||
            !TryWriteUInt32(ctx, surfaceAddress + 0x24, height))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return SetSuccess(ctx);
    }

    [SysAbiExport(
        Nid = "vRxf4d0ulPs",
        ExportName = "sceFontRenderSurfaceSetScissor",
        Target = Generation.Gen5, LibraryName = "libSceFont")]
    public static int RenderSurfaceSetScissor(CpuContext ctx)
    {
        var surfaceAddress = ctx[CpuRegister.Rdi];
        if (surfaceAddress == 0)
        {
            return SetSuccess(ctx);
        }

        return TryWriteUInt32(ctx, surfaceAddress + 0x18, (uint)ctx[CpuRegister.Rsi]) &&
            TryWriteUInt32(ctx, surfaceAddress + 0x1C, (uint)ctx[CpuRegister.Rdx]) &&
            TryWriteUInt32(ctx, surfaceAddress + 0x20, (uint)ctx[CpuRegister.Rcx]) &&
            TryWriteUInt32(ctx, surfaceAddress + 0x24, (uint)ctx[CpuRegister.R8])
                ? SetSuccess(ctx)
                : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "C-4Qw5Srlyw",
        ExportName = "sceFontGenerateCharGlyph",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int GenerateCharGlyph(CpuContext ctx)
    {
        var outputAddress = ctx[CpuRegister.Rcx];
        if (outputAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!TryRentGlyph(ctx, out var glyph) ||
            !ctx.TryWriteUInt64(outputAddress, glyph))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return SetSuccess(ctx);
    }

    [SysAbiExport(
        Nid = "8-zmgsxkBek",
        ExportName = "sceFontGlyphDefineAttribute",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int GlyphDefineAttribute(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "LHDoRWVFGqk",
        ExportName = "sceFontDeleteGlyph",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int DeleteGlyph(CpuContext ctx)
    {
        var glyphPointerAddress = ctx[CpuRegister.Rsi];
        if (glyphPointerAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!ctx.TryReadUInt64(glyphPointerAddress, out var glyph))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (glyph != 0)
        {
            lock (AllocationGate)
            {
                FreeGlyphs.Push(glyph);
            }
        }

        return ctx.TryWriteUInt64(glyphPointerAddress, 0)
            ? SetSuccess(ctx)
            : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "kAenWy1Zw5o",
        ExportName = "sceFontRenderCharGlyphImageHorizontal",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int RenderCharGlyphImageHorizontal(CpuContext ctx)
    {
        var surfaceAddress = ctx[CpuRegister.Rdx];
        var metricsAddress = ctx[CpuRegister.Rcx];
        var resultAddress = ctx[CpuRegister.R8];
        var font = ctx[CpuRegister.Rdi];
        var code = (uint)ctx[CpuRegister.Rsi];
        if (!IsValidCode(code)) return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        var glyph = GetGlyph(font, code);
        var width = glyph.Width;
        var height = glyph.Height;

        if (metricsAddress != 0 && !WriteGlyphMetrics(ctx, metricsAddress, glyph, GetFontScale(font).Height))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        ctx.GetXmmRegister(0, out var xBits, out _);
        ctx.GetXmmRegister(1, out var yBits, out _);
        var x = BitConverter.UInt32BitsToSingle((uint)xBits);
        var y = BitConverter.UInt32BitsToSingle((uint)yBits);
        var left = float.IsFinite(x) ? (int)MathF.Floor(x + glyph.BearingX) : 0;
        var top = float.IsFinite(y) ? (int)MathF.Floor(y - glyph.BearingY) : 0;

        ulong buffer = 0;
        uint pitch = 0;
        uint pixelSize = 0;
        if (resultAddress != 0)
        {
            Span<byte> result = stackalloc byte[RenderOutputSize];
            result.Clear();
            if (surfaceAddress != 0 && ctx.TryReadUInt64(surfaceAddress, out buffer) &&
                ctx.TryReadUInt32(surfaceAddress + 8, out pitch) &&
                ctx.TryReadUInt32(surfaceAddress + 12, out pixelSize))
            {
                BinaryPrimitives.WriteUInt64LittleEndian(result[8..], buffer);
                BinaryPrimitives.WriteUInt32LittleEndian(result[16..], pitch);
                result[20] = (byte)pixelSize;
            }
            BinaryPrimitives.WriteUInt32LittleEndian(result[24..], (uint)Math.Max(left, 0));
            BinaryPrimitives.WriteUInt32LittleEndian(result[28..], (uint)Math.Max(top, 0));
            BinaryPrimitives.WriteUInt32LittleEndian(result[32..], (uint)Math.Max(width + Math.Min(left, 0), 0));
            BinaryPrimitives.WriteUInt32LittleEndian(result[36..], (uint)Math.Max(height + Math.Min(top, 0), 0));
            BinaryPrimitives.WriteSingleLittleEndian(result[40..], glyph.BearingX);
            BinaryPrimitives.WriteSingleLittleEndian(result[44..], glyph.BearingY);
            BinaryPrimitives.WriteSingleLittleEndian(result[48..], glyph.Advance);
            BinaryPrimitives.WriteSingleLittleEndian(result[52..], glyph.Advance);
            BinaryPrimitives.WriteUInt32LittleEndian(result[56..], (uint)width);
            BinaryPrimitives.WriteUInt32LittleEndian(result[60..], (uint)height);
            if (!ctx.Memory.TryWrite(resultAddress, result))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }
        }

        if (surfaceAddress != 0 && !DrawGlyph(ctx, surfaceAddress, left, top, width, height, glyph.Coverage))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return SetSuccess(ctx);
    }

    private static int SetFontHeight(CpuContext ctx)
    {
        ctx.GetXmmRegister(0, out var widthBits, out _);
        ctx.GetXmmRegister(1, out var heightBits, out _);
        var width = BitConverter.UInt32BitsToSingle((uint)widthBits);
        var height = BitConverter.UInt32BitsToSingle((uint)heightBits);
        if (float.IsFinite(height) && height > 1)
        {
            FontScales[ctx[CpuRegister.Rdi]] = (
                float.IsFinite(width) && width > 1 ? Math.Clamp(width, 8, 128) : Math.Clamp(height, 8, 128),
                Math.Clamp(height, 8, 128));
        }
        return SetSuccess(ctx);
    }

    private static (float Width, float Height) GetFontScale(ulong font) =>
        FontScales.TryGetValue(font, out var scale) ? scale : (16, 16);

    private static bool IsValidCode(uint code) => (code & 0xffff0000) == 0x80000000 || Rune.IsValid((int)code);

    private static FontGlyph GetGlyph(ulong font, uint code)
    {
        var scale = GetFontScale(font);
        return (FontFaces.TryGetValue(font, out var face) ? face : DefaultFont.Value).GetGlyph(code, scale.Width, scale.Height);
    }

    private static bool WriteGlyphMetrics(CpuContext ctx, ulong address, FontGlyph glyph, float height)
    {
        Span<byte> metrics = stackalloc byte[32];
        float[] values = [glyph.Width, glyph.Height, glyph.BearingX, glyph.BearingY, glyph.Advance, 0, 0, height];
        for (var index = 0; index < values.Length; index++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(metrics[(index * 4)..], values[index]);
        }
        return ctx.Memory.TryWrite(address, metrics);
    }

    internal static bool DrawGlyph(CpuContext ctx, ulong surface, int left, int top,
        int glyphWidth, int glyphHeight, byte[] coverage)
    {
        if (!ctx.TryReadUInt64(surface, out var buffer) ||
            !ctx.TryReadUInt32(surface + 8, out var pitch) ||
            !ctx.TryReadUInt32(surface + 12, out var pixelSize) ||
            !ctx.TryReadUInt32(surface + 16, out var width) ||
            !ctx.TryReadUInt32(surface + 20, out var height) ||
            !ctx.TryReadUInt32(surface + 24, out var sx0) ||
            !ctx.TryReadUInt32(surface + 28, out var sy0) ||
            !ctx.TryReadUInt32(surface + 32, out var sx1) ||
            !ctx.TryReadUInt32(surface + 36, out var sy1))
        {
            return false;
        }
        pixelSize &= 0xff;
        if (buffer == 0 || pixelSize is < 1 or > 4 || pitch == 0 || width == 0 || height == 0)
        {
            return true;
        }

        var x0 = Math.Max(left, (int)Math.Min(sx0, width));
        var y0 = Math.Max(top, (int)Math.Min(sy0, height));
        var x1 = Math.Min(left + glyphWidth, (int)Math.Min(sx1, width));
        var y1 = Math.Min(top + glyphHeight, (int)Math.Min(sy1, height));
        if (x0 >= x1 || y0 >= y1 || (ulong)x1 * pixelSize > pitch)
        {
            return true;
        }

        var line = new byte[(x1 - x0) * pixelSize];
        for (var yy = y0; yy < y1; yy++)
        {
            var address = buffer + (ulong)yy * pitch + (ulong)x0 * pixelSize;
            if (!ctx.Memory.TryRead(address, line))
            {
                return false;
            }
            for (var xx = x0; xx < x1; xx++)
            {
                var alpha = coverage[(yy - top) * glyphWidth + xx - left];
                if (alpha != 0)
                {
                    var pixel = line.AsSpan((xx - x0) * (int)pixelSize, (int)pixelSize);
                    for (var component = 0; component < pixel.Length; component++)
                        pixel[component] = (byte)(alpha + (pixel[component] * (255 - alpha) + 127) / 255);
                }
            }
            if (!ctx.Memory.TryWrite(address, line))
            {
                return false;
            }
        }
        return true;
    }

    [SysAbiExport(
        Nid = "vzHs3C8lWJk",
        ExportName = "sceFontCloseFont",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int CloseFont(CpuContext ctx)
    {
        FontFaces.TryRemove(ctx[CpuRegister.Rdi], out _);
        FontScales.TryRemove(ctx[CpuRegister.Rdi], out _);
        return SetSuccess(ctx);
    }

    [SysAbiExport(
        Nid = "1QjhKxrsOB8",
        ExportName = "sceFontUnbindRenderer",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int UnbindRenderer(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "exAxkyVLt0s",
        ExportName = "sceFontDestroyRenderer",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int DestroyRenderer(CpuContext ctx)
    {
        var rendererPointerAddress = ctx[CpuRegister.Rdi];
        if (rendererPointerAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        return ctx.TryWriteUInt64(rendererPointerAddress, 0)
            ? SetSuccess(ctx)
            : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    private static bool TryRentGlyph(CpuContext ctx, out ulong glyph)
    {
        lock (AllocationGate)
        {
            if (FreeGlyphs.Count > 0)
            {
                glyph = FreeGlyphs.Pop();
                return TryWriteUInt16(ctx, glyph, GlyphMagic);
            }
        }

        return TryAllocateOpaque(ctx, GlyphSize, out glyph) &&
               TryWriteUInt16(ctx, glyph, GlyphMagic);
    }

    private static int ReturnSelection(CpuContext ctx, ref ulong selectionAddress, uint objectSize)
    {
        if (ctx[CpuRegister.Rdi] != 0)
        {
            ctx[CpuRegister.Rax] = 0;
            return 0;
        }

        lock (AllocationGate)
        {
            if (selectionAddress == 0)
            {
                if (!TryAllocateOpaque(ctx, 0x20, out selectionAddress) ||
                    !TryWriteUInt32(ctx, selectionAddress, 0) ||
                    !TryWriteUInt32(ctx, selectionAddress + 4, objectSize))
                {
                    selectionAddress = 0;
                }
            }
        }

        ctx[CpuRegister.Rax] = selectionAddress;
        return 0;
    }

    private static int CreateOpaqueHandle(CpuContext ctx, ulong outputAddress, int size, ushort magic)
    {
        if (outputAddress == 0 || !TryAllocateOpaque(ctx, size, out var handle))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (!TryWriteUInt16(ctx, handle, magic) || !ctx.TryWriteUInt64(outputAddress, handle))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return SetSuccess(ctx);
    }

    private static bool TryAllocateOpaque(CpuContext ctx, int size, out ulong address)
    {
        address = 0;
        if (ctx.Memory is not IGuestMemoryAllocator allocator ||
            !allocator.TryAllocateGuestMemory((ulong)size, 0x10, out address))
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[size];
        bytes.Clear();
        return ctx.Memory.TryWrite(address, bytes);
    }

    private static bool TryWriteUInt16(CpuContext ctx, ulong address, ushort value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        return ctx.Memory.TryWrite(address, bytes);
    }

    private static bool TryWriteUInt32(CpuContext ctx, ulong address, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return ctx.Memory.TryWrite(address, bytes);
    }

    private static int SetSuccess(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    private static int SetReturn(CpuContext ctx, OrbisGen2Result result)
    {
        ctx[CpuRegister.Rax] = unchecked((ulong)(int)result);
        return (int)result;
    }
}
