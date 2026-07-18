// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Threading;

namespace SharpEmu.Libs.Audio;

public static class AcmExports
{
    private static readonly ConcurrentDictionary<uint, AcmContextState> Contexts = new();
    private static int _nextContextId;

    private sealed record AcmContextState(
        ulong ParameterAddress,
        ulong WorkMemoryAddress,
        ulong WorkMemorySize);

    [SysAbiExport(
        Nid = "ZIXln2K3XMk",
        ExportName = "sceAcmContextCreate",
        Target = Generation.Gen5,
        LibraryName = "libSceAcm")]
    public static int AcmContextCreate(CpuContext ctx)
    {
        var outputAddress = ctx[CpuRegister.Rdi];
        if (outputAddress == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var contextId = unchecked((uint)Interlocked.Increment(ref _nextContextId));
        Span<byte> value = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(value, contextId);
        if (!ctx.Memory.TryWrite(outputAddress, value))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        Contexts[contextId] = new AcmContextState(
            ParameterAddress: ctx[CpuRegister.Rsi],
            WorkMemoryAddress: ctx[CpuRegister.Rcx],
            WorkMemorySize: ctx[CpuRegister.R8]);

        Trace(
            $"context_create context={contextId} out=0x{outputAddress:X} " +
            $"param=0x{ctx[CpuRegister.Rsi]:X} memory=0x{ctx[CpuRegister.Rcx]:X} " +
            $"size=0x{ctx[CpuRegister.R8]:X}");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "jBgBjAj02R8",
        ExportName = "sceAcmContextDestroy",
        Target = Generation.Gen5,
        LibraryName = "libSceAcm")]
    public static int AcmContextDestroy(CpuContext ctx)
    {
        var contextId = unchecked((uint)ctx[CpuRegister.Rdi]);
        Contexts.TryRemove(contextId, out _);
        Trace($"context_destroy context={contextId}");
        return ctx.SetReturn(0);
    }

    internal static void ResetForTests()
    {
        Contexts.Clear();
        Interlocked.Exchange(ref _nextContextId, 0);
    }

    private static void Trace(string message)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_ACM"), "1", StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"[LOADER][TRACE] acm.{message}");
        }
    }
}
