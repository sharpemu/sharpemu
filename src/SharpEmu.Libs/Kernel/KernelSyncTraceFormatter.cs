// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Kernel;

/// <summary>
/// Shared formatting for opt-in kernel synchronization diagnostics.
/// Callers must gate this behind their trace flag so normal synchronization
/// paths do not allocate strings or walk guest frame chains.
/// </summary>
internal static class KernelSyncTraceFormatter
{
    internal static string FormatContext(CpuContext ctx)
    {
        _ = KernelPthreadState.TryGetCurrentThreadIdentity(out var pthread, out var identity);
        var threadName = identity.Name ?? "<unknown>";
        var returnRip = GuestThreadExecution.TryGetCurrentImportCallFrame(out var importFrame)
            ? importFrame.ReturnRip
            : TryReadReturnRip(ctx);

        return $"thread='{threadName}' pthread=0x{pthread:X16} " +
               $"gth=0x{GuestThreadExecution.CurrentGuestThreadHandle:X16} " +
               $"managed={Environment.CurrentManagedThreadId} ret=0x{returnRip:X16} " +
               $"frames={FormatFrameChain(ctx)}";
    }

    internal static string FormatCurrentThread()
    {
        _ = KernelPthreadState.TryGetCurrentThreadIdentity(out var pthread, out var identity);
        var threadName = identity.Name ?? Thread.CurrentThread.Name ?? "<unknown>";
        return $"thread='{threadName}' pthread=0x{pthread:X16} " +
               $"gth=0x{GuestThreadExecution.CurrentGuestThreadHandle:X16} " +
               $"managed={Environment.CurrentManagedThreadId}";
    }

    internal static string FormatFrameChain(CpuContext ctx)
    {
        Span<ulong> returns = stackalloc ulong[4];
        var count = 0;
        var frame = ctx[CpuRegister.Rbp];
        while (count < returns.Length && frame != 0)
        {
            if (!ctx.TryReadUInt64(frame, out var nextFrame) ||
                !ctx.TryReadUInt64(frame + sizeof(ulong), out var returnAddress))
            {
                break;
            }

            returns[count++] = returnAddress;
            if (nextFrame <= frame || nextFrame - frame > 0x100000)
            {
                break;
            }

            frame = nextFrame;
        }

        return count switch
        {
            0 => "none",
            1 => $"0x{returns[0]:X16}",
            2 => $"0x{returns[0]:X16},0x{returns[1]:X16}",
            3 => $"0x{returns[0]:X16},0x{returns[1]:X16},0x{returns[2]:X16}",
            _ => $"0x{returns[0]:X16},0x{returns[1]:X16}," +
                 $"0x{returns[2]:X16},0x{returns[3]:X16}",
        };
    }

    private static ulong TryReadReturnRip(CpuContext ctx)
    {
        _ = ctx.TryReadUInt64(ctx[CpuRegister.Rsp], out var returnRip);
        return returnRip;
    }
}
