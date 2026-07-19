// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Font;

public static class FontExports
{
    private static readonly object AllocationGate = new();
    private static ulong _librarySelectionAddress;
    private static ulong _rendererSelectionAddress;

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
        Nid = "N1EBMeGhf7E",
        ExportName = "sceFontSetScalePixel",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int SetScalePixel(CpuContext ctx) => SetSuccess(ctx);

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
    public static int SetupRenderScalePixel(CpuContext ctx) => SetSuccess(ctx);

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
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // Baseline, line advance, decoration extent: the same invented geometry
        // as GetRenderCharGlyphMetrics.
        var values = new[] { 12.0f, 16.0f, 0.0f };
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
    public static int OpenFontSet(CpuContext ctx) =>
        CreateOpaqueHandle(ctx, ctx[CpuRegister.R8], 0x100, magic: 0x0F02);

    [SysAbiExport(
        Nid = "KXUpebrFk1U",
        ExportName = "sceFontOpenFontMemory",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int OpenFontMemory(CpuContext ctx) =>
        CreateOpaqueHandle(ctx, ctx[CpuRegister.R8], 0x100, magic: 0x0F02);

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

        var values = new[] { 8.0f, 16.0f, 0.0f, 12.0f, 8.0f, 0.0f, 0.0f, 16.0f };
        for (var index = 0; index < values.Length; index++)
        {
            if (!TryWriteUInt32(
                    ctx,
                    metricsAddress + (ulong)(index * sizeof(float)),
                    BitConverter.SingleToUInt32Bits(values[index])))
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

    // sceFontGetVerticalLayout: mirror GetHorizontalLayout. The engine reads
    // baseline / line-advance / decoration extent floats out of rsi; write the
    // same invented geometry so layout math doesn't divide by zero. Without
    // this export the import traps and AVs during font init (e.g. Astro Bot).
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
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var values = new[] { 12.0f, 16.0f, 0.0f };
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
        Nid = "m8IWpATfdlU",
        ExportName = "sceFontOpen",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int Open(CpuContext ctx) => CreateOpaqueHandle(ctx, ctx[CpuRegister.R8], 0x100, magic: 0x0F01);

    [SysAbiExport(
        Nid = "0MSWKGP6vTY",
        ExportName = "sceFontOpenWithEdition",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int OpenWithEdition(CpuContext ctx) => CreateOpaqueHandle(ctx, ctx[CpuRegister.R8], 0x100, magic: 0x0F01);

    [SysAbiExport(
        Nid = "BNMZ8FLUb0M",
        ExportName = "sceFontClose",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int Close(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "W20K7jlecQs",
        ExportName = "sceFontGetFontList",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int GetFontList(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "8IYoZvD+ftY",
        ExportName = "sceFontGetFontInfo",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int GetFontInfo(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "6uyC8vkrjQg",
        ExportName = "sceFontGetFontInfoByIndexNumber",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int GetFontInfoByIndexNumber(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "xA+ZY8vB23Q",
        ExportName = "sceFontFindFont",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int FindFont(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "LicXq6sYii4",
        ExportName = "sceFontFindFontByIndexNumber",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int FindFontByIndexNumber(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "btKlMfbvHvg",
        ExportName = "sceFontGetCharInfo",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int GetCharInfo(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "DQ88qpIfHxE",
        ExportName = "sceFontGetCharImageRect",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int GetCharImageRect(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "JyXkZN0zNp0",
        ExportName = "sceFontGetCharGlyphImage",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int GetCharGlyphImage(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "A+CR5wsReME",
        ExportName = "sceFontRenderChar",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int RenderChar(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "iJ0ITQd7j2g",
        ExportName = "sceFontRenderText",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int RenderText(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "+61DJFF+O3M",
        ExportName = "sceFontRenderTextEx",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int RenderTextEx(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "IFq-MmVoXEc",
        ExportName = "sceFontRenderTextEx2",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int RenderTextEx2(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "ragjw+t57U4",
        ExportName = "sceFontSetResolution",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int SetResolution(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "2XuwSn9FMaU",
        ExportName = "sceFontGetResolution",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int GetResolution(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "goZvzzIyEk0",
        ExportName = "sceFontSetEdgeColor",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int SetEdgeColor(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "nJl94f-bXxg",
        ExportName = "sceFontGetEdgeColor",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int GetEdgeColor(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "YrhnAYrwEJw",
        ExportName = "sceFontSetMemoryOn",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int SetMemoryOn(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "CI4i7qJkk9E",
        ExportName = "sceFontGetMemoryOn",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int GetMemoryOn(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "TP9NxdE4cDA",
        ExportName = "sceFontSetAlias",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int SetAlias(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "vm5H3KMkpPc",
        ExportName = "sceFontGetAlias",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int GetAlias(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "8yEjBGvO1-I",
        ExportName = "sceFontGetShadowColor",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int GetShadowColor(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "h2GFElmd83w",
        ExportName = "sceFontSetShadowColor",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int SetShadowColor(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "t03kSLl5HWs",
        ExportName = "sceFontGetShadowOn",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int GetShadowOn(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "vyBe+agXceY",
        ExportName = "sceFontSetShadowOn",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int SetShadowOn(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "jXWM95SzSJs",
        ExportName = "sceFontGetLibraryVersion",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int GetLibraryVersion(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "diRlpc19ApE",
        ExportName = "sceFontGetRendererVersion",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int GetRendererVersion(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "jxmva5iwliA",
        ExportName = "sceFontSetDefaultFontH",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int SetDefaultFontH(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "dhlBfjZHVnU",
        ExportName = "sceFontGetDefaultFontH",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int GetDefaultFontH(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "48MzfN8Nm3Q",
        ExportName = "sceFontSetDefaultFontV",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int SetDefaultFontV(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "LYh5a3WJ09U",
        ExportName = "sceFontGetDefaultFontV",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int GetDefaultFontV(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "Oa0d5-qaRtY",
        ExportName = "sceFontGetCharImageRect2",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int GetCharImageRect2(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "yQClzy6yt-0",
        ExportName = "sceFontGetCharGlyphImage2",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int GetCharGlyphImage2(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "6qiwCw0LcnU",
        ExportName = "sceFontRenderChar2",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int RenderChar2(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "wdJQ-W0c6VI",
        ExportName = "sceFontRenderText2",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int RenderText2(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "vNPSSjHCdKU",
        ExportName = "sceFontGetKerningInfo",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int GetKerningInfo(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "yFhLVX1SpW0",
        ExportName = "sceFontSetKerningOn",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int SetKerningOn(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "6Rq45C8Cl9U",
        ExportName = "sceFontGetKerningOn",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int GetKerningOn(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "XwbRCHnGWeQ",
        ExportName = "sceFontOpenUserFile",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int OpenUserFile(CpuContext ctx) => CreateOpaqueHandle(ctx, ctx[CpuRegister.R8], 0x100, magic: 0x0F01);

    [SysAbiExport(
        Nid = "vk-0K-cNaTw",
        ExportName = "sceFontOpenUserMemory",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int OpenUserMemory(CpuContext ctx) => CreateOpaqueHandle(ctx, ctx[CpuRegister.R8], 0x100, magic: 0x0F01);

    [SysAbiExport(
        Nid = "ajs6g0lMxYQ",
        ExportName = "sceFontCloseUserFile",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int CloseUserFile(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "P84DDPAiJrw",
        ExportName = "sceFontGetFontH",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int GetFontH(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "Nne5dPtWRBY",
        ExportName = "sceFontGetFontV",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int GetFontV(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "rwdGxz8wXtQ",
        ExportName = "sceFontGetScale",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int GetScale(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "2qGvC5UtqhU",
        ExportName = "sceFontSetScale",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int SetScale(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "mxt2u-yBTPY",
        ExportName = "sceFontGetCharImageBuffer",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int GetCharImageBuffer(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "Nx+Jqkgli2Q",
        ExportName = "sceFontGetCharImageBuffer2",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int GetCharImageBuffer2(CpuContext ctx) => SetSuccess(ctx);

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
