// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Json;

public static class JsonExports
{
    [SysAbiExport(
        Nid = "-hJRce8wn1U",
        ExportName = "_ZN3sce4Json12MemAllocatorC2Ev",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int MemAllocatorConstructor(CpuContext ctx)
    {
        var thisAddress = ctx[CpuRegister.Rdi];
        TraceJson("MemAllocator.ctor", thisAddress, 0);
        ctx[CpuRegister.Rax] = thisAddress;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "OcAgPxcq5Vk",
        ExportName = "_ZN3sce4Json12MemAllocatorD2Ev",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int MemAllocatorDestructor(CpuContext ctx)
    {
        TraceJson("MemAllocator.dtor", ctx[CpuRegister.Rdi], 0);
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "cK6bYHf-Q5E",
        ExportName = "_ZN3sce4Json11InitializerC1Ev",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int InitializerConstructor(CpuContext ctx)
    {
        var thisAddress = ctx[CpuRegister.Rdi];
        TraceJson("Initializer.ctor", thisAddress, 0);
        ctx[CpuRegister.Rax] = thisAddress;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "RujUxbr3haM",
        ExportName = "_ZN3sce4Json11InitializerD1Ev",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int InitializerDestructor(CpuContext ctx)
    {
        TraceJson("Initializer.dtor", ctx[CpuRegister.Rdi], 0);
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "Cxwy7wHq4J0",
        ExportName = "_ZN3sce4Json11Initializer10initializeEPKNS0_13InitParameterE",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int InitializerInitialize(CpuContext ctx)
    {
        var thisAddress = ctx[CpuRegister.Rdi];
        var initParameterAddress = ctx[CpuRegister.Rsi];
        if (thisAddress == 0)
        {
            ctx[CpuRegister.Rax] = unchecked((ulong)(int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        TraceJson("Initializer.initialize", thisAddress, initParameterAddress);
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "WSOuge5IsCg",
        ExportName = "_ZN3sce4Json15InitParameter2C2Ev",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson2")]
    public static int InitParameter2Constructor(CpuContext ctx)
    {
        var thisAddress = ctx[CpuRegister.Rdi];
        TraceJson("InitParameter2.ctor", thisAddress, ctx[CpuRegister.Rsi]);
        ctx[CpuRegister.Rax] = thisAddress;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "I2QC8PYhJWY",
        ExportName = "_ZN3sce4Json15InitParameter212setAllocatorERNS0_12MemAllocatorE",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson2")]
    public static int InitParameter2SetAllocator(CpuContext ctx)
    {
        var thisAddress = ctx[CpuRegister.Rdi];
        TraceJson("InitParameter2.setAllocator", thisAddress, ctx[CpuRegister.Rsi]);
        ctx[CpuRegister.Rax] = thisAddress;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "Eu95jmqn5Rw",
        ExportName = "_ZN3sce4Json15InitParameter217setFileBufferSizeEm",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson2")]
    public static int InitParameter2SetFileBufferSize(CpuContext ctx)
    {
        var thisAddress = ctx[CpuRegister.Rdi];
        TraceJson("InitParameter2.setFileBufferSize", thisAddress, ctx[CpuRegister.Rsi]);
        ctx[CpuRegister.Rax] = thisAddress;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "IXW-z8pggfg",
        ExportName = "_ZN3sce4Json12Initializer2C1Ev",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson2")]
    public static int Initializer2Constructor(CpuContext ctx)
    {
        var thisAddress = ctx[CpuRegister.Rdi];
        TraceJson("Initializer2.ctor", thisAddress, ctx[CpuRegister.Rsi]);
        ctx[CpuRegister.Rax] = thisAddress;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static void TraceJson(string operation, ulong thisAddress, ulong argument)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_JSON"), "1", StringComparison.Ordinal))
        {
            return;
        }

        Console.Error.WriteLine(
            $"[LOADER][TRACE] json.{operation} this=0x{thisAddress:X16} arg=0x{argument:X16}");
    }
}
