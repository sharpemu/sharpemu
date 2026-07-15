// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Kernel;

public static class KernelExceptionCompatExports
{
    private static readonly HashSet<int> AllowedSignals = new() { 1, 4, 8, 10, 11, 30 };
    private static readonly Dictionary<int, ulong> _installedHandlers = new();
    private static readonly object _gate = new();

    [SysAbiExport(
        Nid = "WkwEd3N7w0Y",
        ExportName = "sceKernelInstallExceptionHandler",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int InstallExceptionHandler(CpuContext ctx)
    {
        var signum = unchecked((int)ctx[CpuRegister.Rdi]);
        var handler = ctx[CpuRegister.Rsi];

        if (!AllowedSignals.Contains(signum))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        lock (_gate)
        {
            if (_installedHandlers.ContainsKey(signum))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_ALREADY_EXISTS;
            }

            _installedHandlers[signum] = handler;
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "Qhv5ARAoOEc",
        ExportName = "sceKernelRemoveExceptionHandler",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int RemoveExceptionHandler(CpuContext ctx)
    {
        var signum = unchecked((int)ctx[CpuRegister.Rdi]);

        if (!AllowedSignals.Contains(signum))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        lock (_gate)
        {
            _installedHandlers.Remove(signum);
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "il03nluKfMk",
        ExportName = "sceKernelRaiseException",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int RaiseException(CpuContext ctx)
    {
        var targetThread = ctx[CpuRegister.Rdi];
        var exceptionType = unchecked((int)ctx[CpuRegister.Rsi]);
        if (targetThread == 0 || exceptionType is < 0 or >= 128)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        ulong handler;
        lock (_gate)
        {
            if (!_installedHandlers.TryGetValue(exceptionType, out handler))
            {
                // The kernel accepts a raise for a type with no process
                // handler; there is simply no user callback to deliver.
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
            }
        }

        if (string.Equals(
                Environment.GetEnvironmentVariable("SHARPEMU_IGNORE_GUEST_EXCEPTIONS"),
                "1",
                StringComparison.Ordinal))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
        }

        var scheduler = GuestThreadExecution.Scheduler;
        string? error = null;
        if (scheduler is null ||
            !scheduler.TryRaiseGuestException(
                ctx,
                targetThread,
                handler,
                exceptionType,
                out error))
        {
            Console.Error.WriteLine(
                $"[LOADER][WARN] sceKernelRaiseException delivery failed: " +
                $"target=0x{targetThread:X16} type=0x{exceptionType:X2} " +
                $"error={error ?? "scheduler unavailable"}");
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY);
        }

        if (string.Equals(
                Environment.GetEnvironmentVariable("SHARPEMU_LOG_GUEST_EXCEPTIONS"),
                "1",
                StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                $"[LOADER][TRACE] guest_exception.raise " +
                $"target=0x{targetThread:X16} type=0x{exceptionType:X2} " +
                $"handler=0x{handler:X16}");
        }

        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
    }

    private static int SetReturn(CpuContext ctx, OrbisGen2Result result)
    {
        var value = (int)result;
        ctx[CpuRegister.Rax] = unchecked((ulong)value);
        return value;
    }
}
