// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Network;

public static class SslExports
{
    private const int SslErrorInvalidId = unchecked((int)0x8095F006);
    private const int SslErrorOutOfSize = unchecked((int)0x8095F008);

    private static readonly ConcurrentDictionary<int, SslContext> _contexts = new();
    private static int _nextContextId;

    private sealed record SslContext(ulong PoolSize);

    [SysAbiExport(
        Nid = "hdpVEUDFW3s",
        ExportName = "sceSslInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSsl")]
    public static int SslInit(CpuContext ctx)
    {
        var poolSize = ctx[CpuRegister.Rdi];
        if (poolSize == 0)
        {
            return ctx.SetReturn(SslErrorOutOfSize);
        }

        var id = Interlocked.Increment(ref _nextContextId);
        _contexts[id] = new SslContext(poolSize);

        TraceSsl("init", id, poolSize);
        ctx[CpuRegister.Rax] = unchecked((ulong)id);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "0K1yQ6Lv-Yc",
        ExportName = "sceSslTerm",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSsl")]
    public static int SslTerm(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!_contexts.TryRemove(id, out _))
        {
            return ctx.SetReturn(SslErrorInvalidId);
        }

        TraceSsl("term", id, 0);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "viRXSHZYd0c",
        ExportName = "sceSslClose",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSsl")]
    public static int SslClose(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        TraceSsl("close", id, 0);
        return ctx.SetReturn(0);
    }

    private static void TraceSsl(string operation, int id, ulong arg0)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_SSL"), "1", StringComparison.Ordinal))
        {
            return;
        }

        Console.Error.WriteLine(
            $"[LOADER][TRACE] ssl.{operation} id={id} arg0=0x{arg0:X16}");
    }
}
