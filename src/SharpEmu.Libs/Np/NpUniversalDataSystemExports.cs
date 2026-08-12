// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers.Binary;

namespace SharpEmu.Libs.Np;

public static class NpUniversalDataSystemExports
{
    private const int ErrorInvalidArgument = unchecked((int)0x80553102);
    private const int MaximumGuestStringBytes = 64 * 1024;

    [SysAbiExport(
        Nid = "sjaobBgqeB4",
        ExportName = "sceNpUniversalDataSystemInitialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemInitialize(CpuContext ctx)
    {
        var parameterAddress = ctx[CpuRegister.Rdi];
        if (parameterAddress == 0)
        {
            return InvalidArgument(ctx);
        }

        if (!ctx.TryReadUInt64(parameterAddress, out var size) ||
            !ctx.TryReadUInt64(parameterAddress + sizeof(ulong), out var poolSize))
        {
            return MemoryFault(ctx);
        }

        if (size < 2 * sizeof(ulong))
        {
            return InvalidArgument(ctx);
        }

        NpUniversalDataSystemState.Initialize(poolSize);
        return Success(ctx);
    }

    [SysAbiExport(
        Nid = "47UAEuQl+iI",
        ExportName = "sceNpUniversalDataSystemTerminate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemTerminate(CpuContext ctx)
    {
        NpUniversalDataSystemState.Terminate();
        return Success(ctx);
    }

    [SysAbiExport(
        Nid = "5zBnau1uIEo",
        ExportName = "sceNpUniversalDataSystemCreateContext",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemCreateContext(CpuContext ctx)
    {
        var outputAddress = ctx[CpuRegister.Rdi];
        if (outputAddress == 0)
        {
            return InvalidArgument(ctx);
        }

        if (!ctx.TryWriteInt32(outputAddress, 0))
        {
            return MemoryFault(ctx);
        }

        if (!NpUniversalDataSystemState.TryCreateContext(
                unchecked((int)ctx[CpuRegister.Rsi]),
                unchecked((uint)ctx[CpuRegister.Rdx]),
                ctx[CpuRegister.Rcx],
                out var context))
        {
            return InvalidArgument(ctx);
        }

        return ctx.TryWriteInt32(outputAddress, context) ? Success(ctx) : MemoryFault(ctx);
    }

    [SysAbiExport(
        Nid = "wB7IWzGp2v0",
        ExportName = "sceNpUniversalDataSystemDestroyContext",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemDestroyContext(CpuContext ctx) =>
        StateResult(ctx, NpUniversalDataSystemState.DestroyContext(unchecked((int)ctx[CpuRegister.Rdi])));

    [SysAbiExport(
        Nid = "hT0IAEvN+M0",
        ExportName = "sceNpUniversalDataSystemCreateHandle",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemCreateHandle(CpuContext ctx)
    {
        var outputAddress = ctx[CpuRegister.Rdi];
        if (outputAddress == 0)
        {
            return InvalidArgument(ctx);
        }

        if (!ctx.TryWriteInt32(outputAddress, 0))
        {
            return MemoryFault(ctx);
        }

        if (!NpUniversalDataSystemState.TryCreateServiceHandle(out var handle))
        {
            return InvalidArgument(ctx);
        }

        return ctx.TryWriteInt32(outputAddress, handle) ? Success(ctx) : MemoryFault(ctx);
    }

    [SysAbiExport(
        Nid = "AUIHb7jUX3I",
        ExportName = "sceNpUniversalDataSystemDestroyHandle",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemDestroyHandle(CpuContext ctx) =>
        StateResult(ctx, NpUniversalDataSystemState.DestroyServiceHandle(unchecked((int)ctx[CpuRegister.Rdi])));

    [SysAbiExport(
        Nid = "jZCqWFgMehE",
        ExportName = "sceNpUniversalDataSystemAbortHandle",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemAbortHandle(CpuContext ctx) =>
        StateResult(ctx, NpUniversalDataSystemState.AbortServiceHandle(unchecked((int)ctx[CpuRegister.Rdi])));

    [SysAbiExport(
        Nid = "tpFJ8LIKvPw",
        ExportName = "sceNpUniversalDataSystemRegisterContext",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemRegisterContext(CpuContext ctx) =>
        StateResult(
            ctx,
            NpUniversalDataSystemState.RegisterContext(
                unchecked((int)ctx[CpuRegister.Rdi]),
                unchecked((int)ctx[CpuRegister.Rsi]),
                ctx[CpuRegister.Rdx]));

    [SysAbiExport(
        Nid = "s6W4Zl4Slgk",
        ExportName = "sceNpUniversalDataSystemCreateEventPropertyObject",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemCreateEventPropertyObject(CpuContext ctx)
    {
        var outputAddress = ctx[CpuRegister.Rdi];
        if (outputAddress == 0)
        {
            return InvalidArgument(ctx);
        }

        if (!ctx.TryWriteUInt64(outputAddress, 0))
        {
            return MemoryFault(ctx);
        }

        if (!NpUniversalDataSystemState.TryCreateObject(out var handle))
        {
            return InvalidArgument(ctx);
        }

        return ctx.TryWriteUInt64(outputAddress, handle) ? Success(ctx) : MemoryFault(ctx);
    }

    [SysAbiExport(
        Nid = "kKUH0Viib3c",
        ExportName = "sceNpUniversalDataSystemDestroyEventPropertyObject",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemDestroyEventPropertyObject(CpuContext ctx) =>
        StateResult(ctx, NpUniversalDataSystemState.DestroyObject(ctx[CpuRegister.Rdi]));

    [SysAbiExport(
        Nid = "Hm7qubT3b70",
        ExportName = "sceNpUniversalDataSystemCreateEventPropertyArray",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemCreateEventPropertyArray(CpuContext ctx)
    {
        var outputAddress = ctx[CpuRegister.Rdi];
        if (outputAddress == 0)
        {
            return InvalidArgument(ctx);
        }

        if (!ctx.TryWriteUInt64(outputAddress, 0))
        {
            return MemoryFault(ctx);
        }

        if (!NpUniversalDataSystemState.TryCreateArray(out var handle))
        {
            return InvalidArgument(ctx);
        }

        return ctx.TryWriteUInt64(outputAddress, handle) ? Success(ctx) : MemoryFault(ctx);
    }

    [SysAbiExport(
        Nid = "W-0xwY0ZMjw",
        ExportName = "sceNpUniversalDataSystemDestroyEventPropertyArray",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemDestroyEventPropertyArray(CpuContext ctx) =>
        StateResult(ctx, NpUniversalDataSystemState.DestroyArray(ctx[CpuRegister.Rdi]));

    [SysAbiExport(
        Nid = "p+GcLqwpL9M",
        ExportName = "sceNpUniversalDataSystemCreateEvent",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemCreateEvent(CpuContext ctx)
    {
        if (!TryReadGuestString(ctx, ctx[CpuRegister.Rdi], out var name, out var error))
        {
            return SetResult(ctx, error);
        }

        var eventOutputAddress = ctx[CpuRegister.Rdx];
        var propertyOutputAddress = ctx[CpuRegister.Rcx];
        if (eventOutputAddress == 0)
        {
            return InvalidArgument(ctx);
        }

        if (!ctx.TryWriteUInt64(eventOutputAddress, 0) ||
            (propertyOutputAddress != 0 && !ctx.TryWriteUInt64(propertyOutputAddress, 0)))
        {
            return MemoryFault(ctx);
        }

        if (!NpUniversalDataSystemState.TryCreateEvent(
                name,
                ctx[CpuRegister.Rsi],
                propertyOutputAddress != 0,
                out var eventHandle,
                out var propertyHandle))
        {
            return InvalidArgument(ctx);
        }

        if (!ctx.TryWriteUInt64(eventOutputAddress, eventHandle) ||
            (propertyOutputAddress != 0 && !ctx.TryWriteUInt64(propertyOutputAddress, propertyHandle)))
        {
            return MemoryFault(ctx);
        }

        return Success(ctx);
    }

    [SysAbiExport(
        Nid = "wG+84pnNIuo",
        ExportName = "sceNpUniversalDataSystemDestroyEvent",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemDestroyEvent(CpuContext ctx) =>
        StateResult(ctx, NpUniversalDataSystemState.DestroyEvent(ctx[CpuRegister.Rdi]));

    [SysAbiExport(
        Nid = "CzkKf7ahIyU",
        ExportName = "sceNpUniversalDataSystemPostEvent",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemPostEvent(CpuContext ctx) =>
        StateResult(
            ctx,
            NpUniversalDataSystemState.PostEvent(
                unchecked((int)ctx[CpuRegister.Rdi]),
                unchecked((int)ctx[CpuRegister.Rsi]),
                ctx[CpuRegister.Rdx],
                ctx[CpuRegister.Rcx]));

    [SysAbiExport(
        Nid = "su7jW3VDDb4",
        ExportName = "sceNpUniversalDataSystemGetMemoryStat",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemGetMemoryStat(CpuContext ctx)
    {
        var outputAddress = ctx[CpuRegister.Rdi];
        if (outputAddress == 0)
        {
            return InvalidArgument(ctx);
        }

        var stat = NpUniversalDataSystemState.GetMemoryStat();
        Span<byte> output = stackalloc byte[3 * sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(output, stat.PoolSize);
        BinaryPrimitives.WriteUInt64LittleEndian(output[sizeof(ulong)..], stat.MaximumInUseSize);
        BinaryPrimitives.WriteUInt64LittleEndian(output[(2 * sizeof(ulong))..], stat.CurrentInUseSize);
        return ctx.Memory.TryWrite(outputAddress, output) ? Success(ctx) : MemoryFault(ctx);
    }

    [SysAbiExport(
        Nid = "MfDb+4Nln64",
        ExportName = "sceNpUniversalDataSystemEventPropertyObjectSetString",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemEventPropertyObjectSetString(CpuContext ctx)
    {
        if (!TryReadObjectKey(ctx, out var key, out var error) ||
            !TryReadGuestString(ctx, ctx[CpuRegister.Rdx], out var value, out error))
        {
            return SetResult(ctx, error);
        }

        return SetObjectValue(ctx, key, new UdsValue(UdsValueKind.String, value));
    }

    [SysAbiExport(
        Nid = "YE4dbtbz6OE",
        ExportName = "sceNpUniversalDataSystemEventPropertyObjectSetInt32",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemEventPropertyObjectSetInt32(CpuContext ctx) =>
        SetObjectPrimitive(ctx, UdsValueKind.Int32, unchecked((int)ctx[CpuRegister.Rdx]));

    [SysAbiExport(
        Nid = "AzD4irAcKE4",
        ExportName = "sceNpUniversalDataSystemEventPropertyObjectSetUInt32",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemEventPropertyObjectSetUInt32(CpuContext ctx) =>
        SetObjectPrimitive(ctx, UdsValueKind.UInt32, unchecked((uint)ctx[CpuRegister.Rdx]));

    [SysAbiExport(
        Nid = "56QLTqx911s",
        ExportName = "sceNpUniversalDataSystemEventPropertyObjectSetInt64",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemEventPropertyObjectSetInt64(CpuContext ctx) =>
        SetObjectPrimitive(ctx, UdsValueKind.Int64, unchecked((long)ctx[CpuRegister.Rdx]));

    [SysAbiExport(
        Nid = "xvsP5Yz6FmY",
        ExportName = "sceNpUniversalDataSystemEventPropertyObjectSetUInt64",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemEventPropertyObjectSetUInt64(CpuContext ctx) =>
        SetObjectPrimitive(ctx, UdsValueKind.UInt64, ctx[CpuRegister.Rdx]);

    [SysAbiExport(
        Nid = "lbPlT4+QVcE",
        ExportName = "sceNpUniversalDataSystemEventPropertyObjectSetFloat32",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemEventPropertyObjectSetFloat32(CpuContext ctx)
    {
        ctx.GetXmmRegister(0, out var low, out _);
        return SetObjectPrimitive(
            ctx,
            UdsValueKind.Float32,
            BitConverter.Int32BitsToSingle(unchecked((int)(uint)low)));
    }

    [SysAbiExport(
        Nid = "4Fu8tHW+u-k",
        ExportName = "sceNpUniversalDataSystemEventPropertyObjectSetFloat64",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemEventPropertyObjectSetFloat64(CpuContext ctx)
    {
        ctx.GetXmmRegister(0, out var low, out _);
        return SetObjectPrimitive(ctx, UdsValueKind.Float64, BitConverter.Int64BitsToDouble(unchecked((long)low)));
    }

    [SysAbiExport(
        Nid = "Fidd8vWgyVE",
        ExportName = "sceNpUniversalDataSystemEventPropertyObjectSetBool",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemEventPropertyObjectSetBool(CpuContext ctx) =>
        SetObjectPrimitive(ctx, UdsValueKind.Bool, (byte)ctx[CpuRegister.Rdx] != 0);

    [SysAbiExport(
        Nid = "74ASEqxSnkM",
        ExportName = "sceNpUniversalDataSystemEventPropertyObjectSetObject",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemEventPropertyObjectSetObject(CpuContext ctx) =>
        SetObjectNode(ctx, childIsArray: false);

    [SysAbiExport(
        Nid = "Wxbg5x3pTXA",
        ExportName = "sceNpUniversalDataSystemEventPropertyObjectSetArray",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemEventPropertyObjectSetArray(CpuContext ctx) =>
        SetObjectNode(ctx, childIsArray: true);

    [SysAbiExport(
        Nid = "4llLk7YJRTE",
        ExportName = "sceNpUniversalDataSystemEventPropertyArraySetString",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemEventPropertyArraySetString(CpuContext ctx)
    {
        if (!TryReadGuestString(ctx, ctx[CpuRegister.Rsi], out var value, out var error))
        {
            return SetResult(ctx, error);
        }

        return AddArrayValue(ctx, new UdsValue(UdsValueKind.String, value));
    }

    [SysAbiExport(
        Nid = "JmgwKm96Lq4",
        ExportName = "sceNpUniversalDataSystemEventPropertyArraySetFloat32",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemEventPropertyArraySetFloat32(CpuContext ctx)
    {
        ctx.GetXmmRegister(0, out var low, out _);
        return AddArrayValue(
            ctx,
            new UdsValue(UdsValueKind.Float32, BitConverter.Int32BitsToSingle(unchecked((int)(uint)low))));
    }

    [SysAbiExport(
        Nid = "0+l4QSWCM4E",
        ExportName = "sceNpUniversalDataSystemEventPropertyArraySetBool",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemEventPropertyArraySetBool(CpuContext ctx) =>
        AddArrayValue(ctx, new UdsValue(UdsValueKind.Bool, (byte)ctx[CpuRegister.Rsi] != 0));

    [SysAbiExport(
        Nid = "XY14n3jNIpE",
        ExportName = "sceNpUniversalDataSystemEventPropertyArraySetObject",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemEventPropertyArraySetObject(CpuContext ctx) =>
        AddArrayNode(ctx, childIsArray: false);

    [SysAbiExport(
        Nid = "rdi9BAfDLq8",
        ExportName = "sceNpUniversalDataSystemEventPropertyArraySetArray",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemEventPropertyArraySetArray(CpuContext ctx) =>
        AddArrayNode(ctx, childIsArray: true);

    private static int SetObjectPrimitive(CpuContext ctx, UdsValueKind kind, object value)
    {
        if (!TryReadObjectKey(ctx, out var key, out var error))
        {
            return SetResult(ctx, error);
        }

        return SetObjectValue(ctx, key, new UdsValue(kind, value));
    }

    private static int SetObjectValue(CpuContext ctx, string key, UdsValue value) =>
        StateResult(ctx, NpUniversalDataSystemState.SetObjectValue(ctx[CpuRegister.Rdi], key, value));

    private static int AddArrayValue(CpuContext ctx, UdsValue value) =>
        StateResult(ctx, NpUniversalDataSystemState.AddArrayValue(ctx[CpuRegister.Rdi], value));

    private static int SetObjectNode(CpuContext ctx, bool childIsArray)
    {
        if (!TryReadObjectKey(ctx, out var key, out var error))
        {
            return SetResult(ctx, error);
        }

        var outputAddress = ctx[CpuRegister.Rcx];
        if (outputAddress != 0 && !ctx.TryWriteUInt64(outputAddress, 0))
        {
            return MemoryFault(ctx);
        }

        if (!NpUniversalDataSystemState.TrySetObjectNode(
                ctx[CpuRegister.Rdi],
                key,
                ctx[CpuRegister.Rdx],
                childIsArray,
                outputAddress != 0,
                out var resolvedHandle))
        {
            return InvalidArgument(ctx);
        }

        return outputAddress == 0 || ctx.TryWriteUInt64(outputAddress, resolvedHandle)
            ? Success(ctx)
            : MemoryFault(ctx);
    }

    private static int AddArrayNode(CpuContext ctx, bool childIsArray)
    {
        var outputAddress = ctx[CpuRegister.Rdx];
        if (outputAddress != 0 && !ctx.TryWriteUInt64(outputAddress, 0))
        {
            return MemoryFault(ctx);
        }

        if (!NpUniversalDataSystemState.TryAddArrayNode(
                ctx[CpuRegister.Rdi],
                ctx[CpuRegister.Rsi],
                childIsArray,
                outputAddress != 0,
                out var resolvedHandle))
        {
            return InvalidArgument(ctx);
        }

        return outputAddress == 0 || ctx.TryWriteUInt64(outputAddress, resolvedHandle)
            ? Success(ctx)
            : MemoryFault(ctx);
    }

    private static bool TryReadObjectKey(CpuContext ctx, out string key, out int error) =>
        TryReadGuestString(ctx, ctx[CpuRegister.Rsi], out key, out error);

    private static bool TryReadGuestString(CpuContext ctx, ulong address, out string value, out int error)
    {
        if (address == 0)
        {
            value = string.Empty;
            error = ErrorInvalidArgument;
            return false;
        }

        if (!ctx.TryReadNullTerminatedUtf8(address, MaximumGuestStringBytes, out value))
        {
            error = (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            return false;
        }

        error = 0;
        return true;
    }

    private static int StateResult(CpuContext ctx, bool success) =>
        success ? Success(ctx) : InvalidArgument(ctx);

    private static int Success(CpuContext ctx) => SetResult(ctx, 0);

    private static int InvalidArgument(CpuContext ctx) => SetResult(ctx, ErrorInvalidArgument);

    private static int MemoryFault(CpuContext ctx) =>
        SetResult(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);

    private static int SetResult(CpuContext ctx, int result) => ctx.SetReturn(result, typeof(long));
}
