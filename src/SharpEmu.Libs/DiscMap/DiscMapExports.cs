// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.DiscMap;

/// <summary>
/// HLE implementation of libSceDiscMap, ported from Kyty's LibDiscMap.cpp
/// (https://github.com/InoriRus/Kyty, MIT). Titles installed to internal
/// storage query these entry points on nearly every file access to decide
/// whether a read must be redirected to the Blu-ray drive. Reporting every
/// request as already resident on HDD lets the regular file system path
/// service the read.
/// </summary>
public static class DiscMapExports
{
    private const int DiscMapErrorInvalidArgument = unchecked((int)0x81100001);
    private const int DiscMapErrorLocationNotMapped = unchecked((int)0x81100002);
    private const int DiscMapErrorFileNotFound = unchecked((int)0x81100003);
    private const int DiscMapErrorNoBitmapInfo = unchecked((int)0x81100004);

    [SysAbiExport(
        Nid = "lbQKqsERhtE",
        ExportName = "sceDiscMapIsRequestOnHDD",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceDiscMap")]
    public static int DiscMapIsRequestOnHDD(CpuContext ctx)
    {
        var pathAddress = ctx[CpuRegister.Rdi];
        var offset = ctx[CpuRegister.Rsi];
        var size = ctx[CpuRegister.Rdx];
        var resultAddress = ctx[CpuRegister.Rcx];

        if (pathAddress == 0 || resultAddress == 0)
        {
            return DiscMapErrorInvalidArgument;
        }

        if (!ctx.TryWriteInt32(resultAddress, 1))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        TraceDiscMap(ctx, "sceDiscMapIsRequestOnHDD", pathAddress, offset, size);

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // Synthetic label for an uncatalogued NID (the Unknown* convention); the NID is authoritative.
    #pragma warning disable SHEM006
    [SysAbiExport(
        Nid = "fJgP+wqifno",
        ExportName = "sceDiscMapUnknownFJgP",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceDiscMap")]
    public static int DiscMapUnknownFJgP(CpuContext ctx) => WriteMappingTriple(ctx, "sceDiscMapUnknownFJgP");
    #pragma warning restore SHEM006

    // Synthetic label for an uncatalogued NID (the Unknown* convention); the NID is authoritative.
    #pragma warning disable SHEM006
    [SysAbiExport(
        Nid = "ioKMruft1ek",
        ExportName = "sceDiscMapUnknownIoKM",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceDiscMap")]
    public static int DiscMapUnknownIoKM(CpuContext ctx) => WriteMappingTriple(ctx, "sceDiscMapUnknownIoKM");
    #pragma warning restore SHEM006

    private static int WriteMappingTriple(CpuContext ctx, string exportName)
    {
        var pathAddress = ctx[CpuRegister.Rdi];
        var offset = ctx[CpuRegister.Rsi];
        var size = ctx[CpuRegister.Rdx];
        var flagsAddress = ctx[CpuRegister.Rcx];
        var outAddress1 = ctx[CpuRegister.R8];
        var outAddress2 = ctx[CpuRegister.R9];

        if (pathAddress == 0 || flagsAddress == 0 || outAddress1 == 0 || outAddress2 == 0)
        {
            return DiscMapErrorInvalidArgument;
        }

        if (!ctx.TryWriteUInt64(flagsAddress, 0) ||
            !ctx.TryWriteUInt64(outAddress1, 0) ||
            !ctx.TryWriteUInt64(outAddress2, 0))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        TraceDiscMap(ctx, exportName, pathAddress, offset, size);

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static void TraceDiscMap(CpuContext ctx, string exportName, ulong pathAddress, ulong offset, ulong size)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_DISCMAP"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var path = ctx.TryReadNullTerminatedUtf8(pathAddress, 1024, out var value)
            ? value
            : $"<unreadable 0x{pathAddress:X16}>";
        Console.Error.WriteLine($"[HLE][DISCMAP] {exportName} path={path} offset=0x{offset:X} size=0x{size:X}");
    }
}
